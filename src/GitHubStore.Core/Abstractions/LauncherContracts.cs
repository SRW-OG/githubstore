namespace GitHubStore.Core.Abstractions;

/// <summary>A launcher item comes only from a completed Store-origin installation receipt.</summary>
public sealed record LauncherApp
{
    public required string InstallId { get; init; }
    public required AppId AppId { get; init; }
    public required string DisplayName { get; init; }
    public required string InstallDirectory { get; init; }
    public required string ExecutablePath { get; init; }
    public string? DisplayVersion { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
    public bool IsAvailable { get; init; }

    /// <summary>
    /// 这条登记当前的实际状态。<see cref="InstalledStatus.Installed"/> 之外的状态都必须如实展示，
    /// 不能把"文件缺失"或"索引读不出来"当成未安装，也不能因此自动删除任何记录或文件。
    /// </summary>
    public InstalledStatus Status { get; init; } = InstalledStatus.Installed;

    /// <summary>
    /// 本商店自己解包到受管安装根目录的便携/绿色安装：启动器只凭登记就能判定这一类。
    /// 由第三方安装程序安装的应用永远保持 false。
    /// </summary>
    public bool StoreManagedRemoval { get; init; }

    /// <summary>
    /// 本商店用 Windows Installer 执行过、且登记里带有从该安装包读出的合法产品码的 MSI 安装。
    /// 与 <see cref="StoreManagedRemoval"/> 一样由启动器在读取登记时算出，所以已安装页不必逐条重读登记；
    /// 但两者都只是<em>展示</em>依据——真正的删除授权由移除服务按安装 ID 重新读取登记后独立判定，
    /// 调用方传入的快照无法授予任何删除权限，也无法提供产品码。
    /// </summary>
    public bool StorePerformedMsiRemoval { get; init; }
}

/// <summary>
/// 一条已登记安装的核对结果。四种状态互斥且含义不同：
/// <see cref="Installed"/> 表示登记与入口文件一致；<see cref="NeedsRepair"/> 表示登记存在但入口
/// 文件已缺失（可以定位修复，绝不能当成未安装）；<see cref="Unknown"/> 表示索引本身读不出来，
/// 此时无法判断任何一条登记，调用方必须按"无法核对"处理；<see cref="NotInstalled"/> 只表示
/// 索引可读且确实没有对应登记。
/// </summary>
public enum InstalledStatus { Unknown, NotInstalled, Installed, NeedsRepair }

public sealed record LauncherLaunchResult(bool Started, string? ErrorCode = null);

public interface IManagedAppLauncher
{
    event EventHandler? Changed;

    /// <summary>
    /// 当前生效的受管安装根目录（绝对路径、已规范化、无结尾分隔符）。
    /// 空字符串表示该实现不管理安装根目录，因此也无法证明任何目录归本程序所有，
    /// 所有删除类操作都必须据此拒绝——默认值即这种保守情形。
    /// </summary>
    string InstallRoot => string.Empty;

    Task<IReadOnlyList<LauncherApp>> ListAsync(CancellationToken ct = default);
    Task RegisterCompletedInstallAsync(InstallRecord installation, string displayName, CancellationToken ct = default);
    Task UnregisterAsync(string installId, CancellationToken ct = default);
    Task<LauncherLaunchResult> LaunchAsync(string installId, CancellationToken ct = default);

    /// <summary>
    /// 按安装 ID 重新读取登记原文（已通过实现自身的校验与路径规范化）。
    /// 删除文件之类的写操作必须先读取登记原文确认所有权，绝不能凭 UI 快照执行。
    /// 默认实现返回 null：不能提供登记原文的实现就无法证明文件所有权，
    /// 因此也无法授权删除，这样既不会出现"未实现却可删文件"的降级路径，
    /// 也不会强制改写只实现只读视图的既有替身。
    /// </summary>
    Task<InstallRecord?> GetAsync(string installId, CancellationToken ct = default) =>
        Task.FromResult<InstallRecord?>(null);

