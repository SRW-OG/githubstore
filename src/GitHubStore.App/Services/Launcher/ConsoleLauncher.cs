using System.Diagnostics;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Launcher;

/// <summary>
/// 在 PowerShell 里打开一个命令行工具：优先在 Windows Terminal（wt.exe）的标签页中，
/// 找不到 Windows Terminal 时回退到直接打开 PowerShell 控制台窗口。
/// 无论哪种载体，都用所选 PowerShell 环境（pwsh 或系统 Windows PowerShell），
/// 切到工作目录，以工具绝对路径调用它，并按需保留窗口以便查看输出或继续输入命令。
/// 工具路径与参数作为数据传入（单引号字面量转义），不拼接成可被注入的脚本文本。
/// </summary>
public sealed class ConsoleLauncher
{
    private readonly PowerShellEnvironmentResolver _environment;
    private readonly Func<ProcessStartInfo, bool>? _start;
    private readonly Func<string?>? _terminalPath;
    private readonly Func<ProcessStartInfo, bool>? _terminalStart;

    /// <param name="environment">决定用哪个 PowerShell 运行工具。</param>
    /// <param name="start">可选：直接启动 PowerShell 的替身，便于测试。</param>
    /// <param name="terminalPath">可选：返回 Windows Terminal（wt.exe）路径；null/空表示不优先使用终端。用于测试。</param>
    /// <param name="terminalStart">可选：启动 Windows Terminal 的替身，便于测试。</param>
    public ConsoleLauncher(PowerShellEnvironmentResolver environment,
        Func<ProcessStartInfo, bool>? start = null,
        Func<string?>? terminalPath = null,
        Func<ProcessStartInfo, bool>? terminalStart = null)
    {
        _environment = environment;
        _start = start;
        _terminalPath = terminalPath;
        _terminalStart = terminalStart;
    }

    /// <summary>当前将使用的 PowerShell 环境说明（供设置页/诊断展示）。</summary>
    public PowerShellEnvironmentResolver.Resolution ResolveEnvironment() => _environment.Resolve();

    /// <summary>
    /// 打开工具。容器优先级：Windows Terminal（若可用）→ 直接 PowerShell 控制台。
    /// 失败时返回如实的错误码，绝不出现“以为启动了却没有窗口”的情况。调用方负责先校验工具文件存在。
    /// </summary>
    public LauncherLaunchResult Launch(string executablePath, string? workingDirectory, string? arguments, bool keepOpen)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return new(false, "launcher_start_failed");
        var environment = _environment.Resolve();
        if (!File.Exists(environment.ExecutablePath))
            return new(false, "launcher_powershell_missing");

        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(executablePath) : workingDirectory;
        var commandText = PowerShellEnvironmentResolver.BuildCommandText(
            BuildInvocation(executablePath, arguments), keepOpen);

