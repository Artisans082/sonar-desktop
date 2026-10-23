using System.Text.RegularExpressions;
using Sonar.Models;

namespace Sonar.Services;

/// <summary>
/// Markdown → 块模型解析器（纯 CPU，可放到后台线程）。
/// 与 Python md_render.py 支持的语法一致：标题、粗体、行内代码、代码块、
/// 引用、列表、表格、分隔线；并额外把表格按真实列结构输出。
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex InlineRegex = new(@"(\*\*[^*\n]+\*\*|`[^`\n]+`)",
        RegexOptions.Compiled);

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(\s*)[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^(\s*)(\d+)[.、]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRegex = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new(@"^:?-{2,}:?$", RegexOptions.Compiled);

    public static List<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var tableBuffer = new List<string>();
        var inCode = false;
        var codeBuffer = new List<string>();

        void FlushTable()
        {
            if (tableBuffer.Count == 0) return;
            var rows = new List<List<string>>();
            var hasHeader = false;
            foreach (var raw in tableBuffer)
            {
                var cells = raw.Trim().Trim('|').Split('|')
                    .Select(c => c.Trim()).ToList();
                if (cells.Count > 0 && cells.All(c => c.Length == 0 || TableSeparatorRegex.IsMatch(c)))
                {
                    hasHeader = rows.Count > 0;
                    continue;
                }
                rows.Add(cells);
            }
            tableBuffer.Clear();
            if (rows.Count == 0) return;
            var columns = rows.Max(r => r.Count);
            foreach (var row in rows)
            {
                while (row.Count < columns) row.Add("");
            }
            blocks.Add(new MdBlock
            {
                Type = MdBlockType.Table,
                Rows = rows,
                HasHeader = hasHeader,
            });
        }

        void FlushCode()
        {
            if (codeBuffer.Count == 0) return;
            blocks.Add(new MdBlock
            {
                Type = MdBlockType.CodeBlock,
                Lines = new List<string>(codeBuffer),
            });
            codeBuffer.Clear();
        }

        foreach (var line in lines)
        {
            var stripped = line.Trim();

            if (stripped.StartsWith("```", StringComparison.Ordinal))
            {
                FlushTable();
                if (inCode)
                {
                    FlushCode();
                    inCode = false;
                }
                else
                {
                    inCode = true;
                }
                continue;
            }

            if (inCode)
            {
                codeBuffer.Add(line);
                continue;
            }

            if (line.TrimStart().StartsWith('|') && stripped.EndsWith('|') && stripped.Length > 1)
            {
                tableBuffer.Add(line);
                continue;
            }
            FlushTable();

            if (stripped.Length == 0)
            {
                blocks.Add(new MdBlock { Type = MdBlockType.Blank });
                continue;
            }

            var heading = HeadingRegex.Match(stripped);
            if (heading.Success)
            {
                blocks.Add(new MdBlock
                {
                    Type = MdBlockType.Heading,
                    Level = heading.Groups[1].Value.Length,
                    Inlines = ParseInline(heading.Groups[2].Value),
                });
                continue;
            }

            if (RuleRegex.IsMatch(stripped))
            {
                blocks.Add(new MdBlock { Type = MdBlockType.Rule });
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                blocks.Add(new MdBlock
                {
                    Type = MdBlockType.Quote,
                    Inlines = ParseInline(line.TrimStart()[1..].TrimStart()),
                });
                continue;
            }

            var bullet = BulletRegex.Match(line);
            if (bullet.Success)
            {
                blocks.Add(new MdBlock
                {
                    Type = MdBlockType.Bullet,
                    Level = bullet.Groups[1].Value.Length / 2,
                    Inlines = ParseInline(bullet.Groups[2].Value),
                });
                continue;
            }

            var numbered = NumberedRegex.Match(line);
            if (numbered.Success)
            {
                blocks.Add(new MdBlock
                {
                    Type = MdBlockType.Numbered,
                    Level = numbered.Groups[1].Value.Length / 2,
                    Number = numbered.Groups[2].Value,
                    Inlines = ParseInline(numbered.Groups[3].Value),
                });
                continue;
            }

            blocks.Add(new MdBlock
            {
                Type = MdBlockType.Paragraph,
                Inlines = ParseInline(line),
            });
        }

        FlushTable();
        if (inCode) FlushCode();
        return blocks;
    }

    public static List<MdInline> ParseInline(string? line)
    {
        var result = new List<MdInline>();
        if (string.IsNullOrEmpty(line)) return result;

        foreach (var part in InlineRegex.Split(line))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**", StringComparison.Ordinal) &&
                part.EndsWith("**", StringComparison.Ordinal))
            {
                result.Add(new MdInline { Text = part[2..^2], Bold = true });
            }
            else if (part.Length > 2 && part.StartsWith('`') && part.EndsWith('`'))
            {
                result.Add(new MdInline { Text = part[1..^1], Code = true });
            }
            else
            {
                result.Add(new MdInline { Text = part });
            }
        }
        return result;
    }
}
