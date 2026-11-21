namespace Reglet;

/// <summary>A table with rows and optional column widths.</summary>
public sealed class TableBlock : Block
{
    private IReadOnlyList<Length>? _columnWidths;
    private Length? _cellPadding;
    private Length? _cornerRadius;
    private double? _widthPercent;

    /// <summary>Creates a table from rows.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public TableBlock(IEnumerable<TableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = ModelCollections.Snapshot(rows, nameof(rows));
    }

    /// <summary>The rows of the table (header rows are marked).</summary>
    public IReadOnlyList<TableRow> Rows { get; }

    /// <summary>Optional column widths; null means automatic widths.</summary>
    public IReadOnlyList<Length>? ColumnWidths
    {
        get => _columnWidths;
        init
        {
            if (value is not null)
            {
                var widths = ModelCollections.Snapshot(value, nameof(ColumnWidths));
                if (widths.Any(width => width.Points <= 0))
                {
                    throw new ArgumentOutOfRangeException(nameof(ColumnWidths), "Column widths must be positive.");
                }

                _columnWidths = widths;
            }
            else
            {
                _columnWidths = null;
            }
        }
    }

    /// <summary>Borders of the table (all grid lines); null means no borders.</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>
    /// radius of the corners of the table outline (OF-B04); null means square corners. The PDF
    /// draws the background and the outer border of the table with rounded corners; DOCX and ODT
    /// have no rounded table corners and report the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The radius is negative.</exception>
    public Length? CornerRadius
    {
        get => _cornerRadius;
        init
        {
            if (value is { Points: < 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(CornerRadius), "The corner radius must not be negative.");
            }

            _cornerRadius = value;
        }
    }

    /// <summary>Uniform cell padding; null means the default spacing.</summary>
    public Length? CellPadding
    {
        get => _cellPadding;
        init
        {
            if (value is { Points: < 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(CellPadding), "Cell padding must not be negative.");
            }

            _cellPadding = value;
        }
    }

    /// <summary>Table width in percent of the available width; null means full width.</summary>
    public double? WidthPercent
    {
        get => _widthPercent;
        init
        {
            if (value is { } percent && (!double.IsFinite(percent) || percent <= 0 || percent > 100))
            {
                throw new ArgumentOutOfRangeException(nameof(WidthPercent), "Table width must be between 0 and 100 percent.");
            }

            _widthPercent = value;
        }
    }

    /// <summary>
    /// reusable table style (phase Y, Y05). Its borders and cell padding apply where the table
    /// has no direct value; header and banded-row backgrounds are conditional formats.
    /// </summary>
    public TableStyle? Style { get; init; }

    /// <summary>a copy with other rows and every other property kept (used by the tree rebuilds).</summary>
    internal TableBlock WithRows(IEnumerable<TableRow> rows) => new(rows)
    {
        ColumnWidths = ColumnWidths,
        Border = Border,
        CornerRadius = CornerRadius,
        CellPadding = CellPadding,
        WidthPercent = WidthPercent,
        Style = Style,
    };
}
