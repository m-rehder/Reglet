namespace Reglet;

/// <summary>Detects whether a document contains a table of contents.</summary>
internal static class TableOfContentsScan
{
    /// <summary>Whether any block (body, list or table cell) is a table of contents.</summary>
    public static bool Contains(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var section in document.Sections)
        {
            if (Contains(section.Blocks))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TableOfContents:
                    return true;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        if (Contains(item.Blocks))
                        {
                            return true;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            if (Contains(cell.Blocks))
                            {
                                return true;
                            }
                        }
                    }

                    break;
            }
        }

        return false;
    }
}
