namespace Reglet;

/// <summary>Computes the content statistics of a document (phase AE, AE02).</summary>
internal static class DocumentMetrics
{
    public static DocumentStatistics Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var counts = new Counts();
        foreach (var section in document.Sections)
        {
            counts.Sections++;
            Walk(section.Blocks, counts);
            foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
            {
                if (band is not null)
                {
                    Walk(band.Blocks, counts);
                }
            }
        }

        return new DocumentStatistics(
            counts.Sections, counts.Blocks, counts.Paragraphs, counts.Headings,
            counts.Lists, counts.ListItems, counts.Tables, counts.TableRows, counts.TableCells,
            counts.Images, counts.Words, counts.Characters);
    }

    private static void Walk(IReadOnlyList<Block> blocks, Counts counts)
    {
        foreach (var block in blocks)
        {
            counts.Blocks++;
            switch (block)
            {
                case Paragraph paragraph:
                    counts.Paragraphs++;
                    Inlines(paragraph.Inlines, counts);
                    break;
                case Heading heading:
                    counts.Headings++;
                    Inlines(heading.Inlines, counts);
                    break;
                case ListBlock list:
                    counts.Lists++;
                    foreach (var item in list.Items)
                    {
                        counts.ListItems++;
                        Walk(item.Blocks, counts);
                    }

                    break;
                case TableBlock table:
                    counts.Tables++;
                    foreach (var row in table.Rows)
                    {
                        counts.TableRows++;
                        foreach (var cell in row.Cells)
                        {
                            counts.TableCells++;
                            Walk(cell.Blocks, counts);
                        }
                    }

                    break;
                case ImageBlock:
                    counts.Images++;
                    break;
            }
        }
    }

    private static void Inlines(IReadOnlyList<Inline> inlines, Counts counts)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    CountText(run.Text, counts);
                    break;
                case Hyperlink hyperlink:
                    Inlines(hyperlink.Inlines, counts);
                    break;
                case Footnote note:
                    Walk(note.Blocks, counts);
                    break;
            }
        }
    }

    private static void CountText(string text, Counts counts)
    {
        counts.Characters += text.Length;
        var inWord = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                counts.Words++;
                inWord = true;
            }
        }
    }

    private sealed class Counts
    {
        public int Sections;
        public int Blocks;
        public int Paragraphs;
        public int Headings;
        public int Lists;
        public int ListItems;
        public int Tables;
        public int TableRows;
        public int TableCells;
        public int Images;
        public int Words;
        public int Characters;
    }
}
