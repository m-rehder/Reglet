namespace Reglet;

/// <summary>
/// collects the general fields (<see cref="Field"/>) of a document, in the order of the
/// document (phase Z).
/// </summary>
internal static class FieldScan
{
    /// <summary>All general fields of the document (body, headers and footers).</summary>
    public static IEnumerable<Field> Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var section in document.Sections)
        {
            foreach (var field in Collect(section.Blocks))
            {
                yield return field;
            }

            foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
            {
                if (band is null)
                {
                    continue;
                }

                foreach (var field in Collect(band.Blocks))
                {
                    yield return field;
                }
            }
        }
    }

    /// <summary>All general fields of a block list.</summary>
    private static IEnumerable<Field> Collect(IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var field in Collect(paragraph.Inlines))
                    {
                        yield return field;
                    }

                    break;
                case Heading heading:
                    foreach (var field in Collect(heading.Inlines))
                    {
                        yield return field;
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var field in Collect(item.Blocks))
                        {
                            yield return field;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var field in Collect(cell.Blocks))
                            {
                                yield return field;
                            }
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>All general fields of an inline list.</summary>
    private static IEnumerable<Field> Collect(IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Field field:
                    yield return field;
                    break;
                case Hyperlink hyperlink:
                    foreach (var field in Collect(hyperlink.Inlines))
                    {
                        yield return field;
                    }

                    break;
            }
        }
    }
}
