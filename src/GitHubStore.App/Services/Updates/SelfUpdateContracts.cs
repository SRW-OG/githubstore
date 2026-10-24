using GitHubStore.Core.Catalog;

namespace GitHubStore.App.Services.Updates;

/// <summary>
/// 自更新的固定口径。这些常量是硬编码的官方来源，程序<em>不接受</em>任何用户提供的更新地址或主机，
/// 因此不存在“把更新指向别处”的配置面。
/// </summary>
public static class SelfUpdatePolicy
{
    /// <summary>官方仓库所有者。程序只从该所有者下的这一个仓库更新自己。</summary>
    public const string OfficialOwner = "SRW-OG";

    /// <summary>官方仓库名。</summary>
    public const string OfficialRepositoryName = "githubstore";

    /// <summary>官方仓库全名，用于日志与界面展示。</summary>
    public const string OfficialRepository = OfficialOwner + "/" + OfficialRepositoryName;

    /// <summary>官方仓库网页地址。</summary>
    public const string OfficialRepositoryUrl = "https://github.com/" + OfficialRepository;

    /// <summary>最新正式版发行接口；草稿与预发布版不会由该接口返回，程序仍会二次核对。</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + OfficialRepository + "/releases/latest";

    /// <summary>GitHub REST API 主机；只能接受它的 HTTPS 响应。</summary>
    public const string ApiHost = "api.github.com";

    /// <summary>发布资产主机；安装包与校验文件都必须来自它。</summary>
    public const string AssetHost = "github.com";

    /// <summary>发布资产路径前缀（owner/repo/releases/download）。</summary>
    public const string ReleaseDownloadPrefix = "/" + OfficialRepository + "/releases/download/";

    /// <summary>
    /// 官方安装包唯一的架构后缀，与 packaging/GitHubStore.iss 的 OutputBaseFilename 和
    /// 脚本的 Architecture=win-x64 一致。其他架构的资产不会被当作更新候选。
    /// </summary>
    public const string InstallerArchitecture = "win-x64";

    /// <summary>应用数据目录下的暂存子目录名；只放安装包与校验文件，不作他用。</summary>
    public const string StagingDirectoryName = "updates";

    /// <summary>
    /// 由发行标签推导出的官方安装包文件名：GitHubStore-Setup-&lt;版本&gt;-win-x64.exe。
    /// 标签本身按 vMajor.Minor.Patch 递增，具体名称以构建脚本实测结果为准（脚本会把实际版本号写进程序集）。
    /// </summary>
    public static string ExpectedInstallerName(string version) =>
        "GitHubStore-Setup-" + version + "-" + InstallerArchitecture + ".exe";

    /// <summary>与安装包同目录发布的校验文件名：&lt;安装包名&gt;.sha256，内容为脚本写出的 SHA-256。</summary>
    public static string ExpectedChecksumName(string installerName) => installerName + ".sha256";

    /// <summary>发行说明地址由程序用官方仓库地址拼出，不采用接口返回的任意链接。</summary>
    public static string ReleaseNotesUrl(string tag) =>
        OfficialRepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag);
}

/// <summary>
/// 自更新稳定错误码。与下载层错误码分开，便于界面区分“更新本身失败”和“安装包下载失败”，
/// 也保证调用方永远拿到稳定值而不是异常文本。
/// </summary>
public static class SelfUpdateErrorCodes
{
    /// <summary>自更新服务未接入（未注册或没有 HTTP 客户端）。</summary>
    public const string Unavailable = "self_update_unavailable";

    /// <summary>响应不是 api.github.com 的 HTTPS 响应（含被重定向到其他主机）。</summary>
    public const string UnsupportedSource = "self_update_unsupported_source";

    /// <summary>网络失败或超时。</summary>
    public const string NetworkError = "self_update_network_error";

    /// <summary>GitHub API 限流。</summary>
    public const string RateLimited = "self_update_rate_limited";

    /// <summary>响应不完整、格式不受支持，或没有已发布的正式版。</summary>
    public const string InvalidResponse = "self_update_invalid_response";

    /// <summary>GitHub 拒绝了这次请求（非限流导致的 401/403）。</summary>
    public const string AccessDenied = "self_update_access_denied";

    /// <summary>最新发行版是草稿或预发布版，不作为更新候选。</summary>
    public const string NotStable = "self_update_not_stable";

    /// <summary>发行标签不是预期的 Major.Minor.Patch，无法确定官方安装包名称。</summary>
    public const string UnsupportedVersion = "self_update_unsupported_version";

    /// <summary>发行版里没有预期的官方安装包（名称或架构不符，或存在重复资产）。</summary>
    public const string InstallerMissing = "self_update_installer_missing";

    /// <summary>发行版没有随附 .sha256 校验文件。</summary>
    public const string ChecksumAssetMissing = "self_update_checksum_asset_missing";

    /// <summary>校验文件内容无法识别（不是唯一的 64 位十六进制 SHA-256，或指向别的文件）。</summary>
    public const string ChecksumAssetInvalid = "self_update_checksum_asset_invalid";

