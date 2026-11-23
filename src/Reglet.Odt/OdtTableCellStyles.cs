using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// collects and writes the automatic ODT cell, row and table styles for the table features
/// Y01–Y04 (vertical alignment, background, padding, borders, row height, width in percent).
/// </summary>
/// <remarks>
/// A cell without direct formatting keeps the fixed <c>Cell</c>/<c>CellR</c>/<c>CellU</c>/
/// <c>CellRU</c> styles; cells with direct formatting get their own style (<c>TC1</c>, …).
/// </remarks>
internal sealed class OdtTableCellStyles
{
    private readonly record struct Cell(CellKey Key, string Name);
    private readonly record struct Row(RowKey Key, string Name);
    private readonly record struct Table(TableKey Key, string Name);

    private readonly record struct CellKey(
        Color? Background,
        (double Horizontal, double Vertical)? Padding,
        string Borders,
        TableCellVerticalAlignment VerticalAlignment,
        string AreaBorder,
        string AreaText);

    private readonly record struct RowKey(double? Height, TableRowHeightRule Rule, bool KeepTogether);

    private readonly record struct TableKey(double? WidthPercent, string Border, double? CellPaddingHorizontal, double? CellPaddingVertical, double? LeftIndent, double? FixedWidth, string Parent)
    {
        public bool IsEmpty => WidthPercent is null && Border.Length == 0 && CellPaddingHorizontal is null && CellPaddingVertical is null && LeftIndent is null && FixedWidth is null;
    }

    private readonly Dictionary<CellKey, string> _cells = [];
    private readonly Dictionary<RowKey, string> _rows = [];
    private readonly Dictionary<TableKey, string> _tables = [];
    private readonly int _cellStart;
    private readonly int _rowStart;
    private readonly int _tableStart;

    private OdtTableCellStyles(int cellStart = 0, int rowStart = 0, int tableStart = 0)
    {
        _cellStart = cellStart;
        _rowStart = rowStart;
        _tableStart = tableStart;
    }

    /// <summary>Collects the styles of all tables in the given block groups.</summary>
    public static OdtTableCellStyles Collect(
        IEnumerable<IEnumerable<Block>> groups,
        OdtBreakPlan breaks,
        int cellStart = 0,
        int rowStart = 0,
        int tableStart = 0)
    {
        ArgumentNullException.ThrowIfNull(breaks);
        var styles = new OdtTableCellStyles(cellStart, rowStart, tableStart);
        foreach (var blocks in groups)
        {
            Walk(blocks, breaks, styles);
        }

        return styles;
    }

    /// <summary>An empty set (for writer passes without tables).</summary>
    public static OdtTableCellStyles Empty { get; } = new();

    /// <summary>Name of the cell style (default position style if there is no direct formatting).</summary>
    public string CellStyleName(
        TableBlock table,
        TableCell cell,
        bool lastColumn,
        bool lastRow,
        Color? background,
        TableStyleArea? area = null)
    {
        var key = CellKeyOf(table, cell, background, area);
        if (key == DefaultCellKey)
        {
            return "Cell" + (lastColumn ? "R" : string.Empty) + (lastRow ? "U" : string.Empty);
        }

        return _cells.TryGetValue(key, out var name) ? name : "Cell";
    }

    /// <summary>Name of the row style, or null if the row has neither a height nor KeepTogether.</summary>
    public string? RowStyleName(TableRow row)
        => RowKeyOf(row) is { } key && _rows.TryGetValue(key, out var name) ? name : null;

    private static RowKey? RowKeyOf(TableRow row)
        => row.Height is null && !row.KeepTogether ? null : new RowKey(row.Height?.Points, row.HeightRule, row.KeepTogether);

    /// <summary>Name of the table style, or null if the table has no width/border/padding.</summary>
    public string? TableStyleName(TableBlock table, string? parent)
    {
        var key = TableKeyOf(table, parent);
        return key.IsEmpty
            ? null
            : _tables.TryGetValue(key, out var name) ? name : null;
    }

    private static readonly CellKey DefaultCellKey = new(
        null, null, string.Empty, TableCellVerticalAlignment.Top, string.Empty, string.Empty);

