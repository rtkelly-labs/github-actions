using System.Text.RegularExpressions;

namespace EstateActions;

internal static partial class ReleaseNames
{
    /// <summary>The manifest, read through the contents API at the draft's commit.</summary>
    public const string ManifestPath = ".github/release-packages.json";

    /// <summary><c>&lt;package&gt;/v&lt;version&gt;</c>, e.g. <c>Resultful/v0.2.0</c>.</summary>
    [GeneratedRegex("^([A-Za-z0-9._-]+)/v([0-9A-Za-z.+-]+)\\z")]
    public static partial Regex Tag();

    [GeneratedRegex("^[0-9a-f]{40}\\z")]
    public static partial Regex CommitSha();

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z")]
    public static partial Regex PackageId();

    public static (string Key, string Version) ParseTag(string tag)
    {
        var match = Tag().Match(tag);
        if (!match.Success)
            throw new ActionFailure($"'{tag}' isn't <package>/v<version>.");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
