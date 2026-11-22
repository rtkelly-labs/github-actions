using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace EstateActions;

/// <summary>
/// nuget.org's flat container, on its own <see cref="HttpClient"/> that never carries the GitHub token.
/// </summary>
internal sealed class NuGetApi(HttpClient http)
{
    public const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    public static HttpClient CreateClient(string userAgent, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(FlatContainer);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>
    /// The version's nuspec: 200 is public (returns it), 404 is not (returns null), and anything else throws. An outage
    /// must never read as "not public": discard would then delete a live version's draft.
    /// </summary>
    public async Task<string?> GetNuspecAsync(string packageId, string version, CancellationToken ct)
    {
        var id = packageId.ToLowerInvariant();
        var v = version.ToLowerInvariant();
        using var response = await http.GetAsync($"{id}/{v}/{id}.nuspec", ct).ConfigureAwait(false);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false),
            HttpStatusCode.NotFound => null,
            _ => throw new ActionFailure($"nuget.org answered HTTP {(int)response.StatusCode} for {packageId} {version}; try again later."),
        };
    }

    /// <summary>The <c>commit</c> attribute of the nuspec's first <c>&lt;repository&gt;</c> element, or null.</summary>
    public static string? RepositoryCommit(Stream nuspec)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(nuspec, settings);
        return XDocument.Load(reader).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("commit")?.Value;
    }

    public static string? RepositoryCommit(string nuspec)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(nuspec));
        return RepositoryCommit(stream);
    }
}
