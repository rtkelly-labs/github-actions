using System.Text.Json.Serialization;

namespace EstateActions;

/// <summary>A GitHub release, as <c>GET /repos/{owner}/{repo}/releases</c> lists it (only the fields used).</summary>
internal sealed record Release(long Id, string? TagName, string? TargetCommitish, bool Draft, bool Prerelease)
{
    public string Describe() => $"{Id} {TargetCommitish} (draft: {(Draft ? "true" : "false")})";
}

internal sealed record CreateReleaseRequest(
    string TagName, string TargetCommitish, string Name, string Body, bool Draft, bool Prerelease);

/// <summary>Publishes a draft. <c>MakeLatest</c> is the string GitHub expects: "true", "false" or "legacy".</summary>
internal sealed record UpdateReleaseRequest(string TagName, bool Draft, bool Prerelease, string MakeLatest);

internal sealed record GitRef(string? Ref, GitObject? Object);

internal sealed record GitObject(string? Sha, string? Type);

internal sealed record ApiError(string? Message, List<ApiErrorDetail>? Errors);

internal sealed record ApiErrorDetail(string? Resource, string? Field, string? Code, string? Message);

/// <summary><c>.github/release-packages.json</c>, read as data at the draft's commit.</summary>
internal sealed record ReleaseManifest(Dictionary<string, ManifestPackage>? Packages);

internal sealed record ManifestPackage(string? PackageId, string[]? DependsOn);

/// <summary>One entry of the release plan (<c>plan_json</c>), as the consumer's planning step writes it.</summary>
internal sealed record PlanEntry(string? Key, string? PackageId, string? Version);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    AllowTrailingCommas = false)]
[JsonSerializable(typeof(List<Release>))]
[JsonSerializable(typeof(Release))]
[JsonSerializable(typeof(CreateReleaseRequest))]
[JsonSerializable(typeof(UpdateReleaseRequest))]
[JsonSerializable(typeof(GitRef))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(List<PlanEntry>))]
internal sealed partial class ActionsJson : JsonSerializerContext;
