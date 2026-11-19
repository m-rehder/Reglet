namespace Reglet;

/// <summary>
/// the conditional table formats (phase Y, Y05): header row, banded rows, last row and
/// first/last column. The bandwidth counts the data rows (leading header rows excluded), so a
/// table with and without a header starts banding on its first data row.
/// </summary>
internal static class TableConditionalStyle
{
    /// <summary>Whether the data row at <paramref name="rowIndex"/> carries the band background.</summary>
    public static bool IsBanded(TableBlock table, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (rowIndex < 0 || rowIndex >= table.Rows.Count || table.Rows[rowIndex].IsHeader)
        {
            return false;
        }

        var headerCount = 0;
        while (headerCount < table.Rows.Count && table.Rows[headerCount].IsHeader)
        {
            headerCount++;
        }

        return (rowIndex - headerCount) % 2 == 0;
    }

    /// <summary>
    /// The conditional formatting of the area a cell belongs to (the header row, the banded row,
    /// the first or last column, the last row). Null when the style has no such area.
    /// </summary>
    public static TableStyleArea? AreaOf(
        TableBlock table,
        TableStyle? style,
        TableRow row,
        int rowIndex,
        int columnIndex,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(row);
        if (style is null)
        {
            return null;
        }

        if (row.IsHeader)
        {
            return style.HeaderRow;
        }

        if (rowIndex == table.Rows.Count - 1 && style.LastRow is { } lastRow)
        {
            return lastRow;
        }

        if (IsBanded(table, rowIndex) && style.BandRow is { } band)
        {
            return band;
        }

        if (columnIndex == 0 && style.FirstColumn is { } firstColumn)
        {
            return firstColumn;
        }

        if (columnIndex == columnCount - 1 && style.LastColumn is { } lastColumn)
        {
            return lastColumn;
        }

        return null;
    }
}
