using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GitHubStore.App.Services.Launcher;

/// <summary>
/// 决定命令行工具用哪个 PowerShell 运行：优先用户配置并验证过的 PowerShell 7（pwsh.exe），
/// 没有配置时回落到系统自带的 Windows PowerShell（powershell.exe）。
/// 只读文件系统，执行后干净退出，绝不修改系统环境或 PATH。
/// </summary>
public sealed class PowerShellEnvironmentResolver
{
    /// <summary>一次环境解析结果。配置路径不可用时会带上回退原因，供调用方如实提示。</summary>
    public sealed record Resolution(string ExecutablePath, bool IsPowerShell7, string? FallbackReason = null);

    private readonly Func<string?> _configuredPath;

    /// <param name="configuredPath">返回用户设置里保存的 PowerShell 7 路径；null 或空表示未配置。</param>
    public PowerShellEnvironmentResolver(Func<string?> configuredPath) => _configuredPath = configuredPath;

    public static string SystemPowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 解析当前应使用的 PowerShell 可执行文件。配置了 pwsh 但不可用时，回落到系统 Windows PowerShell，
    /// 并在 <see cref="Resolution.FallbackReason"/> 说明原因，让调用方提示用户而不是静默切换。
    /// </summary>
    public Resolution Resolve()
    {
        var configured = _configuredPath();
        if (!string.IsNullOrWhiteSpace(configured))
            return IsRunnableExecutable(configured)
                ? new(configured, IsPowerShell7(configured))
                : new(SystemPowerShellPath, false, "configured_not_found");
        return new(SystemPowerShellPath, false);
    }

    public static bool IsPowerShell7(string? path) =>
        path is not null && Path.GetFileName(path).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsRunnableExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// 在常见安装位置里寻找 PowerShell 7，用于设置页“检测 PowerShell 7”按钮预填。
    /// 只检查文件是否存在，不运行任何命令；找不到返回 null。
    /// </summary>
    public static string? FindPowerShell7()
    {
        var candidates = new List<string>();
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
            candidates.Add(Path.Combine(programFiles, "Microsoft", "PowerShell", "7", "pwsh.exe"));
            candidates.Add(Path.Combine(programFiles, "PowerShell", "pwsh.exe"));
        }
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(directory.Trim(), "pwsh.exe")); }
            catch (Exception ex) when (ex is ArgumentException) { }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate)) continue;
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>把单个字符串安全地包成 PowerShell 单引号字面量（内部单引号翻倍）。</summary>
    public static string QuoteForPowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>编码为 -EncodedCommand 需要的 Base64(UTF-16LE) 文本，避免命令行重建带来的注入。</summary>
    public static string EncodeCommand(string commandText) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(commandText));

    /// <summary>
    /// 把“调用命令行工具”的脚本包进一个按需保留窗口的 -Command 文本。
    /// 保留窗口：执行完停在一个可交互提示符，用户可直接继续输入命令，随后 exit 关闭。
    /// 关闭窗口：执行完立即退出 PowerShell 进程。try/finally 保证 Ctrl+C 中断后仍按策略处理窗口。
    /// </summary>
    public static string BuildCommandText(string invocation, bool keepOpen)
    {
        var opener = "$ErrorActionPreference='Continue'; try { " + invocation + " } finally { ";
        var closer = keepOpen
            ? "Write-Host ''; Write-Host 'Command finished. You can keep typing commands; exit to close.' }"
            : "}";
        return opener + closer;
    }
}
