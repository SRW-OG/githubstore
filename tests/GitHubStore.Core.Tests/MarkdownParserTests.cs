using GitHubStore.App.Services.Markdown;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>
/// 发行说明 Markdown 解析。解析器是纯 BCL，这些用例不触碰 UI 与网络。
/// 目标不是 CommonMark 完备性，而是把「渲染成什么样」的既有行为固定下来。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MarkdownParserTests
{
    // ── 空输入 ───────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\t ")]
    public void Parse_OnNullOrBlankInput_ReturnsNoBlocks(string? markdown)
    {
        Assert.Empty(MarkdownParser.Parse(markdown));
    }

    // ── 标题 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("# One", 1)]
    [InlineData("## Two", 2)]
    [InlineData("### Three", 3)]
    [InlineData("#### Four", 4)]
    [InlineData("##### Five", 5)]
    [InlineData("###### Six", 6)]
    public void Heading_MapsHashCountToLevel(string markdown, int expectedLevel)
    {
        var heading = Assert.IsType<MarkdownHeading>(Assert.Single(MarkdownParser.Parse(markdown)));

        Assert.Equal(expectedLevel, heading.Level);
        Assert.NotEmpty(heading.Inlines);
    }

    [Fact]
    public void Heading_WithoutSpaceAfterHashes_IsTreatedAsParagraph()
    {
        // 七个及以上 # 或缺少空格都不是标题；这里是后者。
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse("##NoSpace")));

        Assert.Equal("##NoSpace", InlineText(paragraph.Inlines));
    }

    [Fact]
    public void MultipleHeadings_ProduceOneBlockEach()
    {
        var blocks = MarkdownParser.Parse("# First\n## Second\n###### Sixth");

        Assert.Equal(3, blocks.Count);
        Assert.Equal(new[] { 1, 2, 6 }, blocks.Cast<MarkdownHeading>().Select(h => h.Level));
    }

    // ── 段落 ─────────────────────────────────────────────────

    [Fact]
    public void ConsecutiveLines_AreJoinedIntoASingleParagraph()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(
            Assert.Single(MarkdownParser.Parse("first line\nsecond line")));

        Assert.Equal("first line second line", InlineText(paragraph.Inlines));
    }

    [Fact]
    public void BlankLineSeparatesParagraphs()
    {
        var blocks = MarkdownParser.Parse("first\n\nsecond");

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.IsType<MarkdownParagraph>(block));
    }

    [Fact]
    public void PlainText_ProducesOneInlineWithoutMarkers()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(
            Assert.Single(MarkdownParser.Parse("just words")));
        var inline = Assert.Single(paragraph.Inlines);

        Assert.Equal("just words", inline.Text);
        Assert.False(inline.Bold);
        Assert.False(inline.Italic);
        Assert.False(inline.Code);
        Assert.Null(inline.LinkUrl);
    }

    // ── 行内标记 ─────────────────────────────────────────────

    [Fact]
    public void Bold_IsMarkedAndDelimitersRemoved()
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("**bold**"));

        Assert.Equal("bold", inline.Text);
        Assert.True(inline.Bold);
    }

    [Theory]
    [InlineData("*italic*")]
    [InlineData("_italic_")]
    public void Italic_IsMarkedAndDelimitersRemoved(string markdown)
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines(markdown));

        Assert.Equal("italic", inline.Text);
        Assert.True(inline.Italic);
    }

    [Fact]
    public void InlineCode_IsMarkedAndDelimitersRemoved()
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("`code`"));

        Assert.Equal("code", inline.Text);
        Assert.True(inline.Code);
    }

    [Fact]
    public void Link_KeepsLabelAndUrl()
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("[label](https://example.com/page)"));

        Assert.Equal("label", inline.Text);
        Assert.Equal("https://example.com/page", inline.LinkUrl);
    }

    [Fact]
    public void Image_KeepsAltTextOnlyAndDropsUrl()
    {
        // 远程图片刻意不加载，因此图片不能留下 LinkUrl（否则会被渲染成可点击链接）。
        var inline = Assert.Single(MarkdownParser.ParseInlines("![alt text](https://example.com/i.png)"));

        Assert.Equal("alt text", inline.Text);
        Assert.Null(inline.LinkUrl);
    }

    [Fact]
    public void MixedInlineMarkup_ProducesOneSegmentPerMark()
    {
        var inlines = MarkdownParser.ParseInlines("plain **bold** and `code` end");

        Assert.Equal(5, inlines.Count);
        Assert.Equal("plain ", inlines[0].Text);
        Assert.True(inlines[1].Bold);
        Assert.Equal("bold", inlines[1].Text);
        Assert.True(inlines[3].Code);
        Assert.Equal("code", inlines[3].Text);
    }

    [Theory]
    [InlineData("**unclosed")]
    [InlineData("*unclosed")]
    [InlineData("`unclosed")]
    [InlineData("[unclosed](")]
    [InlineData("*")]
    public void UnclosedMarkup_FallsBackToPlainTextAndNeverThrows(string markdown)
    {
        var inlines = MarkdownParser.ParseInlines(markdown);

        Assert.NotEmpty(inlines);
        Assert.Equal(markdown, InlineText(inlines));
        Assert.All(inlines, inline => Assert.False(inline.Bold || inline.Italic || inline.Code));
    }

    [Fact]
    public void ParseInlines_OnEmptyOrNull_ReturnsNothing()
    {
        Assert.Empty(MarkdownParser.ParseInlines(null));
        Assert.Empty(MarkdownParser.ParseInlines(string.Empty));
    }

    // ── 列表 ─────────────────────────────────────────────────

    [Fact]
    public void OrderedList_CarriesNumberAndIsMarkedOrdered()
    {
        var item = Assert.IsType<MarkdownListItem>(Assert.Single(MarkdownParser.Parse("3. third")));

        Assert.True(item.Ordered);
        Assert.Equal(3, item.Number);
        Assert.Equal("third", InlineText(item.Inlines));
    }

    [Theory]
    [InlineData("- hyphen", "hyphen")]
    [InlineData("* star", "star")]
    [InlineData("+ plus", "plus")]
    public void UnorderedMarkers_AreNotReportedAsOrdered(string markdown, string expectedText)
    {
        var item = Assert.IsType<MarkdownListItem>(Assert.Single(MarkdownParser.Parse(markdown)));

        // 曾出现过的缺陷：用 Group2（整个标记分支）判断有序，导致 '-' 也被标为有序。
        Assert.False(item.Ordered);
        Assert.Equal(0, item.Depth);
        Assert.Equal(expectedText, InlineText(item.Inlines));
    }

    [Theory]
    [InlineData("- one", 0)]
    [InlineData("  - two", 1)]
    [InlineData("    - three", 2)]
    public void ListDepth_IsDerivedFromIndentation(string markdown, int expectedDepth)
    {
        var item = Assert.IsType<MarkdownListItem>(Assert.Single(MarkdownParser.Parse(markdown)));

        Assert.Equal(expectedDepth, item.Depth);
    }

    [Fact]
    public void ListItems_StaySeparateBlocks()
    {
        var blocks = MarkdownParser.Parse("- one\n- two\n- three");

        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, block => Assert.IsType<MarkdownListItem>(block));
    }

    [Fact]
    public void ListItemWithInlineMarkup_KeepsBothSegments()
    {
        var item = Assert.IsType<MarkdownListItem>(Assert.Single(MarkdownParser.Parse("- **bold** tail")));

        Assert.Equal(2, item.Inlines.Count);
        Assert.True(item.Inlines[0].Bold);
        Assert.Equal(" tail", item.Inlines[1].Text);
    }

    // ── 引用 ─────────────────────────────────────────────────

    [Fact]
    public void Quote_IsRecognizedAndMarkerStripped()
    {
        var quote = Assert.IsType<MarkdownQuote>(Assert.Single(MarkdownParser.Parse("> quoted text")));

        Assert.Equal("quoted text", InlineText(quote.Inlines));
    }

    [Fact]
    public void ConsecutiveQuoteLines_AreMergedIntoOneQuote()
    {
        var quote = Assert.IsType<MarkdownQuote>(Assert.Single(MarkdownParser.Parse("> first\n> second")));

        Assert.Equal("first second", InlineText(quote.Inlines));
    }

    // ── 代码块 ───────────────────────────────────────────────

    [Fact]
    public void FencedCodeBlock_KeepsLanguageAndContent()
    {
        var code = Assert.IsType<MarkdownCodeBlock>(
            Assert.Single(MarkdownParser.Parse("```csharp\nvar x = 1;\n```")));

        Assert.Equal("csharp", code.Language);
        // 解析器用 AppendLine 逐行拼接后再 TrimEnd，因此比较前需要归一化换行。
        Assert.Equal("var x = 1;", code.Text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void FencedCodeBlock_WithoutLanguage_HasNullLanguage()
    {
        var code = Assert.IsType<MarkdownCodeBlock>(
            Assert.Single(MarkdownParser.Parse("```\nplain\n```")));

        Assert.Null(code.Language);
        Assert.Equal("plain", code.Text);
    }

    [Fact]
    public void UnterminatedCodeFence_StillYieldsOneCodeBlock()
    {
        // 发行说明被截断时很常见；必须能渲染且不抛异常。
        var code = Assert.IsType<MarkdownCodeBlock>(
            Assert.Single(MarkdownParser.Parse("```bash\necho hi")));

        Assert.Equal("bash", code.Language);
        Assert.Equal("echo hi", code.Text);
    }

    [Fact]
    public void CodeFenceContent_IsNotTreatedAsMarkdown()
    {
        var code = Assert.IsType<MarkdownCodeBlock>(
            Assert.Single(MarkdownParser.Parse("```\n# not a heading\n- not a list\n```")));

        // 解析器用 AppendLine 逐行拼接，行间换行随平台而定，比较前先归一化。
        Assert.Equal("# not a heading\n- not a list", code.Text.Replace("\r\n", "\n"));
    }

    // ── 分隔线 ───────────────────────────────────────────────

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    public void DividerForms_AreRecognized(string markdown)
    {
        Assert.IsType<MarkdownDivider>(Assert.Single(MarkdownParser.Parse(markdown)));
    }

    [Theory]
    [InlineData("--")]
    [InlineData("-*-")]
    public void NonDividerSequences_BecomeParagraphs(string markdown)
    {
        Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse(markdown)));
    }

    // ── 表格 ─────────────────────────────────────────────────

    [Fact]
    public void Table_WithSeparatorRow_ParsesHeadersAndRows()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(
            "| Name | Size |\n| --- | --- |\n| a.exe | 1 MB |\n| b.exe | 2 MB |")));

        Assert.Equal(new[] { "Name", "Size" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "a.exe", "1 MB" }, table.Rows[0]);
        Assert.Equal(new[] { "b.exe", "2 MB" }, table.Rows[1]);
    }

    [Fact]
    public void Table_WithoutLeadingOrTrailingPipes_IsStillParsed()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(
            "Name | Size\n--- | ---\na.exe | 1 MB")));

        Assert.Equal(new[] { "Name", "Size" }, table.Headers);
        Assert.Equal(new[] { "a.exe", "1 MB" }, Assert.Single(table.Rows));
    }

    [Fact]
    public void PipeText_WithoutSeparatorRow_IsAParagraph()
    {
        // 没有分隔行就不是表格；否则普通的竖线文本会被误渲染。
        Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse("a | b")));
    }

    [Fact]
    public void Table_StopsAtFirstBlankLine()
    {
        var blocks = MarkdownParser.Parse("| A |\n| --- |\n| 1 |\n\ntrailing");

        Assert.Equal(2, blocks.Count);
        Assert.IsType<MarkdownTable>(blocks[0]);
        Assert.IsType<MarkdownParagraph>(blocks[1]);
    }

    // ── 上限与组合 ───────────────────────────────────────────

    [Fact]
    public void MaximumBlocks_CapsTheNumberOfReturnedBlocks()
    {
        var markdown = string.Join("\n\n", Enumerable.Range(1, 10).Select(index => $"para{index}"));

        var blocks = MarkdownParser.Parse(markdown, maximumBlocks: 3);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("para1", InlineText(Assert.IsType<MarkdownParagraph>(blocks[0]).Inlines));
    }

    [Fact]
    public void MaximumBlocks_DoesNotExceedTheLimitEvenWithTrailingContent()
    {
        // 命中上限后循环立即退出，此时段落缓冲必为空，因此不会多出第 3 个块。
        var blocks = MarkdownParser.Parse("a\n\nb\n\nc", maximumBlocks: 2);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("a", InlineText(Assert.IsType<MarkdownParagraph>(blocks[0]).Inlines));
        Assert.Equal("b", InlineText(Assert.IsType<MarkdownParagraph>(blocks[1]).Inlines));
    }

    [Fact]
    public void MaximumBlocks_OfOne_ReturnsExactlyOneBlock()
    {
        var blocks = MarkdownParser.Parse("a\n\nb", maximumBlocks: 1);

        Assert.Single(blocks);
    }

    [Fact]
    public void MixedDocument_PreservesBlockOrder()
    {
        var blocks = MarkdownParser.Parse(
            "# Title\n\nIntro text\n\n- item one\n\n> note\n\n---\n\n```json\n{}\n```");

        Assert.Equal(6, blocks.Count);
        Assert.IsType<MarkdownHeading>(blocks[0]);
        Assert.IsType<MarkdownParagraph>(blocks[1]);
        Assert.IsType<MarkdownListItem>(blocks[2]);
        Assert.IsType<MarkdownQuote>(blocks[3]);
        Assert.IsType<MarkdownDivider>(blocks[4]);
        Assert.IsType<MarkdownCodeBlock>(blocks[5]);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(
            Assert.Single(MarkdownParser.Parse("first\r\nsecond")));

        Assert.Equal("first second", InlineText(paragraph.Inlines));
    }

    [Fact]
    public void DefaultMaximumBlocks_IsAPositiveSafetyLimit()
    {
        Assert.True(MarkdownParser.DefaultMaximumBlocks > 0);
    }

    private static string InlineText(IReadOnlyList<MarkdownInline> inlines) =>
        string.Concat(inlines.Select(inline => inline.Text));
}
