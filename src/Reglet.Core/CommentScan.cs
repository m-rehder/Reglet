namespace Reglet;

/// <summary>
/// Collects the comments of a document (ADR-0026): the ones in the body text, which the writers
/// can place, and the number of those elsewhere (header, footer, note), which they report.
/// </summary>
internal static class CommentScan
{
    /// <summary>The comments of a document.</summary>
    internal sealed record CommentInfo(
        IReadOnlyList<Comment> Body,
        IReadOnlySet<int> EndedIds,
        int Elsewhere);

    /// <summary>Collects the comments of the body, the notes and the bands.</summary>
    public static CommentInfo Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var body = new List<Comment>();
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

        return new CommentInfo(body, ended, elsewhere);
    }

    private static void Blocks(
        IReadOnlyList<Block> blocks, bool inBody, List<Comment> body, HashSet<int> ended, ref int elsewhere)
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
        IReadOnlyList<Inline> inlines, bool inBody, List<Comment> body, HashSet<int> ended, ref int elsewhere)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Comment comment when inBody:
                    body.Add(comment);
                    break;
                case Comment:
                    elsewhere++;
                    break;
                case CommentEnd end when inBody:
                    ended.Add(end.Id);
                    break;
                case Footnote note:
                    // A comment in a note has no place in the comments part of the body.
                    var inNoteComments = new List<Comment>();
                    Blocks(note.Blocks, inBody: false, inNoteComments, ended, ref elsewhere);
                    break;
            }
        }
    }
}
