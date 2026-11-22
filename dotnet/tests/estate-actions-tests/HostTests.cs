namespace EstateActions.Tests;

public sealed class HostTests
{
    [Fact]
    public void Annotation_data_is_escaped_so_untrusted_text_cannot_end_it()
    {
        Assert.Equal("a%25b%0D%0A::warning::c", RunnerHost.EscapeData("a%b\r\n::warning::c"));
    }

    [Fact]
    public async Task An_unknown_command_prints_usage_and_exits_2()
    {
        var host = new TestHost();

        var exit = await Commands.RunAsync(["release-everything"], host, null, null, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.StartsWith("usage: estate-actions", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_fails_with_a_clear_error()
    {
        var host = new TestHost();
        host.Inputs.Remove("GITHUB_TOKEN");
        host.Inputs["RELEASE_TAG"] = "Demo/v1.0.0";

        var exit = await Run.Command(Commands.Finalize, host, new FakeGitHub());

        Assert.Equal(1, exit);
        Assert.Equal("GITHUB_TOKEN isn't set.", Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_next_page_on_another_host_is_not_followed_with_the_token()
    {
        var handler = new RedirectingPages();
        var host = new TestHost();
        host.Inputs["RELEASE_TAG"] = "Demo/v1.0.0";

        var exit = await Commands.RunAsync([Commands.Finalize], host, handler, new FakeNuGet(), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("on another host (evil.test)", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_next_page_link_that_loops_is_refused()
    {
        var handler = new RedirectingPages("https://api.github.test/repos/octo-org/octo-repo/releases?per_page=100");
        var host = new TestHost();
        host.Inputs["RELEASE_TAG"] = "Demo/v1.0.0";

        var exit = await Commands.RunAsync([Commands.Finalize], host, handler, new FakeNuGet(), (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("didn't end", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    private sealed class RedirectingPages(string next = "https://evil.test/releases?page=2") : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
            return Task.FromResult(response);
        }
    }
}
