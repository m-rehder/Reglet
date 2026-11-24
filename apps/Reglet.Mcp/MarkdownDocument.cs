using System.Text;
using System.Text.RegularExpressions;

namespace Reglet.Mcp;

/// <summary>
/// Turns the Markdown an agent writes into a Reglet document. A deliberate subset: headings (<c>#</c> to <c>######</c>),
/// paragraphs (lines joined until a blank line), <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c> in paragraphs,
/// bullet lists (<c>-</c>, <c>*</c>, <c>+</c>), numbered lists (<c>1.</c>), pipe tables with a header row, and
/// <c>---</c> as a page break. Anything else is kept as paragraph text.
/// </summary>
public static partial class MarkdownDocument
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.None, 1000)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$", RegexOptions.None, 1000)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(.*)$", RegexOptions.None, 1000)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$", RegexOptions.None, 1000)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"(\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|\*(?<italic>[^*\s][^*]*?)\*|_(?<italic>[^_\s][^_]*?)_|`(?<code>[^`]+)`)", RegexOptions.None, 1000)]
    private static partial Regex Emphasis();

    /// <summary>Builds a document from Markdown.</summary>
    public static PortableDocument Parse(string markdown, string? title)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var builder = PortableDocument.Create();
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Metadata(title: title);
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0)
            {
                var text = string.Join(' ', paragraph.Select(line => line.Trim()));
                builder.Paragraph(p => AddInlines(p, text));
                paragraph.Clear();
            }
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                Flush();
                builder.Heading(Plain(heading.Groups[2].Value), heading.Groups[1].Value.Length);
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                Flush();
                builder.PageBreak();
                continue;
            }

            if (BulletLine().IsMatch(line) || NumberedLine().IsMatch(line))
            {
                Flush();
                var numbered = NumberedLine().IsMatch(line) && !BulletLine().IsMatch(line);
                var pattern = numbered ? NumberedLine() : BulletLine();
                var items = new List<string>();
                while (index < lines.Length && pattern.Match(lines[index]) is { Success: true } item)
                {
                    items.Add(Plain(item.Groups[1].Value));
                    index++;
                }

                index--;
                if (numbered)
                {
                    builder.NumberedList([.. items]);
                }
                else
                {
                    builder.BulletList([.. items]);
                }

                continue;
            }

            if (line.TrimStart().StartsWith('|') && index + 1 < lines.Length && TableSeparator().IsMatch(lines[index + 1]))
            {
                Flush();
                var header = Cells(line);
                var rows = new List<string[]>();
                index += 2;
                while (index < lines.Length && lines[index].TrimStart().StartsWith('|'))
                {
                    rows.Add(Cells(lines[index]));
                    index++;
                }

                index--;
                var columns = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Length));
                builder.Table(table =>
                {
                    table.Header(Pad(header, columns));
                    foreach (var row in rows)
                    {
                        table.Row(Pad(row, columns));
                    }
                });
                continue;
            }

            paragraph.Add(line);
        }

        Flush();
        return builder.Build();
    }

    private static string[] Cells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => Plain(cell.Trim())).ToArray();
    }

    private static string[] Pad(string[] cells, int columns)
        => cells.Length >= columns ? cells : [.. cells, .. Enumerable.Repeat(string.Empty, columns - cells.Length)];

    /// <summary>The text without emphasis markers (for headings, list items and table cells).</summary>
    private static string Plain(string text)
        => Emphasis().Replace(text, match => match.Groups["bold"].Success ? match.Groups["bold"].Value
            : match.Groups["italic"].Success ? match.Groups["italic"].Value
            : match.Groups["code"].Value);

    private static void AddInlines(ParagraphBuilder paragraph, string text)
    {
        var position = 0;
        foreach (Match match in Emphasis().Matches(text))
        {
            if (match.Index > position)
            {
                paragraph.Text(text[position..match.Index]);
            }

            if (match.Groups["bold"].Success)
            {
                paragraph.Bold(match.Groups["bold"].Value);
            }
            else if (match.Groups["italic"].Success)
            {
                paragraph.Italic(match.Groups["italic"].Value);
            }
            else
            {
                paragraph.Span(match.Groups["code"].Value, fontFamily: "Courier");
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            paragraph.Text(text[position..]);
        }
    }
}
