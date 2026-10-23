namespace GitHubStore.Core.Inventory;

/// <summary>
/// 将明确的可执行文件与原始参数分开。含空格的路径必须加引号；不猜测路径前缀。
/// 此处只解析，是否允许启动及文件是否存在由平台层验证。
/// </summary>
public static class CommandLineSplitter
{
    /// <summary>兼容原签名；非法或有歧义的输入返回两个空串。</summary>
    public static (string FileName, string Arguments) Split(string? commandLine) =>
        TrySplit(commandLine, out var fileName, out var arguments)
            ? (fileName, arguments) : (string.Empty, string.Empty);

    public static bool TrySplit(string? commandLine, out string fileName, out string arguments)
    {
        fileName = arguments = string.Empty;
        var text = commandLine?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Any(c => char.IsControl(c) && c != '\t')) return false;

        string candidate;
        string rest;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1 || (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1]))) return false;
            candidate = text[1..end];
            rest = text[(end + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(candidate)) return false;
        }
        else
        {
            var end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            candidate = text[..end];
            rest = text[end..].Trim();
            if (candidate.Contains('"')) return false;
            // A token such as C:\App must never become the executable prefix of
            // C:\App Folder\uninstall.exe. Bare msiexec is the sole system alias.
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !candidate.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (!HasBalancedQuotes(rest)) return false;
        fileName = candidate;
        arguments = rest;
        return true;
    }

    private static bool HasBalancedQuotes(string arguments)
    {
        var quoted = false;
        var slashes = 0;
        foreach (var c in arguments)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"' && slashes % 2 == 0) quoted = !quoted;
            slashes = 0;
        }
        return !quoted;
    }
}
