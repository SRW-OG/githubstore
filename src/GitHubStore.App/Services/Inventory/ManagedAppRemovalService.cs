using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using GitHubStore.Core.Abstractions;
using Microsoft.Win32;

namespace GitHubStore.App.Services.Inventory;

/// <summary>移除一条本程序自建安装的结果；失败用稳定错误码表达，不抛异常给 UI。</summary>
public sealed record ManagedAppRemovalResult
{
    public bool Removed { get; init; }
    public string? ErrorCode { get; init; }

    /// <summary>
    /// 本程序实际执行的卸载命令行（仅 MSI 路径有值），用于让 UI 区分“本程序删除了受管目录”
    /// 与“本程序驱动 Windows Installer 卸载”；第三方交互式卸载器不在此列。
    /// </summary>
    public string? CommandLine { get; init; }

    public static ManagedAppRemovalResult Success(string? commandLine = null) =>
        new() { Removed = true, CommandLine = commandLine };
    public static ManagedAppRemovalResult Failure(string errorCode) => new() { ErrorCode = errorCode };
}

/// <summary>一次 msiexec 卸载调用的观察结果：只有退出码，退出码本身不构成结论。</summary>
public sealed record MsiUninstallOutcome(int ExitCode);

/// <summary>
/// 移除一条本程序自建安装。两条互斥路径，都只处理“本程序自己拥有”的安装：
/// <list type="bullet">
/// <item><b>绿色/便携解包</b>：本程序用 msiexec 之外的路径创建的文件，删除它在安装根目录下创建的那一个子目录；</item>
/// <item><b>本程序执行的 MSI</b>：调用 <c>msiexec /x &lt;产品码&gt; /qn /norestart</c>，
/// 由 Windows Installer 自己卸载，然后<em>核验</em>产品与目录确实消失。</item>
/// </list>
/// 这不是“猜一个卸载器”：产品码是安装时从<em>我们下载并校验过的那个 MSI</em> 的 Property 表里读出来、
/// 随登记持久化的权威标识；第三方安装程序（NSIS/Inno 等）的登记没有可信产品码，
/// 因此一律拒绝并返回 <see cref="UninstallErrorCodes.NotStoreOwned"/>。
/// 所有安全校验都在改动任何状态之前完成，且刻意在删除侧独立重做一遍，不依赖调用方传入的快照。
/// </summary>
public sealed class ManagedAppRemovalService
{
    /// <summary>子树遍历上限；超过即拒绝移除，而不是冒险删除一个无法确认的目录。</summary>
    private const int MaximumTreeEntries = 200_000;

    /// <summary>msiexec 的“安装时发生致命错误”退出码，实践中绝大多数是权限不足。</summary>
    private const int MsiErrorFatalDuringInstall = 1603;

    /// <summary>msiexec 的“本次安装需要提升权限”退出码。</summary>
    private const int MsiErrorInstallRequiresElevation = 1925;

    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IManagedAppLauncher _launcher;
    private readonly Func<string, CancellationToken, Task> _deleteDirectory;
    private readonly Func<string, string, CancellationToken, Task<MsiUninstallOutcome>> _runMsiUninstall;
    private readonly Func<string, bool> _isProductRegistered;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string> _resolveMsiExec;

    /// <param name="launcher">提供登记原文、安装根目录与登记移除的受管启动器。</param>
    /// <param name="deleteDirectory">测试注入的删除实现；为 null 时使用真实的递归删除。</param>
    /// <param name="runMsiUninstall">测试注入的 msiexec 调用；参数为 (msiexec 绝对路径, 产品码, 取消令牌)。</param>
    /// <param name="isProductRegistered">测试注入的产品注册检查；为 null 时只读卸载注册表。</param>
    /// <param name="directoryExists">测试注入的目录存在性检查。</param>
    /// <param name="resolveMsiExec">测试注入的系统目录 msiexec 解析。</param>
    public ManagedAppRemovalService(IManagedAppLauncher launcher,
        Func<string, CancellationToken, Task>? deleteDirectory = null,
        Func<string, string, CancellationToken, Task<MsiUninstallOutcome>>? runMsiUninstall = null,
        Func<string, bool>? isProductRegistered = null,
        Func<string, bool>? directoryExists = null,
        Func<string>? resolveMsiExec = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _deleteDirectory = deleteDirectory ?? DeleteOwnedDirectoryAsync;
        _runMsiUninstall = runMsiUninstall ?? RunMsiUninstallAsync;
        _isProductRegistered = isProductRegistered ?? IsProductRegisteredInUninstallRegistry;
        _directoryExists = directoryExists ?? Directory.Exists;
        _resolveMsiExec = resolveMsiExec ?? ResolveMsiExec;
    }

