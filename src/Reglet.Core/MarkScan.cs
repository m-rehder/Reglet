namespace Reglet;

/// <summary>
/// Collects the comments (ADR-0026) or tracked changes (ADR-0027) of a document: the starts in the body text,
/// which the writers can place, the ids that have an end, and the number of starts elsewhere (header, footer,
/// note), which they report.
/// </summary>
internal static class MarkScan
{
    /// <summary>The marks of a document.</summary>
    internal sealed record MarkInfo<TStart>(
        IReadOnlyList<TStart> Body,
        IReadOnlySet<int> EndedIds,
        int Elsewhere);

    /// <summary>Collects the starts and ends of the body, the notes and the bands.</summary>
    public static MarkInfo<TStart> Collect<TStart, TEnd>(PortableDocument document, Func<TEnd, int> endId)
        where TStart : Inline
        where TEnd : Inline
    {
        ArgumentNullException.ThrowIfNull(document);

        var scan = new Scan<TStart, TEnd>(endId);
        foreach (var section in document.Sections)
        {
            scan.Blocks(section.Blocks, inBody: true);
            foreach (var band in new[]
            {
                section.Header, section.Footer, section.FirstHeader, section.FirstFooter,
                section.EvenHeader, section.EvenFooter,
            })
            {
                if (band is not null)
                {
                    scan.Blocks(band.Blocks, inBody: false);
                }
            }
        }

        return new MarkInfo<TStart>(scan.Body, scan.Ended, scan.Elsewhere);
    }

    private sealed class Scan<TStart, TEnd>(Func<TEnd, int> endId)
        where TStart : Inline
        where TEnd : Inline
    {
        public List<TStart> Body { get; } = [];

        public HashSet<int> Ended { get; } = [];

        public int Elsewhere { get; private set; }

        public void Blocks(IReadOnlyList<Block> blocks, bool inBody)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        Inlines(paragraph.Inlines, inBody);
                        break;
                    case Heading heading:
                        Inlines(heading.Inlines, inBody);
                        break;
                    case ListBlock list:
                        foreach (var item in list.Items)
                        {
                            Blocks(item.Blocks, inBody);
                        }

                        break;
                    case TableBlock table:
                        foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                        {
                            Blocks(cell.Blocks, inBody);
                        }

                        break;
                }
            }
        }

        private void Inlines(IReadOnlyList<Inline> inlines, bool inBody)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TStart start when inBody:
                        Body.Add(start);
                        break;
                    case TStart:
                        Elsewhere++;
                        break;
                    case TEnd end when inBody:
                        Ended.Add(endId(end));
                        break;
                    case Hyperlink hyperlink:
                        Inlines(hyperlink.Inlines, inBody);
                        break;
                    case Footnote note:
                        // A mark in a note has no place in the parts of the body.
                        Blocks(note.Blocks, inBody: false);
                        break;
                }
            }
        }
    }
}

/// <summary>The comments of a document (ADR-0026).</summary>
internal static class CommentScan
{
    public static MarkScan.MarkInfo<Comment> Collect(PortableDocument document)
        => MarkScan.Collect<Comment, CommentEnd>(document, end => end.Id);
}

/// <summary>The tracked changes of a document (ADR-0027).</summary>
internal static class RevisionScan
{
    public static MarkScan.MarkInfo<TrackedChange> Collect(PortableDocument document)
        => MarkScan.Collect<TrackedChange, TrackedChangeEnd>(document, end => end.Id);
}
