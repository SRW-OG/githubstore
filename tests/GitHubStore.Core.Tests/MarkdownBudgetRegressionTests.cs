using GitHubStore.App.Services.Markdown;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>直接调用源链接的生产解析器；不创建 WinUI 对象、不访问网络。</summary>
[Trait("Category", "Unit")]
public sealed class MarkdownBudgetRegressionTests
{
    [Theory]
    [InlineData("# h")]
    [InlineData("- item")]
    [InlineData("> quote")]
    [InlineData("- - -")]
    [InlineData("```cs\ncode\n```")]
    [InlineData("| A |\n| --- |\n| row |")]
    public void PendingParagraphAndFollowingBlock_ShareOneStrictBudget(string following)
    {
        var blocks = MarkdownParser.Parse("a\n" + following, 1, out var truncation);

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(blocks));
        Assert.Equal("a", Text(paragraph.Inlines));
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.Blocks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveBlockBudget_ReturnsEmptyAndReportsOmittedContent(int maximumBlocks)
    {
        Assert.Empty(MarkdownParser.Parse("# content", maximumBlocks, out var truncation));
        Assert.Equal(MarkdownTruncationReason.Blocks, truncation);
        Assert.Empty(MarkdownParser.Parse(null, maximumBlocks, out truncation));
        Assert.Equal(MarkdownTruncationReason.None, truncation);
    }

