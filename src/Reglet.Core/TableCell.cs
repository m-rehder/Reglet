namespace Reglet;

/// <summary>Vertical alignment of the content of a table cell.</summary>
public enum TableCellVerticalAlignment
{
    /// <summary>Top.</summary>
    Top,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Bottom.</summary>
    Bottom,
}

/// <summary>Rule for the row height of a table.</summary>
public enum TableRowHeightRule
{
    /// <summary>Height from the content (default).</summary>
    Auto,

    /// <summary>Minimum height.</summary>
    AtLeast,

    /// <summary>Fixed height.</summary>
    Exactly,
}

/// <summary>A table cell.</summary>
public sealed record TableCell
{
    private TextAlignment _alignment = TextAlignment.Left;
    private TableCellVerticalAlignment _verticalAlignment = TableCellVerticalAlignment.Top;
    private int _columnSpan = 1;
    private int _rowSpan = 1;
    private Length? _padding;

    /// <summary>Creates a cell with a paragraph from the specified text.</summary>
    public TableCell(string text)
        : this(new Block[] { new Paragraph(new TextRun(text)) })
    {
    }

    /// <summary>Creates a cell with exactly one block.</summary>
    public TableCell(Block block)
        : this(new[] { block })
    {
    }

    /// <summary>Creates a cell with several blocks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public TableCell(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = ModelCollections.Snapshot(blocks, nameof(blocks));
    }

    /// <summary>The blocks of the cell.</summary>
    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>Horizontal alignment of the cell content.</summary>
    public TextAlignment Alignment
    {
        get => _alignment;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(Alignment), value, "Unknown text alignment.");
            }

            _alignment = value;
        }
    }

    /// <summary>Vertical alignment of the cell content.</summary>
    public TableCellVerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(VerticalAlignment), value, "Unknown vertical alignment.");
            }

            _verticalAlignment = value;
        }
    }

    /// <summary>
    /// The largest column or row span (1000). The writers lay out a span cell by cell, so an unbounded span from a
    /// file (two billion) kept them busy indefinitely; real tables stay far below (Word has at most 63 columns).
    /// </summary>
    public const int MaxSpan = 1000;

    /// <summary>Number of spanned columns (default 1, at most <see cref="MaxSpan"/>).</summary>
    public int ColumnSpan
    {
        get => _columnSpan;
        init => _columnSpan = value is >= 1 and <= MaxSpan
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ColumnSpan), "Column span must be between 1 and 1000.");
    }

    /// <summary>Number of spanned rows (default 1, at most <see cref="MaxSpan"/>).</summary>
    public int RowSpan
    {
        get => _rowSpan;
        init => _rowSpan = value is >= 1 and <= MaxSpan
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RowSpan), "Row span must be between 1 and 1000.");
    }

    /// <summary>Background color of the cell; null means none.</summary>
    public Color? Background { get; init; }

    /// <summary>Padding of the cell; null means the table spacing.</summary>
    public Length? Padding
    {
        get => _padding;
        init
        {
            if (value is { Points: < 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Padding), "Cell padding must not be negative.");
            }

            _padding = value;
        }
    }

    /// <summary>Custom cell borders; null means the table borders.</summary>
    public ParagraphBorders? Borders { get; init; }
}
