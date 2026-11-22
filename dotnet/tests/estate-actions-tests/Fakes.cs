using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EstateActions.Tests;

/// <summary>
/// An in-memory GitHub REST API with the behaviours the release actions depend on, each learned against the real API
/// (see the opt-in integration test):
/// <list type="bullet">
/// <item>A draft creates no tag; publishing it (PATCH draft=false) creates the tag at its target_commitish.</item>
/// <item>target_commitish is ignored when the tag already exists.</item>
/// <item>A second draft with the same tag_name is accepted silently.</item>
/// <item>A new release appears in <c>GET /releases</c> only after a lag (<see cref="ListLag"/> list calls).</item>
/// <item><c>GET /releases</c> pages, linked by <c>Link: rel="next"</c>.</item>
/// </list>
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string Api = "https://api.github.test";
    public const string Repo = "octo-org/octo-repo";

    private readonly Lock _gate = new();
    private long _nextId = 1000;
    private int _listCalls;

    public List<FakeRelease> Releases { get; } = [];

    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string Path, string Ref), string> Files { get; } = [];

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>How many list calls after its creation a release made through the API stays unlisted.</summary>
    public int ListLag { get; set; }

    public int PageSize { get; set; } = 100;

    /// <summary>Releases created through the API stay unlisted until this many have been created (then all appear).</summary>
    public int HoldCreatedUntil { get; set; }

    private int _created;

    /// <summary>Deleted releases keep appearing in the list (deletes are eventually consistent too).</summary>
    public bool DeletedStayListed { get; set; }

    private readonly List<FakeRelease> _deleted = [];

    /// <summary>Answer a request with this status instead (e.g. an outage), when it returns non-null.</summary>
    public Func<HttpMethod, string, HttpStatusCode?> Fault { get; set; } = (_, _) => null;

    /// <summary>The JSON body sent with a <see cref="Fault"/>.</summary>
    public string FaultBody { get; set; } = """{"message":"simulated failure"}""";

    /// <summary>A tag created by publishing reads as absent for this many reads.</summary>
    public int TagReadLag { get; set; }

    private int _tagHiddenReads;

    public FakeRelease Seed(string tag, string target, bool draft = true, bool prerelease = false, int hiddenForListCalls = 0)
    {
        lock (_gate)
        {
            var release = new FakeRelease(_nextId++, tag, target, draft, prerelease, _listCalls + hiddenForListCalls);
            Releases.Add(release);
            return release;
        }
    }

    public void Manifest(string commit, string json) => Files[(".github/release-packages.json", commit)] = json;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, body, request.Headers.Authorization?.ToString()));
            if (Fault(request.Method, path) is { } status)
                return Json(status, FaultBody);
            return Route(request.Method, request.RequestUri, path, body);
        }
    }

    private HttpResponseMessage Route(HttpMethod method, Uri uri, string path, string? body)
    {
        var prefix = $"/repos/{Repo}/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        var rest = path[prefix.Length..];

        if (rest == "releases" && method == HttpMethod.Get)
            return ListReleases(uri);
        if (rest == "releases" && method == HttpMethod.Post)
            return CreateRelease(body!);
        if (rest.StartsWith("releases/", StringComparison.Ordinal) && long.TryParse(rest["releases/".Length..], out var id))
        {
            var release = Releases.Find(r => r.Id == id);
            if (release is null)
                return Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            if (method == HttpMethod.Delete)
            {
                Releases.Remove(release);
                if (DeletedStayListed)
                    _deleted.Add(release);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (method == HttpMethod.Patch)
                return UpdateRelease(release, body!);
        }
        if (rest.StartsWith("git/ref/tags/", StringComparison.Ordinal) && method == HttpMethod.Get)
        {
            var tag = Uri.UnescapeDataString(rest["git/ref/tags/".Length..]);
            if (_tagHiddenReads > 0)
            {
                _tagHiddenReads--;
                return Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            }
            return Tags.TryGetValue(tag, out var sha)
                ? Json(HttpStatusCode.OK, $$"""{"ref":"refs/tags/{{tag}}","object":{"sha":"{{sha}}","type":"commit"} }""")
                : Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        }
        if (rest.StartsWith("contents/", StringComparison.Ordinal) && method == HttpMethod.Get)
        {
            var file = Uri.UnescapeDataString(rest["contents/".Length..]);
            var at = Query(uri, "ref") ?? "";
            return Files.TryGetValue((file, at), out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) }
                : Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        }
        return Json(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
    }

    private HttpResponseMessage ListReleases(Uri uri)
    {
        var page = int.Parse(Query(uri, "page") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        if (page == 1)
            _listCalls++;
        var visible = Releases.Concat(_deleted)
            .Where(r => r.VisibleAfterListCall < _listCalls && !(r.CreatedThroughApi && _created < HoldCreatedUntil))
            .OrderByDescending(r => r.Id).ToList();
        var slice = visible.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        var array = new JsonArray(slice.Select(r => (JsonNode)r.ToJson()).ToArray());
        var response = Json(HttpStatusCode.OK, array.ToJsonString());
        if (page * PageSize < visible.Count)
            response.Headers.Add("Link", $"<{Api}/repos/{Repo}/releases?per_page=100&page={page + 1}>; rel=\"next\", <{Api}/repos/{Repo}/releases?per_page=100&page=99>; rel=\"last\"");
        return response;
    }

    private HttpResponseMessage CreateRelease(string body)
    {
        var json = JsonNode.Parse(body)!;
        var release = new FakeRelease(
            _nextId++, (string)json["tag_name"]!, (string)json["target_commitish"]!, (bool)json["draft"]!, (bool)json["prerelease"]!,
            _listCalls + ListLag)
        {
            Name = (string?)json["name"],
            Body = (string?)json["body"],
            CreatedThroughApi = true,
        };
        _created++;
        // Accepted silently even when the tag already has a draft.
        Releases.Add(release);
        return Json(HttpStatusCode.Created, release.ToJson().ToJsonString());
    }

    private HttpResponseMessage UpdateRelease(FakeRelease release, string body)
    {
        var json = JsonNode.Parse(body)!;
        release.Prerelease = (bool)json["prerelease"]!;
        release.MakeLatest = (string?)json["make_latest"];
        if ((string?)json["tag_name"] is { } name)
            release.TagName = name;
        else if (release.Draft)
            // Seen in real runs: editing a draft without tag_name can detach it from its tag name.
            release.TagName = $"untagged-{release.Id:x20}";
        if (release.Draft && !(bool)json["draft"]!)
        {
            release.Draft = false;
            // The tag is created at target_commitish only if it doesn't exist; an existing tag is adopted as is.
            if (Tags.TryAdd(release.TagName, release.Target))
                _tagHiddenReads = TagReadLag;
        }
        return Json(HttpStatusCode.OK, release.ToJson().ToJsonString());
    }

    private static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p[0] == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization);

internal sealed class FakeRelease(long id, string tagName, string target, bool draft, bool prerelease, int visibleAfterListCall)
{
    public long Id { get; } = id;

    public string TagName { get; set; } = tagName;

    public string Target { get; } = target;

    public bool Draft { get; set; } = draft;

    public bool Prerelease { get; set; } = prerelease;

    public string? MakeLatest { get; set; }

    public string? Name { get; init; }

    public string? Body { get; init; }

    public bool CreatedThroughApi { get; init; }

    public int VisibleAfterListCall { get; } = visibleAfterListCall;

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["tag_name"] = TagName,
        ["target_commitish"] = Target,
        ["draft"] = Draft,
        ["prerelease"] = Prerelease,
        ["name"] = Name,
        ["body"] = Body,
        ["html_url"] = $"https://github.test/{FakeGitHub.Repo}/releases/{Id}",
    };
}

