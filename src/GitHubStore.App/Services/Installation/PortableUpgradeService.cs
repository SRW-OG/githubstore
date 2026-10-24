using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GitHubStore.App.Services.Download;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using CatalogApp = GitHubStore.Core.Abstractions.App;
using Version = GitHubStore.Core.Abstractions.Version;

namespace GitHubStore.App.Services.Installation;

/// <summary>
/// 便携更新/回滚的阶段。仅供进度展示：阶段名不代表结果，
/// 只有返回结果的 <c>Succeeded</c>/<c>ErrorCode</c> 才是结论。
/// </summary>
public enum PortableUpgradePhase
{
    Preparing, Downloading, Extracting, Verifying, Journaling, BackingUp, Swapping, Registering, Completed,
}

public sealed record PortableUpgradeProgress(PortableUpgradePhase Phase, double? Percent = null);

/// <summary>
/// 一次便携更新的输入。候选版本由调用方（软件源/已确认的目录条目）给出，
/// 但更新目标永远是<em>按安装 ID 重新读取</em>的登记：界面快照不能作为写入依据。
/// </summary>
public sealed record PortableUpgradeRequest
{
    public required string InstallId { get; init; }
    public required CatalogApp App { get; init; }
    public required Release Release { get; init; }
    public required Asset Asset { get; init; }

    /// <summary>可选：库策略里配置好的入口相对路径，优先级高于“沿用旧入口”。</summary>
    public string? ExecutableRelativePath { get; init; }
}

public sealed record PortableUpgradeResult(bool Succeeded, string? ErrorCode = null, string? InstallId = null,
    string? TargetDirectory = null, string? ExecutablePath = null, string? DisplayVersion = null,
    bool FilesRestored = false, bool FilesMayRemain = false);

public sealed record PortableRollbackResult(bool Succeeded, string? ErrorCode = null, string? InstallId = null,
    string? TargetDirectory = null, string? ExecutablePath = null, string? DisplayVersion = null,
    bool FilesMayRemain = false);

/// <summary>
/// 启动恢复的统计。<see cref="Restored"/> 表示确实用备份换回了旧版本；
/// <see cref="CleanedUp"/> 表示没有需要换回的备份（改动尚未开始，或已由上次调用还原），只清掉了遗留产物。
/// 失败单列在 <see cref="Failed"/> 与 <see cref="ErrorCodes"/> 中，绝不混进成功计数。
/// </summary>
public sealed record PortableRecoveryResult(int Examined, int Restored, int CleanedUp, int Failed,
    IReadOnlyList<string> ErrorCodes)
{
    public static PortableRecoveryResult Nothing { get; } = new(0, 0, 0, 0, []);
}

/// <summary>
/// 更新进行中的持久化日志（journal）。它只在“文件可能已被改动”的时间窗内存在：
/// 写入时机在备份动作<em>之前</em>，删除时机在登记成功或还原成功<em>之后</em>。
/// 因此“存在 journal”就等于“上次更新可能被打断，必须还原”。
/// </summary>
public sealed record PortableUpgradeJournal
{
    public required int SchemaVersion { get; init; }
    public required string InstallId { get; init; }
    public required string TargetDir { get; init; }
    public required string BackupDir { get; init; }
    public required string StageDir { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required InstallRecord PreviousReceipt { get; init; }
    public required string PreviousDisplayName { get; init; }

    /// <summary>
    /// 提交标记：新版本文件已就位<em>且</em>新登记已写入后置为 true（并立即落盘）。
    /// <para>
    /// 它是“journal 存在就必须还原”这条不变式的例外开关。否则会出现一种坏局面：
    /// 更新已经成功、登记也已写入，但删除 journal 这一步失败——下次启动就会把用户刚装好的
    /// 新版本又还原回旧版本。置位成功之前一律视为“未提交”，此时任何失败都必须还原。
    /// </para>
    /// </summary>
    public bool Completed { get; init; }
}

/// <summary>
/// 一次成功更新后保留下来的“上一个版本”，供用户显式回滚。
/// 它指向保留的备份目录；记录损坏或指向受管目录之外时一律拒绝回滚，绝不猜测。
/// </summary>
public sealed record PortableRollbackRecord
{
    public required int SchemaVersion { get; init; }
    public required string InstallId { get; init; }
    public required string TargetDir { get; init; }
    public required string BackupDir { get; init; }
    public required DateTimeOffset RetainedAt { get; init; }
    public required InstallRecord PreviousReceipt { get; init; }
    public required string PreviousDisplayName { get; init; }

    /// <summary>被替换掉的那一版（较新）版本号原文，仅用于展示。</summary>
    public string? ReplacedVersion { get; init; }
}

/// <summary>
/// 仅供故障注入测试使用：抛出它表示“进程在此处被强杀”。
/// 服务<em>不会</em>对它做任何还原（正常的 catch/finally 都不处理它），把现场原样留给
/// <see cref="PortableUpgradeService.RecoverAsync"/> 的启动恢复流程。
/// 生产代码永不抛出此异常；它存在的唯一目的是让“崩溃后磁盘上是什么状态”可被真实断言，
/// 而不是靠模拟一个假的 journal 文件。
/// </summary>
public sealed class PortableUpgradeSimulatedCrashException : Exception
{
    public PortableUpgradeSimulatedCrashException(PortableUpgradePhase phase) : base(phase.ToString()) => Phase = phase;

    public PortableUpgradePhase Phase { get; }
}

/// <summary>稳定错误码。调用方按码映射文案，绝不解析异常文本。</summary>
public static class PortableUpgradeErrorCodes
{
    public const string Cancelled = "update_cancelled";
    public const string Busy = "update_busy";
    public const string InvalidRequest = "update_invalid_request";

    /// <summary>按安装 ID 读不到登记原文（未知 ID、已移除，或索引本身读不出来）。一个字节都没动。</summary>
    public const string InstallationNotFound = "update_installation_not_found";

    /// <summary>索引本身无法读取，因此无法证明任何一条安装的所有权。未改动任何文件。</summary>
    public const string ScanFailed = "update_scan_failed";

    /// <summary>登记存在，但不是本程序解包出来的便携/绿色安装（例如 EXE/MSI 安装器装的）。未改动任何文件。</summary>
    public const string NotPortable = "update_not_portable";

    /// <summary>登记的目标目录或入口文件已不存在：没有可替换的“当前版本”。</summary>
    public const string TargetMissing = "update_target_missing";

    /// <summary>候选资产既不是便携归档、也不是被识别为便携可执行文件，不能走整目录替换。</summary>
    public const string UnsupportedAsset = "update_unsupported_asset";

    /// <summary>目标/暂存/备份路径未通过校验（非安装根目录直接子目录、重解析点、UNC、保留目录名等）。未改动任何文件。</summary>
    public const string UnsafePath = "update_unsafe_path";

    /// <summary>登记与请求描述的不是同一个应用。</summary>
    public const string IdentityMismatch = "update_identity_mismatch";

    /// <summary>版本比较结论为“候选更旧”：拒绝以降级冒充更新。</summary>
    public const string NotAnUpgrade = "update_not_an_upgrade";

    /// <summary>版本比较结论为“相同”。版本<em>无法比较</em>（Different）时不会返回此码，而是继续更新。</summary>
    public const string AlreadyCurrent = "update_already_current";

