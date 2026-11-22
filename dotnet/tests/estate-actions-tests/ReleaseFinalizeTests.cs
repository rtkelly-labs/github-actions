using System.Net;

namespace EstateActions.Tests;

public sealed class ReleaseFinalizeTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Other = "2222222222222222222222222222222222222222";
    private const string Tag = "Demo/v1.2.0";
    private const string DependantTag = "Dependant/v1.2.0";

    private const string Manifest = """
        {"schema_version":1,"packages":{
          "Demo":{"package_id":"Demo.Pkg","depends_on":[]},
          "Dependant":{"package_id":"Dependant.Pkg","depends_on":["Demo"]}}}
        """;

    private static (FakeGitHub GitHub, FakeNuGet NuGet) World(bool prerelease = false)
    {
        var github = new FakeGitHub();
        github.Manifest(Sha, Manifest);
        github.Manifest(Other, Manifest);
        github.Seed(Tag, Sha, prerelease: prerelease);
        return (github, new FakeNuGet());
    }

    private static TestHost Host(string tag = Tag, string? discard = null)
    {
        var host = new TestHost();
        host.Inputs["RELEASE_TAG"] = tag;
        if (discard is not null)
            host.Inputs["RELEASE_DISCARD"] = discard;
        return host;
    }

    private static bool Changed(FakeGitHub github) =>
        github.Requests.Exists(r => r.Method != HttpMethod.Get);

    [Fact]
    public async Task A_public_version_built_from_the_draft_commit_is_published_and_its_tag_read_back()
    {
        var (github, nuget) = World();
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(0, exit);
        Assert.Empty(host.Errors);
        var release = Assert.Single(github.Releases);
        Assert.False(release.Draft);
        Assert.Equal(Sha, github.Tags[Tag]);
        var patch = Assert.Single(github.Requests, r => r.Method == HttpMethod.Patch);
        var body = Run.Body(patch);
        Assert.False(body.GetProperty("draft").GetBoolean());
        Assert.False(body.GetProperty("prerelease").GetBoolean());
        // Sent explicitly: without it the edit can rename the draft to untagged-<hash>.
        Assert.Equal(Tag, body.GetProperty("tag_name").GetString());
        Assert.Equal(Tag, release.TagName);
        Assert.Equal("true", body.GetProperty("make_latest").GetString());
        // The tag is read back after publishing.
        Assert.Equal(HttpMethod.Get, github.Requests[^1].Method);
        Assert.EndsWith($"git/ref/tags/{Tag}", github.Requests[^1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("published", host.Outputs["outcome"]);
        Assert.Contains($"Published {Tag}: tagged at `{Sha}`.", host.SummaryText.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tag_that_is_slow_to_appear_after_publishing_is_waited_for()
    {
        var (github, nuget) = World();
        github.TagReadLag = 2;
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host();
        var delays = new List<TimeSpan>();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget, delays);

        Assert.Equal(0, exit);
        Assert.Equal([ReleaseFinalize.TagReadInterval, ReleaseFinalize.TagReadInterval], delays);
        Assert.Equal("published", host.Outputs["outcome"]);
    }

    [Fact]
    public async Task A_prerelease_is_published_as_a_prerelease_and_not_made_latest()
    {
        var (github, nuget) = World(prerelease: true);
        nuget.Public("Demo.Pkg", "1.2.0", Sha);

        var exit = await Run.Command(Commands.Finalize, Host(), github, nuget);

        Assert.Equal(0, exit);
        var body = Run.Body(Assert.Single(github.Requests, r => r.Method == HttpMethod.Patch));
        Assert.True(body.GetProperty("prerelease").GetBoolean());
        Assert.Equal("false", body.GetProperty("make_latest").GetString());
    }

    [Fact]
    public async Task The_tag_lands_on_the_draft_commit_even_after_main_moved_on()
    {
        // Nothing in finalize reads main: the draft's target_commitish is the only commit it uses.
        var (github, nuget) = World();
        github.Files[(".github/release-packages.json", "main")] = """{"packages":{}}""";
        nuget.Public("Demo.Pkg", "1.2.0", Sha);

        var exit = await Run.Command(Commands.Finalize, Host(), github, nuget);

        Assert.Equal(0, exit);
        Assert.Equal(Sha, github.Tags[Tag]);
        Assert.Contains(github.Requests, r => r.Uri.Query == $"?ref={Sha}");
    }

    [Fact]
    public async Task Finalizing_again_is_a_no_op()
    {
        var (github, nuget) = World();
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        Assert.Equal(0, await Run.Command(Commands.Finalize, Host(), github, nuget));
        github.Requests.Clear();
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(0, exit);
        Assert.False(Changed(github));
        Assert.Equal("already-published", host.Outputs["outcome"]);
        Assert.Contains($"{Tag} is already published; nothing to do.", host.Lines);
    }

    [Fact]
    public async Task A_version_not_yet_public_changes_nothing()
    {
        var (github, nuget) = World();
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(0, exit);
        Assert.False(Changed(github));
        Assert.True(Assert.Single(github.Releases).Draft);
        Assert.Equal("not-public", host.Outputs["outcome"]);
        Assert.Contains("Demo 1.2.0 isn't on nuget.org yet (promote it there first); nothing changed.", host.Lines);
        var lookup = Assert.Single(nuget.Requests);
        Assert.Equal("/v3-flatcontainer/demo.pkg/1.2.0/demo.pkg.nuspec", lookup.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_nuget_outage_fails_rather_than_reading_as_not_public(HttpStatusCode status)
    {
        var (github, nuget) = World();
        nuget.Status("Demo.Pkg", "1.2.0", status);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Equal($"nuget.org answered HTTP {(int)status} for Demo.Pkg 1.2.0; try again later.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_package_built_from_another_commit_is_refused()
    {
        var (github, nuget) = World();
        nuget.Public("Demo.Pkg", "1.2.0", Other);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Equal($"Demo 1.2.0 on nuget.org was built from '{Other}', but the draft is at {Sha}.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_package_that_records_no_commit_is_refused()
    {
        var (github, nuget) = World();
        nuget.Responses["/v3-flatcontainer/demo.pkg/1.2.0/demo.pkg.nuspec"] = (HttpStatusCode.OK, FakeNuGet.Nuspec("Demo.Pkg", "1.2.0", null));

        var exit = await Run.Command(Commands.Finalize, Host(), github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
    }

    [Fact]
    public async Task Discarding_a_live_version_is_refused()
    {
        var (github, nuget) = World();
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host(discard: "true");

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Equal("Demo 1.2.0 is on nuget.org; finalize it rather than discarding it.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task Discarding_a_version_that_is_not_live_deletes_the_draft()
    {
        var (github, nuget) = World();
        var host = Host(discard: "true");

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(0, exit);
        Assert.Empty(github.Releases);
        Assert.Empty(github.Tags);
        Assert.Equal("discarded", host.Outputs["outcome"]);
        Assert.Contains($"Deleted the draft for {Tag} (was at `{Sha}`).", host.Lines);
    }

    [Fact]
    public async Task Discarding_during_a_nuget_outage_deletes_nothing()
    {
        var (github, nuget) = World();
        nuget.Status("Demo.Pkg", "1.2.0", HttpStatusCode.ServiceUnavailable);

        var exit = await Run.Command(Commands.Finalize, Host(discard: "true"), github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Single(github.Releases);
    }

    [Fact]
    public async Task A_dependant_before_its_dependency_at_the_same_commit_is_refused()
    {
        var (github, nuget) = World();
        github.Seed(DependantTag, Sha);
        nuget.Public("Dependant.Pkg", "1.2.0", Sha);
        var host = Host(DependantTag);

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Equal($"Dependant depends on Demo, whose draft {Tag} isn't finalized yet; finalize it first.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_pending_draft_of_the_dependency_at_another_commit_does_not_block()
    {
        var github = new FakeGitHub();
        github.Manifest(Sha, Manifest);
        github.Seed("Demo/v1.3.0", Other);
        github.Seed(DependantTag, Sha);
        var nuget = new FakeNuGet();
        nuget.Public("Dependant.Pkg", "1.2.0", Sha);

        var exit = await Run.Command(Commands.Finalize, Host(DependantTag), github, nuget);

        Assert.Equal(0, exit);
        Assert.Equal(Sha, github.Tags[DependantTag]);
        Assert.True(github.Releases.Single(r => r.TagName == "Demo/v1.3.0").Draft);
    }

    [Fact]
    public async Task A_pending_draft_whose_package_merely_shares_the_dependency_prefix_does_not_block()
    {
        var (github, nuget) = World();
        github.Releases.Clear();
        github.Seed("DemoTools/v1.0.0", Sha);
        github.Seed(DependantTag, Sha);
        nuget.Public("Dependant.Pkg", "1.2.0", Sha);

        Assert.Equal(0, await Run.Command(Commands.Finalize, Host(DependantTag), github, nuget));
        Assert.Equal(Sha, github.Tags[DependantTag]);
    }

    [Fact]
    public async Task A_dependant_after_its_dependency_is_finalized_is_published()
    {
        var (github, nuget) = World();
        github.Seed(DependantTag, Sha);
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        nuget.Public("Dependant.Pkg", "1.2.0", Sha);

        Assert.Equal(0, await Run.Command(Commands.Finalize, Host(), github, nuget));
        Assert.Equal(0, await Run.Command(Commands.Finalize, Host(DependantTag), github, nuget));

        Assert.Equal(Sha, github.Tags[DependantTag]);
    }

    [Fact]
    public async Task A_tag_made_by_hand_is_refused_because_the_draft_would_not_create_it()
    {
        var (github, nuget) = World();
        github.Tags[Tag] = Other;
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Equal($"the tag {Tag} already exists; the draft would not create it at {Sha}.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_tag_lookup_that_fails_other_than_404_is_not_read_as_absent()
    {
        var (github, nuget) = World();
        github.Fault = (m, p) => m == HttpMethod.Get && p.Contains("/git/ref/tags/", StringComparison.Ordinal) ? HttpStatusCode.BadGateway : null;
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.StartsWith($"couldn't read the tag {Tag} (HTTP 502", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_settled_drafts_for_the_tag_are_refused()
    {
        var (github, nuget) = World();
        github.Seed(Tag, Sha);
        nuget.Public("Demo.Pkg", "1.2.0", Sha);
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.False(Changed(github));
        Assert.Empty(nuget.Requests);
        Assert.Equal($"expected one draft release for {Tag}, found 2.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task No_draft_for_the_tag_is_refused()
    {
        var github = new FakeGitHub();
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github);

        Assert.Equal(1, exit);
        Assert.Equal($"expected one draft release for {Tag}, found 0.", Assert.Single(host.Errors));
    }

    [Theory]
    [InlineData("Demo-1.2.0")]
    [InlineData("Demo/1.2.0")]
    [InlineData("Demo/v")]
    [InlineData("Demo/v1.2.0/x")]
    public async Task A_malformed_tag_is_refused_before_any_request(string tag)
    {
        var github = new FakeGitHub();
        var host = Host(tag);

        var exit = await Run.Command(Commands.Finalize, host, github);

        Assert.Equal(1, exit);
        Assert.Empty(github.Requests);
        Assert.Equal($"'{tag}' isn't <package>/v<version>.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_draft_that_targets_a_branch_rather_than_a_commit_is_refused()
    {
        var github = new FakeGitHub();
        github.Seed(Tag, "main");
        var host = Host();

        var exit = await Run.Command(Commands.Finalize, host, github);

        Assert.Equal(1, exit);
        Assert.Equal($"the draft for {Tag} targets 'main', not a commit.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_package_missing_from_the_manifest_at_the_draft_commit_is_refused()
    {
        var github = new FakeGitHub();
        github.Manifest(Sha, """{"packages":{"Demo":{"package_id":"Demo.Pkg"}}}""");
        github.Seed("Missing/v1.0.0", Sha);
        var host = Host("Missing/v1.0.0");

        var exit = await Run.Command(Commands.Finalize, host, github);

        Assert.Equal(1, exit);
        Assert.Equal($"Missing isn't in .github/release-packages.json at {Sha}.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_package_id_that_is_not_a_nuget_id_is_refused_before_nuget_is_asked()
    {
        var github = new FakeGitHub();
        github.Manifest(Sha, """{"packages":{"Demo":{"package_id":"../../evil"}}}""");
        github.Seed(Tag, Sha);
        var nuget = new FakeNuGet();

        var exit = await Run.Command(Commands.Finalize, Host(), github, nuget);

        Assert.Equal(1, exit);
        Assert.Empty(nuget.Requests);
    }

    [Fact]
    public async Task The_github_token_is_never_sent_to_nuget()
    {
        var (github, nuget) = World();
        nuget.Public("Demo.Pkg", "1.2.0", Sha);

        await Run.Command(Commands.Finalize, Host(), github, nuget);

        Assert.NotEmpty(nuget.Requests);
        Assert.All(nuget.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task A_version_is_looked_up_lowercased()
    {
        var github = new FakeGitHub();
        github.Manifest(Sha, Manifest);
        github.Seed("Demo/v1.2.0-Beta01", Sha, prerelease: true);
        var nuget = new FakeNuGet();
        nuget.Public("Demo.Pkg", "1.2.0-beta01", Sha);

        var exit = await Run.Command(Commands.Finalize, Host("Demo/v1.2.0-Beta01"), github, nuget);

        Assert.Equal(0, exit);
        Assert.Equal("/v3-flatcontainer/demo.pkg/1.2.0-beta01/demo.pkg.nuspec", Assert.Single(nuget.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Discard_must_be_true_or_false()
    {
        var (github, nuget) = World();
        var host = Host(discard: "yes");

        var exit = await Run.Command(Commands.Finalize, host, github, nuget);

        Assert.Equal(1, exit);
        Assert.Empty(github.Requests);
    }
}
