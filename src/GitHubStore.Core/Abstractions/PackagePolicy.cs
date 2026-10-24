using System.Text.Json.Serialization;

namespace GitHubStore.Core.Abstractions;

/// <summary>Optional library package selection and future installation metadata.</summary>
/// <remarks>
/// 未知字段一律拒绝：库文件由人工维护，把 releaseChanel 之类的拼写错误当成默认值
/// 会静默地换掉用户以为已经配置好的版本策略。宁可整份库校验失败并保留原缓存。
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageAssetRule
{
    [JsonConverter(typeof(JsonStringEnumConverter<Arch>))]
    public Arch Architecture { get; init; }
    public IReadOnlyList<string> Include { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}
