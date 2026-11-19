namespace Reglet;

/// <summary>Fluent builder for a table.</summary>
public sealed class TableBuilder
{
    private readonly List<TableRow> _rows = [];
    private IReadOnlyList<Length>? _columnWidths;

    internal TableBuilder()
    {
    }

    /// <summary>Adds a header row with the specified cell texts.</summary>
    public TableBuilder Header(params string[] cells) => AddRow(cells, isHeader: true);

    /// <summary>Adds a data row with the specified cell texts.</summary>
    public TableBuilder Row(params string[] cells) => AddRow(cells, isHeader: false);

    /// <summary>Sets optional column widths.</summary>
    public TableBuilder ColumnWidths(params Length[] widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        _columnWidths = widths.ToArray();
        return this;
    }

    /// <summary>Creates the table.</summary>
    public TableBlock Build() => new(_rows) { ColumnWidths = _columnWidths };

    private TableBuilder AddRow(string[] cells, bool isHeader)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var row = new TableRow(cells.Select(cell => new TableCell(cell))) { IsHeader = isHeader };
        _rows.Add(row);
        return this;
    }
}
