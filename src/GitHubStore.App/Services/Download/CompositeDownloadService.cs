using GitHubStore.App.Services.Acceleration;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>保持请求指定引擎；加速失败仅对原始来源重试一次。HTTP 代理由传入的 HttpClient 配置。</summary>
public sealed class CompositeDownloadService : IDownloadService
{
    private readonly HttpDownloadService _http;
    private readonly Aria2ProcessRunner _aria2;
    private readonly XgetUrlRewriter _accelerator;
    private bool? _aria2Available;

    public CompositeDownloadService(HttpClient httpClient, string? aria2cPath = null, string? acceleratorBase = null)
    {
        _http = new HttpDownloadService(httpClient);
        _aria2 = new Aria2ProcessRunner(aria2cPath);
        _accelerator = new XgetUrlRewriter(acceleratorBase);
    }

    /// <summary>仅配置 aria2 的代理。HTTP handler 必须由调用方创建并配置；赋值此属性不改变 HttpClient。</summary>
    public ProxySettings? Proxy { get; set; }

    public IReadOnlyList<DownloadEngine> AvailableEngines => _aria2Available == true
        ? [DownloadEngine.Http, DownloadEngine.Aria2] : [DownloadEngine.Http];

    public async Task<bool> ProbeAria2Async(CancellationToken ct = default)
    {
        _aria2Available = await _aria2.ProbeAsync(ct).ConfigureAwait(false);
        return _aria2Available.Value;
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ct.IsCancellationRequested) return DownloadResult.Failure(DownloadErrorCodes.Cancelled, request.Source, request.Engine);
        // P2P 需要真实多文件产物模型，UI 应显示本批尚未支持，不探测也不启动进程。
        if (!string.IsNullOrWhiteSpace(request.PeerSource))
            return DownloadResult.Failure(DownloadErrorCodes.PeerNotSupported, request.Source, DownloadEngine.Aria2);
        var validation = DownloadFileRules.Validate(request, out _);
        if (validation is not null) return DownloadResult.Failure(validation, request.Source, request.Engine);

        var effective = request.Source;
        var rewritten = request.UseAccelerator && _accelerator.TryRewrite(request.Source, out effective);
        if (request.UseAccelerator && !rewritten)
            progress?.Report(new DownloadProgress { Source = request.Source, Message = "accelerator_unsupported_host" });
        // 不为 HTTP 下载探测外部程序。显式 aria2 请求的缺失/失败保留其准确错误码。
        var proxy = Proxy;
        Task<DownloadResult> RunAsync(Uri source) => request.Engine == DownloadEngine.Aria2
            ? _aria2.DownloadAsync(request with { Source = source }, proxy, false, progress, ct)
            : _http.DownloadAsync(request with { Source = source }, progress, ct);

        var result = await RunAsync(effective).ConfigureAwait(false);
        if (!rewritten || result.Succeeded || result.ErrorCode == DownloadErrorCodes.Cancelled || ct.IsCancellationRequested)
            return result;
        if (result.ErrorCode is DownloadErrorCodes.Aria2NotConfigured or DownloadErrorCodes.Aria2NotFound
            or DownloadErrorCodes.NoTargetDirectory or DownloadErrorCodes.TargetNotWritable or DownloadErrorCodes.InvalidVerification)
            return result;
        progress?.Report(new DownloadProgress { Source = request.Source, Retries = 1, Message = "accelerator_failed_retrying_origin" });
        // HttpDownloadService 的来源元数据阻止把加速站点的 .part 拼接到原站。
        return await RunAsync(request.Source).ConfigureAwait(false);
    }

    internal static bool LooksLikePeerSource(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && uri.Query.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase);
        return Path.IsPathFullyQualified(trimmed) && trimmed.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }
}
