namespace GitHubStore.Core.Abstractions;

/// <summary>
/// 已安装程序的一条展示记录。StoreInstall 来自本程序成功安装登记；
/// 其他来源供系统库存实现使用，不会进入当前已安装页。
/// </summary>
public sealed record InstalledProgram
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>卸载命令行原文；null 表示该程序未登记卸载命令。</summary>
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ProductCode { get; init; }
    public RegistryOrigin Origin { get; init; }
    /// <summary>来源键路径或发现目录路径，与 Origin 共同构成安装身份。</summary>
    public required string SourceKeyPath { get; init; }
    /// <summary>同一安装身份或同路径目录发现的来源记录数（&gt;= 1）。</summary>
    public int DuplicateCount { get; init; } = 1;

    /// <summary>来源视图/范围与来源键共同标识一次安装；显示名、版本变化不改变身份。</summary>
    public string Id => $"{Origin}:{SourceKeyPath.Trim().ToUpperInvariant()}";

    /// <summary>只使用普通卸载命令；安静命令不能兑现交互卸载承诺。</summary>
    public string? EffectiveUninstallString =>
        string.IsNullOrWhiteSpace(UninstallString) ? null : UninstallString;

    /// <summary>
    /// 本程序在安装根目录下自行解包安装（绿色/便携）的文件，由本程序直接移除；
    /// 这不是可执行的卸载命令，UI 不得据此展示或执行任何命令行。
    /// </summary>
    public bool StoreManagedRemoval { get; init; }

    /// <summary>
    /// 本程序用 Windows Installer 亲自执行过的 MSI 安装，且登记里带有从该安装包读出的合法产品码。
    /// 移除方式是 msiexec /x &lt;产品码&gt;，由 Windows Installer 自己卸载并核验结果。
    /// 这同样不是"发现来的卸载命令"：产品码来自我们安装的那个包，不是猜测。
    /// 产品码缺失或非法时为 false——此时按钮不亮，也不执行任何进程。
    /// </summary>
    public bool StorePerformedMsiRemoval { get; init; }

    /// <summary>Explicit receipt-only removal; this does not assert the application was uninstalled.</summary>
    public bool CanRemoveRegistration { get; init; }

    /// <summary>
    /// 这条登记的核对结果。用于区分“可运行”“入口缺失可修复”“无法核对”，
    /// 界面不得把后两种显示成未安装，也不得据此自动删除记录或文件。
    /// </summary>
    public InstalledStatus Status { get; init; } = InstalledStatus.Installed;

    /// <summary>
    /// 该安装的 Windows 卸载登记核对结果。只有在已找到可用登记、或本程序能自行移除时才可卸载；
    /// “没有匹配到”与“无法核对”都不等于已卸载。
    /// </summary>
    public UninstallRegistrationStatus RegistrationStatus { get; init; } = UninstallRegistrationStatus.Unknown;

    /// <summary>入口缺失但登记完整时可修复；索引不可读或未登记时不可修复。</summary>
    public bool CanRepairExecutable => Status == InstalledStatus.NeedsRepair
        && Origin == RegistryOrigin.StoreInstall && !string.IsNullOrWhiteSpace(SourceKeyPath);

    public bool CanUninstall => StoreManagedRemoval || StorePerformedMsiRemoval
        || !string.IsNullOrWhiteSpace(EffectiveUninstallString);
}

public enum RegistryOrigin { Hklm64, Hklm32, Hkcu, GreenInstall, StoreInstall }

/// <summary>
/// 这条安装的 Windows 卸载登记核对结果。三个值不能混为一谈：
/// <see cref="Available"/> 表示找到了唯一可信的登记；<see cref="NotFound"/> 表示读取成功但确实
/// 没有匹配到登记；<see cref="Unknown"/> 表示读取失败，无法判断。后两者都不能当作“已卸载”。
/// </summary>
public enum UninstallRegistrationStatus { Available, NotFound, Unknown }

/// <summary>
/// 一次成功移除到底做了什么。三种语义对用户意味着完全不同的事，
/// 文案不能把它们混为一谈（尤其不能把"本程序已核验完成的静默卸载"说成"启动了卸载器"）。
/// </summary>
public enum UninstallRemovalKind
{
    /// <summary>启动了目标程序自带的交互式卸载器，结果由该卸载器决定。</summary>
    UninstallerLaunched,

    /// <summary>本程序删除了自己解包创建的那个受管子目录。</summary>
    ManagedDirectoryDeleted,

    /// <summary>本程序用登记的产品码驱动 Windows Installer 卸载，并已核验产品与目录消失。</summary>
    RemovedByProductCode,

    /// <summary>Only the Store receipt was removed; application and user files were preserved.</summary>
    RegistrationRemoved,

    /// <summary>Only the recorded executable path was corrected; no files were moved or deleted.</summary>
    RepairApplied,
}

