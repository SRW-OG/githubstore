using System.Text.Json.Serialization;

namespace GitHubStore.Core.Abstractions;

/// <summary>Optional library package selection and future installation metadata.</summary>
public sealed record PackagePolicy
{
    public PackageReleaseChannel ReleaseChannel { get; init; }
    public string? PinnedTag { get; init; }
    public IReadOnlyList<PackageAssetRule> AssetRules { get; init; } = [];
    public PackageInstallFormat InstallFormat { get; init; }
    public string? ExecutableRelativePath { get; init; }
    public IReadOnlyList<string> PreservePaths { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageReleaseChannel>))]
public enum PackageReleaseChannel { Stable, LatestPrerelease, Pinned }
[JsonConverter(typeof(JsonStringEnumConverter<PackageInstallFormat>))]
public enum PackageInstallFormat { Auto, PortableArchive, PortableExecutable, Installer }

public sealed record PackageAssetRule
{
    [JsonConverter(typeof(JsonStringEnumConverter<Arch>))]
    public Arch Architecture { get; init; }
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}
