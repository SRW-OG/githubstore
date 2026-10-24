using System.Globalization;
using GitHubStore.Core.Abstractions;
// 简单名 Version 同时命中 System.Version 与本项目契约类型，别名让签名保持可读且无歧义。
using Version = GitHubStore.Core.Abstractions.Version;

namespace GitHubStore.Core.Catalog;

/// <summary>
/// 候选版本相对“已安装版本”的关系。
/// <para>
/// <see cref="Different"/> 表示<em>无法判定</em>（版本号格式不可比较，或两侧确实是不同的不可解析文本），
/// 它绝不等于“有更新”：调用方不得把它当作 <see cref="Newer"/> 处理。
/// 这一取向是刻意的——宁可漏报更新，也不误报更新。
/// </para>
/// </summary>
public enum VersionRelation
{
    /// <summary>无法判定为更新、降级或相等。默认值即最保守的一档。</summary>
    Different = 0,

    /// <summary>可比较且两侧等价（已按大小写与前导 v 归一化）。</summary>
    Same,

    /// <summary>可比较且候选版本更高。</summary>
    Newer,

    /// <summary>可比较且候选版本更低（降级，不是更新）。</summary>
    Older,
}

/// <summary>
/// 已安装版本与候选发布标签的比较。只做纯文本判定：不联网、不读文件、不改变任何状态。
/// <para>
/// <b>文本优先</b>：<see cref="Version.Raw"/> 是权威事实；只有当 Raw 无法解析时，才退回到登记里
/// 同样来自持久化数据的 <see cref="Version.Scheme"/>/<see cref="Version.Parts"/>。
/// 这样做的原因是本程序自己的安装流程用原始标签写入 <see cref="VersionScheme.Opaque"/>，
/// 若把该枚举当作“声明不可比较”，任何真实更新都将永远无法被识别。
/// </para>
/// <para>
/// <b>比较规则</b>（与 docs/01 §3.4 一致，并按“宁可漏报”收紧）：
/// 去前导 <c>v</c>/<c>V</c>、去 <c>+build</c> 元数据 → 点分数字段（1–4 段，缺段补 0）按数值比较；
/// 纯日期形式（<c>20240115</c> 或 <c>2024.01.15</c>）按 y/m/d 比较；
/// 预发布标识按 SemVer 排序（数字段 &lt; 字母段，短列表 &lt; 长列表，正式版 &gt; 预发布版）；
/// 无法解析的一侧与可解析的一侧<em>不可比较</em>；数字版本与日期版本<em>不可比较</em>。
/// </para>
/// </summary>
public static class VersionComparer
{
    /// <summary>最长可接受的版本文本长度；超过即按不可比较处理，不做截断猜测。</summary>
    private const int MaximumVersionLength = 256;

    /// <summary>日期形式的合理年份区间；区间外的纯数字按普通数字段处理。</summary>
    private const int MinimumYear = 1900;
    private const int MaximumYear = 2999;

    /// <summary>单个数字段的最长位数；超过即视为不可解析，避免溢出与臆测。</summary>
    private const int MaximumSegmentDigits = 18;

    /// <summary>
    /// 判断候选发布标签相对已安装版本的关系。任何不可比较的情形都返回
    /// <see cref="VersionRelation.Different"/>，永远不会返回 <see cref="VersionRelation.Newer"/>。
    /// </summary>
    public static VersionRelation Relate(Version installed, string? candidateTag)
    {
        var comparison = Compare(installed, candidateTag);
        return RelateComparison(comparison);
    }

    /// <summary>
    /// 与 <see cref="Relate"/> 相同的判定，但接受两段原始文本，便于调用方在没有
    /// <see cref="Version"/> 记录时复用同一把尺子。
    /// </summary>
    public static VersionRelation RelateText(string? installedRaw, string? candidateTag) =>
        RelateComparison(CompareText(installedRaw, candidateTag));

    /// <summary>
    /// 比较结果。<see cref="VersionCompare.Result"/> 的符号含义固定为
    /// “候选相对已安装”：&gt; 0 表示候选更新，&lt; 0 表示候选更旧，0 表示相等或不可判定；
    /// <see cref="VersionCompare.Comparable"/> 为 false 时 Result 恒为 0，且调用方必须忽略它。
    /// </summary>
    public static VersionCompare Compare(Version installed, string? candidateTag)
    {
        if (installed is null) return new VersionCompare(0, false);
        return CompareParsed(ParseInstalled(installed), ParseText(candidateTag));
    }

