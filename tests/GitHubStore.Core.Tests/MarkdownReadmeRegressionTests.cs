using GitHubStore.App.Services.Markdown;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>README syntax regressions, using only the production BCL parser.</summary>
[Trait("Category", "Unit")]
public sealed class MarkdownReadmeRegressionTests
{
    [Theory]
    [InlineData("  ## Install ##", 2, "Install")]
    [InlineData("Title\n=====", 1, "Title")]
    [InlineData("Title\n---", 2, "Title")]
    [InlineData("<h1 align=\"center\">Tool &amp; docs</h1>", 1, "Tool & docs")]
    [InlineData("##", 2, "")]
    public void ReadmeHeadingsStripPresentationMarkers(string source, int level, string title)
    {
        var heading = Assert.IsType<MarkdownHeading>(Assert.Single(MarkdownParser.Parse(source)));
        Assert.Equal(level, heading.Level);
        Assert.Equal(title, Text(heading.Inlines));
    }

    [Fact]
    public void HeadingClosingHashesRequireWhitespace()
    {
        var heading = Assert.IsType<MarkdownHeading>(Assert.Single(MarkdownParser.Parse("## C#")));
        Assert.Equal("C#", Text(heading.Inlines));
    }

    [Fact]
    public void SetextHeadingRespectsBlockBudgetAndReportsFollowingContent()
    {
        var blocks = MarkdownParser.Parse("Title\n---\n\nBody", 1, out var truncation);
        Assert.IsType<MarkdownHeading>(Assert.Single(blocks));
        Assert.True(truncation.HasFlag(MarkdownTruncationReason.Blocks));
    }

    [Fact]
    public void OrderedListsContinueNumberingAcrossNestedListsButResetAfterAParagraph()
    {
        var blocks = MarkdownParser.Parse("3. start\n   - child\n1. next\n\nParagraph\n\n1. new list");
        var items = blocks.OfType<MarkdownListItem>().ToArray();
        Assert.Equal(new[] { 3, 1, 4, 1 }, items.Select(item => item.Number));
        Assert.Equal(new[] { true, false, true, true }, items.Select(item => item.Ordered));
        Assert.True(items[1].Depth > items[0].Depth);
    }

    [Fact]
    public void TaskListContinuationAndTabIndentKeepContentInTheRightItem()
    {
        var items = MarkdownParser.Parse("- [x] done\n  continued **bold**\n\t- [ ] nested\n- next")
            .Cast<MarkdownListItem>().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal(true, items[0].Checked);
        Assert.Equal("done continued bold", Text(items[0].Inlines));
        Assert.Contains(items[0].Inlines, inline => inline.Bold && inline.Text == "bold");
        Assert.Equal(false, items[1].Checked);
        Assert.Equal(2, items[1].Depth);
        Assert.Null(items[2].Checked);
    }

    [Fact]
    public void QuoteIndentationAndNestedDepthDoNotLeakMarkers()
    {
        var quotes = MarkdownParser.Parse("  > first\n  > second\n  >\n  > paragraph\n > > nested")
            .Cast<MarkdownQuote>().ToArray();
        Assert.Equal(2, quotes.Length);
        Assert.Equal("first second\n\nparagraph", Text(quotes[0].Inlines));
        Assert.Equal(1, quotes[0].Depth);
        Assert.Equal(2, quotes[1].Depth);
        Assert.Equal("nested", Text(quotes[1].Inlines));
    }

