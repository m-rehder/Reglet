namespace Reglet;

/// <summary>
/// Documented priority rule for column widths: in
/// <see cref="TableBlock.ColumnWidths"/> apply unchanged;
/// the remaining columns share the remaining available width equally.
/// All returns are in points.
/// </summary>
internal static class TableLayout
{
    /// <summary>
    /// The columns a table occupies: positions covered by a row span push the cells of a row to
    /// the right, so the widest row alone can be too few.
    /// </summary>
    public static int LogicalColumns(TableBlock table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var covered = new List<int>();
        foreach (var row in table.Rows)
        {
            var column = 0;
            foreach (var cell in row.Cells)
            {
                while (column < covered.Count && covered[column] > 0)
                {
                    column++;
                }

                for (var end = column + Math.Max(1, cell.ColumnSpan); column < end; column++)
                {
                    if (column == covered.Count)
                    {
                        covered.Add(0);
                    }

                    covered[column] = cell.RowSpan;
                }
            }

            for (var index = 0; index < covered.Count; index++)
            {
                covered[index] = Math.Max(0, covered[index] - 1);
            }
        }

        return covered.Count;
    }

    /// <summary>
    /// paragraph style of a paragraph in a cell: one of <see cref="TextAlignment.Left"/>
    /// deviating cell alignment overrides the paragraph alignment; otherwise the
    /// keep the paragraph's own alignment.
    /// </summary>
    public static ParagraphStyle CellParagraphStyle(ParagraphStyle style, TableCell cell)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(cell);

