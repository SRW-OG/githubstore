using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Inventory;

namespace GitHubStore.App.Services.Inventory;

/// <summary>不搜索 PATH，不尝试路径前缀，不修改第三方参数。</summary>
public sealed class UninstallCommandValidator
{
    private readonly string _systemDirectory;
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
        "rundll32.exe", "regsvr32.exe", "bash.exe", "sh.exe", "wsl.exe",
    };

    // 默认值来自操作系统特殊目录，绝不来自 PATH 或当前工作目录。
    public UninstallCommandValidator(string? systemDirectory = null) =>
        _systemDirectory = systemDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.System);

    public bool TryValidate(string? commandLine, out UninstallCommand? command, out string? errorCode)
    {
        command = null;
        errorCode = UninstallErrorCodes.InvalidUninstallCommand;
        if (!CommandLineSplitter.TrySplit(commandLine, out var fileName, out var arguments) ||
            HasSilentSwitch(arguments)) return false;

        try
        {
            var isMsiAlias = fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
            if (isMsiAlias)
            {
                if (string.IsNullOrWhiteSpace(_systemDirectory) || !Path.IsPathFullyQualified(_systemDirectory)) return false;
                fileName = Path.Combine(_systemDirectory, "msiexec.exe");
            }

            if (!Path.IsPathFullyQualified(fileName) ||
                !Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                fileName.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;

            var fullPath = Path.GetFullPath(fileName);
            var name = Path.GetFileName(fullPath);
            if (Interpreters.Contains(name)) return false;
            if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(_systemDirectory) ||
                 !Path.IsPathFullyQualified(_systemDirectory) ||
                 !fullPath.Equals(Path.GetFullPath(Path.Combine(_systemDirectory, "msiexec.exe")),
                     StringComparison.OrdinalIgnoreCase))) return false;

            if (!File.Exists(fullPath))
            {
                errorCode = UninstallErrorCodes.ExecutableNotFound;
                return false;
            }
            command = new UninstallCommand(fullPath, arguments);
            errorCode = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool HasSilentSwitch(string arguments)
    {
        // 仅用于识别已知开关；原始参数不会被重组或剥除。
        var token = new System.Text.StringBuilder();
        var quoted = false;
        var slashes = 0;
        foreach (var c in arguments)
        {
            if (c == '"' && slashes % 2 == 0) quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (IsSilent(token.ToString())) return true;
                token.Clear();
            }
            else token.Append(c);
            slashes = c == '\\' ? slashes + 1 : 0;
        }
        return IsSilent(token.ToString());
    }

    private static bool IsSilent(string value)
    {
        var token = value.ToLowerInvariant();
        return token is "/s" or "-s" or "/silent" or "-silent" or "--silent"
            or "/verysilent" or "/quiet" or "-quiet" or "--quiet" or "/passive"
            or "/unattended" or "--unattended" or "--silent=true" or "--silent=1"
            or "--quiet=true" or "--quiet=1" or "/suppressmsgboxes"
            or "/q" or "-q" or "/qn" or "/qn+" or "/qn-" or "/qb" or "/qb!" or "/qb-"
            or "/qb+" or "/qb!-" or "/qb!+" or "/qb-!" or "/qb+!";
    }
}
