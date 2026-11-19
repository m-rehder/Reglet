namespace Reglet;

/// <summary>
/// Collects the tracked changes of a document (ADR-0027): the starts in the body text, which the writers
/// can place, the ids that have an end, and the number of changes elsewhere (header, footer, note), which
/// they report.
/// </summary>
internal static class RevisionScan
{
    /// <summary>The tracked changes of a document.</summary>
    internal sealed record RevisionInfo(
        IReadOnlyList<TrackedChange> Body,
        IReadOnlySet<int> EndedIds,
        int Elsewhere);

    /// <summary>Collects the tracked changes of the body, the notes and the bands.</summary>
    public static RevisionInfo Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var body = new List<TrackedChange>();
        var ended = new HashSet<int>();
        var elsewhere = 0;
        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks, inBody: true, body, ended, ref elsewhere);
            foreach (var band in new[]
            {
                section.Header, section.Footer, section.FirstHeader, section.FirstFooter,
                section.EvenHeader, section.EvenFooter,
            })
            {
                if (band is not null)
                {
                    Blocks(band.Blocks, inBody: false, body, ended, ref elsewhere);
                }
            }
        }

        return new RevisionInfo(body, ended, elsewhere);
    }

    private static void Blocks(
        IReadOnlyList<Block> blocks, bool inBody, List<TrackedChange> body, HashSet<int> ended, ref int elsewhere)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Inlines(paragraph.Inlines, inBody, body, ended, ref elsewhere);
                    break;
                case Heading heading:
                    Inlines(heading.Inlines, inBody, body, ended, ref elsewhere);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Blocks(item.Blocks, inBody, body, ended, ref elsewhere);
                    }

                    break;
                case TableBlock table:
                    foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        Blocks(cell.Blocks, inBody, body, ended, ref elsewhere);
                    }

                    break;
            }
        }
    }

    private static void Inlines(
        IReadOnlyList<Inline> inlines, bool inBody, List<TrackedChange> body, HashSet<int> ended, ref int elsewhere)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TrackedChange change when inBody:
                    body.Add(change);
                    break;
                case TrackedChange:
                    elsewhere++;
                    break;
                case TrackedChangeEnd end when inBody:
                    ended.Add(end.Id);
                    break;
                case Hyperlink hyperlink:
                    Inlines(hyperlink.Inlines, inBody, body, ended, ref elsewhere);
                    break;
                case Footnote note:
                    Blocks(note.Blocks, inBody: false, body, ended, ref elsewhere);
                    break;
            }
        }
    }
}