    public const string DownloadFailed = "update_download_failed";
    public const string ChecksumMismatch = "update_checksum_mismatch";
    public const string LengthMismatch = "update_length_mismatch";
    public const string InvalidDigest = "update_invalid_digest";
    public const string InvalidArchive = "update_invalid_archive";
    public const string ArchiveLimit = "update_archive_limit";
    public const string UnsafeArchive = "update_unsafe_archive";
    public const string ExecutableMissing = "update_executable_missing";
    public const string ExecutableInvalid = "update_executable_invalid";
    public const string ExecutableChoiceRequired = "update_executable_choice_required";
    public const string InvalidExecutableChoice = "update_invalid_executable_choice";

    /// <summary>journal 未能落盘：此时一个字节都还没动，直接放弃本次更新。</summary>
    public const string JournalFailed = "update_journal_failed";

    /// <summary>旧登记未能写回。文件已换回旧版本，但登记与文件不一致，journal 保留待恢复流程重试。</summary>
    public const string ReceiptRestoreFailed = "update_receipt_restore_failed";

    /// <summary>失败后未能还原：备份与 journal 都保留原处，下次启动的恢复流程会再试。绝不删除它们。</summary>
    public const string RollbackFailed = "update_rollback_failed";

    public const string RegistrationFailed = "update_registration_failed";
    public const string AccessDenied = "update_access_denied";
    public const string IoFailed = "update_io_failed";
    public const string Failed = "update_failed";

    /// <summary>journal 存在但内容不可信（损坏、schema 不符、路径不合规）。未删除该文件，也未改动任何目标目录。</summary>
    public const string RecoveryJournalInvalid = "update_recovery_journal_invalid";
    public const string RecoveryFailed = "update_recovery_failed";

    public const string RollbackNotAvailable = "rollback_not_available";
    public const string RollbackNotPortable = "rollback_not_portable";
    public const string RollbackInstallationNotFound = "rollback_installation_not_found";
    public const string RollbackUnsafePath = "rollback_unsafe_path";
    public const string RollbackRegistrationFailed = "rollback_registration_failed";
    public const string RollbackIncomplete = "rollback_incomplete";
}

/// <summary>
/// 便携（绿色/解包）安装的更新与回滚。
/// <para>
/// <b>只处理本程序自己解包出来的文件。</b>判据是登记原文：<see cref="StoreOwnedInstallation.OwnsExtractedFiles"/>。
/// EXE/MSI 等安装器安装的应用一律返回 <see cref="PortableUpgradeErrorCodes.NotPortable"/>：
/// 本服务不启动任何安装器、不读注册表、也不接管它们的卸载。
/// </para>
/// <para>
/// <b>更新序列</b>（任何一步失败都不会留下“半新半旧”的目录）：
/// 按安装 ID 重读登记 → 校验目标与根目录路径 → 版本比较 → 复用 PackageCacheService 下载、独立校验大小与 SHA-256
/// → 解包到安装根的<em>同级</em>暂存目录 → 校验入口可执行文件 → 写 journal 并落盘
/// → 现有目标目录整体改名为同级备份 → 新目录改名就位 → 用<em>同一个 InstallId</em>重新登记。
/// 备份开始之后的任何失败都会把备份换回原位并写回<em>旧登记</em>；journal 只在成功之后才删除。
/// </para>
/// <para>
/// <b>成功之后</b>：上一个版本作为“保留备份”留在安装根目录下，供用户显式回滚。
/// 同一安装只保留一份（下一次成功更新会先删掉更早的那一份，并在删除失败时如实报告可能残留）。
/// </para>
/// <para>
/// <b>本服务刻意不做的事</b>：不删除安装根目录之外的任何路径；不执行候选包里的任何程序；
/// 不做增量/差分更新；不迁移或合并用户数据（保留下来的只有上一版目录本身）；
/// 不在没有用户显式请求时自动回滚；不处理 EXE/MSI 安装器的更新与卸载。
/// </para>
/// </summary>
public sealed class PortableUpgradeService
{
    /// <summary>本进程内的便携更新互斥；拿不到立即返回 busy，不排队（避免用户以为卡住）。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private const int MaximumEntries = 10000;
    private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumMetadataBytes = 262144;
    private const int MaximumPathLength = 2048;
    private const int JournalSchemaVersion = 1;
    private const int RollbackRecordSchemaVersion = 1;

    private const string StagePrefix = ".githubstore-stage-";
    private const string BackupPrefix = ".githubstore-backup-";
    private const string ReplacedPrefix = ".githubstore-replaced-";
    private const string JournalDirectoryName = "upgrade-journal";
    private const string RollbackDirectoryName = "upgrade-rollback";

    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly IDownloadService _downloads;
    private readonly IManagedAppLauncher _launcher;
    private readonly PreferencesService _preferences;
    private readonly string _dataDirectory;
    private readonly Func<string> _root;
    private readonly PackageCacheService _packageCache;
    private readonly Func<PortableUpgradePhase, CancellationToken, Task>? _checkpoint;

    /// <param name="installRootProvider">
    /// 生效的安装根目录。默认取 <see cref="IManagedAppLauncher.InstallRoot"/>；
    /// 根目录为空字符串（该实现不管理安装根目录）时本服务拒绝执行任何更新。
    /// </param>
    /// <param name="checkpoint">
    /// 故障注入点，仅供测试在指定阶段制造失败或模拟崩溃。
    /// 它无法改变任何所有权判断（目标路径、备份路径、删除范围都不来自它），只能中断流程以验证还原路径。
    /// </param>
    public PortableUpgradeService(IDownloadService downloads, IManagedAppLauncher launcher, PreferencesService preferences,
        string dataDirectory, Func<string>? installRootProvider = null, PackageCacheService? packageCache = null,
        Func<PortableUpgradePhase, CancellationToken, Task>? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _downloads = downloads;
        _launcher = launcher;
        _preferences = preferences;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _root = installRootProvider ?? (() => launcher.InstallRoot);
        // 与安装流程共用同一个包缓存：同一资产已下载并校验过时不再联网。
        _packageCache = packageCache ?? new PackageCacheService(
            () => StorageLocations.ResolveCacheDirectory(preferences.Current, dataDirectory));
        _checkpoint = checkpoint;
    }

    /// <summary>
    /// 该安装的 journal 路径：<c>&lt;dataDirectory&gt;/upgrade-journal/&lt;sha256(installId)&gt;.json</c>。
    /// 安装 ID 本身不参与路径拼接，因此无法借它越界。该文件由本服务独占写入，调用方不得手写。
    /// </summary>
    public string GetJournalPath(string installId)
    {
        if (!ValidText(installId, 256))
            throw new ArgumentException("An install id of at most 256 characters is required.", nameof(installId));
        return Path.Combine(_dataDirectory, JournalDirectoryName, MetadataKey(installId) + ".json");
    }

    /// <summary>该安装是否有未完成的更新，需要在启动时执行 <see cref="RecoverAsync"/>。</summary>
    public bool HasPendingRecovery(string installId) => File.Exists(GetJournalPath(installId));

    /// <summary>
    /// 该安装是否保留了可回退的上一版本。界面据此决定是否显示回退入口；
    /// 这里只检查保留记录是否存在，不读取注册表、不执行任何命令，也不改动任何文件。
    /// </summary>
    public bool HasRetainedPreviousVersion(string installId)
    {
        if (!ValidText(installId, 256)) return false;
        return File.Exists(Path.Combine(_dataDirectory, RollbackDirectoryName, MetadataKey(installId) + ".json"));
    }

