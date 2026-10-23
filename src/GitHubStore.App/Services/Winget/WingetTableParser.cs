using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Winget;

/// <summary>Reads the CLI's terminal columns; whitespace splitting loses single-gap rows.</summary>
internal static class WingetTableParser
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.CultureInvariant);
    private static readonly Regex PackageId = new(@"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_+\-]+)+\z", RegexOptions.CultureInvariant);

    internal static string CleanOutput(string output) => Ansi.Replace(output, string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static bool TryParse(string output, int limit, out IReadOnlyList<WingetPackage> items)
    {
        var results = new List<WingetPackage>();
        items = results;
        var lines = CleanOutput(output).Split('\n');
        var separator = Array.FindIndex(lines, line =>
        {
            var value = line.Trim();
            return value.Length >= 8 && (value.All(c => c == '-') || value.All(c => c == '─'));
        });
        if (separator <= 0)
            return lines.Any(line => line.Trim().Equals("No package found matching input criteria.", StringComparison.OrdinalIgnoreCase)
                || line.Trim().Equals("找不到与输入条件匹配的程序包。", StringComparison.Ordinal)
                || line.Trim().Equals("未找到与输入条件匹配的程序包。", StringComparison.Ordinal));

        var header = lines[separator - 1];
        var headings = Regex.Matches(header, @"\S+");
        if (headings.Count < 3) return false;
        var starts = headings.Select(match => DisplayWidth(header[..match.Index])).ToArray();
        var sourceColumn = -1;
        for (var column = 3; column < headings.Count; column++)
            if (headings[column].Value.Equals("Source", StringComparison.OrdinalIgnoreCase) || headings[column].Value == "源")
                sourceColumn = column;

        var malformed = false;
        for (var index = separator + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("已找到", StringComparison.Ordinal) || trimmed.StartsWith("Found ", StringComparison.OrdinalIgnoreCase)) continue;
            // Other repositories can appear in captured output from older, unscoped calls.
            if (sourceColumn >= 0 && !Column(line, starts, sourceColumn).Equals("winget", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Column(line, starts, 0);
            var id = Column(line, starts, 1);
            var version = Column(line, starts, 2);
            if (name.Length == 0 || !PackageId.IsMatch(id) || version.Length == 0)
            {
                malformed = true;
                continue;
            }
            results.Add(new WingetPackage { Id = id, Name = name, Version = version });
        }
        if (malformed) return false;
        items = results.Take(limit).ToArray();
        return true;
    }

    private static string Column(string line, int[] starts, int column)
    {
        var start = starts[column];
        var end = column + 1 < starts.Length ? starts[column + 1] : int.MaxValue;
        var position = 0;
        var value = new StringBuilder();
        foreach (var rune in line.EnumerateRunes())
        {
            if (position >= end) break;
            if (position >= start) value.Append(rune.ToString());
            position += CellWidth(rune);
        }
        return value.ToString().Trim();
    }

    private static int DisplayWidth(string value)
    {
        var width = 0;
        foreach (var rune in value.EnumerateRunes()) width += CellWidth(rune);
        return width;
    }

    private static int CellWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;
        var value = rune.Value;
        // Terminal full-width CJK, Hangul, full-width forms and supplementary ideographs.
        return value is >= 0x1100 and <= 0x115F or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
    }
}
