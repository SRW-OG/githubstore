using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GitHubStore.App.Services.Markdown;

/// <summary>
/// 发行说明用的纯 BCL 轻量解析器，不追求 CommonMark 完备性。
/// 字符预算均按 UTF-16 代码单元计算；截断时不拆开代理项对。
/// </summary>
public static partial class MarkdownParser
{
    public const int DefaultMaximumBlocks = 600;
    public const int MaximumInputCharacters = 65_536;
    public const int MaximumInlineCharacters = 4_096;
    public const int MaximumInlineFragments = 128;
    public const int MaximumTotalInlineFragments = 2_048;
    public const int MaximumCodeCharacters = 8_192;
    public const int MaximumCodeLines = 128;
    public const int MaximumCodeLanguageCharacters = 64;
    public const int MaximumTableRows = 32; // 不含表头。
    public const int MaximumTableColumns = 12;
    public const int MaximumTableCells = 256; // 含表头和补齐的空单元格。
    public const int MaximumTotalTableCells = 512;
    public const int MaximumTableCellCharacters = 256;

    /// <summary>非正 maximumBlocks 返回空；正数仍受 DefaultMaximumBlocks 硬上限约束。</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown, int maximumBlocks = DefaultMaximumBlocks)
        => Parse(markdown, maximumBlocks, out _);