    // ODF has the padding only on the cells: a cell takes its own, or else the table's.
    private static CellKey CellKeyOf(TableBlock table, TableCell cell, Color? background, TableStyleArea? area) => new(
        background,
        TableLayout.CellPadding(table, cell) is var (horizontal, vertical)
            && (horizontal != Typography.TableCellPaddingHorizontal || vertical != Typography.TableCellPaddingVertical)
                ? (horizontal.Points, vertical.Points)
                : null,
        BordersKey(cell.Borders),
        cell.VerticalAlignment,
        BorderKey(area?.Border),
        AreaTextKey(area));

    /// <summary>The key of the conditional text formatting of an area.</summary>
    private static string AreaTextKey(TableStyleArea? area)
    {
        if (area is null || (area.Bold is not true && area.Italic is not true && area.TextColor is null))
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (area.Bold == true)
        {
            parts.Add("bold");
        }

        if (area.Italic == true)
        {
            parts.Add("italic");
        }

        if (area.TextColor is { } color)
        {
            parts.Add(color.ToString());
        }

        return string.Join(',', parts);
    }

    private static TableKey TableKeyOf(TableBlock table, string? parent) => new(
        table.WidthPercent,
        BorderKey(table.Border ?? table.Style?.Border),
        (table.CellPaddingHorizontal ?? table.CellPadding ?? table.Style?.CellPadding)?.Points,
        (table.CellPaddingVertical ?? table.CellPadding ?? table.Style?.CellPadding)?.Points,
        table.LeftIndent?.Points,
        table.FixedColumnWidths && table.ColumnWidths is { Count: > 0 } widths && widths.Count == TableLayout.LogicalColumns(table)
            ? widths.Sum(width => width.Points)
            : null,
        parent ?? string.Empty);

