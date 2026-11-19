namespace Reglet;

/// <summary>Collects the footnotes and endnotes of a document in document order.</summary>
internal static class NoteScan
{
    /// <summary>All notes of the document (body and bands) in document order.</summary>
    public static IReadOnlyList<Footnote> Collect(PortableDocument document)
        => Collect(document, body: true, bands: true);

    /// <summary>
    /// The notes of the body in document order. Notes inside a header or footer are left out
    /// because no format places them there (see <see cref="Text"/>).
    /// </summary>
    public static IReadOnlyList<Footnote> CollectBody(PortableDocument document)
        => Collect(document, body: true, bands: false);

    /// <summary>The notes of the headers and footers of all sections in document order.</summary>
    public static IReadOnlyList<Footnote> CollectBands(PortableDocument document)
        => Collect(document, body: false, bands: true);

    /// <summary>
    /// The text of a note in one line. A note inside a header or footer cannot be placed as a
    /// note by the formats (Word shows none there and LibreOffice drops it, a note reference in a
    /// WordprocessingML header part even makes the file unreadable for LibreOffice), so the
    /// writers write this text at the reference instead and report it.
    /// </summary>
    public static string Text(Footnote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var lines = new List<string>();
        Lines(note.Blocks, lines);
        return string.Join(' ', lines);
    }

    private static IReadOnlyList<Footnote> Collect(PortableDocument document, bool body, bool bands)
    {
        ArgumentNullException.ThrowIfNull(document);
        var notes = new List<Footnote>();
        var visiting = new HashSet<Footnote>();
        foreach (var section in document.Sections)
        {
            if (body)
            {
                Blocks(section.Blocks, notes, visiting);
            }

            if (!bands)
            {
                continue;
            }

            foreach (var band in Bands(section))
            {
                if (band is not null)
                {
                    Blocks(band.Blocks, notes, visiting);
                }
            }
        }

        return notes;
    }

    private static IEnumerable<HeaderFooter?> Bands(Section section)
    {
        yield return section.Header;
        yield return section.Footer;
        yield return section.FirstHeader;
        yield return section.FirstFooter;
        yield return section.EvenHeader;
        yield return section.EvenFooter;
    }

    private static void Blocks(IReadOnlyList<Block> blocks, List<Footnote> notes, HashSet<Footnote> visiting)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Inlines(paragraph.Inlines, notes, visiting);
                    break;
                case Heading heading:
                    Inlines(heading.Inlines, notes, visiting);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Blocks(item.Blocks, notes, visiting);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Blocks(cell.Blocks, notes, visiting);
                        }
                    }

                    break;
            }
        }
    }

    private static void Inlines(IReadOnlyList<Inline> inlines, List<Footnote> notes, HashSet<Footnote> visiting)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Footnote note:
                    notes.Add(note);
                    // A note inside a note is another note in the parts; its body is collected too.
                    if (visiting.Add(note))
                    {
                        Blocks(note.Blocks, notes, visiting);
                        visiting.Remove(note);
                    }

                    break;
                case Hyperlink hyperlink:
                    Inlines(hyperlink.Inlines, notes, visiting);
                    break;
            }
        }
    }

    /// <summary>The plain text of the blocks of a note, one line per paragraph or heading.</summary>
    private static void Lines(IReadOnlyList<Block> blocks, List<string> lines)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    AddLine(HeadingScan.PlainText(paragraph.Inlines), lines);
                    break;
                case Heading heading:
                    AddLine(HeadingScan.PlainText(heading.Inlines), lines);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Lines(item.Blocks, lines);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Lines(cell.Blocks, lines);
                        }
                    }

                    break;
            }
        }
    }

    private static void AddLine(string text, List<string> lines)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            lines.Add(trimmed);
        }
    }
}