    /// <summary>兼容重载：返回被保留的块，并报告所有触发的预算；提示不占用内容块预算。</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown, int maximumBlocks, out MarkdownTruncationReason truncation)
    {
        var reasons = MarkdownTruncationReason.None;
        var input = LimitText(markdown ?? string.Empty, MaximumInputCharacters, out var inputCut);
        if (inputCut) reasons |= MarkdownTruncationReason.InputCharacters;
        if (string.IsNullOrWhiteSpace(input))
        {
            truncation = reasons;
            return [];
        }
        if (maximumBlocks <= 0)
        {
            truncation = reasons | MarkdownTruncationReason.Blocks;
            return [];
        }

        // 必须先裁剪，再归一化和 Split，避免先为整个不可信输入分配行数组。
        var lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var references = ReadReferences(lines);
        var orderedNumbers = new Dictionary<int, int>();
        var previousWasList = false;
        var blockLimit = Math.Min(maximumBlocks, DefaultMaximumBlocks);
        var blocks = new List<MarkdownBlock>();
        var index = 0;
        var paragraph = new List<string>();
        var remainingInlines = MaximumTotalInlineFragments;
        var remainingCells = MaximumTotalTableCells;

        IReadOnlyList<MarkdownInline> Inlines(string text)
        {
            var result = ParseInlinesCore(text, Math.Min(MaximumInlineFragments, remainingInlines), out var inlineReasons, out _, references);
            reasons |= inlineReasons;
            remainingInlines -= result.Count;
            return result;
        }

        IReadOnlyList<string> TableInlines(IReadOnlyList<string> cells, List<IReadOnlyList<MarkdownInline>> parsedCells)
        {
            var result = new List<string>(cells.Count);
            foreach (var cell in cells)
            {
                var inlines = ParseInlinesCore(cell, Math.Min(MaximumInlineFragments, remainingInlines), out var inlineReasons, out var consumed, references);
                reasons |= inlineReasons;
                remainingInlines -= inlines.Count;
                parsedCells.Add(inlines);
                // 保留受预算接受的源前缀，渲染时使用缓存的行内片段以保留引用链接。
                result.Add(cell[..consumed]);
            }
            return result;
        }

        bool TryAddBlock(MarkdownBlock block)
        {
            if (blocks.Count >= blockLimit)
            {
                reasons |= MarkdownTruncationReason.Blocks;
                return false;
            }
            blocks.Add(block);
            return true;
        }

        bool FlushParagraph()
        {
            if (paragraph.Count == 0) return true;
            var text = string.Join(" ", paragraph).Replace("\n ", "\n", StringComparison.Ordinal);
            paragraph.Clear();
            if (blocks.Count >= blockLimit)
            {
                reasons |= MarkdownTruncationReason.Blocks;
                return false;
            }
            return TryAddBlock(new MarkdownParagraph(Inlines(text)));
        }

        bool PrepareBlock()
        {
            if (!FlushParagraph()) return false;
            if (blocks.Count < blockLimit) return true;
            reasons |= MarkdownTruncationReason.Blocks;
            return false;
        }

        while (index < lines.Length && blocks.Count < blockLimit)
        {
            var line = lines[index];
            var trimmed = line.TrimEnd();

            if (TryFence(trimmed, out var fenceMarker, out var fenceLength, out var fenceIndent, out var info))
            {
                previousWasList = false;
                if (!PrepareBlock()) break;
                var language = LimitText(info.Trim(), MaximumCodeLanguageCharacters, out var languageCut);
                if (languageCut) reasons |= MarkdownTruncationReason.CodeCharacters;
                var code = new StringBuilder();
                var codeLines = 0;
                var codeFull = false;
                index++;
                while (index < lines.Length && !IsClosingFence(lines[index], fenceMarker, fenceLength))
                {
                    if (codeLines >= MaximumCodeLines)
                    {
                        reasons |= MarkdownTruncationReason.CodeLines;
                        codeFull = true;
                    }
                    if (!codeFull)
                    {
                        var separator = codeLines > 0 ? "\n" : string.Empty;
                        var available = MaximumCodeCharacters - code.Length;
                        if (separator.Length > available)
                        {
                            reasons |= MarkdownTruncationReason.CodeCharacters;
                            codeFull = true;
                        }
                        else
                        {
                            code.Append(separator);
                            var sourceLine = lines[index];
                            var remove = 0;
                            while (remove < Math.Min(fenceIndent, sourceLine.Length) && sourceLine[remove] == ' ') remove++;
                            var content = LimitText(sourceLine[remove..], available - separator.Length, out var codeCut);
                            code.Append(content);
                            if (codeCut)
                            {
                                reasons |= MarkdownTruncationReason.CodeCharacters;
                                codeFull = true;
                            }
                        }
                    }
                    codeLines++;
                    index++;
                }
                if (index < lines.Length) index++; // 未闭合围栏仍返回受预算约束的代码块。
                TryAddBlock(new MarkdownCodeBlock(code.ToString(), language.Length == 0 ? null : language));
                continue;
            }

            if (SkipOpaqueHtml(lines, ref index))
            {
                if (!FlushParagraph()) break;
                previousWasList = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed) || LinkDefinitionRegex().IsMatch(trimmed))
            {
                if (!FlushParagraph()) break;
                index++;
                continue;
            }

            if (IsHtmlBoundary(trimmed))
            {
                if (!FlushParagraph()) break;
                previousWasList = false;
                index++;
                continue;
            }

            if (index + 1 < lines.Length && IsSetextUnderline(lines[index + 1], out var setextLevel)
                && !ListRegex().IsMatch(line) && !TryQuote(line, out _, out _) && !IsDivider(trimmed))
            {
                if (blocks.Count >= blockLimit) { reasons |= MarkdownTruncationReason.Blocks; break; }
                paragraph.Add(trimmed.Trim());
                var title = string.Join(" ", paragraph);
                paragraph.Clear();
                TryAddBlock(new MarkdownHeading(setextLevel, Inlines(title)));
                previousWasList = false;
                index += 2;
                continue;
            }

            var htmlHeading = HtmlHeadingRegex().Match(trimmed);
            if (htmlHeading.Success)
            {
                if (!PrepareBlock()) break;
                TryAddBlock(new MarkdownHeading(htmlHeading.Groups[1].Value[0] - '0', Inlines(htmlHeading.Groups[2].Value)));
                previousWasList = false;
                index++;
                continue;
            }

            if (IsDivider(trimmed))
            {
                if (!PrepareBlock()) break;
                TryAddBlock(new MarkdownDivider());
                previousWasList = false;
                index++;
                continue;
            }

            var headingMatch = HeadingRegex().Match(trimmed);
            if (headingMatch.Success)
            {
                if (!PrepareBlock()) break;
                var title = ClosingHeadingRegex().Replace(headingMatch.Groups[2].Value, string.Empty).Trim();
                TryAddBlock(new MarkdownHeading(headingMatch.Groups[1].Value.Length, Inlines(title)));
                previousWasList = false;
                index++;
                continue;
            }

            if (trimmed.Contains('|') && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                if (!PrepareBlock()) break;
                var tableCellBudget = Math.Min(MaximumTableCells, remainingCells);
                var headerReasons = MarkdownTruncationReason.None;
                var headers = SplitTableRow(trimmed, Math.Min(MaximumTableColumns, tableCellBudget), ref headerReasons);
                if (tableCellBudget < MaximumTableColumns && headerReasons.HasFlag(MarkdownTruncationReason.TableColumns))
                    headerReasons |= MarkdownTruncationReason.TableCells;
                reasons |= headerReasons;
                var parsedCells = new List<IReadOnlyList<MarkdownInline>>();
                headers = TableInlines(headers, parsedCells);
                var columns = headers.Count;
                var cells = columns;
                index += 2;
                var rows = new List<IReadOnlyList<string>>();
                while (index < lines.Length && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    if (rows.Count >= MaximumTableRows)
                        reasons |= MarkdownTruncationReason.TableRows;
                    else if (columns == 0 || cells + columns > tableCellBudget)
                        reasons |= MarkdownTruncationReason.TableCells;
                    else
                    {
                        var row = SplitTableRow(lines[index], columns, ref reasons).ToList();
                        while (row.Count < columns) row.Add(string.Empty);
                        rows.Add(TableInlines(row, parsedCells));
                        cells += columns;
                    }
                    index++; // 丢弃同一表格的超额行，不能让它们作为段落绕过表格预算。
                }
                remainingCells -= cells;
                previousWasList = false;
                if (columns > 0) TryAddBlock(new MarkdownTable(headers, rows) { ParsedCells = parsedCells });
                else reasons |= MarkdownTruncationReason.TableCells;
                continue;
            }

            if (TryQuote(trimmed, out var quoteDepth, out _))
            {
                if (!PrepareBlock()) break;
                previousWasList = false;
                var quoteText = new StringBuilder();
                while (index < lines.Length && TryQuote(lines[index], out var depth, out var content) && depth == quoteDepth)
                {
                    if (quoteText.Length > 0) quoteText.Append(content.Length == 0 ? "\n\n" : quoteText[^1] == '\n' ? string.Empty : " ");
                    quoteText.Append(content);
                    index++;
                }
                TryAddBlock(new MarkdownQuote(Inlines(quoteText.ToString()), quoteDepth));
                continue;
            }

            var listMatch = ListRegex().Match(line);
            if (listMatch.Success)
            {
                if (!PrepareBlock()) break;
                var indentation = IndentWidth(listMatch.Groups[1].Value);
                var depth = Math.Min(16, indentation / 2);
                var ordered = listMatch.Groups[3].Success;
                var number = 1;
                if (!previousWasList) orderedNumbers.Clear();
                foreach (var key in orderedNumbers.Keys.Where(key => key > indentation).ToArray()) orderedNumbers.Remove(key);
                if (ordered)
                {
                    _ = int.TryParse(listMatch.Groups[3].Value, out number);
                    if (orderedNumbers.TryGetValue(indentation, out var previous)) number = previous == int.MaxValue ? previous : previous + 1;
                    orderedNumbers[indentation] = number;
                }
                else orderedNumbers.Remove(indentation);
                var itemText = new StringBuilder(listMatch.Groups[4].Value.Trim());
                index++;
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])
                    && IndentWidth(lines[index]) > indentation && !ListRegex().IsMatch(lines[index])
                    && !TryFence(lines[index].TrimStart(), out _, out _, out _, out _)
                    && !TryQuote(lines[index].TrimStart(), out _, out _) && !HeadingRegex().IsMatch(lines[index]))
                {
                    itemText.Append(' ').Append(lines[index].Trim());
                    index++;
                }
                var content = itemText.ToString();
                bool? completed = null;
                if (content.Length >= 3 && content[0] == '[' && content[2] == ']'
                    && (content.Length == 3 || char.IsWhiteSpace(content[3])) && content[1] is ' ' or 'x' or 'X')
                {
                    completed = content[1] != ' ';
                    content = content[3..].TrimStart();
                }
                TryAddBlock(new MarkdownListItem(ordered, number, depth, Inlines(content), completed));
                previousWasList = true;
                continue;
            }

            previousWasList = false;
            var slashBreak = line.EndsWith('\\') && !IsEscaped(line, line.Length - 1);
            var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || slashBreak;
            paragraph.Add((slashBreak ? trimmed[..^1] : trimmed).Trim() + (hardBreak ? "\n" : string.Empty));
            index++;
        }

        FlushParagraph();
        if (index < lines.Length && lines.Skip(index).Any(line => !string.IsNullOrWhiteSpace(line)))
            reasons |= MarkdownTruncationReason.Blocks;
        truncation = reasons;
        return blocks;
    }

    /// <summary>未闭合标记按普通文本处理；每次调用均受字符和片段硬预算约束。</summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string? text)
        => ParseInlines(text, out _);

    public static IReadOnlyList<MarkdownInline> ParseInlines(string? text, out MarkdownTruncationReason truncation)
        => ParseInlinesCore(text, MaximumInlineFragments, out truncation, out _);

    private static IReadOnlyList<MarkdownInline> ParseInlinesCore(string? text, int maximumFragments, out MarkdownTruncationReason truncation, out int consumedCharacters,
        IReadOnlyDictionary<string, string>? references = null, int nesting = 0)
    {
        var reasons = MarkdownTruncationReason.None;
        text = LimitText(text ?? string.Empty, MaximumInlineCharacters, out var textCut);
        if (textCut) reasons |= MarkdownTruncationReason.InlineCharacters;
        var inlines = new List<MarkdownInline>();
        var index = 0;
        var consumed = 0;
        var buffer = new StringBuilder();
        var htmlBold = false;
        var htmlItalic = false;
        var htmlCode = false;
        string? htmlLink = null;

        bool AddInline(MarkdownInline inline)
        {
            if (inlines.Count >= maximumFragments)
            {
                reasons |= MarkdownTruncationReason.InlineFragments;
                return false;
            }
            inlines.Add(inline with
            {
                Bold = inline.Bold || htmlBold, Italic = inline.Italic || htmlItalic, Code = inline.Code || htmlCode,
                LinkUrl = inline.LinkUrl ?? htmlLink,
            });
            return true;
        }

        bool FlushBuffer()
        {
            if (buffer.Length == 0) return true;
            var result = AddInline(new MarkdownInline(buffer.ToString()));
            if (result) consumed = index;
            buffer.Clear();
            return result;
        }

        while (index < text.Length)
        {
            if (inlines.Count >= maximumFragments)
            {
                reasons |= MarkdownTruncationReason.InlineFragments;
                break;
            }
            var current = text[index];
            if (current == '\\' && index + 1 < text.Length && text[index + 1] is >= '!' and <= '~' && !char.IsLetterOrDigit(text[index + 1]))
            {
                buffer.Append(text[index + 1]);
                index += 2;
                continue;
            }
            if (current == '&')
            {
                var end = text.IndexOf(';', index + 1);
                if (end > index && end - index <= 32)
                {
                    var entity = text[index..(end + 1)];
                    var decoded = WebUtility.HtmlDecode(entity);
                    if (decoded != entity) { buffer.Append(decoded); index = end + 1; continue; }
                }
            }

            MarkdownInline? marked = null;
            IReadOnlyList<MarkdownInline>? nested = null;
            var next = index;
            if (current == '<')
            {
                if (text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
                {
                    var endComment = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                    index = endComment < 0 ? text.Length : endComment + 3;
                    continue;
                }
                var end = text.IndexOf('>', index + 1);
                if (end > index)
                {
                    var tag = text[index..(end + 1)];
                    var destination = text[(index + 1)..end];
                    if (IsSafeLink(destination))
                    {
                        marked = new MarkdownInline(destination, LinkUrl: destination);
                        next = end + 1;
                    }
                    else
                    {
                        var match = HtmlTagRegex().Match(tag);
                        if (match.Success)
                        {
                            if (!FlushBuffer()) break;
                            var name = match.Groups[2].Value.ToLowerInvariant();
                            var closing = match.Groups[1].Value.Length != 0;
                            if (!closing && name is "script" or "style")
                            {
                                var close = text.IndexOf("</" + name, end + 1, StringComparison.OrdinalIgnoreCase);
                                var closeEnd = close < 0 ? -1 : text.IndexOf('>', close);
                                index = closeEnd < 0 ? text.Length : closeEnd + 1;
                                consumed = index;
                                continue;
                            }
                            if (name is "strong" or "b") htmlBold = !closing;
                            else if (name is "em" or "i") htmlItalic = !closing;
                            else if (name is "code" or "kbd" or "samp") htmlCode = !closing;
                            else if (name == "a")
                            {
                                var href = HtmlAttribute(tag, "href");
                                htmlLink = !closing && IsSafeLink(href) ? href : null;
                            }
                            else if (name == "img" && !closing)
                            {
                                var alt = HtmlAttribute(tag, "alt");
                                // Image URLs are deliberately not navigation targets, even inside an HTML anchor.
                                var link = htmlLink;
                                htmlLink = null;
                                var added = AddInline(new MarkdownInline(string.IsNullOrWhiteSpace(alt) ? "[图片]" : alt));
                                htmlLink = link;
                                if (!added) break;
                            }
                            else if (name == "br" || closing && name is "p" or "div" or "summary") buffer.Append('\n');
                            index = end + 1;
                            consumed = index;
                            continue;
                        }
                    }
                }
            }
            if (marked is null && current == '`')
            {
                var length = RunLength(text, index, '`');
                var end = FindRun(text, index + length, '`', length);
                if (end >= 0)
                {
                    var code = text[(index + length)..end].Replace('\n', ' ');
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Any(c => c != ' ')) code = code[1..^1];
                    marked = new MarkdownInline(code, Code: true);
                    next = end + length;
                }
            }
            if (marked is null && current is '*' or '_' && nesting < 16
                && !(current == '_' && index > 0 && char.IsLetterOrDigit(text[index - 1])))
            {
                var length = Math.Min(3, RunLength(text, index, current));
                var end = FindEmphasisEnd(text, index + length, current, length);
                if (end > index + length && !char.IsWhiteSpace(text[index + length]))
                {
                    if (!FlushBuffer()) break;
                    if (inlines.Count >= maximumFragments) { reasons |= MarkdownTruncationReason.InlineFragments; break; }
                    nested = ParseInlinesCore(text[(index + length)..end], maximumFragments - inlines.Count,
                        out var nestedReasons, out _, references, nesting + 1)
                        .Select(inline => inline with { Bold = inline.Bold || length >= 2, Italic = inline.Italic || length is 1 or 3 }).ToArray();
                    reasons |= nestedReasons;
                    next = end + length;
                }
            }
            if (marked is null && nested is null && (current == '[' || current == '!' && index + 1 < text.Length && text[index + 1] == '[')
                && TryReadLink(text, index, references, out var label, out var url, out next, out var image))
            {
                if (image)
                    marked = new MarkdownInline(string.IsNullOrWhiteSpace(label) ? "[图片]" : WebUtility.HtmlDecode(label));
                else if (nesting < 16)
                {
                    if (!FlushBuffer()) break;
                    if (inlines.Count >= maximumFragments) { reasons |= MarkdownTruncationReason.InlineFragments; break; }
                    nested = ParseInlinesCore(label, maximumFragments - inlines.Count, out var nestedReasons, out _, references, nesting + 1)
                        .Select(inline => inline with { LinkUrl = IsSafeLink(url) ? url : null }).ToArray();
                    reasons |= nestedReasons;
                }
                else marked = new MarkdownInline(label, LinkUrl: IsSafeLink(url) ? url : null);
            }

            if (nested is not null)
            {
                foreach (var inline in nested) { if (!AddInline(inline)) break; }
                index = next;
                consumed = index;
                continue;
            }
            if (marked is not null)
            {
                if (!FlushBuffer()) break;
                // Markdown images remain inert even when embedded in an HTML link.
                var priorLink = htmlLink;
                if (current == '!') htmlLink = null;
                var added = AddInline(marked);
                htmlLink = priorLink;
                if (!added) break;
                index = next;
                consumed = index;
                continue;
            }
            buffer.Append(current);
            index++;
        }
        FlushBuffer();
        truncation = reasons;
        consumedCharacters = consumed;
        return inlines;
    }

    internal static bool IsSafeLink(string url)
    {
        if (url.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        // Uri.UserInfo 对 https://@host 返回空串，仍须拒绝显式的空 userinfo。
        var authorityStart = url.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return false;
        var authority = url.AsSpan(authorityStart + 3);
        var end = authority.IndexOfAny('/', '?', '#');
        if (end >= 0) authority = authority[..end];
        return !authority.Contains('@');
    }

    internal static string LimitText(string text, int maximumCharacters, out bool truncated)
    {
        truncated = text.Length > maximumCharacters;
        if (!truncated) return text;
        var length = Math.Max(0, maximumCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length];
    }

    private static bool IsDivider(string line)
    {
        var stripped = line.Replace(" ", string.Empty);
        return stripped.Length >= 3 && (stripped.All(c => c == '-') || stripped.All(c => c == '*') || stripped.All(c => c == '_'));
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = SplitRawTableCells(line);
        return cells.Count > 0 && cells.All(cell => TableSeparatorCellRegex().IsMatch(cell.Trim()));
    }

    private static IReadOnlyList<string> SplitTableRow(string line, int maximumColumns, ref MarkdownTruncationReason reasons)
    {
        var raw = SplitRawTableCells(line);
        if (raw.Count > maximumColumns)
            reasons |= maximumColumns == 0 ? MarkdownTruncationReason.TableCells : MarkdownTruncationReason.TableColumns;
        var cells = new List<string>();
        foreach (var value in raw.Take(maximumColumns))
        {
            var cell = LimitText(value.Trim(), MaximumTableCellCharacters, out var cellCut);
            if (cellCut) reasons |= MarkdownTruncationReason.TableCellCharacters;
            cells.Add(cell);
        }
        return cells;
    }

    private static IReadOnlyList<string> SplitRawTableCells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|') && !IsEscaped(text, text.Length - 1)) text = text[..^1];
        var cells = new List<string>();
        var start = 0;
        var codeLength = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '`' && !IsEscaped(text, index))
            {
                var run = RunLength(text, index, '`');
                if (codeLength == 0)
                {
                    // An unmatched tick is ordinary text, not a reason to swallow the remaining columns.
                    if (FindRun(text, index + run, '`', run) >= 0) codeLength = run;
                }
                else if (run == codeLength) codeLength = 0;
                index += run - 1;
            }
            else if (text[index] == '|' && codeLength == 0 && !IsEscaped(text, index))
            {
                cells.Add(text[start..index]);
                start = index + 1;
            }
        }
        cells.Add(text[start..]);
        return cells;
    }

    private static bool SkipOpaqueHtml(string[] lines, ref int index)
    {
        var start = lines[index].TrimStart();
        string? close = null;
        if (start.StartsWith("<!--", StringComparison.Ordinal)) close = "-->";
        else if (start.StartsWith("<script", StringComparison.OrdinalIgnoreCase)
            && start.Length > 7 && (char.IsWhiteSpace(start[7]) || start[7] == '>')) close = "</script>";
        else if (start.StartsWith("<style", StringComparison.OrdinalIgnoreCase)
            && start.Length > 6 && (char.IsWhiteSpace(start[6]) || start[6] == '>')) close = "</style>";
        if (close is null) return false;
        while (index < lines.Length)
        {
            var end = lines[index].IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                var remaining = lines[index][(end + close.Length)..];
                if (!string.IsNullOrWhiteSpace(remaining)) lines[index] = remaining;
                else index++;
                return true;
            }
            index++;
        }
        return true;
    }

    private static bool TryFence(string line, out char marker, out int length, out int indent, out string info)
    {
        marker = '\0'; length = 0; indent = 0; info = string.Empty;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3 || indent == line.Length || line[indent] is not ('`' or '~')) return false;
        marker = line[indent];
        length = RunLength(line, indent, marker);
        if (length < 3) return false;
        info = line[(indent + length)..];
        return marker != '`' || !info.Contains('`');
    }

    private static bool IsClosingFence(string line, char marker, int minimumLength)
        => TryFence(line, out var closeMarker, out var closeLength, out _, out var info)
            && marker == closeMarker && closeLength >= minimumLength && string.IsNullOrWhiteSpace(info);

    private static int RunLength(string text, int index, char marker)
    {
        var end = index;
        while (end < text.Length && text[end] == marker) end++;
        return end - index;
    }

    private static int FindRun(string text, int start, char marker, int length)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] != marker) continue;
            var run = RunLength(text, index, marker);
            if (run == length) return index;
            index += run - 1;
        }
        return -1;
    }

    private static int FindEmphasisEnd(string text, int start, char marker, int length)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '`' && !IsEscaped(text, index))
            {
                var ticks = RunLength(text, index, '`');
                var close = FindRun(text, index + ticks, '`', ticks);
                if (close >= 0) { index = close + ticks - 1; continue; }
            }
            if (text[index] != marker || IsEscaped(text, index)) continue;
            var run = RunLength(text, index, marker);
            if (run >= length && !char.IsWhiteSpace(text[index - 1])
                && !(marker == '_' && index + run < text.Length && char.IsLetterOrDigit(text[index + run]))) return index + run - length;
            index += run - 1;
        }
        return -1;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        while (index > 0 && text[--index] == '\\') slashes++;
        return slashes % 2 != 0;
    }

    private static int IndentWidth(string line)
    {
        var width = 0;
        foreach (var character in line)
        {
            if (character == ' ') width++;
            else if (character == '\t') width += 4 - width % 4;
            else break;
        }
        return width;
    }

    private static bool TryQuote(string line, out int depth, out string content)
    {
        var index = 0;
        depth = 0;
        content = string.Empty;
        while (index < line.Length && line[index] == ' ') index++;
        if (index > 3) return false;
        while (index < line.Length && line[index] == '>')
        {
            depth++;
            index++;
            if (index < line.Length && line[index] == ' ') index++;
        }
        if (depth == 0) return false;
        depth = Math.Min(depth, 16);
        content = line[index..].TrimEnd();
        return true;
    }

    private static bool IsSetextUnderline(string line, out int level)
    {
        level = 0;
        var indent = line.Length - line.TrimStart(' ').Length;
        var text = line.Trim();
        if (indent > 3 || text.Length == 0 || text[0] is not ('=' or '-') || !text.All(c => c == text[0])) return false;
        level = text[0] == '=' ? 1 : 2;
        return true;
    }

    private static bool IsHtmlBoundary(string line)
        => HtmlBoundaryRegex().IsMatch(line.Trim());

    private static string HtmlAttribute(string tag, string name)
    {
        foreach (Match match in HtmlAttributeRegex().Matches(tag))
        {
            if (!string.Equals(match.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            return WebUtility.HtmlDecode(value);
        }
        return string.Empty;
    }

    private static string ReferenceKey(string label)
        => string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static IReadOnlyDictionary<string, string> ReadReferences(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var marker = '\0';
        var length = 0;
        foreach (var line in lines)
        {
            if (marker != '\0')
            {
                if (IsClosingFence(line, marker, length)) marker = '\0';
                continue;
            }
            if (TryFence(line, out marker, out length, out _, out _)) continue;
            marker = '\0';
            var match = LinkDefinitionRegex().Match(line);
            if (match.Success && result.Count < 256 && TryDestination(match.Groups[2].Value, out var url))
                result.TryAdd(ReferenceKey(match.Groups[1].Value), url);
        }
        return result;
    }

    private static bool TryReadLink(string text, int start, IReadOnlyDictionary<string, string>? references,
        out string label, out string url, out int next, out bool image)
    {
        label = url = string.Empty;
        next = start;
        image = text[start] == '!';
        var opening = image ? start + 1 : start;
        var balance = 1;
        var close = opening + 1;
        for (; close < text.Length; close++)
        {
            if (IsEscaped(text, close)) continue;
            if (text[close] == '[') balance++;
            else if (text[close] == ']' && --balance == 0) break;
        }
        if (close == text.Length) return false;
        label = text[(opening + 1)..close];
        next = close + 1;
        if (next < text.Length && text[next] == '(')
        {
            balance = 1;
            var end = next + 1;
            var angle = false;
            var quote = '\0';
            for (; end < text.Length; end++)
            {
                if (IsEscaped(text, end)) continue;
                var character = text[end];
                if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
                if (character is '\'' or '"' && end > next + 1 && char.IsWhiteSpace(text[end - 1])) { quote = character; continue; }
                if (character == '<') { angle = true; continue; }
                if (character == '>') { angle = false; continue; }
                if (angle) continue;
                if (character == '(') balance++;
                else if (character == ')' && --balance == 0) break;
            }
            if (end == text.Length || !TryDestination(text[(next + 1)..end], out url)) return false;
            next = end + 1;
            return true;
        }
        var key = label;
        if (next < text.Length && text[next] == '[')
        {
            var end = text.IndexOf(']', next + 1);
            if (end < 0) return false;
            var explicitKey = text[(next + 1)..end];
            if (explicitKey.Length > 0) key = explicitKey;
            next = end + 1;
        }
        if (references is null || !references.TryGetValue(ReferenceKey(key), out var target)) return false;
        url = target;
        return true;
    }

    private static bool TryDestination(string source, out string url)
    {
        url = string.Empty;
        var text = source.Trim();
        if (text.Length == 0) return true;
        var end = 0;
        if (text[0] == '<')
        {
            end = text.IndexOf('>');
            if (end < 0) return false;
            url = text[1..end];
            end++;
        }
        else
        {
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            url = text[..end];
        }
        var title = text[end..].Trim();
        if (title.Length > 0 && !(title.Length >= 2 && (title[0] == '"' && title[^1] == '"'
            || title[0] == '\'' && title[^1] == '\'' || title[0] == '(' && title[^1] == ')'))) return false;
        url = WebUtility.HtmlDecode(BackslashEscapeRegex().Replace(url, "$1"));
        return true;
    }

    [GeneratedRegex("^ {0,3}(#{1,6})(?:[ \\t]+(.*)|[ \\t]*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("[ \\t]+#+[ \\t]*$")]
    private static partial Regex ClosingHeadingRegex();

    [GeneratedRegex("^([ \\t]*)([-*+]|(\\d{1,9})[.)])[ \\t]+(.*)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex("^ {0,3}\\[([^\\]]+)\\]:[ \\t]*(.*)$")]
    private static partial Regex LinkDefinitionRegex();

    [GeneratedRegex("^:?-{3,}:?$")]
    private static partial Regex TableSeparatorCellRegex();

    [GeneratedRegex("^ {0,3}<h([1-6])(?:[ \\t]+[^<>]*)?>(.*?)</h\\1>[ \\t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadingRegex();

    [GeneratedRegex("^</?(?:p|div|details|summary|section|article|ul|ol|table|thead|tbody)(?:[ \\t]+[^<>]*)?[ \\t]*>$", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBoundaryRegex();

    [GeneratedRegex("^<(/?)([a-z][a-z0-9]*)(?:[ \\t]+[^<>]*)?[ \\t]*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("([a-z][a-z0-9-]*)[ \\t]*=[ \\t]*(?:\"([^\"]*)\"|'([^']*)'|([^ \\t<>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex("\\\\([!\"#$%&'()*+,./:;<=>?@\\[\\]\\\\^_`{|}~-])")]
    private static partial Regex BackslashEscapeRegex();
}