    /// <summary>
    /// 为一条已完成登记重新指定主程序，用于入口文件被移动或改名后的修复。
    /// 只改入口路径：安装目录、安装身份、来源、版本和移除相关标记全部保持不变；
    /// 新入口必须仍然位于原安装目录之内并通过可执行文件校验，否则拒绝。
    /// 默认实现拒绝：不能写入登记的实现也无法提供修复。
    /// </summary>
    Task<LauncherLaunchResult> RepairExecutableAsync(string installId, AppId expectedAppId,
        string executablePath, CancellationToken ct = default) =>
        Task.FromResult(new LauncherLaunchResult(false, "launcher_repair_unsupported"));
}

/// <summary>
/// 判定一条登记是否代表"本商店自己解包出来的文件"。
/// 只有绿色/便携解包会产生这种登记，判定逻辑集中在此，避免读取侧与删除侧出现分歧。
/// </summary>
public static class StoreOwnedInstallation
{
    /// <summary>Store 来源、已完成登记，且安装方式是本商店的绿色解包或便携产物。</summary>
    public static bool OwnsExtractedFiles(InstallRecord? installation) =>
        installation is { Origin: Origin.Store, State: InstallState.Done }
        && (installation.Strategy == Strategy.Green || installation.Installer == InstallerKind.Portable);

    /// <summary>
    /// 本商店用 Windows Installer 亲自执行过的 MSI 安装。判据是登记本身，
    /// 而不是猜测：Store 来源 + 已完成 + Installer 为 Msi。
    /// 这里<em>不</em>要求产品码有效——能不能真正移除由
    /// <see cref="CanRemove"/> 决定，这样"是我们装的"与"现在能安全删除"两个问题不会混为一谈。
    /// 由第三方安装程序安装的应用永远不会满足这一条。
    /// </summary>
    public static bool IsStorePerformedMsi(InstallRecord? installation) =>
        installation is { Origin: Origin.Store, State: InstallState.Done, Installer: InstallerKind.Msi };

    /// <summary>
    /// 本商店用 Windows Installer 执行过、且登记里带有合法产品码的 MSI 安装。
    /// 产品码缺失或非法时返回 false：没有可信的产品码就没有可信的卸载依据，
    /// 此时按钮不亮、也不执行任何进程。
    /// </summary>
    public static bool CanRemoveMsi(InstallRecord? installation) =>
        IsStorePerformedMsi(installation) && ProductCodeFormat.IsValid(installation!.ProductCode);

    /// <summary>
    /// 本商店是否拥有足够依据去移除这条安装。两条互斥路径：
    /// <list type="bullet">
    /// <item>绿色/便携解包：本商店创建的目录，可直接删除；</item>
    /// <item>本商店执行的 MSI：登记里必须带有从该安装包读出的、格式合法的产品码，
    /// 才能用 msiexec /x 驱动 Windows Installer 自己卸载。产品码缺失或非法即拒绝，
    /// 绝不猜测卸载命令。</item>
    /// </list>
    /// </summary>
    public static bool CanRemove(InstallRecord? installation) =>
        OwnsExtractedFiles(installation) || CanRemoveMsi(installation);
}

/// <summary>
/// 产品码（Windows Installer ProductCode）的格式判定。集中在此，让安装侧与删除侧使用同一把尺子：
/// 安装侧只记录合法产品码，删除侧再校验一次，任何一侧放宽都不会让可疑文本进入 msiexec 命令行。
/// 产品码不是秘密，但它是"只能来自安装包、绝不接受调用方输入"的标识，因此必须严格校验。
/// </summary>
public static class ProductCodeFormat
{
    /// <summary>花括号包裹的 8-4-4-4-12 十六进制 GUID，共 38 个字符。</summary>
    public static bool IsValid(string? productCode)
    {
        if (productCode is null || productCode.Length != 38 || productCode[0] != '{' || productCode[^1] != '}') return false;
        for (var index = 1; index < 37; index++)
        {
            var character = productCode[index];
            if (index is 9 or 14 or 19 or 24)
            {
                if (character != '-') return false;
                continue;
            }
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }
}

public sealed class LauncherException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