/// <summary>启动卸载器的结果；失败用稳定错误码表达，不抛异常给 UI。</summary>
public sealed record UninstallLaunchResult
{
    public bool Started { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>实际启动的命令行，仅用于展示与诊断。</summary>
    public string? CommandLine { get; init; }

    /// <summary>
    /// 成功路径的实际语义；默认值保持"启动了卸载器"，与既有的
    /// <see cref="Success"/> 行为一致，因此既有调用方无需改动。
    /// </summary>
    public UninstallRemovalKind Kind { get; init; } = UninstallRemovalKind.UninstallerLaunched;

    public static UninstallLaunchResult Success(string commandLine) =>
        new() { Started = true, CommandLine = commandLine, Kind = UninstallRemovalKind.UninstallerLaunched };

    /// <summary>
    /// 本程序删除了它自己解包的安装目录。没有启动任何命令行，
    /// 因此 <see cref="CommandLine"/> 为 null——调用方不能把它当成"启动了卸载器"。
    /// </summary>
    public static UninstallLaunchResult Removed() =>
        new() { Started = true, Kind = UninstallRemovalKind.ManagedDirectoryDeleted };

    /// <summary>
    /// 本程序用 Windows Installer 移除了自己安装的 MSI，并已核验产品与目录消失。
    /// 这里确实有命令行，但它是本程序构造并<em>等待完成</em>的 msiexec 命令，
    /// 而不是"交还给用户交互的第三方卸载器"：文案必须区分这两者。
    /// </summary>
    public static UninstallLaunchResult RemovedByProductCode(string commandLine) =>
        new() { Started = true, CommandLine = commandLine, Kind = UninstallRemovalKind.RemovedByProductCode };

    public static UninstallLaunchResult Failure(string errorCode) =>
        new() { Started = false, ErrorCode = errorCode };
}

/// <summary>
/// 已安装程序识别与卸载。实现放在平台相关层（需要读取注册表），
/// Core 只声明需求，便于在没有 Windows 的环境下测试上层逻辑。
/// </summary>
public interface IInstalledProgramService
{
    Task<IReadOnlyList<InstalledProgram>> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// 执行一条卸载请求。三种互斥语义：
    /// 1) 程序自带卸载器时启动该卸载器，不静默、不改写参数；
    /// 2) 安装由本程序自己解包登记（Store 来源 + 已完成 + 绿色/便携）时，
    ///    由本程序删除它自己创建的那个安装目录——这是本程序拥有、可据以删除的文件；
    /// 3) 安装由本程序用 Windows Installer 执行（Store 来源 + 已完成 + Installer.Msi + 合法产品码）时，
    ///    用 msiexec /x &lt;产品码&gt; /qn /norestart 驱动 Windows Installer 卸载，并核验产品与目录确实消失。
    /// 产品码来自我们安装的那个包，不是猜测；调用方传入的快照无法授予任何权限。
    /// 第三方安装程序（NSIS/Inno 等）的安装不属于以上任何一类：无法证明哪些文件是它创建的，
    /// 因此不删除任何文件，返回 <see cref="UninstallErrorCodes.NoUninstallString"/>。
    /// </summary>
    Task<UninstallLaunchResult> LaunchUninstallAsync(InstalledProgram program, CancellationToken ct = default);

    /// <summary>Explicitly forget a Store installer receipt without uninstalling or deleting files.</summary>
    Task<UninstallLaunchResult> RemoveRegistrationAsync(InstalledProgram program, CancellationToken ct = default) =>
        Task.FromResult(UninstallLaunchResult.Failure(UninstallErrorCodes.NotStoreOwned));

    /// <summary>
    /// 重新指定入口文件以修复“记录在、程序入口找不到”的安装。只改入口路径，
    /// 不移动或删除任何文件，也不改变安装身份与卸载依据。
    /// </summary>
    Task<UninstallLaunchResult> RepairExecutableAsync(InstalledProgram program, string executablePath,
        CancellationToken ct = default) =>
        Task.FromResult(UninstallLaunchResult.Failure(UninstallErrorCodes.NotStoreOwned));
}

/// <summary>稳定错误码。</summary>
public static class UninstallErrorCodes
{
    public const string NoUninstallString = "no_uninstall_string";
    public const string InvalidUninstallCommand = "invalid_uninstall_command";
    public const string ExecutableNotFound = "executable_not_found";
    public const string LaunchFailed = "launch_failed";
    public const string AccessDenied = "access_denied";
    public const string ScanFailed = "scan_failed";

    /// <summary>登记原文不存在：安装 ID 未知或不属于本程序，未做任何改动。</summary>
    public const string InstallationNotFound = "installation_not_found";

    /// <summary>登记原文无法证明由本程序解包（第三方安装器或状态不完整），未删除任何文件。</summary>
    public const string NotStoreOwned = "not_store_owned";

    /// <summary>
    /// 目标目录未通过路径校验（非安装根目录的直接子目录、重解析点、UNC 等），未删除任何文件。
    /// </summary>
    public const string UnsafeRemovalPath = "unsafe_removal_path";

    /// <summary>登记未能更新，因此一个文件都没有删除。</summary>
    public const string RemovalFailed = "removal_failed";

    /// <summary>选定的入口不是有效的 Windows 可执行文件，登记未被改动。</summary>
    public const string InvalidExecutable = "invalid_executable";

    /// <summary>
    /// 登记已移除，但目录删除未完成：可能残留文件。安装不再出现于启动器，
    /// 但调用方必须如实告知用户可能有残留，不能声称已完全清理。
    /// </summary>
    public const string RemovalIncomplete = "removal_incomplete";
}