    /// <summary>两段原始文本的比较，语义与 <see cref="Compare"/> 完全一致。</summary>
    public static VersionCompare CompareText(string? installedRaw, string? candidateTag) =>
        CompareParsed(ParseText(installedRaw), ParseText(candidateTag));

    private static VersionRelation RelateComparison(VersionCompare comparison)
    {
        if (!comparison.Comparable) return VersionRelation.Different;
        return comparison.Result switch
        {
            > 0 => VersionRelation.Newer,
            < 0 => VersionRelation.Older,
            _ => VersionRelation.Same,
        };
    }

    private static VersionCompare CompareParsed(Parsed? left, Parsed? right)
    {
        if (left is null || right is null) return new VersionCompare(0, false);
        // 不可解析文本一律“不可比较”——包括两段完全相同的不可解析文本。
        // 这不是保守过头的选择：没有任何依据说明 "nightly" 与 "nightly" 是同一个发行版的不同版本，
        // 而一旦在这里判定等价，调用方就可能把它当成“已是最新”而不再提示用户。
        if (left.Family == VersionFamily.Opaque || right.Family == VersionFamily.Opaque)
            return new VersionCompare(0, false);
        // 数字版本与日期版本之间没有可靠序关系（例如 "1.0.0" 与 "20240115"）：不可比较。
        if (left.Family != right.Family) return new VersionCompare(0, false);
        // Result 的方向固定为“候选相对已安装”（见 Compare 的文档）：因此以 right(候选) 为被减数。
        var result = CompareParts(right.Parts, left.Parts);
        if (result == 0) result = ComparePrerelease(right.Pre, left.Pre);
        return new VersionCompare(result, true);
    }

    /// <summary>逐段数值比较，返回符号等于 <c>left - right</c>。缺段补 0：1.2 与 1.2.0 等价。</summary>
    private static int CompareParts(long[] left, long[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var difference = (index < left.Length ? left[index] : 0).CompareTo(index < right.Length ? right[index] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }

    /// <summary>
    /// SemVer 预发布排序：数字标识符按数值比较且恒低于字母标识符；字母标识符按大小写不敏感比较；
    /// 标识符列表短的一方更小（alpha &lt; alpha.1）；有预发布标识的一方低于正式版。
    /// 大小写不敏感是刻意收的一档：大小写差异不应产生“有更新”的假象。
    /// </summary>
    private static int ComparePrerelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            if (index >= left.Length) return -1;
            if (index >= right.Length) return 1;
            var leftNumeric = IsNumericIdentifier(left[index]);
            var rightNumeric = IsNumericIdentifier(right[index]);
            int difference;
            if (leftNumeric && rightNumeric) difference = CompareNumericIdentifier(left[index], right[index]);
            else if (leftNumeric) difference = -1;
            else if (rightNumeric) difference = 1;
            else difference = string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
            if (difference != 0) return difference;
        }
        return 0;
    }