    /// <summary>
    /// 用候选发布替换一条便携安装。只有候选版本更高、或<em>版本无法比较</em>（Different，
    /// 由用户显式发起的更新）时才继续；候选更旧或完全相同时直接拒绝，不会以“更新”为名降级。
    /// </summary>
    public async Task<PortableUpgradeResult> UpdateAsync(PortableUpgradeRequest request,
        IProgress<PortableUpgradeProgress>? progress = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? chooseExecutable = null,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return new(false, PortableUpgradeErrorCodes.Cancelled);
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return new(false, PortableUpgradeErrorCodes.Busy);
        string? stage = null, target = null, root = null;
        var operationId = Guid.NewGuid().ToString("N");
        var backupStarted = false;
        PortableUpgradeJournal? journal = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            Report(progress, PortableUpgradePhase.Preparing);
            ValidateRequest(request);
            root = ResolveRoot();
            InstallRecord? receipt;
            try { receipt = await _launcher.GetAsync(request.InstallId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, PortableUpgradeErrorCodes.ScanFailed, request.InstallId); }
            // 读不到登记原文就无法证明文件所有权：既不更新，也不删除任何东西。
            if (receipt is null) return new(false, PortableUpgradeErrorCodes.InstallationNotFound, request.InstallId);
            if (receipt.AppId != request.App.Id) return new(false, PortableUpgradeErrorCodes.IdentityMismatch, request.InstallId);
            // 只有本程序自己解包的便携/绿色安装才允许整目录替换。
            if (!StoreOwnedInstallation.OwnsExtractedFiles(receipt))
                return new(false, PortableUpgradeErrorCodes.NotPortable, request.InstallId);
            target = ValidateTarget(receipt, root);
            if (ResolveExistingExecutable(receipt, target) is null || !Directory.Exists(target))
                return new(false, PortableUpgradeErrorCodes.TargetMissing, request.InstallId, target);

            var relation = VersionComparer.Relate(receipt.Version, request.Release.TagName);
            if (relation == VersionRelation.Older) return new(false, PortableUpgradeErrorCodes.NotAnUpgrade, request.InstallId, target);
            if (relation == VersionRelation.Same) return new(false, PortableUpgradeErrorCodes.AlreadyCurrent, request.InstallId, target);
            // relation == Different：没有任何证据说明候选不是新版。按用户显式发起的更新继续，
            // 但结论只记事实——界面不得把它说成“确认有新版”。
            await CheckpointAsync(PortableUpgradePhase.Preparing, ct).ConfigureAwait(false);

            var extension = Path.GetExtension(request.Asset.Name).ToLowerInvariant();
            var snapshot = _preferences.Current;
            // 缓存目录与受管目录必须互不包含：否则“清空下载缓存”可能删掉安装，反之亦然。
            await using var cacheLease = await _packageCache.AcquireAsync(ct).ConfigureAwait(false);
            var packageCache = cacheLease.Cache;
            var cacheRoot = SafePath(packageCache.Root);
            if (PathsOverlap(cacheRoot, target) || PathsOverlap(cacheRoot, root))
                return new(false, PortableUpgradeErrorCodes.UnsafePath, request.InstallId, target);
            Directory.CreateDirectory(cacheRoot);
            SafePath(cacheRoot);
            var package = packageCache.TryResolvePath(new Uri(request.Asset.DownloadUrl), request.Asset.Name);
            if (package is null) return new(false, PortableUpgradeErrorCodes.UnsafePath, request.InstallId, target);
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            SafePath(package);
            var cached = await packageCache.FindVerifiedAsync(new Uri(request.Asset.DownloadUrl), request.Asset.Name,
                request.Asset.Size > 0 ? request.Asset.Size : null, request.Asset.Digest, ct).ConfigureAwait(false);
            if (cached is not null) Report(progress, PortableUpgradePhase.Downloading, 100);
            else
            {
                await packageCache.PrepareDownloadAsync(new Uri(request.Asset.DownloadUrl), request.Asset.Name, ct).ConfigureAwait(false);
                var downloadRequest = new DownloadRequest
                {
                    Source = new Uri(request.Asset.DownloadUrl), TargetPath = package, FileName = request.Asset.Name,
                    ExpectedSize = request.Asset.Size > 0 ? request.Asset.Size : null, ExpectedSha256 = request.Asset.Digest,
                    Engine = snapshot.DownloadEngine, UseAccelerator = snapshot.UseAccelerator,
                };
                Report(progress, PortableUpgradePhase.Downloading);
                var downloadProgress = new InlineProgress<DownloadProgress>(
                    p => Report(progress, PortableUpgradePhase.Downloading, p.Total > 0 ? p.Fraction * 100 : null));
                var downloaded = _downloads is ConfiguredDownloadService configured
                    ? await configured.DownloadWithSnapshotAsync(downloadRequest, snapshot, downloadProgress, ct).ConfigureAwait(false)
                    : await _downloads.DownloadAsync(downloadRequest, downloadProgress, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!downloaded.Succeeded) return new(false, DownloadCode(downloaded.ErrorCode), request.InstallId, target);
                SafePath(package);
                if (!File.Exists(package) || !string.Equals(downloaded.OutputPath, package, StringComparison.OrdinalIgnoreCase))
                    return new(false, PortableUpgradeErrorCodes.DownloadFailed, request.InstallId, target);
            }
            var hash = await VerifyDownloadAsync(package, request.Asset, ct).ConfigureAwait(false);
            if (cached is null)
            {
                try
                {
                    await packageCache.RecordCompletedAsync(new Uri(request.Asset.DownloadUrl), request.Asset.Name,
                        request.Asset.Size > 0 ? request.Asset.Size : null, request.Asset.Digest, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Trace.WriteLine("Package retained; its cache verification record could not be saved."); }
            }
            await CheckpointAsync(PortableUpgradePhase.Downloading, ct).ConfigureAwait(false);

            stage = Path.Combine(root, StagePrefix + operationId);
            Directory.CreateDirectory(stage);
            SafePath(stage);
            if (extension == ".zip")
            {
                Report(progress, PortableUpgradePhase.Extracting);
                await ExtractAsync(package, stage, ct).ConfigureAwait(false);
            }
            else
            {
                // 便携单文件：只把这个已校验的文件复制进暂存目录，绝不执行它。
                await using var source = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var destination = new FileStream(Path.Combine(stage, request.Asset.Name),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            }
            await CheckpointAsync(PortableUpgradePhase.Extracting, ct).ConfigureAwait(false);

            Report(progress, PortableUpgradePhase.Verifying);
            var relative = await SelectExecutableAsync(stage, request, receipt, chooseExecutable, ct).ConfigureAwait(false);
            var stagedExecutable = SafePath(Path.Combine(stage, relative));
            if (!File.Exists(stagedExecutable) || !IsExecutable(stagedExecutable))
                return await FailAsync(PortableUpgradeErrorCodes.ExecutableInvalid).ConfigureAwait(false);
            await CheckpointAsync(PortableUpgradePhase.Verifying, ct).ConfigureAwait(false);

            // journal 必须在改动任何文件之前落盘：等到发现“正在更新”时，文件可能已经被换掉了。
            var displayName = await ResolveDisplayNameAsync(receipt, request, ct).ConfigureAwait(false);
            journal = new PortableUpgradeJournal
            {
                SchemaVersion = JournalSchemaVersion, InstallId = request.InstallId, TargetDir = target,
                BackupDir = Path.Combine(root, BackupPrefix + operationId), StageDir = stage,
                StartedAt = DateTimeOffset.UtcNow, PreviousReceipt = receipt, PreviousDisplayName = displayName,
            };
            await WriteMetadataAsync(GetJournalPath(request.InstallId), journal, ct).ConfigureAwait(false);
            await CheckpointAsync(PortableUpgradePhase.Journaling, ct).ConfigureAwait(false);

            Report(progress, PortableUpgradePhase.BackingUp);
            backupStarted = true;
            Directory.Move(target, journal.BackupDir);
            await CheckpointAsync(PortableUpgradePhase.BackingUp, ct).ConfigureAwait(false);
            Report(progress, PortableUpgradePhase.Swapping);
            Directory.Move(stage, target);
            stage = null;
            await CheckpointAsync(PortableUpgradePhase.Swapping, ct).ConfigureAwait(false);

            var executable = SafePath(Path.Combine(target, relative));
            // 就位后复验一次：换目录这一步本身可能失败在文件系统层面。
            if (!Directory.Exists(target) || !File.Exists(executable) || !IsExecutable(executable))
                return await FailAsync(PortableUpgradeErrorCodes.ExecutableInvalid).ConfigureAwait(false);

            Report(progress, PortableUpgradePhase.Registering);
            // 检查点刻意在写登记<em>之前</em>：此处被打断时磁盘上是“新文件 + 旧登记 + 未提交 journal”，
            // 三者互相自洽，恢复流程可以把它们一起换回旧版本。
            await CheckpointAsync(PortableUpgradePhase.Registering, ct).ConfigureAwait(false);
            try
            {
                await _launcher.RegisterCompletedInstallAsync(BuildUpdatedReceipt(receipt, request, hash, executable),
                    displayName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return await FailAsync(PortableUpgradeErrorCodes.RegistrationFailed).ConfigureAwait(false); }

            // 登记写入成功之后才算“已提交”：先把提交标记落盘，成功后才不再还原。
            // 顺序不能颠倒——若在写登记前就提交，崩溃会留下“新文件 + 已提交”这种无法自愈的状态；
            // 而登记写完之后、提交标记落盘之前崩溃，仍会还原成“旧文件 + 旧登记”，同样自洽。
            // 提交标记写失败只是让崩溃后被还原成旧版本，也是保守结果，因此不改判本次更新失败。
            var committed = false;
            try
            {
                await WriteMetadataAsync(GetJournalPath(request.InstallId), journal with { Completed = true },
                    CancellationToken.None).ConfigureAwait(false);
                committed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.WriteLine(ex); }

            // 撤销已不可能：剩下的只是清理与“保留上一版”。
            var filesMayRemain = false;
            try
            {
                var previous = await ReadMetadataAsync<PortableRollbackRecord>(
                    GetRollbackRecordPath(request.InstallId), ct).ConfigureAwait(false);
                await WriteMetadataAsync(GetRollbackRecordPath(request.InstallId), new PortableRollbackRecord
                {
                    SchemaVersion = RollbackRecordSchemaVersion, InstallId = request.InstallId, TargetDir = target,
                    BackupDir = journal.BackupDir, RetainedAt = DateTimeOffset.UtcNow, PreviousReceipt = receipt,
                    PreviousDisplayName = displayName, ReplacedVersion = request.Release.TagName,
                }, CancellationToken.None).ConfigureAwait(false);
                // 同一安装只保留一份上一版；更早的那一份在这里删除，删不掉就如实报告可能残留。
                if (previous is not null && !SamePath(previous.BackupDir, journal.BackupDir)
                    && !TryDeleteOwned(previous.BackupDir, BackupPrefix, root)) filesMayRemain = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 保留记录写不成就无法提供回滚：如实标记，但本次更新本身已经成功，不能改判为失败。
                Trace.WriteLine(ex);
                filesMayRemain = true;
            }
            await DeleteMetadataAsync(GetJournalPath(request.InstallId), CancellationToken.None).ConfigureAwait(false);
            Report(progress, PortableUpgradePhase.Completed, 100);
            return new(true, InstallId: request.InstallId, TargetDirectory: target, ExecutablePath: executable,
                DisplayVersion: request.Release.TagName,
                // journal 没能删掉（提交标记已落盘）时不算残留文件，只是留了一份可忽略的元数据；
                // 真正可能残留的是删不掉的更早备份。
                FilesMayRemain: filesMayRemain || !committed);
        }
        catch (PortableUpgradeSimulatedCrashException)
        {
            // 故障注入：模拟进程被强杀。此处<em>刻意不做任何还原</em>，
            // 把“备份还在、目标目录已不在、journal 还在”的现场留给启动恢复流程验证。
            throw;
        }
        catch (OperationCanceledException) { return await FailAsync(PortableUpgradeErrorCodes.Cancelled).ConfigureAwait(false); }
        catch (UpgradeRequestException ex) { return await FailAsync(ex.Code).ConfigureAwait(false); }
        catch (UpgradePathException) { return await FailAsync(PortableUpgradeErrorCodes.UnsafePath).ConfigureAwait(false); }
        catch (InvalidDataException) { return await FailAsync(PortableUpgradeErrorCodes.InvalidArchive).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) { return await FailAsync(PortableUpgradeErrorCodes.AccessDenied).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return await FailAsync(PortableUpgradeErrorCodes.IoFailed).ConfigureAwait(false); }
        catch (Exception) { return await FailAsync(PortableUpgradeErrorCodes.Failed).ConfigureAwait(false); }
        finally
        {
            // 只清理本进程自己创建、且位于安装根目录之下的暂存目录。
            // 备份与 journal 一律交给还原逻辑处置：在这里删掉它们就等于毁掉用户的上一个可用版本。
            if (stage is not null && root is not null) TryDeleteOwned(stage, StagePrefix, root);
            Gate.Release();
        }

        /// <summary>
        /// 失败收尾。返回码描述<em>最严重</em>的既成事实：还原成功就报原始失败原因并标记
        /// <c>FilesRestored</c>；还原也失败则报 <see cref="PortableUpgradeErrorCodes.RollbackFailed"/>——
        /// 此时用户的安装可能已被破坏，绝不能只报一句“io 失败”掩盖这一点。
        /// 还原失败时备份与 journal 都留在原处，交由启动恢复流程重试；这里绝不删除它们。
        /// </summary>
        async Task<PortableUpgradeResult> FailAsync(string code)
        {
            var restored = false;
            if (backupStarted && journal is not null && root is not null)
            {
                var outcome = await RestorePreviousAsync(journal, root, reRegister: true, CancellationToken.None)
                    .ConfigureAwait(false);
                restored = outcome.Restored && outcome.ErrorCode is null;
            }
            else if (journal is not null)
            {
                // 目标目录还没被改动过，没有任何东西需要换回：直接撤掉 journal。
                await DeleteMetadataAsync(GetJournalPath(request.InstallId), CancellationToken.None).ConfigureAwait(false);
            }
            var filesMayRemain = backupStarted && !restored;
            return new(false, backupStarted && !restored ? PortableUpgradeErrorCodes.RollbackFailed : code,
                request.InstallId, target, FilesRestored: restored, FilesMayRemain: filesMayRemain);
        }
    }

    /// <summary>
    /// 启动恢复：把每一条遗留 journal 对应的备份换回原位，并写回旧登记。可在应用启动时直接调用。
    /// 没有遗留时返回全零计数；任何单条失败都不影响其它条目，也不会删除无法确认的路径。
    /// 与更新/回滚共用同一把进程内互斥锁。
    /// </summary>
    public async Task<PortableRecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        var directory = Path.Combine(_dataDirectory, JournalDirectoryName);
        if (!Directory.Exists(directory)) return PortableRecoveryResult.Nothing;
        if (!await Gate.WaitAsync(0).ConfigureAwait(false))
            return new(0, 0, 0, 0, [PortableUpgradeErrorCodes.Busy]);
        try
        {
            string[] entries;
            try { entries = Directory.GetFiles(directory, "*.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return new(1, 0, 0, 1, [PortableUpgradeErrorCodes.RecoveryFailed]); }
            var examined = 0;
            var restored = 0;
            var cleaned = 0;
            var failed = 0;
            var codes = new List<string>();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                examined++;
                PortableUpgradeJournal? journal;
                try { journal = await ReadMetadataAsync<PortableUpgradeJournal>(entry, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { failed++; codes.Add(PortableUpgradeErrorCodes.RecoveryJournalInvalid); continue; }
                // journal 内容必须自证：安装 ID 合法，且它确实就是按该 ID 推导出来的那一个文件。
                if (journal is null || journal.SchemaVersion != JournalSchemaVersion || !ValidText(journal.InstallId, 256)
                    || !string.Equals(GetJournalPath(journal.InstallId), entry, StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                    codes.Add(PortableUpgradeErrorCodes.RecoveryJournalInvalid);
                    continue;
                }
                string root;
                try { root = ResolveRoot(); }
                catch (Exception) { failed++; codes.Add(PortableUpgradeErrorCodes.UnsafePath); continue; }
                var outcome = await RestorePreviousAsync(journal, root, reRegister: true, ct).ConfigureAwait(false);
                if (outcome.ErrorCode is not null) { failed++; codes.Add(outcome.ErrorCode); continue; }
                if (outcome.Restored) restored++; else cleaned++;
            }
            return new PortableRecoveryResult(examined, restored, cleaned, failed, codes);
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// 用户显式触发的回滚：把保留下来的上一个版本换回原位，并用同一个 InstallId 写回旧登记。
    /// 只有确实存在该安装的保留备份时才可能成功；没有备份一律
    /// <see cref="PortableUpgradeErrorCodes.RollbackNotAvailable"/>，绝不猜一个版本出来。
    /// 若该安装有未完成的 journal（上次更新被打断），先执行那次还原——那本身就是回滚。
    /// </summary>
    public async Task<PortableRollbackResult> RollbackAsync(string installId, CancellationToken ct = default)
    {
        if (!ValidText(installId, 256)) return new(false, PortableUpgradeErrorCodes.RollbackInstallationNotFound);
        if (ct.IsCancellationRequested) return new(false, PortableUpgradeErrorCodes.Cancelled);
        if (!await Gate.WaitAsync(0).ConfigureAwait(false)) return new(false, PortableUpgradeErrorCodes.Busy);
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = ResolveRoot();
            PortableUpgradeJournal? pending;
            try { pending = await ReadMetadataAsync<PortableUpgradeJournal>(GetJournalPath(installId), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, PortableUpgradeErrorCodes.RecoveryJournalInvalid, installId); }
            if (pending is not null)
            {
                if (pending.SchemaVersion != JournalSchemaVersion || !SameText(pending.InstallId, installId))
                    return new(false, PortableUpgradeErrorCodes.RecoveryJournalInvalid, installId);
                var recovery = await RestorePreviousAsync(pending, root, reRegister: true, ct).ConfigureAwait(false);
                if (recovery.ErrorCode is not null)
                    return new(false, recovery.ErrorCode, installId, FilesMayRemain: recovery.FilesMayRemain);
                // 未完成的 journal：这次还原本身就是回滚，到此结束。
                if (recovery.Restored)
                {
                    var restoredReceipt = await _launcher.GetAsync(installId, ct).ConfigureAwait(false);
                    return new(true, InstallId: installId, TargetDirectory: pending.TargetDir,
                        ExecutablePath: restoredReceipt?.ExePath, DisplayVersion: restoredReceipt?.DisplayVersion,
                        FilesMayRemain: recovery.FilesMayRemain);
                }
                // 已提交的 journal 只被清掉了：文件与登记都是新版本，继续走下面“保留备份”的正常路径。
                if (!pending.Completed) return new(false, PortableUpgradeErrorCodes.RollbackNotAvailable, installId);
            }

            InstallRecord? receipt;
            try { receipt = await _launcher.GetAsync(installId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, PortableUpgradeErrorCodes.ScanFailed, installId); }
            if (receipt is null) return new(false, PortableUpgradeErrorCodes.RollbackInstallationNotFound, installId);
            if (!StoreOwnedInstallation.OwnsExtractedFiles(receipt))
                return new(false, PortableUpgradeErrorCodes.RollbackNotPortable, installId);

            PortableRollbackRecord? record;
            try { record = await ReadMetadataAsync<PortableRollbackRecord>(GetRollbackRecordPath(installId), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return new(false, PortableUpgradeErrorCodes.RollbackNotAvailable, installId); }
            // 保留记录必须自证：schema、安装 ID，以及它记录的旧登记确实是这一条安装。
            if (record is null || record.SchemaVersion != RollbackRecordSchemaVersion || !ValidText(record.InstallId, 256)
                || !SameText(record.InstallId, installId) || record.PreviousReceipt.InstallId != installId
                || record.PreviousReceipt.AppId != receipt.AppId)
                return new(false, PortableUpgradeErrorCodes.RollbackNotAvailable, installId);

            var target = ValidateOwnedChild(record.TargetDir, root, null);
            var backup = ValidateOwnedChild(record.BackupDir, root, BackupPrefix);
            // 记录里的三条路径都要逐项通过校验；任何一条不过关就什么都不做。
            if (target is null || backup is null || !SamePath(target, record.TargetDir))
                return new(false, PortableUpgradeErrorCodes.RollbackUnsafePath, installId);
            if (!Directory.Exists(target) || !Directory.Exists(backup))
                return new(false, PortableUpgradeErrorCodes.RollbackNotAvailable, installId);
            ct.ThrowIfCancellationRequested();

            var outcome = await RestorePreviousAsync(new PortableUpgradeJournal
            {
                SchemaVersion = JournalSchemaVersion, InstallId = installId, TargetDir = target, BackupDir = backup,
                StageDir = Path.Combine(root, StagePrefix + Guid.NewGuid().ToString("N")),
                StartedAt = DateTimeOffset.UtcNow, PreviousReceipt = record.PreviousReceipt,
                PreviousDisplayName = record.PreviousDisplayName,
            }, root, reRegister: true, ct).ConfigureAwait(false);
            if (outcome.ErrorCode is not null)
                return new(false, outcome.ErrorCode, installId, FilesMayRemain: outcome.FilesMayRemain);
            if (!outcome.Restored) return new(false, PortableUpgradeErrorCodes.RollbackNotAvailable, installId);
            await DeleteMetadataAsync(GetRollbackRecordPath(installId), CancellationToken.None).ConfigureAwait(false);
            var current = await _launcher.GetAsync(installId, ct).ConfigureAwait(false);
            return new(true, InstallId: installId, TargetDirectory: target, ExecutablePath: current?.ExePath,
                DisplayVersion: current?.DisplayVersion, FilesMayRemain: outcome.FilesMayRemain);
        }
        catch (OperationCanceledException) { return new(false, PortableUpgradeErrorCodes.Cancelled, installId); }
        catch (UpgradePathException) { return new(false, PortableUpgradeErrorCodes.RollbackUnsafePath, installId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(false, PortableUpgradeErrorCodes.RollbackIncomplete, installId); }
        catch (Exception) { return new(false, PortableUpgradeErrorCodes.RollbackIncomplete, installId); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// 把一条 journal 描述的备份换回原位，并按需写回旧登记。
    /// 它<em>只</em>改动 journal 里记录的受管路径，且每条路径都必须先证明是安装根目录的直接子目录。
    /// <paramref name="ct"/> 只在进入时检查一次：一旦开始替换就必须走完，
    /// 否则会留下半新半旧的目录——那比“多花几百毫秒”糟糕得多。
    /// </summary>
    private async Task<RestoreOutcome> RestorePreviousAsync(PortableUpgradeJournal journal, string root,
        bool reRegister, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 已提交的 journal 只是没删掉的元数据：文件与登记都已是新版本，绝不能把它“还原”回旧版本。
        if (journal.Completed)
        {
            await DeleteMetadataAsync(GetJournalPath(journal.InstallId), CancellationToken.None).ConfigureAwait(false);
            return new(false, false, null);
        }
        var target = ValidateOwnedChild(journal.TargetDir, root, null);
        var backup = ValidateOwnedChild(journal.BackupDir, root, BackupPrefix);
        var stage = ValidateOwnedChild(journal.StageDir, root, StagePrefix);
        // 备份路径必须与记录完全一致：磁盘上允许不存在（说明还没走到那一步），但不允许指向别处。
        if (target is null || backup is null || !SamePath(target, journal.TargetDir) || !SamePath(backup, journal.BackupDir))
            return new(false, false, PortableUpgradeErrorCodes.RollbackUnsafePath);
        if (stage is not null) TryDeleteOwned(stage, StagePrefix, root);

        var filesMayRemain = false;
        var restored = false;
        if (Directory.Exists(backup))
        {
            // 把当前（可能是新版本）目录挪到一边而不是直接删除：换回失败时还要把它放回去。
            string? replaced = null;
            if (Directory.Exists(target))
            {
                replaced = Path.Combine(root, ReplacedPrefix + Guid.NewGuid().ToString("N"));
                try { Directory.Move(target, replaced); }
                catch (Exception ex) { Trace.WriteLine(ex); return new(false, false, PortableUpgradeErrorCodes.RollbackFailed); }
            }
            try { Directory.Move(backup, target); }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                if (replaced is not null && !Directory.Exists(target))
                {
                    try { Directory.Move(replaced, target); }
                    catch (Exception inner) { Trace.WriteLine(inner); }
                }
                return new(false, false, PortableUpgradeErrorCodes.RollbackFailed);
            }
            if (replaced is not null && !TryDeleteOwned(replaced, ReplacedPrefix, root)) filesMayRemain = true;
            restored = true;
        }

        if (reRegister)
        {
            var displayName = ValidText(journal.PreviousDisplayName, 200) ? journal.PreviousDisplayName : "Application";
            try
            {
                await _launcher.RegisterCompletedInstallAsync(journal.PreviousReceipt, displayName, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 文件已是旧版本，但登记没写回：保留 journal 供下次启动重试，绝不谎报已还原。
                Trace.WriteLine(ex);
                return new(restored, filesMayRemain, PortableUpgradeErrorCodes.ReceiptRestoreFailed);
            }
        }
        await DeleteMetadataAsync(GetJournalPath(journal.InstallId), CancellationToken.None).ConfigureAwait(false);
        return new(restored, filesMayRemain, null);
    }

    private sealed record RestoreOutcome(bool Restored, bool FilesMayRemain, string? ErrorCode);

    private static void ValidateRequest(PortableUpgradeRequest request)
    {
        if (request is null) throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidRequest);
        if (request.App is not { } app || request.Release is not { } release || request.Asset is not { } asset
            || !ValidText(request.InstallId, 256)
            || app.Id.SourceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(app.Id.PackageId.Value)
            || app.SourceRevision <= 0 || string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName.Length > 200
            || !Regex.IsMatch(app.Owner ?? "", "^[A-Za-z0-9-]+$") || !Regex.IsMatch(app.Repo ?? "", "^[A-Za-z0-9_.-]+$")
            || app.Repo is "." or ".." || release.AppId != app.Id || release.Draft
            || string.IsNullOrWhiteSpace(release.Key) || release.Key.Length > 256
            || string.IsNullOrWhiteSpace(release.TagName) || release.TagName.Length > 256
            || asset.ReleaseKey != release.Key || string.IsNullOrWhiteSpace(asset.InstallerId) || asset.InstallerId.Length > 256
            || release.Assets.Count(a => a == asset) != 1 || release.Assets.Count(a => a.InstallerId == asset.InstallerId) != 1
            || !SafeSegment(asset.Name) || asset.Size < 0
            || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidRequest);
        if (!string.IsNullOrWhiteSpace(asset.Digest)) _ = NormalizeDigest(asset.Digest);
        var extension = Path.GetExtension(asset.Name).ToLowerInvariant();
        var architecture = RuntimeInformation.OSArchitecture switch
        { Architecture.Arm64 => Arch.Arm64, Architecture.X86 => Arch.X86, _ => Arch.Amd64 };
        if (extension == ".zip")
        {
            // 与安装流程完全同一套资产选择策略：linux/macos/符号包等一律不进入便携更新。
            var probe = asset with
            { Name = Path.GetFileNameWithoutExtension(asset.Name) + "-windows-portable" + extension, Installer = InstallerKind.Portable };
            if (DefaultAssetSelectionPolicy.Select(new[] { probe }, architecture, $"{app.Owner}/{app.Repo}") is null)
                throw new UpgradeRequestException(PortableUpgradeErrorCodes.UnsupportedAsset);
        }
        else if (extension == ".exe")
        {
            // 只要不是明确的便携单文件产物，就不把 .exe 当成可解包的更新包：
            // 那更可能是安装器，而本服务绝不执行候选包里的任何程序。
            if (asset.Kind != AssetKind.PortableExe
                && !DefaultAssetSelectionPolicy.IsRecognizedPortable(asset, $"{app.Owner}/{app.Repo}"))
                throw new UpgradeRequestException(PortableUpgradeErrorCodes.UnsupportedAsset);
        }
        else throw new UpgradeRequestException(PortableUpgradeErrorCodes.UnsupportedAsset);
        if (request.ExecutableRelativePath is { } entry)
        {
            if (entry.Replace('\\', '/').Split('/').Any(part => !SafeSegment(part))
                || !Path.GetExtension(entry).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidRequest);
        }
    }

    private static async Task<string> VerifyDownloadAsync(string package, Asset asset, CancellationToken ct)
    {
        await using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        // 不信任下载器的结论：长度与 SHA-256 在这里独立重算一遍。
        if (stream.Length == 0 || asset.Size > 0 && stream.Length != asset.Size)
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.LengthMismatch);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(asset.Digest) && !string.Equals(hash, NormalizeDigest(asset.Digest), StringComparison.OrdinalIgnoreCase))
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.ChecksumMismatch);
        return hash;
    }

    private static string NormalizeDigest(string value)
    {
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        if (!Regex.IsMatch(value, "^[a-fA-F0-9]{64}$")) throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidDigest);
        return value;
    }

    private static async Task ExtractAsync(string package, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > MaximumEntries) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ArchiveLimit);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var parts = (directory ? name[..^1] : name).Split('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            // 只接受普通文件/目录：链接、设备、FIFO、重复路径一律拒绝，避免解包写出暂存目录之外。
            if (parts.Any(p => !SafeSegment(p)) || mode is 0xa000 or 0x6000 or 0x2000 or 0x1000 or 0xc000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || !paths.Add(string.Join('/', parts))) throw new UpgradeRequestException(PortableUpgradeErrorCodes.UnsafeArchive);
            if (entry.Length < 0 || entry.Length > MaximumExpandedBytes - total)
                throw new UpgradeRequestException(PortableUpgradeErrorCodes.ArchiveLimit);
            total += entry.Length;
            var destination = SafePath(Path.Combine(stage, Path.Combine(parts)));
            if (directory) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath(destination);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ArchiveLimit);
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidArchive);
        }
    }

    /// <summary>
    /// 选择新版本的主程序。优先级：显式配置的入口 → 沿用旧版本的入口（同相对路径同名）→ 目录内候选启发式。
    /// 沿用旧入口优先于启发式，因为“更新”的默认期望是替换同一个程序，而不是换一个入口。
    /// </summary>
    private static async Task<string> SelectExecutableAsync(string stage, PortableUpgradeRequest request,
        InstallRecord receipt, Func<IReadOnlyList<string>, CancellationToken, Task<string?>>? choose, CancellationToken ct)
    {
        if (request.ExecutableRelativePath is { } configured) return ValidateConfiguredExecutable(stage, configured, ct);
        if (DeriveRelativeExecutable(receipt) is { } derived)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = SafePath(Path.Combine(stage, derived));
            if (File.Exists(candidate) && IsExecutable(candidate)) return derived;
        }
        var candidates = new List<string>();
        var pending = new Stack<string>();
        pending.Push(SafePath(stage));
        var visited = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++visited > MaximumEntries) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ArchiveLimit);
                SafePath(item);
                if (Directory.Exists(item)) { pending.Push(item); continue; }
                var relative = Path.GetRelativePath(stage, item);
                if (Path.GetExtension(item).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(relative, @"(?i)(^|[\\/_.\- ])(unins\w*|setup|install(?:er)?|update(?:r)?|crash\w*|helper|uninstall\w*)(?=$|[\\/_.\- ])")
                    && IsExecutable(item)) candidates.Add(relative);
            }
        }
        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ExecutableMissing);
        if (candidates.Count == 1) return candidates[0];
        static string Key(string value) => Regex.Replace(value, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        var strong = candidates.Where(x => Key(Path.GetFileNameWithoutExtension(x)) == Key(request.App.DisplayName)
            || Key(Path.GetFileNameWithoutExtension(x)) == Key(request.App.Repo)).ToArray();
        if (strong.Length == 1) return strong[0];
        if (choose is null) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ExecutableChoiceRequired);
        var selected = await choose(candidates.AsReadOnly(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (selected is null) throw new UpgradeRequestException(PortableUpgradeErrorCodes.Cancelled);
        if (!candidates.Contains(selected, StringComparer.Ordinal))
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidExecutableChoice);
        return selected;
    }

    private static string ValidateConfiguredExecutable(string stage, string configured, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parts = configured.Replace('\\', '/').Split('/');
        if (parts.Any(part => !SafeSegment(part))
            || !Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.InvalidRequest);
        var path = SafePath(Path.Combine(stage, Path.Combine(parts)));
        if (!File.Exists(path)) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ExecutableMissing);
        if (!IsExecutable(path)) throw new UpgradeRequestException(PortableUpgradeErrorCodes.ExecutableInvalid);
        return Path.Combine(parts);
    }

    /// <summary>旧登记里入口相对安装目录的路径；越界、非 .exe 或字段缺失时返回 null（退回启发式）。</summary>
    private static string? DeriveRelativeExecutable(InstallRecord receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.ExePath) || string.IsNullOrWhiteSpace(receipt.TargetDir)) return null;
        string relative;
        try { relative = Path.GetRelativePath(receipt.TargetDir, receipt.ExePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(part => !SafeSegment(part))) return null;
        if (!Path.GetExtension(relative).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(parts);
    }

    /// <summary>
    /// 登记的入口文件必须确实是“目标目录之内的 .exe”。返回 null 表示这条安装已不可更新，
    /// 调用方按“目标缺失”处理，而不是去猜一个别处的程序。
    /// </summary>
    private static string? ResolveExistingExecutable(InstallRecord receipt, string? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(receipt.ExePath) || !Path.IsPathFullyQualified(receipt.ExePath)) return null;
        string executable;
        try { executable = SafePath(receipt.ExePath); }
        catch (UpgradePathException) { return null; }
        if (!executable.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(executable) || !IsExecutable(executable)) return null;
        return executable;
    }

    /// <summary>目标目录必须是安装根目录的直接子目录，且不得带本服务的保留目录名前缀。</summary>
    private static string ValidateTarget(InstallRecord receipt, string root) =>
        ValidateOwnedChild(receipt.TargetDir, root, null) ?? throw new UpgradePathException();

    /// <summary>
    /// 校验一条受管路径：绝对、非 UNC、无重解析点、段名合法，且是安装根目录的直接子目录。
    /// <paramref name="requiredPrefix"/> 非空时还要求目录名带该保留前缀（用于暂存/备份/中间目录）；
    /// 为空时反过来拒绝带任何保留前缀的名字——保留目录永远不会被当成用户的安装目标。
    /// 返回 null 表示不通过，调用方必须放弃，而不是“尽力而为”。
    /// </summary>
    private static string? ValidateOwnedChild(string? path, string root, string? requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.Any(char.IsControl)) return null;
        string full;
        try { full = SafePath(path); }
        catch (UpgradePathException) { return null; }
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full) ?? string.Empty);
        if (!string.Equals(parent, trimmedRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (SamePath(full, root)) return null;
        var name = Path.GetFileName(full);
        if (!SafeSegment(name)) return null;
        if (requiredPrefix is not null)
        {
            if (!name.StartsWith(requiredPrefix, StringComparison.Ordinal)) return null;
        }
        else if (name.StartsWith(StagePrefix, StringComparison.Ordinal)
            || name.StartsWith(BackupPrefix, StringComparison.Ordinal)
            || name.StartsWith(ReplacedPrefix, StringComparison.Ordinal)) return null;
        return full;
    }

    /// <summary>
    /// 由旧登记派生新登记。InstallId、AppId、SourceId、PackageId、InstalledAt 一律保持原值：
    /// 更新不是一次新的安装，历史与去重都依赖这些字段不变。
    /// </summary>
    private static InstallRecord BuildUpdatedReceipt(InstallRecord receipt, PortableUpgradeRequest request,
        string hash, string executable) => receipt with
    {
        Source = receipt.Source with
        {
            SourceRevision = request.App.SourceRevision, InstallerId = request.Asset.InstallerId,
            ArtifactUrl = new Uri(request.Asset.DownloadUrl), ArtifactSha256 = hash,
        },
        ReleaseKey = request.Release.Key,
        Version = new Version { Raw = request.Release.TagName, Scheme = VersionScheme.Opaque },
        DisplayVersion = request.Release.TagName,
        ExePath = executable,
        Fingerprint = (receipt.Fingerprint ?? new Fingerprint()) with { AppId = receipt.AppId, AssetSha256 = hash },
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<string> ResolveDisplayNameAsync(InstallRecord receipt, PortableUpgradeRequest request, CancellationToken ct)
    {
        try
        {
            var items = await _launcher.ListAsync(ct).ConfigureAwait(false);
            var existing = items.FirstOrDefault(item => string.Equals(item.InstallId, receipt.InstallId, StringComparison.Ordinal));
            if (existing is not null && ValidText(existing.DisplayName, 200)) return existing.DisplayName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 名字读不出来不能阻断更新：退回候选发布所属应用的显示名。
            Trace.WriteLine(ex);
        }
        return request.App.DisplayName;
    }

    private string GetRollbackRecordPath(string installId) =>
        Path.Combine(_dataDirectory, RollbackDirectoryName, MetadataKey(installId) + ".json");

    /// <summary>journal/保留记录的成员名只用安装 ID 的 SHA-256：安装 ID 本身不参与路径拼接。</summary>
    private static string MetadataKey(string installId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installId))).ToLowerInvariant();

    private static async Task WriteMetadataAsync<T>(string path, T value, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(value, MetadataJson); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new UpgradeRequestException(PortableUpgradeErrorCodes.JournalFailed); }
        if (bytes.Length > MaximumMetadataBytes) throw new UpgradeRequestException(PortableUpgradeErrorCodes.JournalFailed);
        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path)!;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            // WriteThrough：journal 必须在返回前真正落盘，否则“崩溃可恢复”就只是承诺。
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new UpgradeRequestException(PortableUpgradeErrorCodes.JournalFailed); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<T?> ReadMetadataAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return null;
        ct.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumMetadataBytes)
            throw new UpgradeRequestException(PortableUpgradeErrorCodes.RecoveryJournalInvalid);
        T? value;
        try { value = await JsonSerializer.DeserializeAsync<T>(stream, MetadataJson, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { throw new UpgradeRequestException(PortableUpgradeErrorCodes.RecoveryJournalInvalid); }
        return Restore(value);
    }

    /// <summary>
    /// 把 JSON 往返读回的记录还原成与写入前一致的值语义。
    /// <para>
    /// 必要性：<see cref="InstallRecord.Shortcuts"/> 与 <see cref="Version.Parts"/> 声明为
    /// <c>IReadOnlyList&lt;T&gt;</c>，反序列化默认给的是 <c>List&lt;T&gt;</c>；而 record 的相等性按成员比较，
    /// <c>List&lt;T&gt;</c> 与 <c>T[]</c> 并不相等。若不归一化，写回登记的“旧登记”会在结构上与原登记不同——
    /// 功能上未必立刻出错，但这种静默的保真度损失会让“原样还原”这句话站不住脚。
    /// </para>
    /// </summary>
    private static T? Restore<T>(T? value) where T : class => value switch
    {
        PortableUpgradeJournal journal => (T)(object)(journal with { PreviousReceipt = Normalize(journal.PreviousReceipt) }),
        PortableRollbackRecord record => (T)(object)(record with { PreviousReceipt = Normalize(record.PreviousReceipt) }),
        _ => value,
    };

    private static InstallRecord Normalize(InstallRecord receipt) => receipt with
    {
        Shortcuts = receipt.Shortcuts?.ToArray() ?? [],
        Version = receipt.Version with { Parts = receipt.Version.Parts?.ToArray() ?? [] },
    };

    /// <summary>删除 journal/保留记录本身。它位于应用数据目录，不属于安装根目录，失败只记 Trace。</summary>
    private static async Task DeleteMetadataAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path)) File.Delete(path);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { Trace.WriteLine(ex); }
    }

    /// <summary>
    /// 本服务唯一的删除入口：只删除“位于安装根目录之下、且目录名带指定保留前缀”的目录。
    /// 任何一项不满足都返回 false 且一个文件都不删。
    /// </summary>
    private static bool TryDeleteOwned(string path, string prefix, string root)
    {
        try
        {
            var owned = ValidateOwnedChild(path, root, prefix);
            if (owned is null) return false;
            if (!Directory.Exists(owned)) return true;
            DeleteTree(owned);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 删除失败（含 TOCTOU 发现的链接）只记 Trace 并如实返回 false，由调用方报告可能残留。
            Trace.WriteLine(ex);
            return false;
        }
    }

    private static void DeleteTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            // 预检之后才出现的重解析点（TOCTOU）立即失败，绝不穿透链接删除目录之外的文件。
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new UpgradePathException();
            if (Directory.Exists(entry)) DeleteTree(entry); else File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }

    private string ResolveRoot()
    {
        var raw = _root();
        // 空的安装根目录意味着该实现不管理安装根目录，因此无法证明任何路径归本程序所有。
        if (string.IsNullOrWhiteSpace(raw)) throw new UpgradePathException();
        return SafePath(raw);
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)) throw new UpgradePathException();
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var rawRoot = Path.GetPathRoot(path)!;
        if (path[rawRoot.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Length > 0 && !SafeSegment(p)))
            throw new UpgradePathException();
        if (full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Length > 0 && !SafeSegment(p)))
            throw new UpgradePathException();
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UpgradePathException();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static bool SafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..")
        && !value.EndsWith('.') && !value.EndsWith(' ') && value.Length <= 255
        && !Regex.IsMatch(value, "[<>:\"/\\\\|?*\\x00-\\x1f]")
        && !Regex.IsMatch(value.Split('.')[0].TrimEnd(' '), "^(CON|PRN|AUX|NUL|CLOCK\\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase);

    /// <summary>
    /// 只读文件头判定 Windows 可执行文件，与安装流程口径一致：
    /// 不执行任何内容，不足以判定时一律返回 false。
    /// </summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            SafePath(path);
            using var stream = File.OpenRead(path);
            if (stream.Length < 64 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
            stream.Position = 60;
            Span<byte> offsetBytes = stackalloc byte[4];
            if (stream.Read(offsetBytes) != 4) return false;
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(offsetBytes);
            if (offset < 64 || offset > stream.Length - 24) return false;
            stream.Position = offset;
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != 24 || header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0) return false;
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
            return (flags & 2) != 0 && (flags & 0x2000) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UpgradePathException)
        { return false; }
    }

    private static bool ValidText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && text == text.Trim() && !text.Any(char.IsControl);

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool SameText(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    private static string DownloadCode(string? code) => code switch
    {
        DownloadErrorCodes.Cancelled => PortableUpgradeErrorCodes.Cancelled,
        DownloadErrorCodes.ChecksumMismatch => PortableUpgradeErrorCodes.ChecksumMismatch,
        DownloadErrorCodes.LengthMismatch => PortableUpgradeErrorCodes.LengthMismatch,
        DownloadErrorCodes.InvalidVerification => PortableUpgradeErrorCodes.InvalidDigest,
        _ => PortableUpgradeErrorCodes.DownloadFailed,
    };

    private static void Report(IProgress<PortableUpgradeProgress>? progress, PortableUpgradePhase phase, double? percent = null)
    {
        if (progress is null) return;
        try { progress.Report(new(phase, percent)); }
        catch (Exception) { /* 观察者不能改变更新的所有权与结果。 */ }
    }

    private Task CheckpointAsync(PortableUpgradePhase phase, CancellationToken token) =>
        _checkpoint is null ? Task.CompletedTask : _checkpoint(phase, token);

    private sealed class UpgradeRequestException(string code) : Exception(code) { public string Code { get; } = code; }

    private sealed class UpgradePathException : Exception { }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
