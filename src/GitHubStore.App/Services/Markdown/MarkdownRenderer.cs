using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GitHubStore.App.Services.Markdown;

/// <summary>
/// 把 Markdown 块模型渲染为 WinUI 视觉树。远程图片只显示 alt，不发起请求。
/// 模型入口也执行硬预算，避免外部构造的单块绕过解析器限制。
/// </summary>
public static class MarkdownRenderer
{
    public static UIElement Render(string? markdown, int maximumBlocks = MarkdownParser.DefaultMaximumBlocks)
    {
        var blocks = MarkdownParser.Parse(markdown, maximumBlocks, out var truncation);
        return RenderCore(blocks, new RenderBudget { Truncated = truncation != MarkdownTruncationReason.None });
    }

    public static UIElement Render(IReadOnlyList<MarkdownBlock> blocks)
        => RenderCore(blocks, new RenderBudget());

    private static UIElement RenderCore(IReadOnlyList<MarkdownBlock> blocks, RenderBudget budget)
    {
        var panel = new StackPanel { Spacing = 8 };
        var count = Math.Min(blocks.Count, MarkdownParser.DefaultMaximumBlocks);
        if (count < blocks.Count) budget.Truncated = true;
        for (var index = 0; index < count; index++)
            panel.Children.Add(RenderBlock(blocks[index], budget));
        if (budget.Truncated)
            panel.Children.Add(new TextBlock
            {
                Text = "内容过长，已按显示预算截断。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
        return panel;
    }

    private static UIElement RenderBlock(MarkdownBlock block, RenderBudget budget) => block switch
    {
        MarkdownHeading heading => RenderHeading(heading, budget),
        MarkdownParagraph paragraph => RenderTextBlock(paragraph.Inlines, budget),
        MarkdownListItem item => RenderListItem(item, budget),
        MarkdownCodeBlock code => RenderCode(code, budget),
        MarkdownQuote quote => RenderQuote(quote, budget),
        MarkdownDivider => new Rectangle
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Fill = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        },
        MarkdownTable table => RenderTable(table, budget),
        _ => new TextBlock { Text = string.Empty },
    };

    private static UIElement RenderHeading(MarkdownHeading heading, RenderBudget budget)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            FontSize = heading.Level switch { 1 => 22, 2 => 19, 3 => 17, 4 => 15, _ => 14 },
            Margin = new Thickness(0, heading.Level <= 2 ? 8 : 4, 0, 0),
        };
        AppendInlines(text.Inlines, heading.Inlines, budget);
        return text;
    }

    private static UIElement RenderTextBlock(IReadOnlyList<MarkdownInline> inlines, RenderBudget budget)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendInlines(text.Inlines, inlines, budget);
        return text;
    }

    private static UIElement RenderListItem(MarkdownListItem item, RenderBudget budget)
    {
        // 水平 StackPanel 给正文无限宽度；星号列把换行宽度约束为实际剩余空间。
        var row = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(Math.Clamp(item.Depth, 0, 16) * 16, 0, 0, 0),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = item.Checked is { } done ? (done ? "☑" : "☐") : item.Ordered ? $"{item.Number}." : "•",
            MinWidth = 18,
            VerticalAlignment = VerticalAlignment.Top,
        });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendInlines(text.Inlines, item.Inlines, budget);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private static UIElement RenderCode(MarkdownCodeBlock code, RenderBudget budget)
    {
        var content = new StackPanel { Spacing = 4 };
        if (code.Language is { Length: > 0 } language)
            content.Children.Add(new TextBlock
            {
                Text = budget.Limit(language, MarkdownParser.MaximumCodeLanguageCharacters),
                FontSize = 11,
                Opacity = 0.7,
            });
        var codeText = budget.Limit(code.Text, MarkdownParser.MaximumCodeCharacters);
        // 不对模型提供的无界字符串 Split；只在已裁剪的文本中扫描行界限。
        var lines = 1;
        for (var index = 0; index < codeText.Length; index++)
        {
            if (codeText[index] is not ('\r' or '\n')) continue;
            if (lines >= MarkdownParser.MaximumCodeLines)
            {
                codeText = codeText[..index];
                budget.Truncated = true;
                break;
            }
            lines++;
            if (codeText[index] == '\r' && index + 1 < codeText.Length && codeText[index + 1] == '\n') index++;
        }
        content.Children.Add(new TextBlock
        {
            Text = codeText,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = content,
        };
    }

    private static UIElement RenderQuote(MarkdownQuote quote, RenderBudget budget)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
        AppendInlines(text.Inlines, quote.Inlines, budget);
        return new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(Math.Clamp(quote.Depth - 1, 0, 15) * 12, 0, 0, 0),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = text,
        };
    }

    private static UIElement RenderTable(MarkdownTable table, RenderBudget budget)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        var rowCount = Math.Min(table.Rows.Count, MarkdownParser.MaximumTableRows);
        var columns = Math.Min(table.Headers.Count, MarkdownParser.MaximumTableColumns);
        if (rowCount < table.Rows.Count || columns < table.Headers.Count) budget.Truncated = true;
        for (var index = 0; index < rowCount; index++)
        {
            columns = Math.Max(columns, Math.Min(table.Rows[index].Count, MarkdownParser.MaximumTableColumns));
            if (table.Rows[index].Count > MarkdownParser.MaximumTableColumns) budget.Truncated = true;
        }
        var availableCells = Math.Min(MarkdownParser.MaximumTableCells, budget.RemainingCells);
        if (columns > availableCells)
        {
            columns = availableCells;
            budget.Truncated = true;
        }
        if (columns == 0) return grid;
        var allowedRows = Math.Min(rowCount, availableCells / columns - 1);
        if (allowedRows < rowCount) budget.Truncated = true;
        budget.RemainingCells -= columns * (allowedRows + 1);
        for (var index = 0; index < columns; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var rowIndex = 0; rowIndex <= allowedRows; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rowIndex == 0 ? table.Headers : table.Rows[rowIndex - 1];
            for (var columnIndex = 0; columnIndex < columns; columnIndex++)
            {
                var cell = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                };
                if (rowIndex == 0) cell.FontWeight = FontWeights.SemiBold;
                var source = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                IReadOnlyList<MarkdownInline> inlines;
                var cellIndex = rowIndex * columns + columnIndex;
                if (table.ParsedCells is { } parsed && table.Headers.Count == columns && cellIndex < parsed.Count)
                    inlines = parsed[cellIndex];
                else
                {
                    inlines = MarkdownParser.ParseInlines(budget.Limit(source, MarkdownParser.MaximumTableCellCharacters), out var truncation);
                    if (truncation != MarkdownTruncationReason.None) budget.Truncated = true;
                }
                AppendInlines(cell.Inlines, inlines, budget, MarkdownParser.MaximumTableCellCharacters);
                Grid.SetColumn(cell, columnIndex);
                Grid.SetRow(cell, rowIndex);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = grid,
        };
    }

    private static void AppendInlines(InlineCollection target, IReadOnlyList<MarkdownInline> inlines, RenderBudget budget,
        int maximumCharacters = MarkdownParser.MaximumInlineCharacters)
    {
        var count = Math.Min(inlines.Count, Math.Min(MarkdownParser.MaximumInlineFragments, budget.RemainingInlines));
        if (count < inlines.Count) budget.Truncated = true;
        var remainingCharacters = Math.Clamp(maximumCharacters, 0, MarkdownParser.MaximumInlineCharacters);
        for (var index = 0; index < count; index++)
        {
            if (remainingCharacters == 0)
            {
                budget.Truncated = true;
                break;
            }
            var inline = inlines[index];
            var text = budget.Limit(inline.Text, remainingCharacters);
            remainingCharacters -= text.Length;
            budget.RemainingInlines--;
            var run = new Run { Text = text };
            if (inline.Bold) run.FontWeight = FontWeights.SemiBold;
            if (inline.Italic) run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (inline.Code)
            {
                run.FontFamily = new FontFamily("Consolas");
                run.FontSize = 12;
            }

            if (inline.LinkUrl is { Length: > 0 and <= MarkdownParser.MaximumInlineCharacters } url && MarkdownParser.IsSafeLink(url))
            {
                var hyperlink = new Hyperlink { NavigateUri = new Uri(url) };
                hyperlink.Inlines.Add(run);
                target.Add(hyperlink);
            }
            else target.Add(run);
        }
    }

    private sealed class RenderBudget
    {
        public int RemainingInlines { get; set; } = MarkdownParser.MaximumTotalInlineFragments;
        public int RemainingCells { get; set; } = MarkdownParser.MaximumTotalTableCells;
        public bool Truncated { get; set; }

        public string Limit(string text, int maximumCharacters)
        {
            var result = MarkdownParser.LimitText(text, maximumCharacters, out var truncated);
            Truncated |= truncated;
            return result;
        }
    }
}
