namespace GitHubStore.Core.Abstractions;

public enum ManagedToolKind { Aria2, Winget, Xget }

/// <summary>A verified application tool, a system tool, or a built-in network provider.</summary>
public sealed record ToolResolution
{
    public ManagedToolKind Kind { get; init; }
    public bool IsAvailable { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Version { get; init; }
    public Uri? Endpoint { get; init; }
    public string? ErrorCode { get; init; }
    public string? Origin { get; init; }
}

public interface IToolManager
{
    string ToolDirectory { get; }
    Task<ToolResolution> EnsureAsync(ManagedToolKind kind, CancellationToken ct = default);
}

/// <summary>Stable errors; failures never expose an executable as ready.</summary>
public static class ToolErrorCodes
{
    public const string PackageMissing = "tool_package_missing";
    public const string InvalidManifest = "tool_invalid_manifest";
    public const string UnsupportedArchitecture = "tool_unsupported_architecture";
    public const string ArchiveChecksumMismatch = "tool_archive_checksum_mismatch";
    public const string ExecutableChecksumMismatch = "tool_executable_checksum_mismatch";
    public const string InvalidArchive = "tool_invalid_archive";
    public const string PackageTooLarge = "tool_package_too_large";
    public const string UnsafePath = "tool_unsafe_path";
    public const string AccessDenied = "tool_access_denied";
    public const string Busy = "tool_busy";
    public const string IoError = "tool_io_error";
    public const string Cancelled = "tool_cancelled";
    public const string SystemToolMissing = "tool_system_missing";
    public const string UnsupportedTool = "tool_unsupported";
}