    /// <summary>资产地址不是官方仓库的发布资产地址。</summary>
    public const string AssetRejected = "self_update_asset_rejected";

    /// <summary>安装包字节与官方校验文件不一致；已下载内容不会被沿用。</summary>
    public const string ChecksumMismatch = "self_update_checksum_mismatch";

    /// <summary>无法在应用数据目录下准备暂存目录。</summary>
    public const string StagingFailed = "self_update_staging_failed";

    /// <summary>安装包下载或长度/SHA-256 校验失败。</summary>
    public const string DownloadFailed = "self_update_download_failed";

    /// <summary>当前候选不是可判定的更新版本；拒绝下载。</summary>
    public const string NotOffered = "self_update_not_offered";

    /// <summary>尚未完成一次通过校验的下载，因此没有可启动的安装包。</summary>
    public const string NotPrepared = "self_update_not_prepared";

    /// <summary>无法启动已校验的安装程序。</summary>
    public const string LaunchFailed = "self_update_launch_failed";

    /// <summary>调用方取消；未执行任何安装程序。</summary>
    public const string Cancelled = "self_update_cancelled";
}

/// <summary>
/// 启动已校验安装包的委托。生产实现使用 UseShellExecute=true 交给 Shell，
/// 测试注入记录委托，从而<em>绝不</em>真正执行任何安装程序。
/// </summary>
public delegate void SelfUpdateInstallerLauncher(string installerPath);

/// <summary>
/// 一次检查得到的候选发行版。只有当 <see cref="Relation"/> 为
/// <see cref="VersionRelation.Newer"/> 且官方安装包与校验文件都通过校验时，
/// <see cref="IsUpdateOffered"/> 才为 true。
/// </summary>
public sealed record SelfUpdateCandidate
{
    /// <summary>发行标签原文（例如 v0.1.3），仅用于展示与拼装发行说明地址。</summary>
    public required string Tag { get; init; }

    /// <summary>去掉前导 v/V 后的版本数字，用于推导官方安装包名称。</summary>
    public required string Version { get; init; }

    /// <summary>候选相对已安装版本的关系。Different 表示无法判定，绝不当作 Newer。</summary>
    public required VersionRelation Relation { get; init; }

    /// <summary>官方仓库的发行说明地址。</summary>
    public required string ReleaseNotesUrl { get; init; }

    /// <summary>预期的官方安装包文件名。</summary>
    public string? InstallerName { get; init; }

    /// <summary>官方安装包资产地址（必须是官方仓库的发布资产地址）。</summary>
    public Uri? InstallerUri { get; init; }

    /// <summary>发行接口声明的安装包字节数。</summary>
    public long InstallerSize { get; init; }

    /// <summary>预期的校验文件名。</summary>
    public string? ChecksumName { get; init; }

    /// <summary>官方校验文件资产地址。</summary>
    public Uri? ChecksumUri { get; init; }

    /// <summary>发行接口声明的校验文件字节数。</summary>
    public long ChecksumSize { get; init; }

    /// <summary>是否可以作为更新候选（必须是可判定的更新，且官方安装包与校验文件齐全）。</summary>
    public bool IsUpdateOffered => Relation == VersionRelation.Newer
        && InstallerUri is not null && ChecksumUri is not null;
}

/// <summary>检查结果。失败用稳定错误码表达。</summary>
public sealed record SelfUpdateCheckResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public SelfUpdateCandidate? Candidate { get; init; }

    public static SelfUpdateCheckResult Failure(string errorCode) =>
        new() { Succeeded = false, ErrorCode = errorCode };

    public static SelfUpdateCheckResult Success(SelfUpdateCandidate candidate) =>
        new() { Succeeded = true, Candidate = candidate };
}

/// <summary>
/// 下载并校验结果。<see cref="VerificationBasisChecksumName"/> 与 <see cref="Sha256"/>
/// 是界面必须如实展示的校验依据：SHA-256 来自官方发行版内同名 .sha256 资产。
/// </summary>
public sealed record SelfUpdateDownloadResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>下载层的原始错误码（诊断用），便于区分限流、长度不符与网络失败。</summary>
    public string? DownloadErrorCode { get; init; }

    public string? InstallerPath { get; init; }
    public string? Sha256 { get; init; }
    public long Bytes { get; init; }

    /// <summary>实际使用的校验文件名（官方发行版资产名）。</summary>
    public string? VerificationBasisChecksumName { get; init; }

    public static SelfUpdateDownloadResult Failure(string errorCode, string? downloadErrorCode = null) =>
        new() { Succeeded = false, ErrorCode = errorCode, DownloadErrorCode = downloadErrorCode };
}

/// <summary>启动结果。</summary>
public sealed record SelfUpdateLaunchResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }

    public static SelfUpdateLaunchResult Failure(string errorCode) => new() { Succeeded = false, ErrorCode = errorCode };

    public static SelfUpdateLaunchResult Success() => new() { Succeeded = true };
}
