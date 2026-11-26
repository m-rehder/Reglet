namespace Reglet;

/// <summary>Enumerates the tables and collects the table styles used in a document.</summary>
internal static class TableScan
{
    /// <summary>
    /// Reports a table without rows or cells once per document: no format shows it, and it usually comes from a list of
    /// rows the caller expected to be filled.
    /// </summary>
    public static void ReportEmptyTables(PortableDocument document, UnsupportedFeatureHandler handler, string format)
    {
        if (Enumerate(document).Any(table => TableLayout.LogicalColumns(table) == 0))
        {
            handler.Report($"{format}: a table without rows or cells is not shown.");
        }
    }

    /// <summary>All tables of the document (body and bands, including nested tables).</summary>
    public static IEnumerable<TableBlock> Enumerate(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var section in document.Sections)
        {
            foreach (var table in Enumerate(section.Blocks))
            {
                yield return table;
            }

            foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
            {
                if (band is null)
                {
                    continue;
                }

                foreach (var table in Enumerate(band.Blocks))
                {
                    yield return table;
                }
            }
        }
    }

    /// <summary>All tables in a block list (including nested tables).</summary>
    public static IEnumerable<TableBlock> Enumerate(IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TableBlock table:
                    yield return table;
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var nested in Enumerate(cell.Blocks))
                            {
                                yield return nested;
                            }
                        }
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var inner in Enumerate(item.Blocks))
                        {
                            yield return inner;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>The distinct table styles used in the document, in document order.</summary>
    public static IReadOnlyList<TableStyle> CollectStyles(PortableDocument document)
    {
        var styles = new List<TableStyle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Enumerate(document))
        {
            if (table.Style is { } style && seen.Add(style.Name))
            {
                styles.Add(style);
            }
        }

        return styles;
    }
}
