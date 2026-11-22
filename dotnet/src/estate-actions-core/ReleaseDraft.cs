namespace EstateActions;

/// <summary>
/// <c>release-draft</c>: one draft GitHub Release for a tag, at a commit, before anything is staged. A draft creates no
/// tag. Any existing release for the tag (draft or published) fails the step: a run already got that far and may have
/// staged the version, and GitHub would accept a second draft for the same tag without complaint.
/// </summary>
internal sealed class ReleaseDraft(GitHubApi github, IActionHost host, Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>The release list shows a new draft only seconds after it is made: wait up to 12 x 5s for our own.</summary>
    public const int ListAttempts = 12;

    public static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(5);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var tag = host.Require("RELEASE_TAG");
        ReleaseNames.ParseTag(tag);
        var sha = host.Require("RELEASE_SHA");
        if (!ReleaseNames.CommitSha().IsMatch(sha))
            throw new ActionFailure($"the commit '{sha}' isn't a 40-character lowercase sha.");
        var title = host.Require("RELEASE_TITLE");
        var notes = host.Get("RELEASE_NOTES") ?? "";
        var prerelease = host.RequireBool("RELEASE_PRERELEASE");

        var existing = await ReleasesFor(tag, ct).ConfigureAwait(false);
        if (existing.Count > 0)
            throw new ActionFailure($"{tag} already has a release: {Describe(existing)}. Finalize or delete it (release-finalize) before releasing again.");

        var created = await github.CreateReleaseAsync(
            new CreateReleaseRequest(tag, sha, title, notes, Draft: true, prerelease), ct).ConfigureAwait(false);
        if (created.TagName != tag)
            throw new ActionFailure($"GitHub created the draft as {created.TagName}, not {tag}.");
        host.Output("release-id", created.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The check above can miss a draft made at the same time. Wait until this draft is listed; then it must be the
        // tag's only release, or this run takes its own draft back. Two racing runs that both see both both back out.
        List<Release> listed = [];
        for (var attempt = 0; attempt < ListAttempts; attempt++)
        {
            listed = await ReleasesFor(tag, ct).ConfigureAwait(false);
            if (listed.Exists(r => r.Id == created.Id))
                break;
            await delay(ListInterval, ct).ConfigureAwait(false);
        }
        if (!listed.Exists(r => r.Id == created.Id))
            throw new ActionFailure($"the draft {created.Id} for {tag} never appeared in the release list.");
        if (listed.Count != 1)
        {
            await github.DeleteReleaseAsync(created.Id, ct).ConfigureAwait(false);
            throw new ActionFailure($"{tag} has more than one release ({Describe(listed)}); deleted this run's draft {created.Id}.");
        }
        host.Report($"- Drafted `{tag}` at `{sha}`.");
        return 0;
    }

    private async Task<List<Release>> ReleasesFor(string tag, CancellationToken ct) =>
        (await github.ListReleasesAsync(ct).ConfigureAwait(false)).FindAll(r => r.TagName == tag);

    private static string Describe(List<Release> releases) => string.Join(";", releases.Select(r => r.Describe()));
}
