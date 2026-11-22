using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EstateActions;

/// <summary>An HTTP answer from GitHub that the command can't treat as a clear yes or no.</summary>
internal sealed class GitHubApiException(string message, HttpStatusCode? status) : Exception(message)
{
    /// <summary>The HTTP status, or null when the failure wasn't a status (an unsafe next-page link, say).</summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Whether <c>refs/tags/{tag}</c> exists. Only HTTP 404 means absent.</summary>
internal readonly record struct TagState(bool Exists, string? Sha);

/// <summary>
/// The REST calls the release actions make, on a raw <see cref="HttpClient"/>. Reads (GET) are retried a few times on
/// a transient answer (5xx, 429, a rate-limited 403) or a dropped connection. Writes are never retried: a retried POST
/// could create a second draft, and a retried PATCH or DELETE after an unseen success would misreport what happened.
/// </summary>
internal sealed partial class GitHubApi
{
    /// <summary>Attempts per read, including the first.</summary>
    public const int ReadAttempts = 3;

    /// <summary>Pages followed before the release list is treated as runaway.</summary>
    public const int MaxPages = 100;

    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _repo;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Its <see cref="HttpClient.BaseAddress"/> is the API root (ending in '/'), and it carries the token.</param>
    /// <param name="repository"><c>owner/name</c>.</param>
    /// <param name="delay">Waits between read attempts; injected so tests never sleep.</param>
    public GitHubApi(HttpClient http, string repository, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (http.BaseAddress is null)
            throw new ArgumentException("The GitHub client needs a base address.", nameof(http));
        if (!RepositoryName().IsMatch(repository))
            throw new ActionFailure($"'{repository}' isn't owner/name.");
        _http = http;
        _repo = repository;
        _delay = delay ?? ((d, c) => Task.Delay(d, c));
    }

    public static HttpClient CreateClient(string apiUrl, string token, string userAgent, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    /// <summary>
    /// Every release, drafts included, following <c>Link: rel="next"</c>. <c>releases/tags/{tag}</c> never returns a
    /// draft, and drafts are listed only to a token with push access.
    /// </summary>
    public async Task<List<Release>> ListReleasesAsync(CancellationToken ct)
    {
        var all = new List<Release>();
        var seen = new HashSet<Uri>();
        Uri? next = new(_http.BaseAddress!, $"repos/{_repo}/releases?per_page=100");
        while (next is not null)
        {
            if (!seen.Add(next) || seen.Count > MaxPages)
                throw new GitHubApiException($"the release list didn't end after {seen.Count - 1} pages.", null);
            var url = next;
            using var response = await GetAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            await EnsureSuccess(response, "list releases", ct).ConfigureAwait(false);
            var page = await Read(response, ActionsJson.Default.ListRelease, "the release list", ct).ConfigureAwait(false);
            all.AddRange(page);
            next = NextPage(response);
        }
        return all;
    }

    public async Task<Release> CreateReleaseAsync(CreateReleaseRequest request, CancellationToken ct)
    {
        using var content = JsonContent.Create(request, ActionsJson.Default.CreateReleaseRequest);
        using var response = await _http.PostAsync($"repos/{_repo}/releases", content, ct).ConfigureAwait(false);
        await EnsureSuccess(response, "create the release", ct).ConfigureAwait(false);
        return await Read(response, ActionsJson.Default.Release, "the created release", ct).ConfigureAwait(false);
    }

    public async Task UpdateReleaseAsync(long id, UpdateReleaseRequest request, CancellationToken ct)
    {
        using var content = JsonContent.Create(request, ActionsJson.Default.UpdateReleaseRequest);
        using var response = await _http.PatchAsync($"repos/{_repo}/releases/{id}", content, ct).ConfigureAwait(false);
        await EnsureSuccess(response, $"update release {id}", ct).ConfigureAwait(false);
    }

    public async Task DeleteReleaseAsync(long id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"repos/{_repo}/releases/{id}", ct).ConfigureAwait(false);
        await EnsureSuccess(response, $"delete release {id}", ct).ConfigureAwait(false);
    }

