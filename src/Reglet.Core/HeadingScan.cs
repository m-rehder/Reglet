using System.Text;

namespace Reglet;

/// <summary>Collects the headings of a document for a table of contents.</summary>
internal static class HeadingScan
{
    /// <summary>The headings (level and plain text) in document order, up to <paramref name="maxLevel"/>.</summary>
    public static IReadOnlyList<(int Level, string Text)> Collect(PortableDocument document, int maxLevel)
    {
        ArgumentNullException.ThrowIfNull(document);
        var headings = new List<(int Level, string Text)>();
        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks, maxLevel, headings);
        }

        return headings;
    }

    /// <summary>The headings of several block groups, up to <paramref name="maxLevel"/>.</summary>
    public static IReadOnlyList<(int Level, string Text)> Collect(IEnumerable<IEnumerable<Block>> groups, int maxLevel)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var headings = new List<(int Level, string Text)>();
        foreach (var blocks in groups)
        {
            Blocks(blocks, maxLevel, headings);
        }

        return headings;
    }

    private static void Blocks(IEnumerable<Block> blocks, int maxLevel, List<(int Level, string Text)> headings)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading when (int)heading.Level <= maxLevel:
                    var text = PlainText(heading.Inlines);
                    if (text.Length > 0)
                    {
                        headings.Add(((int)heading.Level, text));
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Blocks(item.Blocks, maxLevel, headings);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Blocks(cell.Blocks, maxLevel, headings);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Plain text of inline content.</summary>
    public static string PlainText(IReadOnlyList<Inline> inlines)
    {
        var builder = new StringBuilder();
        Append(builder, inlines);
        return builder.ToString().Trim();
    }

    private static void Append(StringBuilder builder, IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                // shortcut: only the run's own Hidden is seen here, not one from a character style; pass the styles in when that is needed.
                case TextRun run when run.Hidden != true:
                    builder.Append(run.Text);
                    break;
                case Hyperlink hyperlink:
                    Append(builder, hyperlink.Inlines);
                    break;
            }
        }
    }
}
