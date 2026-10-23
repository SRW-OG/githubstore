using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Tools;

/// <summary>Trusted metadata shipped with the app. Hashes are SHA-256 hexadecimal strings.</summary>
public sealed record BundledToolPackage(
    string Version,
    string Architecture,
    string ArchiveResourceName,
    string ArchiveSha256,
    string ExecutableEntry,
    string ExecutableSha256);

/// <summary>Offline package access; implementations must not download or resolve user executables.</summary>
public interface IToolPackageSource
{
    Task<BundledToolPackage?> GetPackageAsync(ManagedToolKind kind, string architecture, CancellationToken ct = default);
    Task<Stream> OpenArchiveAsync(BundledToolPackage package, CancellationToken ct = default);
}

public sealed record ManagedToolLimits
{
    public long MaxArchiveBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxExecutableBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A stable package failure shared by the embedded adapter and the manager.</summary>
public sealed class ToolPackageException : Exception
{
    public ToolPackageException(string errorCode) : base(errorCode) => ErrorCode = errorCode;
    public string ErrorCode { get; }
}