    /// <summary><c>git/ref/tags/{tag}</c>: 404 is absent; any other failure throws rather than being read as absent.</summary>
    public async Task<TagState> GetTagAsync(string tag, CancellationToken ct)
    {
        using var response = await GetAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"repos/{_repo}/git/ref/tags/{EscapePath(tag)}"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new TagState(false, null);
        await EnsureSuccess(response, $"read the tag {tag}", ct).ConfigureAwait(false);
        var gitRef = await Read(response, ActionsJson.Default.GitRef, $"the tag {tag}", ct).ConfigureAwait(false);
        return new TagState(true, gitRef.Object?.Sha);
    }

    /// <summary>A file's bytes at a commit, through the contents API. The bytes are data; nothing here runs them.</summary>
    public async Task<byte[]> GetFileAsync(string path, string commit, CancellationToken ct)
    {
        using var response = await GetAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{_repo}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(commit)}");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
            return request;
        }, ct).ConfigureAwait(false);
        await EnsureSuccess(response, $"read {path} at {commit}", ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a read, retrying a transient answer or a dropped connection up to <see cref="ReadAttempts"/> times.
    /// The last answer is returned as is, for the caller to judge.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < ReadAttempts && e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (attempt >= ReadAttempts || !IsTransient(response))
                return response;
            var wait = RetryAfter(response) ?? Backoff(attempt);
            response.Dispose();
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500
        || response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden
            && (response.Headers.RetryAfter is not null || Header(response, "x-ratelimit-remaining") == "0"));

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(2 * attempt);

    /// <summary>The wait GitHub asks for (<c>Retry-After</c>, or the rate-limit reset), capped; null if it named none.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? wait = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        if (wait is null && long.TryParse(Header(response, "x-ratelimit-reset"), out var reset))
            wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
        if (wait is not { } w)
            return null;
        return w < TimeSpan.Zero ? TimeSpan.Zero : w > MaxRetryWait ? MaxRetryWait : w;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Escapes each segment, keeping the '/' separators.</summary>
    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                var pieces = part.Split(';');
                if (pieces.Length < 2 || !pieces.Skip(1).Any(p => p.Trim() == "rel=\"next\""))
                    continue;
                var url = pieces[0].Trim().TrimStart('<').TrimEnd('>');
                var next = new Uri(url, UriKind.Absolute);
                // The token goes only to the API host it was configured for.
                if (Uri.Compare(next, _http.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                    throw new GitHubApiException($"the release list's next page is on another host ({next.Host}).", null);
                return next;
            }
        }
        return null;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = "";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var error = JsonSerializer.Deserialize(body, ActionsJson.Default.ApiError);
            if (error?.Message is { Length: > 0 } message)
                detail = $": {message}";
            // A 422's reason is in errors[], e.g. {"resource":"Release","field":"tag_name","code":"already_exists"}.
            var reasons = (error?.Errors ?? [])
                .Select(e => e.Message ?? string.Join(' ', new[] { e.Resource, e.Field, e.Code }.Where(s => !string.IsNullOrEmpty(s))))
                .Where(s => s.Length > 0)
                .ToList();
            if (reasons.Count > 0)
                detail += $" [{string.Join("; ", reasons)}]";
        }
        catch (JsonException)
        {
            // Not GitHub's JSON error shape; the status says enough.
        }
        if (RetryAfter(response) is { } wait && IsTransient(response))
            detail += $"; GitHub asked to wait {Math.Ceiling(wait.TotalSeconds)}s";
        if (Header(response, "x-github-request-id") is { Length: > 0 } id)
            detail += $"; request {id}";
        throw new GitHubApiException($"couldn't {what} (HTTP {(int)response.StatusCode}{detail})", response.StatusCode);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, JsonTypeInfo<T> type, string what, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false)
                ?? throw new GitHubApiException($"GitHub returned an empty body for {what}.", response.StatusCode);
        }
        catch (JsonException e)
        {
            throw new GitHubApiException($"GitHub returned unreadable JSON for {what}: {e.Message}", response.StatusCode);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z")]
    private static partial System.Text.RegularExpressions.Regex RepositoryName();
}