/// <summary>nuget.org's flat container: 404 unless a nuspec or a status is set for the path.</summary>
internal sealed class FakeNuGet : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new(StringComparer.Ordinal);

    public List<RecordedRequest> Requests { get; } = [];

    public void Public(string id, string version, string commit) =>
        Responses[$"/v3-flatcontainer/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.nuspec"] =
            (HttpStatusCode.OK, Nuspec(id, version, commit));

    public void Status(string id, string version, HttpStatusCode status) =>
        Responses[$"/v3-flatcontainer/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.nuspec"] = (status, "");

    public static string Nuspec(string id, string version, string? commit) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{id}</id>
            <version>{version}</version>
            {(commit is null ? "" : $"""<repository type="git" url="https://github.test/{FakeGitHub.Repo}" commit="{commit}" />""")}
          </metadata>
        </package>
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, null, request.Headers.Authorization?.ToString()));
        var (status, body) = Responses.TryGetValue(request.RequestUri!.AbsolutePath, out var r) ? r : (HttpStatusCode.NotFound, "Not found");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
    }
}

internal sealed class TestHost : IActionHost
{
    public Dictionary<string, string> Inputs { get; } = new(StringComparer.Ordinal)
    {
        ["GITHUB_API_URL"] = FakeGitHub.Api,
        ["GITHUB_REPOSITORY"] = FakeGitHub.Repo,
        ["GITHUB_TOKEN"] = "test-token",
    };

    public List<string> Errors { get; } = [];

    public List<string> Lines { get; } = [];

    public StringBuilder SummaryText { get; } = new();

    public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

    public string? Get(string name) => Inputs.TryGetValue(name, out var v) ? v : null;

    public void Error(string message)
    {
        lock (Errors)
            Errors.Add(message);
    }

    public void Info(string message)
    {
        lock (Lines)
            Lines.Add(message);
    }

    public void Summary(string markdown)
    {
        lock (SummaryText)
            SummaryText.Append(markdown);
    }

    public void Output(string name, string value)
    {
        lock (Outputs)
            Outputs[name] = value;
    }
}

internal static class Run
{
    public static Task<int> Command(string command, TestHost host, FakeGitHub github, FakeNuGet? nuget = null, List<TimeSpan>? delays = null) =>
        Commands.RunAsync(
            [command], host, github, nuget ?? new FakeNuGet(),
            async (d, _) =>
            {
                if (delays is not null)
                {
                    lock (delays)
                        delays.Add(d);
                }
                await Task.Yield();
            },
            TestContext.Current.CancellationToken);

    public static JsonElement Body(RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement;
}
