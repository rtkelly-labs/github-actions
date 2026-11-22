using System.IO.Compression;
using System.Text.Json;

namespace EstateActions;

/// <summary>
/// <c>release-check-artifacts</c>: the artifacts directory holds exactly the planned packages (a <c>.nupkg</c> and a
/// <c>.snupkg</c> per plan entry, at <c>&lt;key&gt;/&lt;package_id&gt;.&lt;version&gt;</c>), and each nuspec records
/// <c>&lt;repository commit&gt;</c> equal to the run's commit, the value release-finalize later matches against the draft.
/// The whole tree is checked, not only the planned folders: a publish step pushes every package it is handed.
/// </summary>
internal sealed class ReleaseCheckArtifacts(IActionHost host)
{
    public int Run()
    {
        var root = host.Get("RELEASE_ARTIFACTS") is { Length: > 0 } r ? r : "artifacts";
        var sha = host.Require("RELEASE_SHA");
        if (!ReleaseNames.CommitSha().IsMatch(sha))
            throw new ActionFailure($"the commit '{sha}' isn't a 40-character lowercase sha.");

        var expected = ReadPlan(host.Require("RELEASE_PLAN"))
            .SelectMany(p => new[] { $"{p.Key}/{p.PackageId}.{p.Version}.nupkg", $"{p.Key}/{p.PackageId}.{p.Version}.snupkg" })
            .Order(StringComparer.Ordinal).ToArray();
        var found = Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray()
            : [];
        if (!found.SequenceEqual(expected, StringComparer.Ordinal))
            throw new ActionFailure($"{root}/ holds [{string.Join(", ", found)}], expected exactly [{string.Join(", ", expected)}].");

        foreach (var nupkg in found.Where(f => f.EndsWith(".nupkg", StringComparison.Ordinal)))
        {
            var commit = PackageCommit(Path.Combine(root, nupkg));
            if (commit != sha)
                throw new ActionFailure($"{nupkg} records repository commit '{commit}', not {sha}; release-finalize could not match it to its draft.");
        }
        host.Report($"{root}/ holds exactly the planned packages at their planned versions, built from {sha}.");
        return 0;
    }

    private static string? PackageCommit(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var nuspecs = zip.Entries
                .Where(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nuspecs.Count != 1)
                throw new ActionFailure($"{path} has {nuspecs.Count} nuspecs at its root, expected one.");
            using var stream = nuspecs[0].Open();
            return NuGetApi.RepositoryCommit(stream);
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            throw new ActionFailure($"{path} isn't a readable package: {e.Message}");
        }
    }

    private static List<PlanEntry> ReadPlan(string json)
    {
        List<PlanEntry>? plan;
        try
        {
            plan = JsonSerializer.Deserialize(json, ActionsJson.Default.ListPlanEntry);
        }
        catch (JsonException e)
        {
            throw new ActionFailure($"the release plan isn't valid JSON: {e.Message}");
        }
        if (plan is null || plan.Count == 0)
            throw new ActionFailure("the release plan is empty.");
        foreach (var p in plan)
        {
            if (string.IsNullOrEmpty(p.Key) || string.IsNullOrEmpty(p.PackageId) || string.IsNullOrEmpty(p.Version))
                throw new ActionFailure("each release plan entry needs key, package_id and version.");
        }
        return plan;
    }
}
