using System.Text;

namespace Reglet.Mcp;

/// <summary>
/// A document as Markdown-like text for an agent to read: headings with <c>#</c>, paragraphs, lists with <c>-</c>,
/// tables as pipe tables, and a bracketed note for content that has no text (pictures, charts, …).
/// </summary>
public static class DocumentText
{
    /// <summary>The text of the document, cut at <paramref name="maxCharacters"/> with a note.</summary>
    public static string Of(PortableDocument document, int maxCharacters)
    {
        var text = new StringBuilder();
        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks, text, 0);
        }

        var result = text.ToString().TrimEnd();
        return result.Length <= maxCharacters
            ? result
            : result[..maxCharacters] + $"\n\n[… cut at {maxCharacters} of {result.Length} characters]";
    }

    private static void Blocks(IEnumerable<Block> blocks, StringBuilder text, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    text.Append('#', (int)heading.Level).Append(' ').AppendLine(Inlines(heading.Inlines)).AppendLine();
                    break;
                case Paragraph paragraph:
                    var line = Inlines(paragraph.Inlines);
                    if (line.Length > 0)
                    {
                        text.AppendLine(line).AppendLine();
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        var inner = new StringBuilder();
                        Blocks(item.Blocks, inner, depth + 1);
                        var lines = inner.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        for (var index = 0; index < lines.Length; index++)
                        {
                            text.Append(' ', 2 * (list.NestingLevel - 1)).Append(index == 0 ? "- " : "  ").AppendLine(lines[index]);
                        }
                    }

                    text.AppendLine();
                    break;
                case TableBlock table:
                    var first = true;
                    foreach (var row in table.Rows)
                    {
                        var cells = row.Cells.Select(cell =>
                        {
                            var inner = new StringBuilder();
                            Blocks(cell.Blocks, inner, depth + 1);
                            return string.Join(' ', inner.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Replace("|", "\\|", StringComparison.Ordinal);
                        }).ToList();
                        text.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                        if (first)
                        {
                            text.Append('|').Append(string.Concat(cells.Select(_ => " --- |"))).AppendLine();
                            first = false;
                        }
                    }

                    text.AppendLine();
                    break;
                case PageBreak:
                    text.AppendLine("---").AppendLine();
                    break;
                case ImageBlock image:
                    text.AppendLine($"[picture{(string.IsNullOrEmpty(image.AltText) ? string.Empty : ": " + image.AltText)}]").AppendLine();
                    break;
                case ColumnBreak:
                    break;
                default:
                    text.AppendLine($"[{block.GetType().Name}]").AppendLine();
                    break;
            }
        }
    }

    private static string Inlines(IEnumerable<Inline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    text.Append(run.Text);
                    break;
                case Hyperlink link:
                    text.Append(Inlines(link.Inlines));
                    break;
                case InlineImage:
                    text.Append("[picture]");
                    break;
            }
        }

        return text.ToString().Replace("\n", " ", StringComparison.Ordinal).Trim();
    }
}
