using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;

namespace GitHubStore.App.Services.Updates;

/// <summary>
/// 让 GitHubStore 更新它自己：从官方仓库的最新正式版发行版中取出官方安装包，
/// 经原有下载与校验链下载到<em>不执行</em>的暂存目录，最后在用户明确确认后交给 Shell 启动。
/// <para>
/// <b>来源固定</b>：仓库与主机都是编译期常量，程序<em>不接受</em>任何用户提供的更新地址，
/// 因此不存在“把更新指向第三方”的配置面。非 <c>api.github.com</c> 的 HTTPS 响应（含被重定向）一律拒绝。
/// </para>
/// <para>
/// <b>校验依据</b>：必须存在与官方安装包同目录发布的 <c>.sha256</c> 资产，安装包按其中的摘要校验。
/// 若发行版没有校验文件，程序<em>拒绝</em>下载，而不是把“自己算出的哈希与自己对上”当成安全结论。
/// </para>
/// <para>
/// <b>不自我覆盖</b>：本服务从不尝试替换正在运行的程序文件，只启动已校验的安装程序，
/// 由 Inno Setup 负责关闭并替换正在运行的应用。
/// </para>
/// </summary>
public sealed class SelfUpdateService
{
    /// <summary>发行接口响应的大小上限；避免异常响应把内存占满。</summary>
    private const int MaximumReleaseBytes = 512 * 1024;

    /// <summary>校验文件文本的长度上限；构建脚本写出的内容只有一行。</summary>
    private const int MaximumChecksumBytes = 4096;

    /// <summary>发行版资产数量的合理上限；超过即视为响应异常。</summary>
    private const int MaximumAssets = 200;

    /// <summary>单次发行接口请求的超时；超时报告为网络失败，不猜测结果。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IDownloadService _downloads;
    private readonly HttpClient? _http;
    private readonly string _dataDirectory;
    private readonly Func<string?> _currentVersionProvider;
    private readonly SelfUpdateInstallerLauncher? _installerLauncher;

    // 只有通过 SHA-256 校验的安装包才登记在这里；启动入口据此拒绝任何未经校验的路径。
    private string? _verifiedInstallerPath;
    private string? _verifiedSha256;
    private string? _verifiedVersion;

    /// <summary>
    /// <paramref name="installerLauncher"/> 为 null 时<em>不</em>提供启动能力（如实报告为未接入），
    /// 而不是自行猜测如何启动。测试始终注入记录委托，绝不执行任何安装程序。
    /// </summary>
    public SelfUpdateService(IDownloadService downloads, string dataDirectory, HttpClient? http = null,
        Func<string?>? currentVersionProvider = null, SelfUpdateInstallerLauncher? installerLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _downloads = downloads;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _http = http;
        _currentVersionProvider = currentVersionProvider ?? (() => ReadStampedVersion(typeof(SelfUpdateService).Assembly));
        _installerLauncher = installerLauncher;
    }

    /// <summary>当前应用版本原文；取不到返回 null，界面据此显示“未知”而不是编造版本。</summary>
    public string? CurrentVersion => _currentVersionProvider();

    /// <summary>是否存在一次已通过校验、可以启动的安装包。</summary>
    public bool IsInstallerPrepared => _verifiedInstallerPath is not null && File.Exists(_verifiedInstallerPath);

    /// <summary>最近一次通过校验的版本；未准备时为 null。</summary>
    public string? PreparedVersion => _verifiedVersion;

    /// <summary>暂存目录：应用数据目录下的 updates 子目录。安装包只落在这里，绝不落进安装目录。</summary>
    public string StagingDirectory => Path.Combine(_dataDirectory, SelfUpdatePolicy.StagingDirectoryName);