        return cell.Alignment == TextAlignment.Left ? style : style with { Alignment = cell.Alignment };
    }

    /// <summary>
    /// The left/right and top/bottom padding of a cell (or, without one, of the table's cells): the cell's own values, then
    /// the table's, then its style's, then Word's defaults; a value for one axis wins over the uniform one.
    /// </summary>
    public static (Length Horizontal, Length Vertical) CellPadding(TableBlock table, TableCell? cell)
    {
        ArgumentNullException.ThrowIfNull(table);
        var uniform = table.CellPadding ?? table.Style?.CellPadding;
        return (
            cell?.PaddingHorizontal ?? cell?.Padding ?? table.CellPaddingHorizontal ?? uniform ?? Typography.TableCellPaddingHorizontal,
            cell?.PaddingVertical ?? cell?.Padding ?? table.CellPaddingVertical ?? uniform ?? Typography.TableCellPaddingVertical);
    }

    public static double[] ResolveColumnWidths(TableBlock table, int columnCount, double availablePoints)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);

        var widths = new double[columnCount];
        if (columnCount == 0)
        {
            return widths;
        }

        if (!double.IsFinite(availablePoints) || availablePoints <= 0)
        {
            throw new DocumentValidationException("The available table width must be positive.", nameof(availablePoints));
        }

        var custom = table.ColumnWidths;
        if (custom is { Count: > 0 } && custom.Count > columnCount)
        {
            throw new DocumentValidationException("More column widths than table columns are specified.", nameof(table));
        }

        var explicitCount = Math.Min(custom?.Count ?? 0, columnCount);
        var explicitTotal = 0.0;

        for (var column = 0; column < explicitCount; column++)
        {
            widths[column] = custom![column].Points;
            explicitTotal += widths[column];
        }

        // A full set wider than the text area is scaled down, as the percent width does: a read table
        // exceeds it by twip or centimetre rounding, a Word table may reach into the margin. Fixed widths are kept.
        if (explicitCount == columnCount && table.FixedColumnWidths)
        {
            return widths;
        }

        // A negative indent moves the table into the left margin; that room is free for a full set of widths.
        var room = availablePoints - Math.Min(0, table.LeftIndent?.Points ?? 0);
        if (explicitCount == columnCount && explicitTotal > availablePoints)
        {
            var factor = Math.Min(1, room / explicitTotal);
            for (var column = 0; column < columnCount; column++)
            {
                widths[column] *= factor;
            }

            return widths;
        }

        if (explicitTotal > availablePoints || (explicitTotal == availablePoints && explicitCount < columnCount))
        {
            throw new DocumentValidationException("Explicit column widths leave no room for the table.", nameof(table));
        }

        var remainingColumns = columnCount - explicitCount;
        if (remainingColumns > 0)
        {
            var remaining = availablePoints - explicitTotal;
            var equal = remaining / remainingColumns;
            for (var column = explicitCount; column < columnCount; column++)
            {
                widths[column] = equal;
            }
        }

        return widths;
    }

    /// <summary>
    /// The column widths with <see cref="TableBlock.WidthPercent"/> applied: explicit widths keep
    /// their absolute size, the remaining columns share the rest of the percent target. Without an
    /// explicit width the whole target is divided. Explicit widths that exceed the target are
    /// scaled down when they cover every column; a partial set that leaves no room is rejected.
    /// </summary>
    public static double[] ResolveColumnWidthsWithPercent(TableBlock table, int columnCount, double availablePoints)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        if (!double.IsFinite(availablePoints) || availablePoints <= 0)
        {
            throw new DocumentValidationException("The available table width must be positive.", nameof(availablePoints));
        }

        if (table.ColumnWidthPercents is { } percents)
        {
            return ResolvePercentWidths(percents, columnCount, availablePoints * (table.WidthPercent ?? 100) / 100.0);
        }

        if (table.WidthPercent is not { } percent)
        {
            return ResolveColumnWidths(table, columnCount, availablePoints);
        }

        var target = availablePoints * percent / 100.0;
        var custom = table.ColumnWidths;
        var explicitCount = Math.Min(custom?.Count ?? 0, columnCount);
        if (custom is { Count: > 0 } && custom.Count > columnCount)
        {
            throw new DocumentValidationException("More column widths than table columns are specified.", nameof(table));
        }

        if (explicitCount == 0)
        {
            return ResolveColumnWidths(table, columnCount, target);
        }

        var explicitTotal = 0.0;
        for (var column = 0; column < explicitCount; column++)
        {
            explicitTotal += custom![column].Points;
        }

        if (explicitCount == columnCount)
        {
            if (explicitTotal > target && explicitTotal > 0 && !table.FixedColumnWidths)
            {
                var factor = target / explicitTotal;
                return custom!.Select(width => width.Points * factor).ToArray();
            }

            return custom!.Select(width => width.Points).ToArray();
        }

        if (explicitTotal >= target)
        {
            throw new DocumentValidationException("Explicit column widths leave no room for the table.", nameof(table));
        }

        var widths = new double[columnCount];
        for (var column = 0; column < explicitCount; column++)
        {
            widths[column] = custom![column].Points;
        }

        var remaining = target - explicitTotal;
        var equal = remaining / (columnCount - explicitCount);
        for (var column = explicitCount; column < columnCount; column++)
        {
            widths[column] = equal;
        }

        return widths;
    }

    /// <summary>
    /// <see cref="TableBlock.ColumnWidthPercents"/> as points of <paramref name="tableWidth"/>: listed columns take their
    /// share, the others split the rest of 100 percent equally, and the result is stretched to fill the table.
    /// </summary>
    internal static double[] ResolvePercentWidths(IReadOnlyList<double> percents, int columnCount, double tableWidth)
    {
        if (percents.Count > columnCount)
        {
            throw new DocumentValidationException("More column percents than table columns are specified.", "table");
        }

        var shares = new double[columnCount];
        var listed = percents.Sum();
        var rest = columnCount > percents.Count ? (100 - listed) / (columnCount - percents.Count) : 0;
        if (columnCount > percents.Count && rest <= 0)
        {
            throw new DocumentValidationException("Column percents of 100 leave no room for the remaining columns.", "table");
        }

        for (var column = 0; column < columnCount; column++)
        {
            shares[column] = column < percents.Count ? percents[column] : rest;
        }

        var total = shares.Sum();
        return shares.Select(share => tableWidth * share / total).ToArray();
    }
}
