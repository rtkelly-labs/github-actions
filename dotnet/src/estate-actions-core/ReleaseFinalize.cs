using System.Text.Json;

namespace EstateActions;

/// <summary>
/// <c>release-finalize</c>: once a version is public on nuget.org, publish its draft so GitHub creates the tag at the
/// draft's commit. A version that isn't public yet changes nothing, so it is safe to run again and again. With
/// <c>RELEASE_DISCARD</c>, delete a draft whose version is not on nuget.org (it expired there, or was rejected).
/// </summary>
internal sealed class ReleaseFinalize(GitHubApi github, NuGetApi nuget, IActionHost host, Func<TimeSpan, CancellationToken, Task> delay)
{
    public const int TagReadAttempts = 6;

    public static readonly TimeSpan TagReadInterval = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var tag = host.Require("RELEASE_TAG");
        var discard = host.RequireBool("RELEASE_DISCARD", fallback: false);
        var (key, version) = ReleaseNames.ParseTag(tag);

        var all = await github.ListReleasesAsync(ct).ConfigureAwait(false);
        var releases = all.FindAll(r => r.TagName == tag);
        if (releases.Exists(r => !r.Draft))
        {
            host.Output("outcome", "already-published");
            host.Report($"{tag} is already published; nothing to do.");
            return 0;
        }
        if (releases.Count != 1)
            throw new ActionFailure($"expected one draft release for {tag}, found {releases.Count}.");
        var draft = releases[0];
        var sha = draft.TargetCommitish ?? "";
        if (!ReleaseNames.CommitSha().IsMatch(sha))
            throw new ActionFailure($"the draft for {tag} targets '{sha}', not a commit.");

        // The manifest at the draft's commit gives the package id and its dependencies (read as data, never run).
        var package = await ManifestEntry(key, sha, ct).ConfigureAwait(false);
        var packageId = package.PackageId!;
        var nuspec = await nuget.GetNuspecAsync(packageId, version, ct).ConfigureAwait(false);

        if (discard)
        {
            if (nuspec is not null)
                throw new ActionFailure($"{key} {version} is on nuget.org; finalize it rather than discarding it.");
            await github.DeleteReleaseAsync(draft.Id, ct).ConfigureAwait(false);
            host.Output("outcome", "discarded");
            host.Report($"Deleted the draft for {tag} (was at `{sha}`).");
            return 0;
        }
        if (nuspec is null)
        {
            host.Output("outcome", "not-public");
            host.Report($"{key} {version} isn't on nuget.org yet (promote it there first); nothing changed.");
            return 0;
        }

        // The package records the commit it was built from; it must be the draft's.
        string? built;
        try
        {
            built = NuGetApi.RepositoryCommit(nuspec);
        }
        catch (System.Xml.XmlException e)
        {
            throw new ActionFailure($"{key} {version}'s nuspec on nuget.org isn't readable XML: {e.Message}");
        }
        if (built != sha)
            throw new ActionFailure($"{key} {version} on nuget.org was built from '{built}', but the draft is at {sha}.");

        // target_commitish is ignored when the tag already exists, so a tag made by hand would be adopted silently.
        var existingTag = await github.GetTagAsync(tag, ct).ConfigureAwait(false);
        if (existingTag.Exists)
            throw new ActionFailure($"the tag {tag} already exists; the draft would not create it at {sha}.");

        // A package this one depends on, drafted at the same commit, must be finalized first.
        foreach (var dep in package.DependsOn ?? [])
        {
            var prefix = dep + "/v";
            var pending = all.FindAll(r => r.Draft && r.TargetCommitish == sha && r.TagName is { } t && t.StartsWith(prefix, StringComparison.Ordinal));
            if (pending.Count > 0)
                throw new ActionFailure($"{key} depends on {dep}, whose draft {string.Join(",", pending.Select(r => r.TagName))} isn't finalized yet; finalize it first.");
        }

        var latest = draft.Prerelease ? "false" : "true";
        // Name the tag again: an edit that leaves tag_name out can turn a draft into "untagged-<hash>".
        await github.UpdateReleaseAsync(draft.Id, new UpdateReleaseRequest(tag, Draft: false, draft.Prerelease, latest), ct).ConfigureAwait(false);
        // The new tag can take a moment to be readable; absent is retried, a wrong commit is not.
        var tagged = await github.GetTagAsync(tag, ct).ConfigureAwait(false);
        for (var attempt = 1; !tagged.Exists && attempt < TagReadAttempts; attempt++)
        {
            await delay(TagReadInterval, ct).ConfigureAwait(false);
            tagged = await github.GetTagAsync(tag, ct).ConfigureAwait(false);
        }
        if (tagged.Sha != sha)
            throw new ActionFailure($"published, but {tag} points at {(tagged.Exists ? tagged.Sha : "nothing")}, not {sha}.");
        host.Output("outcome", "published");
        host.Report($"Published {tag}: tagged at `{sha}`.");
        return 0;
    }

    private async Task<ManifestPackage> ManifestEntry(string key, string sha, CancellationToken ct)
    {
        var bytes = await github.GetFileAsync(ReleaseNames.ManifestPath, sha, ct).ConfigureAwait(false);
        ReleaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(bytes, ActionsJson.Default.ReleaseManifest);
        }
        catch (JsonException e)
        {
            throw new ActionFailure($"{ReleaseNames.ManifestPath} at {sha} isn't valid JSON: {e.Message}");
        }
        if (manifest?.Packages is null || !manifest.Packages.TryGetValue(key, out var package) || string.IsNullOrEmpty(package.PackageId))
            throw new ActionFailure($"{key} isn't in {ReleaseNames.ManifestPath} at {sha}.");
        if (!ReleaseNames.PackageId().IsMatch(package.PackageId))
            throw new ActionFailure($"{key}'s package_id '{package.PackageId}' in {ReleaseNames.ManifestPath} isn't a NuGet package id.");
        return package;
    }
}
