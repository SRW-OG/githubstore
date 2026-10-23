namespace GitHubStore.Core.Abstractions;

/// <summary>下载引擎。HTTP 走内置实现，Aria2 由应用管理器准备已校验的工具组件。</summary>
public enum DownloadEngine { Http, Aria2 }

/// <summary>代理类型；None 使用系统代理设置，Http/Socks5 使用显式代理。</summary>
public enum ProxyKind { None, Http, Socks5 }

/// <summary>
/// 代理配置。凭据不落明文日志；这里只在内存中传递。
/// </summary>
public sealed record ProxySettings
{
    public ProxyKind Kind { get; init; } = ProxyKind.None;
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }

    public bool IsConfigured => Kind != ProxyKind.None
        && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    /// <summary>aria2 与 HttpClient 都能接受的代理地址；未配置时为 null。</summary>
    public string? ToProxyUri()
    {
        if (!IsConfigured) return null;
        var scheme = Kind == ProxyKind.Socks5 ? "socks5" : "http";
        var credentials = string.IsNullOrWhiteSpace(UserName)
            ? string.Empty
            : $"{Uri.EscapeDataString(UserName)}:{Uri.EscapeDataString(Password ?? string.Empty)}@";
        var host = Host!.Contains(':', StringComparison.Ordinal) ? $"[{Host.Trim('[', ']')}]" : Host;
        return $"{scheme}://{credentials}{host}:{Port}";
    }

    /// <summary>用于日志与界面展示，永不包含密码。</summary>
    public string Describe() => IsConfigured
        ? $"{Kind.ToString().ToLowerInvariant()}://{(Host!.Contains(':', StringComparison.Ordinal) ? "[" + Host.Trim('[', ']') + "]" : Host)}:{Port}"
        : "system";
}

/// <summary>一次下载请求。</summary>
public sealed record DownloadRequest
{
    /// <summary>原始下载地址（GitHub Release 资产等）。</summary>
    public required Uri Source { get; init; }
    /// <summary>目标文件完整路径。</summary>
    public required string TargetPath { get; init; }
    public DownloadEngine Engine { get; init; } = DownloadEngine.Http;
    /// <summary>是否经由 Xget 等加速代理重写地址。</summary>
    public bool UseAccelerator { get; init; }
    /// <summary>P2P 源预留字段；当前返回 PeerNotSupported，待具备独立真实产物模型后开放。</summary>
    public string? PeerSource { get; init; }
    /// <summary>查询用文件名，仅用于展示与加速地址推导。</summary>
    public string? FileName { get; init; }
    /// <summary>资产已知总字节数；null 表示未知，0 表示确认为空文件。</summary>
    public long? ExpectedSize { get; init; }
    /// <summary>可选 SHA-256 十六进制摘要，接受 GitHub 的 sha256: 前缀。</summary>
    public string? ExpectedSha256 { get; init; }
}

/// <summary>下载进度；Done/Total 单位为字节。</summary>
public sealed record DownloadProgress
{
    public required Uri Source { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    /// <summary>已发生的重试次数，用于解释“为什么很慢”。</summary>
    public int Retries { get; init; }
    public string? Message { get; init; }

    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>下载结果；失败用稳定错误码表达，不把原始异常文本抛给界面。</summary>
public sealed record DownloadResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>实际使用的地址（可能已被加速器重写）。</summary>
    public Uri? EffectiveUri { get; init; }
    public string? OutputPath { get; init; }
    public long BytesWritten { get; init; }
    public DownloadEngine Engine { get; init; }
    /// <summary>实际执行的外部命令行，已脱敏，用于诊断展示。</summary>
    public string? CommandLine { get; init; }

    public static DownloadResult Success(Uri effectiveUri, string outputPath, long bytes, DownloadEngine engine, string? commandLine = null) =>
        new()
        {
            Succeeded = true, EffectiveUri = effectiveUri, OutputPath = outputPath,
            BytesWritten = bytes, Engine = engine, CommandLine = commandLine,
        };

    public static DownloadResult Failure(string errorCode, Uri? effectiveUri = null, DownloadEngine engine = DownloadEngine.Http) =>
        new() { Succeeded = false, ErrorCode = errorCode, EffectiveUri = effectiveUri, Engine = engine };
}

/// <summary>稳定错误码。</summary>
public static class DownloadErrorCodes
{
    public const string NoTargetDirectory = "download_no_target_directory";
    public const string TargetNotWritable = "download_target_not_writable";
    public const string UnsupportedSource = "download_unsupported_source";
    public const string NetworkError = "download_network_error";
    public const string HttpStatusError = "download_http_status_error";
    public const string Cancelled = "download_cancelled";
    public const string Aria2NotConfigured = "download_aria2_not_configured";
    public const string Aria2NotFound = "download_aria2_not_found";
    public const string Aria2Failed = "download_aria2_failed";
    public const string Aria2TimedOut = "download_aria2_timed_out";
    public const string InvalidPeerSource = "download_invalid_peer_source";
    public const string PeerNotSupported = "download_peer_not_supported";
    public const string InvalidResponse = "download_invalid_response";
    public const string LengthMismatch = "download_length_mismatch";
    public const string ChecksumMismatch = "download_checksum_mismatch";
    public const string InvalidVerification = "download_invalid_verification";
}

/// <summary>
/// 下载服务。实现负责：选择引擎（HTTP / aria2）、按需重写为加速地址、
/// 汇报进度、完成校验，以及在取消时不发布产物并保留可验证的 HTTP 断点。
/// </summary>
public interface IDownloadService
{
    /// <summary>当前可用的引擎；aria2 未配置或不可用时应只返回 Http。</summary>
    IReadOnlyList<DownloadEngine> AvailableEngines { get; }

    Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>探测 aria2c 是否可用（不做实际下载）。</summary>
    Task<bool> ProbeAria2Async(CancellationToken ct = default);
}