    [Theory]
    [InlineData("~~~sh\necho hi\n~~~", "sh", "echo hi")]
    [InlineData("  ```cs\n  var x = 1;\n  ```", "cs", "var x = 1;")]
    [InlineData("````md\n```\n# code\n```\n````", "md", "```\n# code\n```")]
    [InlineData("```txt\n```not-a-close\nx\n```", "txt", "```not-a-close\nx")]
    public void FencesMatchMarkerLengthAndOnlyCloseWithoutInfo(string source, string language, string expected)
    {
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse(source)));
        Assert.Equal(language, code.Language);
        Assert.Equal(expected, code.Text);
    }

    [Fact]
    public void CodeWhitespaceAndMarkupRemainLiteral()
    {
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse("```\n<a> &amp; **literal**  \n```")));
        Assert.Equal("<a> &amp; **literal**  ", code.Text);
    }

    [Fact]
    public void LongTildeFenceCannotBypassCodeOrBlockBudget()
    {
        var source = "~~~~\n" + new string('x', MarkdownParser.MaximumCodeCharacters + 10) + "\n~~~~\n# tail";
        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(MarkdownParser.Parse(source, 1, out var reasons)));
        Assert.Equal(MarkdownParser.MaximumCodeCharacters, code.Text.Length);
        Assert.True(reasons.HasFlag(MarkdownTruncationReason.CodeCharacters));
        Assert.True(reasons.HasFlag(MarkdownTruncationReason.Blocks));
    }

    [Fact]
    public void TablesKeepEscapedPipesAndPipesInCodeWithinTheirCell()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(
            "| Name | Command |\n| :--- | ---: |\n| a\\|b | `x|y` |")));
        var row = Assert.Single(table.Rows);
        Assert.Equal(2, row.Count);
        Assert.Equal("a|b", Text(MarkdownParser.ParseInlines(row[0])));
        Assert.Equal("x|y", Assert.Single(MarkdownParser.ParseInlines(row[1])).Text);
    }

    [Theory]
    [InlineData("a|b\n--|---\nx|y")]
    [InlineData("a|b\n:|---\nx|y")]
    public void InvalidTableSeparatorRemainsOrdinaryText(string source)
    {
        Assert.DoesNotContain(MarkdownParser.Parse(source), block => block is MarkdownTable);
    }

    [Fact]
    public void LinksHandleBalancedParenthesesOptionalTitlesAndStyledLabels()
    {
        var inlines = MarkdownParser.ParseInlines("[**Docs**](https://example.com/a_(b) \"Read docs\")");
        var link = Assert.Single(inlines);
        Assert.Equal("Docs", link.Text);
        Assert.True(link.Bold);
        Assert.Equal("https://example.com/a_(b)", link.LinkUrl);
    }

    [Fact]
    public void ReferenceDefinitionsResolveAfterUseCaseInsensitivelyAndStayOutOfCode()
    {
        var blocks = MarkdownParser.Parse("[Read][DOCS] [docs][] [docs]\n\n[docs]: <https://example.com/docs> \"Docs\"\n\n```\n[other]: https://wrong.example/\n```\n\n[other]");
        var first = Assert.IsType<MarkdownParagraph>(blocks[0]);
        Assert.Equal(3, first.Inlines.Count(inline => inline.LinkUrl == "https://example.com/docs"));
        Assert.Equal("[other]", Text(Assert.IsType<MarkdownParagraph>(blocks[^1]).Inlines));
    }

    [Theory]
    [InlineData("[Docs](../README.md)", "Docs")]
    [InlineData("[Section](#install)", "Section")]
    [InlineData("[Bad](javascript:alert(1))", "Bad")]
    [InlineData("<a href=\"https://@example.com\">Bad</a>", "Bad")]
    [InlineData("<a href=\"javascript:alert(1)\">Bad</a>", "Bad")]
    public void UnsafeOrUnresolvableTargetsKeepReadableLabelsWithoutNavigation(string source, string expected)
    {
        var inlines = MarkdownParser.ParseInlines(source);
        Assert.Equal(expected, Text(inlines));
        Assert.All(inlines, inline => Assert.Null(inline.LinkUrl));
    }

    [Fact]
    public void InlineCodeMultipleTicksAndLiteralUnderscoresArePreserved()
    {
        var inlines = MarkdownParser.ParseInlines("file_name_here ``a ` b`` **bold `code`**");
        Assert.StartsWith("file_name_here ", Text(inlines));
        Assert.Contains(inlines, inline => inline.Code && inline.Text == "a ` b");
        Assert.Contains(inlines, inline => inline.Bold && inline.Code && inline.Text == "code");
        Assert.DoesNotContain(inlines, inline => inline.Italic);
    }

    [Fact]
    public void EntitiesEscapesHtmlAndAutolinksRenderAsTextOrSafeFormatting()
    {
        var inlines = MarkdownParser.ParseInlines("<strong>Bold &amp; safe</strong><br>\\*literal\\* <https://example.com/docs>");
        Assert.Equal("Bold & safe\n*literal* https://example.com/docs", Text(inlines));
        Assert.Contains(inlines, inline => inline.Bold && inline.Text == "Bold & safe");
        Assert.Contains(inlines, inline => inline.LinkUrl == "https://example.com/docs");
    }

    [Fact]
    public void ImagesHtmlOrMarkdownNeverLoadANavigationTarget()
    {
        var inlines = MarkdownParser.ParseInlines("![Badge](https://example.com/image.png)<img src=\"https://example.com/tracker\" alt=\"Logo &amp; name\">");
        Assert.Equal("BadgeLogo & name", Text(inlines));
        Assert.All(inlines, inline => Assert.Null(inline.LinkUrl));
    }

    [Fact]
    public void OpaqueHtmlDoesNotTurnIntoHeadingsOrLeakComments()
    {
        var blocks = MarkdownParser.Parse("<!--\n# hidden\n-->\n<script>\n# hidden-script\n</script>\n<h2>Visible</h2>\n\nBody");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Visible", Text(Assert.IsType<MarkdownHeading>(blocks[0]).Inlines));
        Assert.Equal("Body", Text(Assert.IsType<MarkdownParagraph>(blocks[1]).Inlines));
    }

    [Fact]
    public void MarkdownHardBreaksKeepNewlinesWhileSoftBreaksStaySpaces()
    {
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(MarkdownParser.Parse("first  \nsecond\\\nthird\nfourth")));
        Assert.Equal("first\nsecond\nthird fourth", Text(paragraph.Inlines));
    }

    [Fact]
    public void TableReferenceLinksRetainResolvedInlinesForTheRenderer()
    {
        var table = Assert.IsType<MarkdownTable>(Assert.Single(MarkdownParser.Parse(
            "Package | Docs\n--- | ---\nTool | [Guide][docs]\n\n[docs]: https://example.com/guide")));
        Assert.NotNull(table.ParsedCells);
        Assert.Equal(4, table.ParsedCells!.Count);
        var link = Assert.Single(table.ParsedCells[3]);
        Assert.Equal("Guide", link.Text);
        Assert.Equal("https://example.com/guide", link.LinkUrl);
    }

    [Theory]
    [InlineData("**bold *italic***", "bold italic", "italic")]
    [InlineData("*italic **bold***", "italic bold", "bold")]
    public void NestedEmphasisSharesItsClosingMarkerRun(string source, string expected, string nested)
    {
        var inlines = MarkdownParser.ParseInlines(source);
        Assert.Equal(expected, Text(inlines));
        Assert.Contains(inlines, inline => inline.Bold && inline.Italic && inline.Text == nested);
    }

    [Fact]
    public void NestedFormattingStillSharesTheGlobalInlineBudget()
    {
        var source = string.Join("\n", Enumerable.Repeat("# **" + string.Concat(Enumerable.Repeat("`x` plain ", 100)) + "**", 40));
        var blocks = MarkdownParser.Parse(source, 100, out var reasons);
        Assert.True(reasons.HasFlag(MarkdownTruncationReason.InlineFragments));
        Assert.True(blocks.OfType<MarkdownHeading>().Sum(block => block.Inlines.Count) <= MarkdownParser.MaximumTotalInlineFragments);
    }

    private static string Text(IReadOnlyList<MarkdownInline> inlines) => string.Concat(inlines.Select(inline => inline.Text));
}