    [Fact]
    public void OversizedRequestedBlockBudget_CannotDisableTheHardLimit()
    {
        var markdown = string.Join("\n", Enumerable.Repeat("# h", MarkdownParser.DefaultMaximumBlocks + 1));

        var blocks = MarkdownParser.Parse(markdown, int.MaxValue, out var truncation);

        Assert.Equal(MarkdownParser.DefaultMaximumBlocks, blocks.Count);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.Blocks));
    }

    [Fact]
    public void ExactBlockBoundary_DoesNotReportTruncationForTrailingWhitespace()
    {
        Assert.Single(MarkdownParser.Parse("# h\n\n ", 1, out var truncation));
        Assert.Equal(MarkdownTruncationReason.None, truncation);
    }

    [Fact]
    public void InputIsClippedBeforeLineParsing_AndCannotRevealContentBeyondThePrefix()
    {
        var markdown = new string(' ', MarkdownParser.MaximumInputCharacters) + "\n# excluded";

        Assert.Empty(MarkdownParser.Parse(markdown, 10, out var truncation));
        Assert.Equal(MarkdownTruncationReason.InputCharacters, truncation);
    }

    [Fact]
    public void InputCharacterBoundary_IsInclusive()
    {
        Assert.Empty(MarkdownParser.Parse(new string(' ', MarkdownParser.MaximumInputCharacters), 1, out var truncation));
        Assert.Equal(MarkdownTruncationReason.None, truncation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainInlineText_IsBoundedWithoutAddingAnExtraFragment(bool overflow)
    {
        var source = new string('x', MarkdownParser.MaximumInlineCharacters + (overflow ? 1 : 0));

        var inline = Assert.Single(MarkdownParser.ParseInlines(source, out var truncation));

        Assert.Equal(MarkdownParser.MaximumInlineCharacters, inline.Text.Length);
        Assert.Equal(overflow, truncation.HasFlag(MarkdownTruncationReason.InlineCharacters));
    }

    [Fact]
    public void CharacterClipping_DoesNotLeaveHalfOfASurrogatePair()
    {
        var prefix = new string('x', MarkdownParser.MaximumInlineCharacters - 1);
        var inline = Assert.Single(MarkdownParser.ParseInlines(prefix + "😀", out var truncation));

        Assert.Equal(prefix, inline.Text);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.InlineCharacters));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyInlineMarkers_CannotBypassTheFragmentLimit(bool overflow)
    {
        var source = string.Concat(Enumerable.Repeat("[`x`](https://e.test)", MarkdownParser.MaximumInlineFragments + (overflow ? 1 : 0)));

        var inlines = MarkdownParser.ParseInlines(source, out var truncation);

        Assert.Equal(MarkdownParser.MaximumInlineFragments, inlines.Count);
        Assert.All(inlines, inline => Assert.True(inline.Code));
        Assert.Equal(overflow, truncation.HasFlag(MarkdownTruncationReason.InlineFragments));
    }

    [Fact]
    public void PlainBufferAndMarkup_UseTheSameFragmentBudget()
    {
        var prefix = string.Concat(Enumerable.Repeat("[`x`](https://e.test)", MarkdownParser.MaximumInlineFragments - 1));

        var inlines = MarkdownParser.ParseInlines(prefix + "plain **omitted**", out var truncation);

        Assert.Equal(MarkdownParser.MaximumInlineFragments, inlines.Count);
        Assert.Equal("plain ", inlines[^1].Text);
        Assert.DoesNotContain(inlines, inline => inline.Text == "omitted");
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.InlineFragments));
    }

    [Fact]
    public void InlineFragmentBudget_IsSharedAcrossTheWholeDocument()
    {
        var heading = "# " + string.Concat(Enumerable.Repeat("[`x`](https://e.test)", MarkdownParser.MaximumInlineFragments));
        var count = MarkdownParser.MaximumTotalInlineFragments / MarkdownParser.MaximumInlineFragments + 1;

        var blocks = MarkdownParser.Parse(string.Join("\n", Enumerable.Repeat(heading, count)), 100, out var truncation);

        Assert.Equal(count, blocks.Count);
        Assert.Equal(MarkdownParser.MaximumTotalInlineFragments, blocks.Cast<MarkdownHeading>().Sum(block => block.Inlines.Count));
        Assert.Empty(Assert.IsType<MarkdownHeading>(blocks[^1]).Inlines);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.InlineFragments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneLongCodeLine_CannotBypassTheCodeCharacterLimit(bool overflow)
    {
        var codeText = new string('x', MarkdownParser.MaximumCodeCharacters + (overflow ? 1 : 0));
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse("```\n" + codeText + "\n```", 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumCodeCharacters, code.Text.Length);
        Assert.Equal(overflow, truncation.HasFlag(MarkdownTruncationReason.CodeCharacters));
    }

    [Fact]
    public void ManyCodeLines_AreClippedWithinTheBlock_AndDoNotBecomeMarkdown()
    {
        var body = string.Join("\n", Enumerable.Repeat("x", MarkdownParser.MaximumCodeLines + 1));

        var blocks = MarkdownParser.Parse("```\n" + body + "\n# hidden\n```\n# after", 10, out var truncation);

        Assert.Equal(2, blocks.Count);
        var code = Assert.IsType<MarkdownCodeBlock>(blocks[0]);
        Assert.Equal(MarkdownParser.MaximumCodeLines, code.Text.Split('\n').Length);
        Assert.DoesNotContain("hidden", code.Text);
        Assert.Equal("after", Text(Assert.IsType<MarkdownHeading>(blocks[1]).Inlines));
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.CodeLines));
    }

    [Fact]
    public void ExactCodeLineBoundary_AndUnclosedFence_AreAccepted()
    {
        var body = string.Join("\r\n", Enumerable.Repeat("x", MarkdownParser.MaximumCodeLines));

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse("```\r\n" + body, 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumCodeLines, code.Text.Split('\n').Length);
        Assert.Equal(MarkdownTruncationReason.None, truncation);
    }

    [Fact]
    public void CodeLanguage_IsAlsoBounded()
    {
        var language = new string('x', MarkdownParser.MaximumCodeLanguageCharacters + 1);
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse("```" + language + "\nx\n```", 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumCodeLanguageCharacters, code.Language!.Length);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.CodeCharacters));
    }

    [Fact]
    public void WideTable_IsClippedAtTheHeaderAndEveryBodyRow()
    {
        var columns = MarkdownParser.MaximumTableColumns + 1;
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(Table(columns, 1), 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumTableColumns, table.Headers.Count);
        Assert.All(table.Rows, row => Assert.Equal(MarkdownParser.MaximumTableColumns, row.Count));
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.TableColumns));
    }

    [Fact]
    public void TallTable_SkipsExcessRowsWithinTheSameBlock()
    {
        var blocks = MarkdownParser.Parse(Table(1, MarkdownParser.MaximumTableRows + 5) + "\n\n# after", 10, out var truncation);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownParser.MaximumTableRows, Assert.IsType<MarkdownTable>(blocks[0]).Rows.Count);
        Assert.IsType<MarkdownHeading>(blocks[1]);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.TableRows));
    }

    [Fact]
    public void ExactTableDimensions_DoNotReportTruncation()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(Table(1, MarkdownParser.MaximumTableRows), 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumTableRows, table.Rows.Count);
        Assert.Equal(MarkdownTruncationReason.None, truncation);
        table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(Table(MarkdownParser.MaximumTableColumns, 1), 1, out truncation)));
        Assert.Equal(MarkdownParser.MaximumTableColumns, table.Headers.Count);
        Assert.Equal(MarkdownTruncationReason.None, truncation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableCellBudget_IncludesHeadersAndCannotBeBypassedByOneBlock(bool overflow)
    {
        const int columns = 8;
        var rows = MarkdownParser.MaximumTableCells / columns - 1;
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(Table(columns, rows + (overflow ? 1 : 0)), 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumTableCells, table.Headers.Count + table.Rows.Sum(row => row.Count));
        Assert.Equal(overflow, truncation.HasFlag(MarkdownTruncationReason.TableCells));
    }

    [Fact]
    public void TotalTableCells_AreSharedAcrossTables()
    {
        var table = Table(8, MarkdownParser.MaximumTableCells / 8 - 1);
        var markdown = string.Join("\n\n", Enumerable.Repeat(table, MarkdownParser.MaximumTotalTableCells / MarkdownParser.MaximumTableCells + 1));

        var blocks = MarkdownParser.Parse(markdown, 10, out var truncation);

        Assert.Equal(MarkdownParser.MaximumTotalTableCells, blocks.Cast<MarkdownTable>().Sum(item => item.Headers.Count + item.Rows.Sum(row => row.Count)));
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.TableCells));
    }

    [Fact]
    public void RaggedRows_ArePaddedWithinTheCellBudget_AndExtraColumnsAreDiscarded()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse("a|b\n---|---\nonly|\na|b|extra", 1, out var truncation)));

        Assert.Equal(new[] { "only", string.Empty }, table.Rows[0]);
        Assert.Equal(new[] { "a", "b" }, table.Rows[1]);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.TableColumns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableCellText_IsBounded(bool overflow)
    {
        var cell = new string('x', MarkdownParser.MaximumTableCellCharacters + (overflow ? 1 : 0));
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(cell + "|b\n---|---\n" + cell + "|b", 1, out var truncation)));

        Assert.Equal(MarkdownParser.MaximumTableCellCharacters, table.Headers[0].Length);
        Assert.Equal(MarkdownParser.MaximumTableCellCharacters, table.Rows[0][0].Length);
        Assert.Equal(overflow, truncation.HasFlag(MarkdownTruncationReason.TableCellCharacters));
    }

    [Theory]
    [InlineData("`first`**second**", "`first`")]
    [InlineData("plain **second**", "plain ")]
    public void TableCellsAndOrdinaryBlocks_ShareInlineBudgetWithoutLeavingHalfAMarker(string cell, string retained)
    {
        var fullHeading = "# " + string.Concat(Enumerable.Repeat("[`x`](https://e.test)", MarkdownParser.MaximumInlineFragments));
        var fullHeadings = MarkdownParser.MaximumTotalInlineFragments / MarkdownParser.MaximumInlineFragments - 1;
        var partialHeading = "# " + string.Concat(Enumerable.Repeat("[`x`](https://e.test)", MarkdownParser.MaximumInlineFragments - 1));
        var markdown = string.Join("\n", Enumerable.Repeat(fullHeading, fullHeadings)) + "\n" + partialHeading
            + "\n" + cell + "|tail\n---|---\nrow|row";

        var blocks = MarkdownParser.Parse(markdown, 100, out var truncation);

        var table = Assert.IsType<MarkdownTable>(blocks[^1]);
        Assert.Equal(retained, table.Headers[0]);
        Assert.Empty(table.Headers[1]);
        Assert.All(Assert.Single(table.Rows), item => Assert.Empty(item));
        var headingFragments = blocks.OfType<MarkdownHeading>().Sum(item => item.Inlines.Count);
        var tableFragments = table.Headers.Concat(table.Rows.SelectMany(row => row)).Sum(item => MarkdownParser.ParseInlines(item).Count);
        Assert.Equal(MarkdownParser.MaximumTotalInlineFragments, headingFragments + tableFragments);
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.InlineFragments));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("javascript:alert")]
    [InlineData("file:///C:/test")]
    [InlineData("mailto:example@example.com")]
    [InlineData("https://user:%3A@example.com")]
    [InlineData("https://user@example.com")]
    [InlineData("https://@example.com")]
    [InlineData("/relative")]
    public void UnsafeLinks_KeepTheirLabelWithoutANavigationTarget(string url)
    {
        var inline = Assert.Single(MarkdownParser.ParseInlines("[label](" + url + ")"));

        Assert.Equal("label", inline.Text);
        Assert.Null(inline.LinkUrl);
    }

    [Fact]
    public void HttpsLinks_AllowAtSignsInPaths_AndImagesOnlyKeepAltText()
    {
        const string url = "https://example.com/@name?q=@value";
        Assert.Equal(url, Assert.Single(MarkdownParser.ParseInlines("[label](" + url + ")")).LinkUrl);
        var image = Assert.Single(MarkdownParser.ParseInlines("![alt](" + url + ")"));
        Assert.Equal("alt", image.Text);
        Assert.Null(image.LinkUrl);
        image = Assert.Single(MarkdownParser.ParseInlines("![](" + url + ")"));
        Assert.NotEmpty(image.Text);
        Assert.Null(image.LinkUrl);
    }

    private static string Text(IReadOnlyList<MarkdownInline> inlines)
        => string.Concat(inlines.Select(inline => inline.Text));

    private static string Table(int columns, int rows)
    {
        var header = string.Join("|", Enumerable.Repeat("h", columns)) + "|";
        var separator = string.Join("|", Enumerable.Repeat("---", columns)) + "|";
        var row = string.Join("|", Enumerable.Repeat("x", columns)) + "|";
        return header + "\n" + separator + (rows == 0 ? string.Empty : "\n" + string.Join("\n", Enumerable.Repeat(row, rows)));
    }
}
