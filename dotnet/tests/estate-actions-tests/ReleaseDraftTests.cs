namespace EstateActions.Tests;

public sealed class ReleaseDraftTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Tag = "Demo/v1.2.0";

    private static TestHost Host(string tag = Tag, string sha = Sha, string prerelease = "false")
    {
        var host = new TestHost();
        host.Inputs["RELEASE_TAG"] = tag;
        host.Inputs["RELEASE_SHA"] = sha;
        host.Inputs["RELEASE_TITLE"] = "Demo.Pkg 1.2.0";
        host.Inputs["RELEASE_NOTES"] = "Changes since 1.1.0:\n\n- one";
        host.Inputs["RELEASE_PRERELEASE"] = prerelease;
        return host;
    }

    [Fact]
    public async Task A_new_draft_is_created_at_the_commit_and_creates_no_tag()
    {
        var github = new FakeGitHub { ListLag = 2 };
        var host = Host(prerelease: "true");
        var delays = new List<TimeSpan>();

        var exit = await Run.Command(Commands.Draft, host, github, delays: delays);

        Assert.Equal(0, exit);
        Assert.Empty(host.Errors);
        var draft = Assert.Single(github.Releases);
        Assert.True(draft.Draft);
        Assert.True(draft.Prerelease);
        Assert.Equal(Sha, draft.Target);
        Assert.Equal("Demo.Pkg 1.2.0", draft.Name);
        Assert.Equal("Changes since 1.1.0:\n\n- one", draft.Body);
        Assert.Empty(github.Tags);
        Assert.Equal(draft.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), host.Outputs["release-id"]);
        Assert.Contains($"- Drafted `{Tag}` at `{Sha}`.", host.SummaryText.ToString(), StringComparison.Ordinal);
        // It waited for the list to catch up (two lagging list calls), 5s apart.
        Assert.Equal([ReleaseDraft.ListInterval, ReleaseDraft.ListInterval], delays);
        Assert.All(github.Requests, r => Assert.Equal("Bearer test-token", r.Authorization));
    }

    [Fact]
    public async Task An_existing_draft_for_the_tag_stops_before_anything_is_created()
    {
        var github = new FakeGitHub();
        var other = github.Seed(Tag, "2222222222222222222222222222222222222222");
        var host = Host();

        var exit = await Run.Command(Commands.Draft, host, github);

        Assert.Equal(1, exit);
        Assert.Equal([other], github.Releases);
        Assert.DoesNotContain(github.Requests, r => r.Method == HttpMethod.Post);
        var error = Assert.Single(host.Errors);
        Assert.StartsWith($"{Tag} already has a release: {other.Id} 2222222222222222222222222222222222222222 (draft: true)", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_published_release_for_the_tag_also_stops_it()
    {
        var github = new FakeGitHub();
        github.Seed(Tag, Sha, draft: false);

        var exit = await Run.Command(Commands.Draft, Host(), github);

        Assert.Equal(1, exit);
        Assert.Single(github.Releases);
    }

    [Fact]
    public async Task A_duplicate_only_listed_after_creating_makes_this_run_delete_its_own_draft()
    {
        var github = new FakeGitHub { ListLag = 1 };
        // Made by another run just before this one, and not yet in the list when this run checks.
        var other = github.Seed(Tag, Sha, hiddenForListCalls: 1);
        var host = Host();

        var exit = await Run.Command(Commands.Draft, host, github);

        Assert.Equal(1, exit);
        Assert.Equal([other], github.Releases);
        var delete = Assert.Single(github.Requests, r => r.Method == HttpMethod.Delete);
        Assert.NotEqual($"/repos/{FakeGitHub.Repo}/releases/{other.Id}", delete.Uri.AbsolutePath);
        Assert.Contains("has more than one release", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_racing_runs_that_each_see_both_drafts_both_back_out()
    {
        // Neither draft is listed until both exist, and a deleted draft lingers in the list, so
        // whichever run looks first, each sees its own draft next to the other.
        var github = new FakeGitHub { HoldCreatedUntil = 2, DeletedStayListed = true };

        var exits = await Task.WhenAll(
            Run.Command(Commands.Draft, Host(), github),
            Run.Command(Commands.Draft, Host(), github));

        Assert.Equal([1, 1], exits);
        Assert.Empty(github.Releases);
        Assert.Equal(2, github.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.Equal(2, github.Requests.Count(r => r.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task Racing_runs_never_leave_two_drafts_whatever_the_interleaving()
    {
        for (var lag = 0; lag <= 4; lag++)
        {
            var github = new FakeGitHub { ListLag = lag };
            var hosts = new[] { Host(), Host(), Host() };

            var exits = await Task.WhenAll(hosts.Select(h => Run.Command(Commands.Draft, h, github)));

            Assert.True(github.Releases.Count <= 1, $"lag {lag}: {github.Releases.Count} drafts survived");
            Assert.Equal(github.Releases.Count, exits.Count(e => e == 0));
        }
    }

    [Fact]
    public async Task A_draft_that_never_appears_in_the_list_fails_without_deleting_it()
    {
        var github = new FakeGitHub { ListLag = 1000 };
        var host = Host();
        var delays = new List<TimeSpan>();

        var exit = await Run.Command(Commands.Draft, host, github, delays: delays);

        Assert.Equal(1, exit);
        Assert.Single(github.Releases);
        Assert.DoesNotContain(github.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal(ReleaseDraft.ListAttempts, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), TimeSpan.FromTicks(delays.Sum(d => d.Ticks)));
        Assert.Contains("never appeared in the release list", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releases_on_a_later_page_are_seen()
    {
        var github = new FakeGitHub { PageSize = 2 };
        var other = github.Seed(Tag, Sha);
        for (var i = 0; i < 5; i++)
            github.Seed($"Other/v1.0.{i}", Sha);

        var exit = await Run.Command(Commands.Draft, Host(), github);

        Assert.Equal(1, exit);
        Assert.Contains(github.Requests, r => r.Uri.Query.Contains("page=3", StringComparison.Ordinal));
        Assert.DoesNotContain(github.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains(other, github.Releases);
    }

    [Theory]
    [InlineData("Demo-1.2.0")]
    [InlineData("Demo/1.2.0")]
    [InlineData("Demo/v1.2.0\n::warning::x")]
    [InlineData("../Demo/v1.2.0")]
    [InlineData("Demo/v1.2.0\n")]
    public async Task A_malformed_tag_is_refused_before_any_request(string tag)
    {
        var github = new FakeGitHub();
        var host = Host(tag: tag);

        var exit = await Run.Command(Commands.Draft, host, github);

        Assert.Equal(1, exit);
        Assert.Empty(github.Requests);
        Assert.EndsWith("isn't <package>/v<version>.", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("ABCDEF1111111111111111111111111111111111")]
    [InlineData("11111111")]
    public async Task The_target_must_be_a_full_commit_sha(string sha)
    {
        var github = new FakeGitHub();

        var exit = await Run.Command(Commands.Draft, Host(sha: sha), github);

        Assert.Equal(1, exit);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task An_api_failure_fails_the_step_without_creating_anything()
    {
        var github = new FakeGitHub { Fault = (m, _) => m == HttpMethod.Get ? System.Net.HttpStatusCode.ServiceUnavailable : null };
        var host = Host();

        var exit = await Run.Command(Commands.Draft, host, github);

        Assert.Equal(1, exit);
        Assert.Empty(github.Releases);
        Assert.Equal("couldn't list releases (HTTP 503: simulated failure)", Assert.Single(host.Errors));
        Assert.Equal(GitHubApi.ReadAttempts, github.Requests.Count);
    }

    [Fact]
    public async Task A_transient_failure_on_a_read_is_retried()
    {
        var failures = 1;
        var github = new FakeGitHub { Fault = (m, _) => m == HttpMethod.Get && failures-- > 0 ? System.Net.HttpStatusCode.BadGateway : null };
        var host = Host();
        var delays = new List<TimeSpan>();

        var exit = await Run.Command(Commands.Draft, host, github, delays: delays);

        Assert.Equal(0, exit);
        Assert.Single(github.Releases);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
    }

    [Fact]
    public async Task A_failed_create_is_never_retried()
    {
        var github = new FakeGitHub { Fault = (m, _) => m == HttpMethod.Post ? System.Net.HttpStatusCode.BadGateway : null };
        var host = Host();

        var exit = await Run.Command(Commands.Draft, host, github);

        Assert.Equal(1, exit);
        Assert.Single(github.Requests, r => r.Method == HttpMethod.Post);
        Assert.StartsWith("couldn't create the release (HTTP 502", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_create_explains_why()
    {
        var github = new FakeGitHub
        {
            Fault = (m, _) => m == HttpMethod.Post ? System.Net.HttpStatusCode.UnprocessableEntity : null,
            FaultBody = """{"message":"Validation Failed","errors":[{"resource":"Release","code":"already_exists","field":"tag_name"}]}""",
        };
        var host = Host();

        Assert.Equal(1, await Run.Command(Commands.Draft, host, github));
        Assert.Equal("couldn't create the release (HTTP 422: Validation Failed [Release tag_name already_exists])", Assert.Single(host.Errors));
    }
}