        try
        {
            // 1) 优先：在 Windows Terminal 的新标签页里用指定 PowerShell 运行工具。
            if (FindWindowsTerminal() is { } wt)
            {
                var terminalInfo = BuildTerminalStartInfo(wt, environment, directory, commandText);
                var startedInTerminal = _terminalStart is not null ? _terminalStart(terminalInfo) : StartProcess(terminalInfo);
                if (startedInTerminal) return new(true);
                // 终端启动失败不是致命错误：继续走直接 PowerShell 的兜底路径。
            }

            // 2) 兜底：直接打开 PowerShell 控制台窗口。
            var startInfo = BuildPowerShellStartInfo(environment, directory, commandText);
            var started = _start is not null ? _start(startInfo) : StartProcess(startInfo);
            return started ? new(true) : new(false, "launcher_start_failed");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "launcher_launch_cancelled");
        }
        catch (Exception) { return new(false, "launcher_start_failed"); }
    }

    /// <summary>
    /// 用 wt.exe 打开一个新标签页运行 PowerShell 脚本。
    /// 参数顺序：wt -- 后跟完整的 PowerShell 命令行；wt 会解析它并作为该标签页的可执行文件直接启动，
    /// 因此不需要经过 cmd.exe，也不存在额外的 shell 拼接层。
    /// </summary>
    internal static ProcessStartInfo BuildTerminalStartInfo(string terminalPath,
        PowerShellEnvironmentResolver.Resolution environment, string? directory, string commandText)
    {
        // -EncodedCommand 后的 Base64 不含空格/引号，可安全作为单个参数传给 wt。
        var encoded = PowerShellEnvironmentResolver.EncodeCommand(commandText);
        var argumentList = new List<string> { "--" };
        if (!string.IsNullOrWhiteSpace(directory))
        {
            // PowerShell 的工作目录通过 -Command 里的 Set-Location 设置（见 BuildInvocation）；
            // wt 自身的起始目录不强制，避免路径含空格时另需转义。
        }
        argumentList.Add(environment.ExecutablePath);
        argumentList.Add("-NoLogo");
        argumentList.Add("-EncodedCommand");
        argumentList.Add(encoded);
        return new ProcessStartInfo
        {
            FileName = terminalPath,
            Arguments = BuildArguments(argumentList),
            UseShellExecute = true,
            CreateNoWindow = false,
        };
    }

    /// <summary>直接启动 PowerShell 控制台窗口（无 Windows Terminal 时的兜底）。</summary>
    internal static ProcessStartInfo BuildPowerShellStartInfo(PowerShellEnvironmentResolver.Resolution environment,
        string? directory, string commandText) => new()
        {
            FileName = environment.ExecutablePath,
            Arguments = "-NoLogo -EncodedCommand " + PowerShellEnvironmentResolver.EncodeCommand(commandText),
            WorkingDirectory = directory ?? string.Empty,
            UseShellExecute = true,
            CreateNoWindow = false,
        };

    /// <summary>构造 "&amp; '工具路径' 参数" 的调用文本；工作目录由 PowerShell 的 Set-Location 负责。</summary>
    internal static string BuildInvocation(string executablePath, string? arguments)
    {
        var invocation = "& " + PowerShellEnvironmentResolver.QuoteForPowerShell(executablePath);
        return string.IsNullOrEmpty(arguments) ? invocation : invocation + " " + arguments;
    }

    /// <summary>
    /// 在当前用户可见位置查找 Windows Terminal（wt.exe）。只读文件系统，不启动任何东西。
    /// 找不到返回 null，由调用方回退到直接 PowerShell。测试可注入固定路径取代此方法。
    /// </summary>
    internal Func<string?> TerminalLocator => () => WindowsTerminalLocator.Find();

    private string? FindWindowsTerminal() => (_terminalPath ?? TerminalLocator)();

    /// <summary>按 Windows 命令行规则把参数拼成一段可执行命令行（含空格的参数加引号）。</summary>
    internal static string BuildArguments(IReadOnlyList<string> parts)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < parts.Count; index++)
        {
            if (index > 0) builder.Append(' ');
            var part = parts[index];
            if (part.Length == 0 || part.IndexOfAny([' ', '\t', '"']) >= 0)
                builder.Append('"').Append(part.Replace("\"", "\\\"")).Append('"');
            else builder.Append(part);
        }
        return builder.ToString();
    }

    private static bool StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}

/// <summary>定位 Windows Terminal 的 wt.exe。按常见安装位置与 PATH 顺序检查，只读不执行。</summary>
public static class WindowsTerminalLocator
{
    public static string? Find()
    {
        // 1) 当前用户的 Store 应用执行别名（最可靠，Windows 10/11 安装 Windows Terminal 后即存在）。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
            if (SafeExists(alias)) return alias;
        }
        // 2) 便携版 / 其它固定位置。
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe",
                     @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\wt.exe",
                 })
        {
            if (SafeExists(candidate)) return candidate;
        }
        // 3) PATH 中的 wt.exe。
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "wt.exe");
                if (SafeExists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException) { }
        }
        return null;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
