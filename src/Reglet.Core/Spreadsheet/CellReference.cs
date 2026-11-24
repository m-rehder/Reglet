using System.Globalization;

namespace Reglet;

/// <summary>A1 cell references: parsing, formatting and Excel's limits.</summary>
internal static class CellReference
{
    public const int MaxRow = 1_048_576;
    public const int MaxColumn = 16_384;

    public static void Check(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, MaxRow);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, MaxColumn);
    }

    public static string ColumnName(int column)
    {
        var name = string.Empty;
        for (; column > 0; column = (column - 1) / 26)
        {
            name = (char)('A' + ((column - 1) % 26)) + name;
        }

        return name;
    }

    public static string Format(int row, int column) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    public static (int Row, int Column) Parse(string reference)
        => TryParse(reference, out var row, out var column)
            ? (row, column)
            : throw new ArgumentException($"'{reference}' is not an A1 cell reference.", nameof(reference));

    /// <summary>Parses <c>B3</c> or <c>$B$3</c>.</summary>
    public static bool TryParse(string? reference, out int row, out int column)
    {
        row = column = 0;
        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        var index = 0;
        if (reference[index] == '$') index++;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]) && column <= MaxColumn)
        {
            column = (column * 26) + (char.ToUpperInvariant(reference[index]) - 'A' + 1);
            index++;
        }

        if (index < reference.Length && reference[index] == '$') index++;
        return column is >= 1 and <= MaxColumn
            && index < reference.Length && char.IsAsciiDigit(reference[index])
            && int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out row)
            && row is >= 1 and <= MaxRow;
    }
}

/// <summary>A rectangular range of cells, such as <c>A1:C3</c>.</summary>
/// <param name="First">The top-left cell (1-based).</param>
/// <param name="Last">The bottom-right cell (1-based).</param>
public readonly record struct CellRange((int Row, int Column) First, (int Row, int Column) Last)
{
    /// <summary>Parses <c>A1:C3</c> (corners in any order) or a single cell <c>B2</c>.</summary>
    /// <exception cref="ArgumentException">The text is not an A1 range.</exception>
    public static CellRange Parse(string range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var parts = range.Split(':');
        if (parts.Length is not (1 or 2)
            || !CellReference.TryParse(parts[0], out var row1, out var column1)
            || !CellReference.TryParse(parts[^1], out var row2, out var column2))
        {
            throw new ArgumentException($"'{range}' is not an A1 range.", nameof(range));
        }

        return new((Math.Min(row1, row2), Math.Min(column1, column2)), (Math.Max(row1, row2), Math.Max(column1, column2)));
    }

    /// <summary>Whether the two ranges share a cell.</summary>
    public bool Overlaps(CellRange other)
        => First.Row <= other.Last.Row && other.First.Row <= Last.Row
            && First.Column <= other.Last.Column && other.First.Column <= Last.Column;

    /// <summary>The range as <c>A1:C3</c> (a single cell as <c>B2</c>).</summary>
    public override string ToString()
        => First == Last
            ? CellReference.Format(First.Row, First.Column)
            : CellReference.Format(First.Row, First.Column) + ":" + CellReference.Format(Last.Row, Last.Column);
}
