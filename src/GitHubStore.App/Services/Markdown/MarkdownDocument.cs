namespace GitHubStore.App.Services.Markdown;

/// <summary>Markdown 渲染用的最小块模型；不追求规范完备，只覆盖发行说明常见写法。</summary>
public abstract record MarkdownBlock;

public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;
public sealed record MarkdownListItem(bool Ordered, int Number, int Depth, IReadOnlyList<MarkdownInline> Inlines, bool? Checked = null) : MarkdownBlock;
public sealed record MarkdownCodeBlock(string Text, string? Language) : MarkdownBlock;
public sealed record MarkdownQuote(IReadOnlyList<MarkdownInline> Inlines, int Depth = 1) : MarkdownBlock;
public sealed record MarkdownDivider : MarkdownBlock;
public sealed record MarkdownTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) : MarkdownBlock
{
    /// <summary>Header cells followed by body cells; null for manually constructed tables.</summary>
    public IReadOnlyList<IReadOnlyList<MarkdownInline>>? ParsedCells { get; init; }
}

/// <summary>行内片段；文本已去除 Markdown 标记，只保留展示属性。</summary>
public sealed record MarkdownInline(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? LinkUrl = null);

/// <summary>保留的内容仍可渲染；非 None 表示输入的一部分因硬预算被省略。</summary>
[Flags]
public enum MarkdownTruncationReason
{
    None = 0,
    InputCharacters = 1 << 0,
    Blocks = 1 << 1,
    InlineCharacters = 1 << 2,
    InlineFragments = 1 << 3,
    CodeCharacters = 1 << 4,
    CodeLines = 1 << 5,
    TableRows = 1 << 6,
    TableColumns = 1 << 7,
    TableCells = 1 << 8,
    TableCellCharacters = 1 << 9,
}