    /// <summary>
    /// 读取官方仓库的最新正式版发行版并与当前版本比较。
    /// 草稿与预发布版被拒绝；同版本或更旧<em>不</em>提示更新；无法解析按“不同版本”处理，绝不谎称更新。
    /// </summary>
    public async Task<SelfUpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (_http is null) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.Unavailable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SelfUpdatePolicy.LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd("GitHubStore/0.1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            // 只接受 api.github.com 的 HTTPS 响应：重定向到其他主机一律拒绝，与目录服务同一口径。
            if (response.RequestMessage?.RequestUri is { } finalUri
                && (finalUri.Scheme != Uri.UriSchemeHttps
                    || !string.Equals(finalUri.Host, SelfUpdatePolicy.ApiHost, StringComparison.OrdinalIgnoreCase)))
                return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.UnsupportedSource);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || response.StatusCode == HttpStatusCode.Forbidden
                    && response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0"))
                return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.RateLimited);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.AccessDenied);
            if (!response.IsSuccessStatusCode)
                return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.NetworkError);
            if (response.Content.Headers.ContentLength > MaximumReleaseBytes)
                return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
            var json = await ReadBoundedAsync(response, MaximumReleaseBytes, timeout.Token).ConfigureAwait(false);
            if (json is null) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
            ct.ThrowIfCancellationRequested();
            return ParseRelease(json, _currentVersionProvider());
        }
        catch (OperationCanceledException)
        {
            return SelfUpdateCheckResult.Failure(ct.IsCancellationRequested
                ? SelfUpdateErrorCodes.Cancelled : SelfUpdateErrorCodes.NetworkError);
        }
        catch (HttpRequestException) { return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.NetworkError); }
        catch (IOException) { return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.NetworkError); }
    }

    /// <summary>
    /// 解析发行版响应并决定是否提示更新。版本判定完全交给 Core 的 <see cref="VersionComparer"/>：
    /// 只有关系为 <see cref="VersionRelation.Newer"/> 时才要求官方安装包与校验文件齐全；
    /// 同版本、更旧与无法判定都如实报告“没有可提供的更新”，且不会去碰网络下载。
    /// </summary>
    private static SelfUpdateCheckResult ParseRelease(string json, string? currentVersion)
    {
        string tag;
        bool draft;
        bool prerelease;
        List<(string Name, long Size, string Url)> assets = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
            tag = root.TryGetProperty("tag_name", out var tagNode) && tagNode.ValueKind == JsonValueKind.String
                ? tagNode.GetString() ?? string.Empty : string.Empty;
            draft = root.TryGetProperty("draft", out var draftNode) && draftNode.ValueKind == JsonValueKind.True;
            prerelease = root.TryGetProperty("prerelease", out var preNode) && preNode.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("assets", out var assetsNode) && assetsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsNode.EnumerateArray())
                {
                    if (assets.Count >= MaximumAssets) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = asset.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                        ? nameNode.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var urlNode) && urlNode.ValueKind == JsonValueKind.String
                        ? urlNode.GetString() : null;
                    var size = asset.TryGetProperty("size", out var sizeNode) && sizeNode.ValueKind == JsonValueKind.Number
                        && sizeNode.TryGetInt64(out var parsedSize) ? parsedSize : -1L;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                    assets.Add((name, size, url));
                }
            }
        }
        catch (JsonException) { return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse); }
        if (string.IsNullOrWhiteSpace(tag)) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
        // 草稿与预发布版都不是更新候选：即使接口把它们返回出来也必须拒绝。
        if (draft || prerelease) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.NotStable);

        var relation = VersionComparer.RelateText(currentVersion, tag);
        var releaseNotesUrl = SelfUpdatePolicy.ReleaseNotesUrl(tag);
        // 无法判定（Different）与同版本、更旧一样，只报告关系，不提供任何下载。
        if (relation != VersionRelation.Newer)
            return SelfUpdateCheckResult.Success(new SelfUpdateCandidate
            {
                Tag = tag, Version = NormalizeTag(tag) ?? tag, Relation = relation, ReleaseNotesUrl = releaseNotesUrl,
            });

        // 关系确实是“更新”，此时才需要推导官方安装包名称；标签不是 Major.Minor.Patch 就无法可靠推导。
        if (NormalizeTag(tag) is not { } version)
            return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.UnsupportedVersion);
        var installerName = SelfUpdatePolicy.ExpectedInstallerName(version);
        var checksumName = SelfUpdatePolicy.ExpectedChecksumName(installerName);
        if (!TrySelectAsset(assets, installerName, out var installer, out var installerError))
            return SelfUpdateCheckResult.Failure(installerError);
        if (!TrySelectAsset(assets, checksumName, out var checksum, out var checksumError))
            return SelfUpdateCheckResult.Failure(checksumError);
        // 资产地址必须落在官方仓库的发布资产路径下；大小必须是可信的正数。
        if (!IsOfficialAsset(installer.Url, installerName, out var installerUri)
            || !IsOfficialAsset(checksum.Url, checksumName, out var checksumUri))
            return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.AssetRejected);
        // 声明为 0 字节的安装包不可能是一个可用的官方安装包。
        if (installer.Size <= 0) return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.InvalidResponse);
        // 校验文件的大小必须像一行摘要：为空或过大时如实报告校验资产无效。
        if (checksum.Size is <= 0 or > MaximumChecksumBytes)
            return SelfUpdateCheckResult.Failure(SelfUpdateErrorCodes.ChecksumAssetInvalid);

        return SelfUpdateCheckResult.Success(new SelfUpdateCandidate
        {
            Tag = tag, Version = version, Relation = relation, ReleaseNotesUrl = releaseNotesUrl,
            InstallerName = installerName, InstallerUri = installerUri, InstallerSize = installer.Size,
            ChecksumName = checksumName, ChecksumUri = checksumUri, ChecksumSize = checksum.Size,
        });
    }

    /// <summary>
    /// 下载官方校验文件与安装包到暂存目录。校验依据是发行版里的 .sha256 资产：
    /// 安装包必须与它逐字节相符，否则下载层不发布产物。此方法<em>从不</em>执行安装程序。
    /// </summary>
    public async Task<SelfUpdateDownloadResult> DownloadAsync(SelfUpdateCandidate candidate,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        // 预先取消同样返回稳定取消码，不把异常抛给界面。
        if (ct.IsCancellationRequested) return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.Cancelled);
        // 只有可判定的更新才允许下载；同版本/更旧/无法判定一律拒绝，界面只能如实显示“不提供更新”。
        if (!candidate.IsUpdateOffered
            || candidate.InstallerUri is not { } installerUri || candidate.ChecksumUri is not { } checksumUri
            || candidate.InstallerName is not { } installerName || candidate.ChecksumName is not { } checksumName)
            return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.NotOffered);

        string staging;
        try
        {
            staging = StagingDirectory;
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.StagingFailed); }

        var checksumPath = Path.Combine(staging, checksumName);
        var installerPath = Path.Combine(staging, installerName);
        DownloadResult download = null!;
        string? expectedSha256;
        try
        {
            // 先取校验文件：安装包的 ExpectedSha256 必须来自它，而不是本轮自己算出的摘要。
            var checksumDownload = await _downloads.DownloadAsync(new DownloadRequest
            {
                Source = checksumUri, TargetPath = checksumPath,
                FileName = checksumName, ExpectedSize = candidate.ChecksumSize, UseAccelerator = false,
            }, progress: null, ct).ConfigureAwait(false);
            if (!checksumDownload.Succeeded)
                return SelfUpdateDownloadResult.Failure(
                    MapDownloadFailure(checksumDownload.ErrorCode), checksumDownload.ErrorCode);
            ct.ThrowIfCancellationRequested();
            if (!TryReadChecksum(checksumPath, installerName, out expectedSha256))
                return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.ChecksumAssetInvalid);

            download = await _downloads.DownloadAsync(new DownloadRequest
            {
                Source = installerUri, TargetPath = installerPath,
                FileName = installerName, ExpectedSize = candidate.InstallerSize,
                ExpectedSha256 = expectedSha256, UseAccelerator = false,
            }, progress, ct).ConfigureAwait(false);
            if (!download.Succeeded)
            {
                // 校验不符时下载层不会发布产物；同时作废任何先前登记，避免旧文件被误当成本次结果。
                ClearPrepared();
                return SelfUpdateDownloadResult.Failure(MapDownloadFailure(download.ErrorCode), download.ErrorCode);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是稳定结果而不是异常：调用方据错误码区分取消与校验失败，且此时没有任何产物被登记。
            ClearPrepared();
            return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearPrepared();
            return SelfUpdateDownloadResult.Failure(SelfUpdateErrorCodes.DownloadFailed);
        }

        _verifiedInstallerPath = installerPath;
        _verifiedSha256 = expectedSha256;
        _verifiedVersion = candidate.Version;
        return new SelfUpdateDownloadResult
        {
            Succeeded = true, InstallerPath = installerPath, Sha256 = expectedSha256,
            Bytes = download.BytesWritten, VerificationBasisChecksumName = checksumName,
        };
    }

    /// <summary>
    /// 启动已校验的安装程序。<paramref name="installerPath"/> 必须正是本次通过校验的那个文件，
    /// 且启动前会<em>重新</em>核对 SHA-256，保证被启动的就是被校验的字节。
    /// 交由 Inno Setup 关闭并替换正在运行的应用；本服务不覆盖自身文件。
    /// </summary>
    public async Task<SelfUpdateLaunchResult> LaunchVerifiedInstallerAsync(string installerPath,
        CancellationToken ct = default)
    {
        if (_installerLauncher is null) return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.Unavailable);
        if (_verifiedInstallerPath is not { } verified || _verifiedSha256 is not { } hash)
            return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.NotPrepared);
        if (!string.Equals(verified, installerPath, StringComparison.OrdinalIgnoreCase))
            return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.NotPrepared);
        // 取消返回稳定结果而不是异常：界面据此显示“已取消”，且此时绝不执行任何程序。
        if (ct.IsCancellationRequested) return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.Cancelled);
        try
        {
            await using var file = new FileStream(verified, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                // 文件在校验之后发生变化：作废登记，绝不放行。
                ClearPrepared();
                return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.ChecksumMismatch);
            }
        }
        catch (OperationCanceledException) { return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.Cancelled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.NotPrepared); }
        ct.ThrowIfCancellationRequested();
        try
        {
            _installerLauncher(verified);
            return SelfUpdateLaunchResult.Success();
        }
        catch (OperationCanceledException) { return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.Cancelled); }
        catch (Exception) { return SelfUpdateLaunchResult.Failure(SelfUpdateErrorCodes.LaunchFailed); }
    }

    /// <summary>
    /// 生产用的启动实现：UseShellExecute=true，交给 Shell 处理 UAC 与安装向导；
    /// 不使用 <c>UseShellExecute=false</c> 的直接执行语义。
    /// </summary>
    public static SelfUpdateInstallerLauncher CreateShellLauncher() => installerPath =>
    {
        using var process = Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true })
            ?? throw new IOException("The installer process could not be started.");
    };

    private void ClearPrepared()
    {
        _verifiedInstallerPath = null;
        _verifiedSha256 = null;
        _verifiedVersion = null;
    }

    /// <summary>把下载层错误码映射到自更新错误码，保留原始码用于诊断。</summary>
    private static string MapDownloadFailure(string? downloadErrorCode) => downloadErrorCode switch
    {
        DownloadErrorCodes.ChecksumMismatch => SelfUpdateErrorCodes.ChecksumMismatch,
        DownloadErrorCodes.Cancelled => SelfUpdateErrorCodes.Cancelled,
        DownloadErrorCodes.UnsupportedSource => SelfUpdateErrorCodes.AssetRejected,
        DownloadErrorCodes.TargetNotWritable => SelfUpdateErrorCodes.StagingFailed,
        DownloadErrorCodes.NoTargetDirectory => SelfUpdateErrorCodes.StagingFailed,
        _ => SelfUpdateErrorCodes.DownloadFailed,
    };

    /// <summary>标签必须是 Major.Minor.Patch（允许一个前导 v/V），否则无法安全推导官方安装包名称。</summary>
    private static string? NormalizeTag(string tag)
    {
        var text = tag.Trim();
        if (text.Length > 1 && text[0] is 'v' or 'V') text = text[1..];
        if (text.Length == 0 || text.Contains('+', StringComparison.Ordinal)
            || text.Contains('-', StringComparison.Ordinal)) return null;
        var segments = text.Split('.');
        if (segments.Length != 3) return null;
        foreach (var segment in segments)
            if (segment.Length is 0 or > 9 || !segment.All(char.IsAsciiDigit)) return null;
        return text;
    }

    /// <summary>按资产名精确匹配；名称缺失或重复都视为歧义并拒绝，不做“随便挑一个”。</summary>
    private static bool TrySelectAsset(List<(string Name, long Size, string Url)> assets, string expectedName,
        out (string Name, long Size, string Url) asset, out string errorCode)
    {
        asset = default;
        errorCode = expectedName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
            ? SelfUpdateErrorCodes.ChecksumAssetMissing : SelfUpdateErrorCodes.InstallerMissing;
        var matches = assets.Where(a => a.Name.Equals(expectedName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return false;
        asset = matches[0];
        return true;
    }

    /// <summary>资产地址必须是无凭据的 HTTPS，且落在官方仓库的发布资产路径下。</summary>
    private static bool IsOfficialAsset(string url, string expectedName, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (!parsed.Host.Equals(SelfUpdatePolicy.AssetHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (!parsed.IsDefaultPort) return false;
        if (!parsed.AbsolutePath.StartsWith(SelfUpdatePolicy.ReleaseDownloadPrefix, StringComparison.Ordinal)) return false;
        if (parsed.Segments.Length == 0
            || !Uri.UnescapeDataString(parsed.Segments[^1]).Equals(expectedName, StringComparison.Ordinal)) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// 读取校验文件。接受「仅摘要」或「摘要 + 文件名」两种写法（构建脚本写出的是后者）；
    /// 出现多个摘要、非法内容，或文件名指向别的文件时一律拒绝。
    /// </summary>
    private static bool TryReadChecksum(string path, string expectedInstallerName, out string? sha256)
    {
        sha256 = null;
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumChecksumBytes) return false;
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 2) return false;
        var digest = tokens[0];
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) return false;
        if (tokens.Length == 2)
        {
            // sha256sum 的二进制模式会用 * 前缀标记文件名；两种写法都接受。
            var name = tokens[1].TrimStart('*');
            if (!name.Equals(expectedInstallerName, StringComparison.OrdinalIgnoreCase)) return false;
        }
        sha256 = digest;
        return true;
    }

    /// <summary>有界读取响应正文；超过上限返回 null，由调用方报告响应无效。</summary>
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, int maximumBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maximumBytes) return null;
            // 用异步写入：同步 Write 会被分析器判为阻塞调用（VSTHRD103）。
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 读取由构建脚本注入的程序集版本；取不到返回 null（界面显示未知），绝不硬编码假版本。
    /// 去掉 <c>+build</c> 元数据，保证与发行标签逐字可比。
    /// </summary>
    internal static string? ReadStampedVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var metadata = informational.IndexOf('+', StringComparison.Ordinal);
            return metadata >= 0 ? informational[..metadata] : informational.Trim();
        }
        return assembly.GetName().Version is { } version ? version.ToString(3) : null;
    }
}
