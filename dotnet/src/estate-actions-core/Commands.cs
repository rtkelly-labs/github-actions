using System.Reflection;

namespace EstateActions;

/// <summary>Dispatches a subcommand, turning a failure into one <c>::error::</c> annotation and exit code 1.</summary>
public static class Commands
{
    public const string Draft = "release-draft";
    public const string Finalize = "release-finalize";
    public const string CheckArtifacts = "release-check-artifacts";

    public static Task<int> RunAsync(string[] args, CancellationToken ct) =>
        RunAsync(args, new RunnerHost(), null, null, (d, c) => Task.Delay(d, c), ct);

    internal static async Task<int> RunAsync(
        string[] args, IActionHost host, HttpMessageHandler? gitHubHandler, HttpMessageHandler? nugetHandler,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        if (args is ["--version"])
        {
            host.Info(Version);
            return 0;
        }
        if (args.Length != 1 || args[0] is not (Draft or Finalize or CheckArtifacts))
        {
            host.Error($"usage: estate-actions <{Draft}|{Finalize}|{CheckArtifacts}>; inputs come from the environment.");
            return 2;
        }
        try
        {
            if (args[0] == CheckArtifacts)
                return new ReleaseCheckArtifacts(host).Run();

            using var gitHubHttp = GitHubApi.CreateClient(
                host.Get("GITHUB_API_URL") is { Length: > 0 } api ? api : "https://api.github.com",
                host.Require("GITHUB_TOKEN"), UserAgent, gitHubHandler);
            var github = new GitHubApi(gitHubHttp, host.Get("RELEASE_REPOSITORY") is { Length: > 0 } repo ? repo : host.Require("GITHUB_REPOSITORY"), delay);
            if (args[0] == Draft)
                return await new ReleaseDraft(github, host, delay).RunAsync(ct).ConfigureAwait(false);

            using var nugetHttp = NuGetApi.CreateClient(UserAgent, nugetHandler);
            return await new ReleaseFinalize(github, new NuGetApi(nugetHttp), host, delay).RunAsync(ct).ConfigureAwait(false);
        }
        catch (ActionFailure e)
        {
            host.Error(e.Message);
        }
        catch (GitHubApiException e)
        {
            host.Error(e.Message);
        }
        catch (HttpRequestException e)
        {
            host.Error($"a request failed: {e.Message}");
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            host.Error($"a request timed out: {e.Message}");
        }
        return 1;
    }

    private static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private static string UserAgent => $"estate-actions/{Version.Split('+')[0]}";
}
