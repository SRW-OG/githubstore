namespace GitHubStore.App.Services.Localization;

/// <summary>
/// 自更新（GitHubStore 更新自身）界面词条。与 <see cref="LocalizationService"/> 词条同构，
/// 单独成文件是为了让本阶段的写入者只碰自己的文件；由组合方合并进主词条表。
/// <para>
/// 这里的文案严格描述已实现的行为：不静默更新、不自动执行安装包、
/// 没有校验文件就拒绝下载、校验失败绝不执行。
/// </para>
/// </summary>
public static class SelfUpdateStrings
{
    /// <summary>zh-CN / en-US 词条；键与 LocalizationService 的键空间共用。</summary>
    public static IReadOnlyDictionary<string, (string Chinese, string English)> Entries { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["SelfUpdateTitle"] = ("应用自更新", "Application update"),
            ["SelfUpdateCurrentVersion"] = ("当前版本：{0}", "Current version: {0}"),
            ["SelfUpdateVersionUnknown"] = ("当前版本未知（构建时未注入版本号）", "Current version unknown (no version was stamped at build time)"),
            ["SelfUpdateCheck"] = ("检查更新", "Check for update"),
            ["SelfUpdateChecking"] = ("正在检查官方最新正式版…", "Checking the official latest stable release…"),
            ["SelfUpdateUpToDate"] = ("已是最新正式版（{0}）。", "You are on the latest stable release ({0})."),
            ["SelfUpdateOlderInstalled"] = ("当前版本高于官方最新正式版（{0}），不提供降级更新。", "The installed version is newer than the official latest stable release ({0}); no downgrade is offered."),
            ["SelfUpdateCannotCompare"] = ("无法比较当前版本与候选版本（{0}），因此不视为有更新。", "The installed and candidate versions ({0}) cannot be compared, so this is not treated as an update."),
            ["SelfUpdateAvailable"] = ("发现新版本 {0}。", "A new version is available: {0}."),
            ["SelfUpdateReleaseNotes"] = ("发行说明", "Release notes"),
            ["SelfUpdateDownload"] = ("下载并安装更新", "Download and install update"),
            ["SelfUpdateDownloading"] = ("正在下载并校验安装包…", "Downloading and verifying the installer…"),
            ["SelfUpdateDownloaded"] = ("安装包已下载并通过校验（SHA-256 一致）。尚未执行任何安装程序。", "The installer was downloaded and verified (SHA-256 matches). Nothing has been executed yet."),
            ["SelfUpdateVerificationBasis"] = ("校验依据：官方发行版随附的 {0} 资产（SHA-256 {1}）。", "Verification basis: the {0} asset published with the official release (SHA-256 {1})."),
            ["SelfUpdateInstall"] = ("启动安装程序", "Run installer"),
            ["SelfUpdateConfirmTitle"] = ("安装更新？", "Install the update?"),
            ["SelfUpdateConfirm"] = ("将启动已校验的安装程序：{0}\n\n安装程序会关闭并替换正在运行的 GitHubStore，可能请求管理员授权（UAC）。是否继续？", "The verified installer will be started: {0}\n\nIt will close and replace the running GitHubStore and may request administrator approval (UAC). Continue?"),
            ["SelfUpdateLaunchHint"] = ("只有在你点击确认后才会启动安装程序；本程序不会自行执行任何安装包，也不会尝试覆盖自己正在运行的文件。", "The installer starts only after you confirm. This application never executes an installer on its own and never overwrites its own running files."),
            ["SelfUpdateLaunched"] = ("已启动安装程序；请按其提示完成更新。", "The installer has been started. Follow its prompts to finish updating."),
            ["SelfUpdateNoChecksum"] = ("该发行版没有随附 .sha256 校验文件，已拒绝下载：没有官方摘要时无法在不自证的情况下校验安装包。", "This release has no .sha256 checksum asset, so the download was refused: without a published digest the installer cannot be verified without self-attestation."),
            ["SelfUpdateChecksumInvalid"] = ("校验文件内容无法识别，或指向其他文件，已拒绝下载。", "The checksum asset is unreadable or names a different file, so the download was refused."),
            ["SelfUpdateChecksumMismatch"] = ("安装包与官方校验文件不一致，已拒绝，且未执行任何安装程序。", "The installer does not match the published checksum. It was refused and nothing was executed."),
            ["SelfUpdateNotStable"] = ("最新发行版是草稿或预发布版，不作为更新候选。", "The latest release is a draft or prerelease and is not an update candidate."),
            ["SelfUpdateInstallerMissing"] = ("该发行版没有预期的官方安装包（{0}），不提供更新。", "This release has no expected official installer ({0}); no update is offered."),
            ["SelfUpdateAssetRejected"] = ("安装包或校验文件的地址不属于官方仓库，已拒绝。", "The installer or checksum address is not from the official repository and was refused."),
            ["SelfUpdateUnsupportedVersion"] = ("发行标签不是 Major.Minor.Patch，无法确定官方安装包名称。", "The release tag is not Major.Minor.Patch, so the official installer name cannot be determined."),
            ["SelfUpdateUnavailable"] = ("自更新服务未接入。", "The self-update service is not connected."),
            ["SelfUpdateNetworkError"] = ("检查更新失败，请检查网络或代理设置后重试。", "The update check failed. Check your connection or proxy settings and retry."),
            ["SelfUpdateRateLimited"] = ("GitHub API 请求次数已达上限，请稍后重试。", "The GitHub API rate limit was reached. Try again later."),
            ["SelfUpdateInvalidResponse"] = ("GitHub 返回的数据不完整或格式不受支持。", "GitHub returned incomplete or unsupported data."),
            ["SelfUpdateUnsupportedSource"] = ("更新来源不是官方 HTTPS 接口，已拒绝。", "The update source is not the official HTTPS endpoint and was refused."),
            ["SelfUpdateAccessDenied"] = ("GitHub 拒绝了这次请求。", "GitHub rejected the request."),
            ["SelfUpdateStagingFailed"] = ("无法准备更新暂存目录，请检查数据目录权限。", "The update staging directory could not be prepared. Check the data directory permissions."),
            ["SelfUpdateDownloadFailed"] = ("安装包下载或校验失败，未执行安装。", "The installer download or verification failed. Nothing was executed."),
            ["SelfUpdateNotOffered"] = ("当前没有可下载的更新候选。", "There is no downloadable update candidate."),
            ["SelfUpdateNotPrepared"] = ("尚未有通过校验的安装包。", "No verified installer is ready yet."),
            ["SelfUpdateLaunchFailed"] = ("无法启动安装程序。", "The installer could not be started."),
            ["SelfUpdateCancelled"] = ("操作已取消，未执行任何安装程序。", "The operation was cancelled. Nothing was executed."),
            ["SelfUpdateNoAutoUpdate"] = ("本程序不会静默更新，也不会自动执行任何安装包。", "This application never updates silently and never executes an installer automatically."),
        };
}