    private static bool IsNumericIdentifier(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    /// <summary>按数值比较纯数字标识符，不经过整数解析，避免超长标识符溢出。</summary>
    private static int CompareNumericIdentifier(string left, string right)
    {
        var leftTrimmed = left.TrimStart('0');
        var rightTrimmed = right.TrimStart('0');
        if (leftTrimmed.Length != rightTrimmed.Length) return leftTrimmed.Length.CompareTo(rightTrimmed.Length);
        return string.CompareOrdinal(leftTrimmed, rightTrimmed);
    }

    /// <summary>
    /// 已安装版本：先解析 <see cref="Version.Raw"/>；只有它解析不出来时才使用登记里的结构化字段，
    /// 因为这些字段同样来自持久化登记，不是调用方临时提出的新主张。
    /// </summary>
    private static Parsed ParseInstalled(Version installed)
    {
        if (ParseText(installed.Raw) is { } parsed) return parsed;
        var normalized = NormalizeOpaque(installed.Raw);
        var prerelease = SplitPrerelease(installed.Pre);
        if (prerelease is not null && installed.Parts.Count is >= 1 and <= 4 && installed.Parts.All(part => part >= 0))
        {
            var parts = installed.Parts.ToArray();
            if (installed.Scheme == VersionScheme.Date)
            {
                var date = parts.Length == 1 ? DecomposeCompactDate(parts[0]) : parts;
                if (date is not null) return new Parsed(VersionFamily.Date, date, prerelease, normalized);
            }
            else if (installed.Scheme is VersionScheme.Semver or VersionScheme.Quad)
            {
                return new Parsed(VersionFamily.Numeric, parts, prerelease, normalized);
            }
        }
        return new Parsed(VersionFamily.Opaque, [], [], normalized);
    }

    private static Parsed? ParseText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.Length > MaximumVersionLength) return null;
        // 只去一个前导 v/V，且仅当其出现在最前面；"v2ray" 这类产品名不会被切掉首字母后当成数字。
        if (text.Length > 1 && text[0] is 'v' or 'V') text = text[1..];
        var metadata = text.IndexOf('+');
        if (metadata >= 0) text = text[..metadata];
        if (text.Length == 0) return null;
        var core = text;
        string[] prerelease = [];
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            core = text[..dash];
            var split = SplitPrerelease(text[(dash + 1)..]);
            if (split is null || split.Length == 0) return null;
            prerelease = split;
        }
        if (core.Length == 0) return null;
        var segments = core.Split('.');
        if (segments.Length == 0 || segments.Length > 4) return null;
        var parts = new long[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length is 0 or > MaximumSegmentDigits || !segment.All(char.IsAsciiDigit)) return null;
            if (!long.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out parts[index])) return null;
        }
        // 日期形式优先于数字形式：2024.01.15 与 20240115 必须是同一种方案，才能互相比较。
        if (parts.Length == 3 && IsCalendarDate(parts[0], parts[1], parts[2]))
            return new Parsed(VersionFamily.Date, parts, prerelease, text);
        if (parts.Length == 1 && DecomposeCompactDate(parts[0]) is { } compact)
            return new Parsed(VersionFamily.Date, compact, prerelease, text);
        // 单个年份（标签形如 "2024"）按日期方案处理：把它当数字会让 "1.0.0" < "2024" 得出
        // “有更新”的错误结论，而两者之间其实没有可靠序关系。
        if (parts.Length == 1 && parts[0] is >= MinimumYear and <= MaximumYear)
            return new Parsed(VersionFamily.Date, parts, prerelease, text);
        return new Parsed(VersionFamily.Numeric, parts, prerelease, text);
    }

    private static bool IsCalendarDate(long year, long month, long day) =>
        year is >= MinimumYear and <= MaximumYear && month is >= 1 and <= 12 && day is >= 1 and <= 31;

    private static long[]? DecomposeCompactDate(long value)
    {
        if (value < MinimumYear * 10000L || value > MaximumYear * 10000L + 1231) return null;
        var year = value / 10000;
        var month = value / 100 % 100;
        var day = value % 100;
        return IsCalendarDate(year, month, day) ? [year, month, day] : null;
    }

    /// <summary>预发布标识按点分；出现空段或非法字符时返回 null，由调用方整体降级为不可解析。</summary>
    private static string[]? SplitPrerelease(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var identifiers = value.Split('.');
        foreach (var identifier in identifiers)
        {
            if (identifier.Length == 0) return null;
            foreach (var character in identifier)
                if (!char.IsAsciiLetterOrDigit(character) && character != '-') return null;
        }
        return identifiers;
    }

    /// <summary>不可解析版本做等价比较时的规范化：去空白、去一个前导 v/V、去 +build 元数据。</summary>
    private static string NormalizeOpaque(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var text = raw.Trim();
        if (text.Length > MaximumVersionLength) return text;
        if (text.Length > 1 && text[0] is 'v' or 'V') text = text[1..];
        var metadata = text.IndexOf('+');
        if (metadata >= 0) text = text[..metadata];
        return text;
    }

    private enum VersionFamily { Opaque, Numeric, Date }

    private sealed record Parsed(VersionFamily Family, long[] Parts, string[] Pre, string Normalized);
}
