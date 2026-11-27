namespace Reglet;

/// <summary>A table row.</summary>
public sealed record TableRow
{
    private Length? _height;
    /// <summary>Creates a row from cells.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is null.</exception>
    public TableRow(IEnumerable<TableCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Cells = ModelCollections.Snapshot(cells, nameof(cells));
    }

    /// <summary>Creates a row from individual cells.</summary>
    public TableRow(params TableCell[] cells)
        : this((IEnumerable<TableCell>)cells)
    {
    }

    /// <summary>The cells of the row.</summary>
    public IReadOnlyList<TableCell> Cells { get; }

    /// <summary>Marks the row as a header row.</summary>
    public bool IsHeader { get; init; }

    /// <summary>
    /// Row height; null means automatic. As in Word, a minimum height is for the content (the cells' top and bottom
    /// padding and the table's grid above the row come on top of it; ODT writes them into <c>style:min-row-height</c>), an
    /// exact height (<see cref="TableRowHeightRule.Exactly"/>) includes both.
    /// </summary>
    public Length? Height
    {
        get => _height;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Height), "Row height must be positive.");
            }

            _height = value;
        }
    }

    /// <summary>Rule for the row height.</summary>
    public TableRowHeightRule HeightRule { get; init; } = TableRowHeightRule.Auto;

    /// <summary>
    /// Keeps the row on one page (Word's <c>w:cantSplit</c>, ODT's <c>fo:keep-together</c>): a row that does not fit at the
    /// bottom of a page moves whole to the next one. A row taller than a page is still split.
    /// </summary>
    public bool KeepTogether { get; init; }

    /// <summary>a copy with other cells and every other property kept (used by the tree rebuilds).</summary>
    internal TableRow WithCells(IEnumerable<TableCell> cells) => new(cells)
    {
        IsHeader = IsHeader,
        Height = Height,
        HeightRule = HeightRule,
        KeepTogether = KeepTogether,
    };
}
