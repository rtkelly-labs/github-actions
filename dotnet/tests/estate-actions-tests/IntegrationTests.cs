using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EstateActions.Tests;

/// <summary>
/// Opt-in: runs draft and finalize against real GitHub (nuget.org stays faked). Set ESTATE_ACTIONS_IT_REPO to a
/// scratch owner/name whose <c>.github/release-packages.json</c> has a <c>Demo</c> package, and GITHUB_TOKEN to a
/// token that can write its releases. Only the releases and tag this test creates are deleted.
/// </summary>
public sealed class IntegrationTests
{
    private static readonly string? Repo = Environment.GetEnvironmentVariable("ESTATE_ACTIONS_IT_REPO");
    private static readonly string? Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    [Fact]
    public async Task Draft_then_finalize_against_real_github()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Repo) || string.IsNullOrEmpty(Token), "Set ESTATE_ACTIONS_IT_REPO and GITHUB_TOKEN to run.");
        var ct = TestContext.Current.CancellationToken;
        using var http = GitHubApi.CreateClient("https://api.github.com", Token!, "estate-actions-tests");
        var api = new GitHubApi(http, Repo!);

        // The newest commit carrying the manifest; when main has moved past it, this also shows the tag lands
        // at the draft commit and not at main.
        var sha = await NewestCommitWith(http, ReleaseNames.ManifestPath, ct);
        var version = $"0.0.0-it.{Guid.NewGuid():N}"[..20];
        var tag = $"Demo/v{version}";
        try
        {
            var draft = Host(tag);
            draft.Inputs["RELEASE_SHA"] = sha;
            draft.Inputs["RELEASE_TITLE"] = $"integration test {version}";
            draft.Inputs["RELEASE_NOTES"] = "Created by the estate-actions integration test; safe to delete.";
            draft.Inputs["RELEASE_PRERELEASE"] = "true";
            Assert.Equal(0, await Real(Commands.Draft, draft, null, ct));
            Assert.False((await api.GetTagAsync(tag, ct)).Exists, "a draft must not create its tag");

            var duplicate = Host(tag);
            foreach (var key in new[] { "RELEASE_SHA", "RELEASE_TITLE", "RELEASE_NOTES", "RELEASE_PRERELEASE" })
                duplicate.Inputs[key] = draft.Inputs[key];
            Assert.Equal(1, await Real(Commands.Draft, duplicate, null, ct));
            Assert.Contains("already has a release", Assert.Single(duplicate.Errors), StringComparison.Ordinal);

            var notPublic = Host(tag);
            Assert.Equal(0, await Real(Commands.Finalize, notPublic, new FakeNuGet(), ct));
            Assert.Equal("not-public", notPublic.Outputs["outcome"]);

            var nuget = new FakeNuGet();
            nuget.Public("Demo.Pkg", version, sha);
            var publish = Host(tag);
            Assert.Equal(0, await Real(Commands.Finalize, publish, nuget, ct));
            Assert.Equal("published", publish.Outputs["outcome"]);
            Assert.Equal(sha, (await api.GetTagAsync(tag, ct)).Sha);

            var again = Host(tag);
            Assert.Equal(0, await Real(Commands.Finalize, again, nuget, ct));
            Assert.Equal("already-published", again.Outputs["outcome"]);
        }
        finally
        {
            foreach (var release in (await api.ListReleasesAsync(CancellationToken.None)).Where(r => r.TagName == tag))
                await api.DeleteReleaseAsync(release.Id, CancellationToken.None);
            using var deleteTag = await http.DeleteAsync($"repos/{Repo}/git/refs/tags/{tag}", CancellationToken.None);
            Assert.True(deleteTag.IsSuccessStatusCode || deleteTag.StatusCode == HttpStatusCode.UnprocessableEntity, $"tag cleanup: HTTP {(int)deleteTag.StatusCode}");
        }
    }

    private static TestHost Host(string tag)
    {
        var host = new TestHost();
        host.Inputs["GITHUB_API_URL"] = "https://api.github.com";
        host.Inputs["GITHUB_TOKEN"] = Token!;
        host.Inputs["GITHUB_REPOSITORY"] = Repo!;
        host.Inputs["RELEASE_TAG"] = tag;
        return host;
    }

    private static Task<int> Real(string command, TestHost host, FakeNuGet? nuget, CancellationToken ct) =>
        Commands.RunAsync([command], host, null, nuget, (d, c) => Task.Delay(d, c), ct);

    private static async Task<string> NewestCommitWith(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync($"repos/{Repo}/commits?path={Uri.EscapeDataString(path)}&per_page=1", ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement[0].GetProperty("sha").GetString()!;
    }
}
