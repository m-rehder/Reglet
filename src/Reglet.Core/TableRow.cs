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

    /// <summary>Line height; null means automatic.</summary>
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

    /// <summary>a copy with other cells and every other property kept (used by the tree rebuilds).</summary>
    internal TableRow WithCells(IEnumerable<TableCell> cells) => new(cells)
    {
        IsHeader = IsHeader,
        Height = Height,
        HeightRule = HeightRule,
    };
}
