namespace GitHubStore.Core.Abstractions;

/// <summary>winget 包条目；来源是 winget 仓库而非 GitHub Release。</summary>
public sealed record WingetPackage
{
    /// <summary>包标识，如 Microsoft.PowerToys（大小写不敏感比较）。</summary>
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? License { get; init; }
    public string? Homepage { get; init; }
    public string? PublisherUrl { get; init; }
    public string? LicenseUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    /// <summary>是否为“常用软件”精选条目。</summary>
    public bool IsFeatured { get; init; }
    /// <summary>静态内置条目；不是实时 winget 查询结果，也未验证当前版本或可用性。</summary>
    public bool IsStatic { get; init; }
}

/// <summary>
/// winget 来源。实现通过 winget CLI 子进程读取包列表；
/// winget 不存在时必须优雅降级，不抛异常给界面。
/// </summary>
public interface IWingetService
{
    /// <summary>winget CLI 是否可用。</summary>
    Task<bool> ProbeAsync(CancellationToken ct = default);

    /// <summary>最近一次探测的失败原因；null 表示成功或尚未探测。</summary>
    string? LastProbeError { get; }

    /// <summary>最近一次探测的错误类别和进程退出码。</summary>
    string? LastProbeErrorCode => null;
    int? LastProbeExitCode => null;

    /// <summary>最近一次搜索的失败细节；调用方取消仍以异常传播。</summary>
    string? LastSearchError => null;

    /// <summary>列出常用软件精选条目（内置清单，不依赖联网）。</summary>
    IReadOnlyList<WingetPackage> ListFeatured();

    /// <summary>按关键字搜索 winget 仓库；失败返回错误结果，调用方取消抛出 OperationCanceledException。</summary>
    Task<WingetSearchResult> SearchAsync(string query, int limit = 30, CancellationToken ct = default);

    /// <summary>Install using winget's native installer; this does not create a managed Store receipt.</summary>
    Task<WingetInstallResult> InstallAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new WingetInstallResult { ErrorCode = WingetErrorCodes.CommandFailed });
    }

    /// <summary>Read live metadata for an exact package ID; missing fields remain null.</summary>
    Task<WingetDetailsResult> GetDetailsAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new WingetDetailsResult { ErrorCode = WingetErrorCodes.InvalidOutput });
    }
}

public sealed record WingetInstallResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public int? ExitCode { get; init; }
    public string? Output { get; init; }
    public bool RebootRequired { get; init; }
}

public sealed record WingetDetailsResult
{
    public WingetPackage? Package { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record WingetSearchResult
{
    public IReadOnlyList<WingetPackage> Items { get; init; } = [];
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public int? ExitCode { get; init; }
    public string? Diagnostic { get; init; }
}

public static class WingetErrorCodes
{
    public const string Busy = "winget_busy";
    public const string InvalidPackageId = "winget_invalid_package_id";
    public const string NotInstalled = "winget_not_installed";
    public const string CommandFailed = "winget_command_failed";
    public const string TimedOut = "winget_timed_out";
    public const string InvalidOutput = "winget_invalid_output";
}
