namespace GitHubStore.Core.Abstractions;

public readonly record struct SourceInstanceId(Guid Value);
public readonly record struct PackageId(string Value);

/// <summary>Global identity. Package identifiers belong to one source instance.</summary>
public readonly record struct AppId(SourceInstanceId SourceId, PackageId PackageId)
{
    public override string ToString() => $"{SourceId.Value:N}:{PackageId.Value}";
}

public enum SourceTrust { Official, Verified, Unsigned, Invalid }
public enum SourceState { Active, Disabled, Refreshing, Stale, Error, Removed }

public sealed record CatalogSource
{
    public required SourceInstanceId InstanceId { get; init; }
    public required Uri EntryUrl { get; init; }
    public required string DeclaredId { get; init; }
    public required string Name { get; init; }
    public SourceTrust Trust { get; init; }
    public SourceState State { get; init; }
    public long ActiveRevision { get; init; }
    public long HighestSeenRevision { get; init; }
    public string? SigningKeyId { get; init; }
    public string? SigningKeyFingerprint { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
}

public sealed record App
{
    public required AppId Id { get; init; }
    public required long SourceRevision { get; init; }
    public required string DisplayName { get; init; }
    public required string Owner { get; init; }
    public required string Repo { get; init; }
    public string? Description { get; init; }
    public string? Homepage { get; init; }
    public string License { get; init; } = "NOASSERTION";
    public string? Language { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = [];
    public int Stars { get; init; }
    public int Forks { get; init; }
    public int OpenIssues { get; init; }
    public bool Archived { get; init; }
    public bool IsTemplate { get; init; }
    public string DefaultBranch { get; init; } = "main";
    public DateTimeOffset PushedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<CategoryId> Categories { get; init; } = [];
    public CategoryId PrimaryCategory { get; init; }
    public bool WindowsReady { get; init; }
    public ExcludeReason Excluded { get; init; } = ExcludeReason.None;
    public DateTimeOffset FetchedAt { get; init; }
    public bool Stale { get; init; }
}

public enum ExcludeReason { None, NoWindowsAsset, Archived, NotAnApp, ManualBlacklist }
public readonly record struct CategoryId(string Value);

public sealed record Category
{
    public required CategoryId Id { get; init; }
    public required string Name { get; init; }
    public required string NameEn { get; init; }
    public string? Icon { get; init; }
    public CategoryId? ParentId { get; init; }
    public int Order { get; init; }
}

public sealed record Release
{
    public required string Key { get; init; }
    public required AppId AppId { get; init; }
    public required string TagName { get; init; }
    public string? Name { get; init; }
    public string? Body { get; init; }
    public bool Prerelease { get; init; }
    public bool Draft { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public IReadOnlyList<Asset> Assets { get; init; } = [];
}

public sealed record Asset
{
    public required string InstallerId { get; init; }
    public required string ReleaseKey { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public required string DownloadUrl { get; init; }
    public string? ApiUrl { get; init; }
    public string? Digest { get; init; }
    public int DownloadCount { get; init; }
    public AssetKind Kind { get; init; }
    public Arch Arch { get; init; }
    public InstallerKind Installer { get; init; }
}

public enum AssetKind
{
    Unknown, InstallerMsi, InstallerExe, ArchiveZip, Archive7z,
    PortableExe, Checksum, Signature, SourceArchive,
}
public enum Arch { Unknown, Amd64, Arm64, X86 }
public enum InstallerKind { Unknown, Msi, Nsis, Inno, Squirrel, Burn, Msix, Zip, Portable, Winget }