    /// <summary>
    /// 按安装 ID 移除本程序自建的安装。任何一步失败都返回稳定错误码，绝不抛异常文本给 UI。
    /// </summary>
    public async Task<ManagedAppRemovalResult> RemoveAsync(string installId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installId) || installId.Length > 256) return NotFound();
        ct.ThrowIfCancellationRequested();

        InstallRecord? receipt;
        try { receipt = await _launcher.GetAsync(installId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.ScanFailed); }

        // GetAsync 本身就是按 ID 重新读取持久化登记，绝不接受 UI 快照或调用方提供的路径与产品码。
        if (receipt is null) return NotFound();

        // 绿色/便携：本程序自己解包出来的文件，由本程序删除那一个受管子目录。
        if (StoreOwnedInstallation.OwnsExtractedFiles(receipt))
            return await RemoveExtractedAsync(receipt, installId, ct).ConfigureAwait(false);

        // 本程序用 Windows Installer 执行过的 MSI：能否移除取决于登记里有没有可信产品码。
        if (StoreOwnedInstallation.IsStorePerformedMsi(receipt))
            return await RemoveStorePerformedMsiAsync(receipt, installId, ct).ConfigureAwait(false);

        // 其余一切（第三方安装器、状态不完整）本程序无法证明文件所有权，一个字节都不动。
        return ManagedAppRemovalResult.Failure(UninstallErrorCodes.NotStoreOwned);
    }

    /// <summary>绿色/便携路径：先移除登记、再删除受管子目录，删除失败如实报告可能残留。</summary>
    private async Task<ManagedAppRemovalResult> RemoveExtractedAsync(InstallRecord receipt, string installId,
        CancellationToken ct)
    {
        // 路径安全校验必须在改动任何状态之前完成：不通过就什么都不做。
        string target;
        try { target = ValidateRemovalTarget(receipt); }
        catch (RemovalPathException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.UnsafeRemovalPath); }
        // 安装根目录自身无法规范化（例如设置被改成 UNC 或相对路径）时同样无法证明安全性。
        catch (LauncherException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.UnsafeRemovalPath); }
        catch (UnauthorizedAccessException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.AccessDenied); }

        // 子树里如果已有重解析点，先拒绝，避免删除到已校验目录之外。
        try { EnsureNoReparsePoints(target, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RemovalPathException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.UnsafeRemovalPath); }
        catch (UnauthorizedAccessException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.AccessDenied); }
        catch (Exception)
        {
            // 预检发生在删除之前：此时一个文件都还没删，任何未预期失败都如实报告，不抛给 UI。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalFailed);
        }

        // 目录已经不在了：状态已经达成，只需把残留登记去掉。
        if (!Directory.Exists(target)) return await UnregisterAsync(installId, ct).ConfigureAwait(false);

        // 先移除登记，再删除文件：即便删除半途失败，启动器里也不会留下指向已删除文件的条目。
        var unregistered = await UnregisterAsync(installId, ct).ConfigureAwait(false);
        if (!unregistered.Removed) return unregistered;

        try { await _deleteDirectory(target, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消不能把已经生效的登记移除翻回成功，也不能声称文件已删除。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete);
        }
        catch (Exception)
        {
            // OS 异常文本可能包含任意路径信息，只报告稳定错误码。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete);
        }
        return ManagedAppRemovalResult.Success();
    }

    /// <summary>
    /// 本程序执行的 MSI 路径：msiexec /x &lt;产品码&gt; /qn /norestart，等待进程结束并核验真实结果。
    /// <para>
    /// 产品码来自安装时从该 MSI 的 Property 表读出的值，随登记持久化，是权威标识而非猜测；
    /// 这里再校验一次格式，且只接受登记原文里的值——调用方无法传入产品码。
    /// </para>
    /// </summary>
    private async Task<ManagedAppRemovalResult> RemoveStorePerformedMsiAsync(InstallRecord receipt, string installId,
        CancellationToken ct)
    {
        // 产品码缺失或非法：登记不足以证明本程序拥有这次安装的卸载依据，拒绝且不执行任何进程。
        // 对 UI 而言这条路径通常先从服务层得到 no_uninstall_string；这里保留 not_store_owned，
        // 以便“记录不完整”与“第三方安装”在移除层含义一致：都没有可信的卸载依据。
        if (!ProductCodeFormat.IsValid(receipt.ProductCode))
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.NotStoreOwned);
        var productCode = receipt.ProductCode!;

        // 目标目录仍要按同一把尺子校验：核验“目录已消失”时需要一个可信的绝对路径，
        // 顺带确保登记没有被改成指向受管目录之外的任意位置。
        string target;
        try { target = ValidateRemovalTarget(receipt); }
        catch (RemovalPathException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.UnsafeRemovalPath); }
        catch (LauncherException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.UnsafeRemovalPath); }
        catch (UnauthorizedAccessException) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.AccessDenied); }

        // msiexec 只能来自系统目录；解析失败就什么都不执行，绝不回退到裸 "msiexec" 或 PATH。
        string msiExec;
        try { msiExec = _resolveMsiExec(); }
        catch (Exception) { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.LaunchFailed); }

        MsiUninstallOutcome outcome;
        try { outcome = await _runMsiUninstall(msiExec, productCode, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消时 msiexec 可能仍在运行或已改动系统：既不能报成功，也不能说系统没变。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 || ex.NativeErrorCode == 740)
        {
            // 5 = 拒绝访问，740 = 需要提升权限。只报稳定错误码，不回显系统文本。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.AccessDenied);
        }
        catch (Exception)
        {
            // 进程未能启动或等待失败：无法证明任何移除发生，登记保持原样以便重试。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalFailed);
        }

        if (outcome.ExitCode != 0)
        {
            // 1603/1925 实际上几乎总是权限问题（需要提升权限才能卸载机器范围安装）；
            // 其余非零退出码只报通用的 removal_failed，绝不回显 msiexec 原文。
            return ManagedAppRemovalResult.Failure(
                outcome.ExitCode is MsiErrorFatalDuringInstall or MsiErrorInstallRequiresElevation
                    ? UninstallErrorCodes.AccessDenied
                    : UninstallErrorCodes.RemovalFailed);
        }

        // 退出码 0 不等于结果：必须核验产品已从卸载注册表消失、且登记的目录确实不在。
        // 任何一项无法确认都当作未完成，绝不因“命令成功返回”就宣称卸载成功。
        bool productStillRegistered, directoryStillExists;
        try
        {
            productStillRegistered = _isProductRegistered(productCode);
            directoryStillExists = _directoryExists(target);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete); }
        catch (Exception)
        {
            // 读注册表/文件系统失败意味着无法核验；不能把“无法确认”说成“已完成”。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete);
        }
        if (productStillRegistered || directoryStillExists)
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalIncomplete);

        // 核验通过后才移除登记：这样绝不会留下指向已被卸载文件的启动器条目。
        // 登记移除失败时文件已经不在，如实报告失败，不假装成功。
        var unregistered = await UnregisterAsync(installId, ct).ConfigureAwait(false);
        if (!unregistered.Removed) return unregistered;
        return ManagedAppRemovalResult.Success(BuildMsiCommandLine(msiExec, productCode));
    }

    /// <summary>仅用于展示与诊断的命令行；参数与真实调用完全一致。</summary>
    private static string BuildMsiCommandLine(string msiExec, string productCode) =>
        "\"" + msiExec + "\" /x " + productCode + " /qn /norestart";

    private async Task<ManagedAppRemovalResult> UnregisterAsync(string installId, CancellationToken ct)
    {
        try
        {
            await _launcher.UnregisterAsync(installId, ct).ConfigureAwait(false);
            return ManagedAppRemovalResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 登记未能更新时一个文件都还没删（或已经由 msiexec 卸载但没有登记残留），如实报告。
            return ManagedAppRemovalResult.Failure(UninstallErrorCodes.RemovalFailed);
        }
    }

    private static ManagedAppRemovalResult NotFound() =>
        ManagedAppRemovalResult.Failure(UninstallErrorCodes.InstallationNotFound);

    /// <summary>
    /// 校验目标目录确实是“当前安装根目录的直接子目录”，并返回规范化后的绝对路径。
    /// 与 ManagedAppLauncher 的路径校验保持同一严格度。
    /// </summary>
    private string ValidateRemovalTarget(InstallRecord receipt)
    {
        var root = NormalizeLocalPath(_launcher.InstallRoot);
        var target = NormalizeLocalPath(receipt.TargetDir);
        // 根目录本身永远不是可删除目标；只有它的直接子目录才是。
        if (SamePath(target, root)) throw new RemovalPathException();
        if (!SamePath(Path.GetDirectoryName(target) ?? string.Empty, root)) throw new RemovalPathException();
        // 祖先链上任一重解析点（含根目录）都会让“直接子目录”这一判断失去意义。
        CheckExistingAncestors(target);
        return target;
    }

    /// <summary>删除前确认整棵子树没有任何重解析点，避免沿链接删除到受管目录之外。</summary>
    private static void EnsureNoReparsePoints(string target, CancellationToken ct)
    {
        if (!Directory.Exists(target)) return;
        var pending = new Stack<string>();
        pending.Push(target);
        var visited = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++visited > MaximumTreeEntries) throw new RemovalPathException();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new RemovalPathException();
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    /// <summary>真实删除：逐项检查重解析点，只删除这一个已校验目录。</summary>
    private static Task DeleteOwnedDirectoryAsync(string target, CancellationToken ct) =>
        Task.Run(() => DeleteTree(target, ct), ct);

    private static void DeleteTree(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            // 重解析点在预检后出现（TOCTOU）时立即失败，绝不穿透链接。
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new RemovalPathException();
            if (Directory.Exists(entry)) DeleteTree(entry, ct);
            else File.Delete(entry);
        }
        ct.ThrowIfCancellationRequested();
        Directory.Delete(directory, false);
    }

    /// <summary>
    /// 系统目录里的 msiexec.exe。与安装侧的 TrustedMsiExec 同一策略：
    /// 只从 OS 特殊目录解析，不查 PATH、不接受调用方传入的路径。
    /// </summary>
    private static string ResolveMsiExec()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system) || !Path.IsPathFullyQualified(system))
            throw new InvalidOperationException("system directory unavailable");
        var path = Path.GetFullPath(Path.Combine(system, "msiexec.exe"));
        if (!File.Exists(path)) throw new InvalidOperationException("msiexec.exe unavailable");
        return path;
    }

    /// <summary>
    /// 起 msiexec 并等待退出。静默参数由本程序固定给出：我们随后自己核验结果，
    /// 因此不需要交互向导，也不把卸载交给用户点选。
    /// </summary>
    private static async Task<MsiUninstallOutcome> RunMsiUninstallAsync(string msiExec, string productCode,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = msiExec,
            WorkingDirectory = Path.GetDirectoryName(msiExec)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/x");
        start.ArgumentList.Add(productCode);
        start.ArgumentList.Add("/qn");
        start.ArgumentList.Add("/norestart");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec did not start");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new MsiUninstallOutcome(process.ExitCode);
    }

    /// <summary>
    /// 产品是否仍登记在卸载注册表里。这是按产品码直接查一个键，不做任何扫描、不推断卸载命令。
    /// 任一视图读取失败都会向上抛出（调用方据此报 removal_incomplete）：读不到就不能断言“已消失”。
    /// </summary>
    private static bool IsProductRegisteredInUninstallRegistry(string productCode) =>
        OperatingSystem.IsWindows() && IsProductRegisteredWindows(productCode);

    [SupportedOSPlatform("windows")]
    private static bool IsProductRegisteredWindows(string productCode)
    {
        var subKey = UninstallKeyPath + "\\" + productCode;
        // 机器范围与当前用户都查：真实安装可能是 ALLUSERS=1，也可能是按用户注册。
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
        {
            var hive = view == RegistryView.Default ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is not null) return true;
        }
        return false;
    }

    /// <summary>与 ManagedAppLauncher.NormalizeLocalPath 同一语义：拒绝 UNC、相对路径、保留名与歧义段。</summary>
    private static string NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)) throw new RemovalPathException();
        try
        {
            var pathRoot = Path.GetPathRoot(path);
            if (pathRoot is null || pathRoot.Length != 3 || !char.IsAsciiLetter(pathRoot[0]) || pathRoot[1] != ':')
                throw new RemovalPathException();
            foreach (var part in path[pathRoot.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part is "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(part))
                    throw new RemovalPathException();
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new RemovalPathException(); }
    }

    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    private static void CheckExistingAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new RemovalPathException();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { throw; }
            catch (IOException) { throw new RemovalPathException(); }
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>路径不合规的内部信号；错误码在边界处统一转换，绝不外泄细节。</summary>
    private sealed class RemovalPathException : Exception { }
}
