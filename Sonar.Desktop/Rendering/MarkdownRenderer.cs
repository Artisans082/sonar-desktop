using System.Windows;
using System.Windows.Documents;
using Sonar.Models;
using Sonar.Services;

namespace Sonar.Rendering;

/// <summary>
/// 把 Markdown 块模型增量写入 FlowDocument。
/// 每个块独立构建，因此 UI 可以按时间预算分帧插入，避免一次性排版卡住界面。
/// </summary>
public static class MarkdownRenderer
{
    public static FlowDocument CreateDocument()
    {
        return new FlowDocument
        {
            FontFamily = ThemePalette.UiFont,
            FontSize = 13.5,
            Foreground = ThemePalette.Text,
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left,
        };
    }

    public static Paragraph CreateSpacer(double height = 6) => new()
    {
        Margin = new Thickness(0),
        FontSize = 1,
        LineHeight = height,
    };

    public static void AppendBlock(BlockCollection blocks, MdBlock block)
    {
        switch (block.Type)
        {
            case MdBlockType.Heading:
                blocks.Add(BuildHeading(block));
                break;
            case MdBlockType.Paragraph:
                blocks.Add(BuildParagraph(block));
                break;
            case MdBlockType.Quote:
                blocks.Add(BuildQuote(block));
                break;
            case MdBlockType.Bullet:
                blocks.Add(BuildBullet(block));
                break;
            case MdBlockType.Numbered:
                blocks.Add(BuildNumbered(block));
                break;
            case MdBlockType.CodeBlock:
                blocks.Add(BuildCode(block));
                break;
            case MdBlockType.Table:
                blocks.Add(BuildTable(block));
                break;
            case MdBlockType.Rule:
                blocks.Add(BuildRule());
                break;
            case MdBlockType.Blank:
                blocks.Add(CreateSpacer(4));
                break;
        }
    }

    private static Paragraph BuildHeading(MdBlock block)
    {
        var (size, brush, margin) = block.Level switch
        {
            1 => (22.0, ThemePalette.Text, new Thickness(0, 18, 0, 10)),
            2 => (18.0, ThemePalette.AccentHover, new Thickness(0, 18, 0, 8)),
            3 => (15.0, ThemePalette.Text, new Thickness(0, 14, 0, 6)),
            _ => (13.5, ThemePalette.TextDim, new Thickness(0, 10, 0, 4)),
        };
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            Margin = margin,
            LineHeight = size * 1.45,
        };
        AddInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static Paragraph BuildParagraph(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            LineHeight = 22,
        };
        AddInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static Paragraph BuildQuote(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 2, 0, 10),
            Padding = new Thickness(12, 6, 10, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = ThemePalette.Accent,
            Background = ThemePalette.AccentSoft,
            Foreground = ThemePalette.TextDim,
            FontStyle = FontStyles.Italic,
            LineHeight = 21,
        };
        AddInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static Paragraph BuildBullet(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(24 + block.Level * 16, 0, 0, 5),
            TextIndent = -14,
            LineHeight = 21,
        };
        paragraph.Inlines.Add(new Run("•  ")
        {
            Foreground = ThemePalette.Accent,
            FontWeight = FontWeights.Bold,
        });
        AddInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static Paragraph BuildNumbered(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(24 + block.Level * 16, 0, 0, 5),
            TextIndent = -18,
            LineHeight = 21,
        };
        paragraph.Inlines.Add(new Run($"{block.Number}. ")
        {
            Foreground = ThemePalette.Accent,
            FontWeight = FontWeights.SemiBold,
        });
        AddInlines(paragraph.Inlines, block.Inlines);
        return paragraph;
    }

    private static Paragraph BuildCode(MdBlock block)
    {
        var paragraph = new Paragraph
        {
            FontFamily = ThemePalette.MonoFont,
            FontSize = 12,
            Foreground = ThemePalette.Text,
            Background = ThemePalette.CodeBg,
            BorderBrush = ThemePalette.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 2, 0, 10),
            LineHeight = 18,
        };
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(block.Lines[i]));
        }
        return paragraph;
    }

    private static Table BuildTable(MdBlock block)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 12),
            BorderBrush = ThemePalette.Border,
            BorderThickness = new Thickness(1),
        };

        var columns = block.Rows.Count > 0 ? block.Rows.Max(r => r.Count) : 0;
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (var rowIndex = 0; rowIndex < block.Rows.Count; rowIndex++)
        {
            var isHeader = block.HasHeader && rowIndex == 0;
            var row = new TableRow
            {
                Background = isHeader ? ThemePalette.AccentSoft : null,
            };
            foreach (var cellText in block.Rows[rowIndex])
            {
                var cell = new TableCell
                {
                    Padding = new Thickness(9, 6, 9, 6),
                    BorderBrush = ThemePalette.Border,
                    BorderThickness = new Thickness(0.5),
                };
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontSize = 12.5,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isHeader ? ThemePalette.AccentHover : ThemePalette.Text,
                    LineHeight = 19,
                };
                AddInlines(paragraph.Inlines, MarkdownParser.ParseInline(cellText), isHeader);
                cell.Blocks.Add(paragraph);
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static Paragraph BuildRule()
    {
        return new Paragraph
        {
            Margin = new Thickness(0, 6, 0, 12),
            BorderBrush = ThemePalette.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            FontSize = 1,
            LineHeight = 1,
        };
    }

    private static void AddInlines(InlineCollection target, IEnumerable<MdInline> inlines,
        bool forceBold = false)
    {
        foreach (var inline in inlines)
        {
            var run = new Run(inline.Text);
            if (inline.Bold || forceBold)
            {
                run.FontWeight = FontWeights.Bold;
                if (inline.Bold && !forceBold) run.Foreground = ThemePalette.Text;
            }
            if (inline.Code)
            {
                run.FontFamily = ThemePalette.MonoFont;
                run.FontSize = 12;
                run.Foreground = ThemePalette.AccentHover;
                run.Background = ThemePalette.CodeBg;
            }
            target.Add(run);
        }
    }
}
