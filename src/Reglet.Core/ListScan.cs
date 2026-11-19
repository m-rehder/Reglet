namespace Reglet;

/// <summary>
/// collects the lists of a document (body, headers and footers), in the order of the document.
/// </summary>
internal static class ListScan
{
    /// <summary>All lists of the document.</summary>
    public static IEnumerable<ListBlock> Collect(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var section in document.Sections)
        {
            foreach (var list in Collect(section.Blocks))
            {
                yield return list;
            }

            foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
            {
                if (band is null)
                {
                    continue;
                }

                foreach (var list in Collect(band.Blocks))
                {
                    yield return list;
                }
            }
        }
    }

    /// <summary>All lists of a block list (including the lists in the items).</summary>
    internal static IEnumerable<ListBlock> Collect(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListBlock list:
                    yield return list;
                    foreach (var item in list.Items)
                    {
                        foreach (var nested in Collect(item.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var nested in Collect(cell.Blocks))
                            {
                                yield return nested;
                            }
                        }
                    }

                    break;
            }
        }
    }
}
