using System.Text.RegularExpressions;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Winget;

/// <summary>Parses human-readable show output without inventing absent metadata.</summary>
internal static class WingetDetailsParser
{
    internal static bool IsSafeId(string? id) => id is { Length: <= 256 }
        && Regex.IsMatch(id, @"\A[A-Za-z0-9_][A-Za-z0-9_+\-]*(?:\.[A-Za-z0-9_+\-]+)+\z", RegexOptions.CultureInvariant);

    internal static bool TryParse(string output, string requestedId, out WingetPackage? package)
    {
        package = null;
        var lines = WingetTableParser.CleanOutput(output).Split('\n');
        var headerIndex = -1;
        string? name = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var header = Regex.Match(lines[i].Trim(), @"\A(?:Found\s+|已找到\s*)(?<name>.+?)\s*\[(?<id>[^\]]+)\]\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!header.Success) continue;
            if (headerIndex >= 0 || !header.Groups["id"].Value.Equals(requestedId, StringComparison.OrdinalIgnoreCase)) return false;
            headerIndex = i;
            name = header.Groups["name"].Value.Trim();
        }
        if (headerIndex < 0) return false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? active = null;
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            // Installer fields are nested and must never overwrite package metadata.
            if (line.Trim() is "Installer:" or "安装程序:" or "安装器:" or "安装程序：" or "安装器：" or "安装:" or "安装：") break;
            var match = Regex.Match(line, @"\A(?<label>[^\s:：][^:：]*?)\s*[:：]\s*(?<value>.*)\z", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                active = Key(match.Groups["label"].Value.Trim());
                if (active is not null) fields[active] = match.Groups["value"].Value.Trim();
            }
            else if (active is "Description" or "ReleaseNotes" && (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0])))
                fields[active] += "\n" + line.Trim();
            else active = null;
        }
        string? Get(string key) => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        package = new WingetPackage
        {
            Id = requestedId, Name = name, Version = Get("Version"), Publisher = Get("Publisher"),
            PublisherUrl = Get("PublisherUrl"), Description = Get("Description"), Homepage = Get("Homepage"),
            License = Get("License"), LicenseUrl = Get("LicenseUrl"), ReleaseNotes = Get("ReleaseNotes"), ReleaseNotesUrl = Get("ReleaseNotesUrl"),
        };
        return true;
    }

    private static string? Key(string label) => label.ToLowerInvariant() switch
    {
        "version" or "版本" => "Version",
        "publisher" or "发布者" or "发行商" or "发布服务器" => "Publisher",
        "publisher url" or "发布者 url" or "发行商 url" or "发布服务器 url" => "PublisherUrl",
        "description" or "描述" => "Description",
        "homepage" or "package url" or "主页" or "包 url" or "程序包 url" => "Homepage",
        "license" or "许可证" or "许可" => "License",
        "license url" or "许可证 url" or "许可 url" => "LicenseUrl",
        "release notes" or "发行说明" or "发布说明" => "ReleaseNotes",
        "release notes url" or "发行说明 url" or "发布说明 url" => "ReleaseNotesUrl",
        _ => null,
    };
}
