namespace Reglet;

/// <summary>A worksheet: addressed cells, merged ranges, column widths, row heights, images and the page setup.</summary>
public sealed class Worksheet
{
    private readonly SortedDictionary<(int Row, int Column), WorksheetCell> _cells = [];
    private readonly List<CellRange> _merged = [];

    internal Worksheet(string name) => Name = name;

    /// <summary>The sheet name.</summary>
    public string Name { get; }

    /// <summary>The cell at a 1-based row and column; it is created when it does not exist yet.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row is not 1–1048576 or the column not 1–16384.</exception>
    public WorksheetCell Cell(int row, int column)
    {
        CellReference.Check(row, column);
        if (!_cells.TryGetValue((row, column), out var cell))
        {
            cell = new WorksheetCell(row, column);
            _cells.Add((row, column), cell);
        }

        return cell;
    }

    /// <summary>The cell at an A1 reference such as <c>B3</c>; it is created when it does not exist yet.</summary>
    /// <exception cref="ArgumentException">The reference is not a single A1 cell reference.</exception>
    public WorksheetCell Cell(string reference)
    {
        var (row, column) = CellReference.Parse(reference);
        return Cell(row, column);
    }

    /// <summary>The cells that exist, by row and then column.</summary>
    public IEnumerable<WorksheetCell> Cells => _cells.Values;

    /// <summary>Merged ranges, in the order they were merged.</summary>
    public IReadOnlyList<CellRange> MergedRanges => _merged;

    /// <summary>Column widths in Excel's character units, by 1-based column; a column without one has the default width.</summary>
    public IDictionary<int, double> ColumnWidths { get; } = new SortedDictionary<int, double>();

    /// <summary>Row heights in points, by 1-based row; a row without one is as high as its content.</summary>
    public IDictionary<int, double> RowHeights { get; } = new SortedDictionary<int, double>();

    /// <summary>Images anchored at cells.</summary>
    public IList<WorksheetImage> Images { get; } = [];

    /// <summary>Paper, orientation, scaling, margins, print area and repeated rows.</summary>
    public SheetPageSetup PageSetup
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = new();

    /// <summary>Merges a range such as <c>A1:C1</c>; the top-left cell keeps the value.</summary>
    /// <exception cref="ArgumentException">The reference is invalid, a single cell, or overlaps a merged range.</exception>
    public void Merge(string range)
    {
        var parsed = CellRange.Parse(range);
        if (parsed.First == parsed.Last)
        {
            throw new ArgumentException("A merged range needs more than one cell.", nameof(range));
        }

        if (_merged.Any(existing => existing.Overlaps(parsed)))
        {
            throw new ArgumentException($"The range {parsed} overlaps a merged range.", nameof(range));
        }

        _merged.Add(parsed);
    }

    /// <summary>Changes the style of every cell in a range such as <c>A1:D4</c> (creating the cells).</summary>
    public void SetStyle(string range, Func<CellStyle, CellStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var parsed = CellRange.Parse(range);
        for (var row = parsed.First.Row; row <= parsed.Last.Row; row++)
        {
            for (var column = parsed.First.Column; column <= parsed.Last.Column; column++)
            {
                var cell = Cell(row, column);
                cell.Style = change(cell.Style);
            }
        }
    }

    /// <summary>Draws a border around a range: the outer side of each edge cell gets <paramref name="line"/>.</summary>
    public void SetOutsideBorder(string range, CellBorderLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var parsed = CellRange.Parse(range);
        for (var row = parsed.First.Row; row <= parsed.Last.Row; row++)
        {
            for (var column = parsed.First.Column; column <= parsed.Last.Column; column++)
            {
                if (row != parsed.First.Row && row != parsed.Last.Row && column != parsed.First.Column && column != parsed.Last.Column)
                {
                    continue;
                }

                var cell = Cell(row, column);
                var border = cell.Style.Border;
                cell.Style = cell.Style with
                {
                    Border = border with
                    {
                        Top = row == parsed.First.Row ? line : border.Top,
                        Bottom = row == parsed.Last.Row ? line : border.Bottom,
                        Left = column == parsed.First.Column ? line : border.Left,
                        Right = column == parsed.Last.Column ? line : border.Right,
                    },
                };
            }
        }
    }

    /// <summary>The cell if it exists, without creating it.</summary>
    internal WorksheetCell? Find(int row, int column) => _cells.GetValueOrDefault((row, column));
}
