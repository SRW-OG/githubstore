namespace GitHubStore.Core.Abstractions;

public sealed record AppInstallRequest
{
    public required App App { get; init; }
    public required Release Release { get; init; }
    public required Asset Asset { get; init; }
    public PackagePolicy? PackagePolicy { get; init; }
}

public enum AppInstallPhase { Preparing, Downloading, Extracting, RunningInstaller, Verifying, Registering, Completed, UsingCache }
public sealed record AppInstallProgress(AppInstallPhase Phase, double? Percent = null);
public sealed record AppInstallResult(bool Succeeded, string? ErrorCode = null, string? TargetDirectory = null,
    string? ExecutablePath = null, bool RequiresRestart = false, bool FilesMayRemain = false,
    InstallDiagnostic? Diagnostic = null);

/// <summary>Bounded installation facts for diagnostics; excludes credentials, command lines and local paths.</summary>
public sealed record InstallDiagnostic
{
    public required string OperationId { get; init; }
    public required string AppName { get; init; }
    public required string Version { get; init; }
    public required string AssetName { get; init; }
    public AppInstallPhase Phase { get; init; }
    public required string ErrorCode { get; init; }
    public string? DownloadErrorCode { get; init; }
    public DownloadEngine? DownloadEngine { get; init; }
    public int? InstallerExitCode { get; init; }
    public bool ExternalInstallerStarted { get; init; }
    public bool ExternalInstallerFinished { get; init; }
    public bool FilesMayRemain { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FailedAt { get; init; }
}

/// <summary>Only registers a Store receipt after verifying a real executable in the installation target.</summary>
public interface IAppInstallService
{
    string GetTargetDirectory(App app);
    Task<AppInstallResult> InstallAsync(AppInstallRequest request,
        IProgress<AppInstallProgress>? progress = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null,
        CancellationToken ct = default);
}
