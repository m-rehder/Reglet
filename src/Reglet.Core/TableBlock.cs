namespace Reglet;

/// <summary>A table with rows and optional column widths.</summary>
public sealed class TableBlock : Block
{
    private IReadOnlyList<Length>? _columnWidths;
    private Length? _cellPadding;
    private Length? _cellPaddingHorizontal;
    private Length? _cellPaddingVertical;
    private Length? _leftIndent;
    private Length? _cornerRadius;
    private double? _widthPercent;
    private IReadOnlyList<double>? _columnWidthPercents;

    /// <summary>The largest <see cref="WidthPercent"/>, Word's limit.</summary>
    internal const double MaxWidthPercent = 600;

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

    /// <summary>
    /// Column widths in percent of the table width (Word's <c>w:tcW w:type="pct"</c>), for a table whose author does not
    /// know the text width; null means none. When set, it replaces <see cref="ColumnWidths"/>: the writers turn it into
    /// widths from the section's text width (times <see cref="WidthPercent"/>). Columns past the list share what is left of
    /// 100 percent equally; a full list that sums to less than 100 is stretched in proportion, as Word does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive or the values sum to more than 100.</exception>
    public IReadOnlyList<double>? ColumnWidthPercents
    {
        get => _columnWidthPercents;
        init
        {
            if (value is not null)
            {
                var percents = ModelCollections.Snapshot(value, nameof(ColumnWidthPercents));
                if (percents.Any(percent => !double.IsFinite(percent) || percent <= 0) || percents.Sum() > 100 + 1e-9)
                {
                    throw new ArgumentOutOfRangeException(nameof(ColumnWidthPercents), "Column percents must be positive and sum to at most 100.");
                }

                _columnWidthPercents = percents;
            }
            else
            {
                _columnWidthPercents = null;
            }
        }
    }

    /// <summary>
    /// Borders of the table (all grid lines); null means the default thin black grid (0.5 pt), <see cref="ParagraphBorder.None"/>
    /// means no borders.
    /// </summary>
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

    /// <summary>Left and right padding of the cells; overrides <see cref="CellPadding"/> on those sides.</summary>
    public Length? CellPaddingHorizontal
    {
        get => _cellPaddingHorizontal;
        init => _cellPaddingHorizontal = NonNegative(value, nameof(CellPaddingHorizontal));
    }

    /// <summary>Top and bottom padding of the cells; overrides <see cref="CellPadding"/> on those sides.</summary>
    public Length? CellPaddingVertical
    {
        get => _cellPaddingVertical;
        init => _cellPaddingVertical = NonNegative(value, nameof(CellPaddingVertical));
    }

    /// <summary>
    /// Moves the table and its borders from the left margin (Word's table indent); negative moves it into the margin, for
    /// example by the left cell padding so the text of the first column stands at the margin. Null means 0.
    /// </summary>
    public Length? LeftIndent
    {
        get => _leftIndent;
        init
        {
            if (value is { } indent && !double.IsFinite(indent.Points))
            {
                throw new ArgumentOutOfRangeException(nameof(LeftIndent), "The table indent must be finite.");
            }

            _leftIndent = value;
        }
    }

    /// <summary>
    /// Keeps a full set of <see cref="ColumnWidths"/> even when it is wider than the text area, so the table runs into the
    /// right margin (Word's fixed table layout, <c>w:tblLayout w:type="fixed"</c>). False, the default, shrinks such a
    /// table in proportion to the text width plus the room a negative <see cref="LeftIndent"/> opens in the left margin.
    /// </summary>
    public bool FixedColumnWidths { get; init; }

    private static Length? NonNegative(Length? value, string name)
        => value is { Points: < 0 } ? throw new ArgumentOutOfRangeException(name, "Cell padding must not be negative.") : value;

    /// <summary>
    /// Table width in percent of the available width, up to 600 as in Word (above 100 the table runs into the right
    /// margin); null means full width.
    /// </summary>
    public double? WidthPercent
    {
        get => _widthPercent;
        init
        {
            if (value is { } percent && (!double.IsFinite(percent) || percent <= 0 || percent > MaxWidthPercent))
            {
                throw new ArgumentOutOfRangeException(nameof(WidthPercent), "Table width must be between 0 and 600 percent.");
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
        ColumnWidthPercents = ColumnWidthPercents,
        Border = Border,
        CornerRadius = CornerRadius,
        CellPadding = CellPadding,
        CellPaddingHorizontal = CellPaddingHorizontal,
        CellPaddingVertical = CellPaddingVertical,
        LeftIndent = LeftIndent,
        FixedColumnWidths = FixedColumnWidths,
        WidthPercent = WidthPercent,
        Style = Style,
    };
}
