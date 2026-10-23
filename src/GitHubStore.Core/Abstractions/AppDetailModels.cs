namespace GitHubStore.Core.Abstractions;

// Associated records preserve the AppDetail contract from docs/01.
// The initial catalog skeleton does not implement probing or installation.
public sealed record Capability
{
    public required AppId AppId { get; init; }
    public required string ReleaseKey { get; init; }
    public required string InstallerId { get; init; }
    public Channel Channel { get; init; }
    public InstallerKind Installer { get; init; }
    public bool SupportsDefault { get; init; }
    public bool SupportsCustom { get; init; }
    public bool SupportsGreen { get; init; }
    public ReasonCode? CustomPathReason { get; init; }
    public bool RequiresElevation { get; init; }
    public Scope Scope { get; init; }
    public IReadOnlyList<string> SilentArgs { get; init; } = [];
    public string? PathArgTemplate { get; init; }
    public long EstSize { get; init; }
    public DateTimeOffset ProbedAt { get; init; }
    public Confidence Confidence { get; init; }
}

public enum Channel { Http, Winget }
public enum Scope { User, Machine, Both }
public enum Confidence { Low, High }
public enum Strategy { Default, Custom, Green }

public sealed record SourceBinding
{
    public required SourceInstanceId SourceId { get; init; }
    public required Uri SourceEntryUrl { get; init; }
    public required long SourceRevision { get; init; }
    public required PackageId PackageId { get; init; }
    public required string InstallerId { get; init; }
    public Uri? ArtifactUrl { get; init; }
    public string? ArtifactSha256 { get; init; }
    public string? WingetPackageId { get; init; }
    public string? WingetVersion { get; init; }
}

public enum Origin { Store, External }

public sealed record InstallRecord
{
    public required string InstallId { get; init; }
    public required AppId AppId { get; init; }
    public required SourceBinding Source { get; init; }
    public required string ReleaseKey { get; init; }
    public Origin Origin { get; init; }
    public Channel Channel { get; init; }
    public Strategy Strategy { get; init; }
    public InstallerKind Installer { get; init; }
    public required Version Version { get; init; }
    public string? DisplayVersion { get; init; }
    public string? TargetDir { get; init; }
    public string? ExePath { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? RegistryKey { get; init; }
    public string? ProductCode { get; init; }
    public string? MsixPackageFamilyName { get; init; }
    public string? WingetId { get; init; }
    public Fingerprint Fingerprint { get; init; } = new();
    public TrustLevel TrustLevel { get; init; }
    public IReadOnlyList<string> Shortcuts { get; init; } = [];
    public DateTimeOffset InstalledAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public InstallState State { get; init; }
}

public sealed record Fingerprint
{
    public string? AssetSha256 { get; init; }
    public string? Publisher { get; init; }
    public string? ProductName { get; init; }
    public AppId? AppId { get; init; }
    public string? Marker { get; init; }
}

public enum InstallState
{
    Queued, Resolving, Downloading, Verifying, AwaitingElevation,
    Installing, Registering, Done, Failed, Cancelled, RolledBack, Partial,
}
public enum TrustLevel { Verified, Whitelisted, Unsigned, Unverified }

public sealed record Version
{
    public required string Raw { get; init; }
    public VersionScheme Scheme { get; init; }
    public IReadOnlyList<long> Parts { get; init; } = [];
    public string? Pre { get; init; }
}
public enum VersionScheme { Semver, Quad, Date, Opaque }
public readonly record struct VersionCompare(int Result, bool Comparable);

public sealed record ReadmeSummary
{
    public required string Key { get; init; }
    public required AppId AppId { get; init; }
    public required string ReleaseTag { get; init; }
    public required string ReadmeSha { get; init; }
    public required string PromptVer { get; init; }
    public required string Model { get; init; }
    public string? Provider { get; init; }
    public required string OneLiner { get; init; }
    public string? WhatItDoes { get; init; }
    public string? WhoFor { get; init; }
    public IReadOnlyList<string> KeyFeatures { get; init; } = [];
    public string? InstallHint { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public IReadOnlyList<string> Caveats { get; init; } = [];
    public CategoryId? CategorySuggestion { get; init; }
    public string? SafetyNote { get; init; }
    public SummaryConfidence Confidence { get; init; }
    public bool Degraded { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}
public enum SummaryConfidence { Low, Medium, High }

public enum ReasonCode
{
    InstallerIgnoresLocation, ManifestNoSwitches, PortableAlready, ScopeUserOnly,
    NeedsElevation, PathNotWritable, PathOnSystemDriveWarn, PathNetworkUnsupported,
    PathTooLong, PathInUse, DiskInsufficient, SignatureInvalid, WingetUnavailable,
    NoWindowsAsset,
}

public static class ReasonCodeExtensions
{
    public static string ToWire(this ReasonCode r) => r switch
    {
        ReasonCode.InstallerIgnoresLocation => "installer_ignores_location",
        ReasonCode.ManifestNoSwitches => "manifest_no_switches",
        ReasonCode.PortableAlready => "portable_already",
        ReasonCode.ScopeUserOnly => "scope_user_only",
        ReasonCode.NeedsElevation => "needs_elevation",
        ReasonCode.PathNotWritable => "path_not_writable",
        ReasonCode.PathOnSystemDriveWarn => "path_on_system_drive_warn",
        ReasonCode.PathNetworkUnsupported => "path_network_unsupported",
        ReasonCode.PathTooLong => "path_too_long",
        ReasonCode.PathInUse => "path_in_use",
        ReasonCode.DiskInsufficient => "disk_insufficient",
        ReasonCode.SignatureInvalid => "signature_invalid",
        ReasonCode.WingetUnavailable => "winget_unavailable",
        ReasonCode.NoWindowsAsset => "no_windows_asset",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };
}
