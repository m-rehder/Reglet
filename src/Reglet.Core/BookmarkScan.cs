using System.Text;

namespace Reglet;

/// <summary>
/// Collects the bookmark ranges of a document: the text between a <see cref="Bookmark"/> and its
/// <see cref="BookmarkEnd"/> (a <c>REF</c> field shows it) and the names that have an explicit end
/// (the writers need them to decide between a zero-width target and a range).
/// </summary>
internal static class BookmarkScan
{
    /// <summary>The bookmark ranges and range names of a document.</summary>
    internal sealed record BookmarkInfo(
        IReadOnlySet<string> RangeNames,
        IReadOnlyDictionary<string, string> Texts);

    /// <summary>Collects the bookmark ranges of the body, the notes and the bands.</summary>
    public static BookmarkInfo Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ranges = new HashSet<string>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var open = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);

        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks, ranges, texts, open);
            foreach (var band in new[]
            {
                section.Header, section.Footer, section.FirstHeader, section.FirstFooter,
                section.EvenHeader, section.EvenFooter,
            })
            {
                if (band is not null)
                {
                    Blocks(band.Blocks, ranges, texts, open);
                }
            }
        }

        // A start without an end is a zero-width target: it contributes no text.
        return new BookmarkInfo(ranges, texts);
    }

    private static void Blocks(
        IReadOnlyList<Block> blocks,
        HashSet<string> ranges,
        Dictionary<string, string> texts,
        Dictionary<string, StringBuilder> open)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Inlines(paragraph.Inlines, ranges, texts, open);
                    break;
                case Heading heading:
                    Inlines(heading.Inlines, ranges, texts, open);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Blocks(item.Blocks, ranges, texts, open);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Blocks(cell.Blocks, ranges, texts, open);
                        }
                    }

                    break;
            }

            // Paragraphs are separated by a space in the text of an open range.
            foreach (var builder in open.Values)
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
        }
    }

    private static void Inlines(
        IReadOnlyList<Inline> inlines,
        HashSet<string> ranges,
        Dictionary<string, string> texts,
        Dictionary<string, StringBuilder> open)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    foreach (var builder in open.Values)
                    {
                        builder.Append(run.Text);
                    }

                    break;
                case Bookmark bookmark:
                    open[bookmark.Name] = new StringBuilder();
                    break;
                case BookmarkEnd end:
                    if (open.Remove(end.Name, out var closed))
                    {
                        ranges.Add(end.Name);
                        texts[end.Name] = closed.ToString().Trim();
                    }

                    break;
                case Hyperlink hyperlink:
                    Inlines(hyperlink.Inlines, ranges, texts, open);
                    break;
                case Footnote note:
                    // A note is its own text flow: its bookmarks are collected on their own.
                    Notes(note.Blocks, ranges, texts);
                    break;
            }
        }
    }

    /// <summary>Collects the ranges of a note without leaking them into the surrounding flow.</summary>
    private static void Notes(
        IReadOnlyList<Block> blocks,
        HashSet<string> ranges,
        Dictionary<string, string> texts)
        => Blocks(blocks, ranges, texts, new Dictionary<string, StringBuilder>(StringComparer.Ordinal));
}