    private static void Walk(IEnumerable<Block> blocks, OdtBreakPlan breaks, OdtTableCellStyles styles)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Walk(item.Blocks, breaks, styles);
                    }

                    break;
                case TableBlock table:
                    styles.RegisterTable(table, breaks.TableStyleName(table));
                    var columnCount = TableLayout.LogicalColumns(table);
                    var rowIndex = 0;
                    foreach (var row in table.Rows)
                    {
                        styles.RegisterRow(row);
                        var style = table.Style;
                        var conditional = row.IsHeader
                            ? style?.HeaderBackground
                            : TableConditionalStyle.IsBanded(table, rowIndex) ? style?.BandBackground : null;
                        if (rowIndex == table.Rows.Count - 1 && !row.IsHeader && style?.LastRowBackground is { } lastRow)
                        {
                            conditional = lastRow;
                        }

                        var column = 0;
                        foreach (var cell in row.Cells)
                        {
                            var span = Math.Max(1, cell.ColumnSpan);
                            var styleBackground = conditional;
                            if (!row.IsHeader && column == 0 && style?.FirstColumnBackground is { } firstColumn)
                            {
                                styleBackground = firstColumn;
                            }
                            else if (!row.IsHeader && column + span - 1 == columnCount - 1 && style?.LastColumnBackground is { } lastColumn)
                            {
                                styleBackground = lastColumn;
                            }

                            var area = TableConditionalStyle.AreaOf(
                                table, style, row, rowIndex, column, columnCount);
                            styles.RegisterCell(table, cell, cell.Background ?? styleBackground, area);
                            column += span;
                        }

                        rowIndex++;
                    }

                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Walk(cell.Blocks, breaks, styles);
                        }
                    }

                    break;
            }
        }
    }

    private void RegisterCell(TableBlock table, TableCell cell, Color? background, TableStyleArea? area = null)
    {
        var key = CellKeyOf(table, cell, background, area);
        if (key != DefaultCellKey && !_cells.ContainsKey(key))
        {
            _cells[key] = "TC" + (_cellStart + _cells.Count + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void RegisterRow(TableRow row)
    {
        if (RowKeyOf(row) is { } key && !_rows.ContainsKey(key))
        {
            _rows[key] = "TR" + (_rowStart + _rows.Count + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void RegisterTable(TableBlock table, string? parent)
    {
        var key = TableKeyOf(table, parent);
        if (!key.IsEmpty && !_tables.ContainsKey(key))
        {
            _tables[key] = "TT" + (_tableStart + _tables.Count + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Writes the collected cell, row and table styles.</summary>
    public void Write(XmlWriter writer)
    {
        foreach (var (key, name) in _cells)
        {
            WriteCellStyle(writer, name, key);
        }

        foreach (var (key, name) in _rows)
        {
            WriteRowStyle(writer, name, key);
        }

        foreach (var (key, name) in _tables)
        {
            WriteTableStyle(writer, name, key);
        }
    }

    private static void WriteCellStyle(XmlWriter writer, string name, CellKey key)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table-cell");
        writer.WriteStartElement("style", "table-cell-properties", OdtNamespaces.Style);

        writer.WriteAttributeString("style", "vertical-align", OdtNamespaces.Style, VerticalAlignmentValue(key.VerticalAlignment));

        if (key.Background is { } background)
        {
            writer.WriteAttributeString("fo", "background-color", OdtNamespaces.Fo, background.ToString());
        }

        if (key.Padding is { } padding)
        {
            writer.WriteAttributeString("fo", "padding-left", OdtNamespaces.Fo, PointsText(padding.Horizontal));
            writer.WriteAttributeString("fo", "padding-right", OdtNamespaces.Fo, PointsText(padding.Horizontal));
            writer.WriteAttributeString("fo", "padding-top", OdtNamespaces.Fo, PointsText(padding.Vertical));
            writer.WriteAttributeString("fo", "padding-bottom", OdtNamespaces.Fo, PointsText(padding.Vertical));
        }
        else
        {
            // A cell style replaces the default one, which would have brought the padding and the grid.
            writer.WriteAttributeString("fo", "padding-left", OdtNamespaces.Fo, PointsText(Typography.TableCellPaddingHorizontal.Points));
            writer.WriteAttributeString("fo", "padding-right", OdtNamespaces.Fo, PointsText(Typography.TableCellPaddingHorizontal.Points));
            writer.WriteAttributeString("fo", "padding-top", OdtNamespaces.Fo, PointsText(Typography.TableCellPaddingVertical.Points));
            writer.WriteAttributeString("fo", "padding-bottom", OdtNamespaces.Fo, PointsText(Typography.TableCellPaddingVertical.Points));
        }

        var writtenBorders = new HashSet<string>(StringComparer.Ordinal);
        if (key.Borders.Length > 0)
        {
            foreach (var side in key.Borders.Split('|'))
            {
                var parts = side.Split(':', 3);
                if (parts.Length == 3)
                {
                    writer.WriteAttributeString("fo", "border-" + parts[0], OdtNamespaces.Fo,
                        parts[1] == "none" ? "none" : parts[1] + " " + parts[2]);
                    writtenBorders.Add(parts[0]);
                }
            }
        }

        // The conditional border of the area applies to all four sides, otherwise the default grid.
        var sideBorder = key.AreaBorder.Length > 0
            ? key.AreaBorder
            : PointsText(Typography.TableBorderWidth.Points) + " solid #000000";
        foreach (var side in new[] { "top", "left", "bottom", "right" })
        {
            if (!writtenBorders.Contains(side))
            {
                writer.WriteAttributeString("fo", "border-" + side, OdtNamespaces.Fo, sideBorder);
            }
        }

        writer.WriteEndElement();

        if (key.AreaText.Length > 0)
        {
            writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
            foreach (var part in key.AreaText.Split(','))
            {
                switch (part)
                {
                    case "bold":
                        writer.WriteAttributeString("fo", "font-weight", OdtNamespaces.Fo, "bold");
                        break;
                    case "italic":
                        writer.WriteAttributeString("fo", "font-style", OdtNamespaces.Fo, "italic");
                        break;
                    default:
                        writer.WriteAttributeString("fo", "color", OdtNamespaces.Fo, part);
                        break;
                }
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteRowStyle(XmlWriter writer, string name, RowKey key)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table-row");
        writer.WriteStartElement("style", "table-row-properties", OdtNamespaces.Style);
        if (key.Height is { } height)
        {
            writer.WriteAttributeString("style", key.Rule == TableRowHeightRule.Exactly ? "row-height" : "min-row-height",
                OdtNamespaces.Style, PointsText(height));
            writer.WriteAttributeString(
                "style", "use-optimal-row-height", OdtNamespaces.Style,
                key.Rule == TableRowHeightRule.Exactly ? "false" : "true");
        }

        if (key.KeepTogether)
        {
            writer.WriteAttributeString("fo", "keep-together", OdtNamespaces.Fo, "always");
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteTableStyle(XmlWriter writer, string name, TableKey key)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table");
        if (key.Parent.Length > 0)
        {
            writer.WriteAttributeString("style", "parent-style-name", OdtNamespaces.Style, key.Parent);
        }

        writer.WriteStartElement("style", "table-properties", OdtNamespaces.Style);
        if (key.WidthPercent is { } percent)
        {
            writer.WriteAttributeString(
                "style", "rel-width", OdtNamespaces.Style,
                percent.ToString("0.##", CultureInfo.InvariantCulture) + "%");
        }

        if (key.Border.Length > 0)
        {
            writer.WriteAttributeString("fo", "border", OdtNamespaces.Fo, key.Border);
        }

        if (key.CellPaddingHorizontal == key.CellPaddingVertical && key.CellPaddingHorizontal is { } padding)
        {
            writer.WriteAttributeString("fo", "padding", OdtNamespaces.Fo, PointsText(padding));
        }
        else
        {
            // Read back by this library; ODF knows the cell padding only on the cells.
            if (key.CellPaddingHorizontal is { } horizontal)
            {
                writer.WriteAttributeString("fo", "padding-left", OdtNamespaces.Fo, PointsText(horizontal));
            }

            if (key.CellPaddingVertical is { } vertical)
            {
                writer.WriteAttributeString("fo", "padding-top", OdtNamespaces.Fo, PointsText(vertical));
            }
        }

        if (key.FixedWidth is { } fixedWidth)
        {
            writer.WriteAttributeString("style", "width", OdtNamespaces.Style, PointsText(fixedWidth));
        }

        if (key.LeftIndent is not null || key.FixedWidth is not null)
        {
            // LibreOffice honours the left margin and a width past the margin only for a left-aligned table.
            writer.WriteAttributeString("table", "align", OdtNamespaces.Table, "left");
        }

        if (key.LeftIndent is { } leftIndent)
        {
            writer.WriteAttributeString("fo", "margin-left", OdtNamespaces.Fo, PointsText(leftIndent));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string VerticalAlignmentValue(TableCellVerticalAlignment alignment) => alignment switch
    {
        TableCellVerticalAlignment.Center => "middle",
        TableCellVerticalAlignment.Bottom => "bottom",
        _ => "top",
    };

    private static string BordersKey(ParagraphBorders? borders)
    {
        if (borders is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        Append("top", borders.Top);
        Append("right", borders.Right);
        Append("bottom", borders.Bottom);
        Append("left", borders.Left);
        return string.Join('|', parts);

        void Append(string side, ParagraphBorder? border)
        {
            if (border is { Style: BorderStyle.None })
            {
                parts.Add(side + ":none:");
            }
            else if (border is not null)
            {
                parts.Add(side + ":" + PointsText(border.Width.Points) + ":" + BorderStyleValue(border.Style) + " " + border.Color);
            }
        }
    }

    private static string BorderKey(ParagraphBorder? border)
        => border is null || border.Style == BorderStyle.None
            ? string.Empty
            : PointsText(border.Width.Points) + " " + BorderStyleValue(border.Style) + " " + border.Color;

    private static string BorderStyleValue(BorderStyle style) => style switch
    {
        BorderStyle.Double => "double",
        BorderStyle.Dotted => "dotted",
        BorderStyle.Dashed => "dashed",
        _ => "solid",
    };

    private static string PointsText(double points)
        => points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
}
