namespace Reglet;

/// <summary>
/// checks a document against the configured <see cref="ResourceLimits"/> (blocks,
/// table cells, text length, nesting depth), including headers and footers. Protects server processes from oversized input.
/// </summary>
internal static class DocumentLimits
{
    public static void Enforce(PortableDocument document, ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(limits);

        var blocks = 0;
        var cells = 0;
        foreach (var section in document.Sections)
        {
            Walk(section.Blocks, limits, 0, ref blocks, ref cells);
            foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
            {
                if (band is not null)
                {
                    Walk(band.Blocks, limits, 0, ref blocks, ref cells);
                }
            }
        }
    }

    private static void Walk(IEnumerable<Block> blocks, ResourceLimits limits, int depth, ref int blockCount, ref int cellCount)
    {
        // Checked before descending, so this walk stays as shallow as the limit.
        if (depth > limits.MaxNestingDepth)
        {
            throw new DocumentValidationException(
                $"Tables, lists and notes are nested deeper than {limits.MaxNestingDepth} levels.");
        }

        foreach (var block in blocks)
        {
            blockCount++;
            if (blockCount > limits.MaxBlocks)
            {
                throw new DocumentValidationException(
                    $"The document exceeds the limit of {limits.MaxBlocks} blocks.");
            }

            switch (block)
            {
                case Paragraph paragraph:
                    CheckInlines(paragraph.Inlines, limits, depth, ref blockCount, ref cellCount);
                    break;
                case Heading heading:
                    CheckInlines(heading.Inlines, limits, depth, ref blockCount, ref cellCount);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Walk(item.Blocks, limits, depth + 1, ref blockCount, ref cellCount);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            cellCount++;
                            if (cellCount > limits.MaxTableCells)
                            {
                                throw new DocumentValidationException(
                                    $"The document exceeds the limit of {limits.MaxTableCells} table cells.");
                            }

                            Walk(cell.Blocks, limits, depth + 1, ref blockCount, ref cellCount);
                        }
                    }

                    break;
            }
        }
    }

    private static void CheckInlines(
        IReadOnlyList<Inline> inlines, ResourceLimits limits, int depth, ref int blockCount, ref int cellCount)
    {
        foreach (var inline in inlines)
        {
            if (inline is TextRun run && run.Text.Length > limits.MaxTextLength)
            {
                throw new DocumentValidationException(
                    $"A text run exceeds the limit of {limits.MaxTextLength} characters.");
            }

            if (inline is Footnote note)
            {
                Walk(note.Blocks, limits, depth + 1, ref blockCount, ref cellCount);
            }
        }
    }
}
