namespace Reglet;

/// <summary>
/// a reusable table style (phase Y, Y05): borders, cell padding, a header row background and
/// a banded-row background. The direct values on <see cref="TableBlock"/> override the style.
/// </summary>
public sealed class TableStyle
{
    /// <summary>Creates a table style.</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public TableStyle(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Style name; used as the DOCX style ID and the ODT style name.</summary>
    public string Name { get; }

    /// <summary>Borders of the grid; null means the default thin black grid (a border of style None removes it).</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>Cell padding; null means the default spacing.</summary>
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

    /// <summary>Background of the header rows; null means none.</summary>
    public Color? HeaderBackground { get; init; }

    /// <summary>Background of the banded (alternating) rows; null means none.</summary>
    public Color? BandBackground { get; init; }

    /// <summary>Background of the first column; null means none.</summary>
    public Color? FirstColumnBackground { get; init; }

    /// <summary>Background of the last column; null means none.</summary>
    public Color? LastColumnBackground { get; init; }

    /// <summary>Background of the last row; null means none.</summary>
    public Color? LastRowBackground { get; init; }

    /// <summary>The conditional formatting of the header rows; null means none.</summary>
    public TableStyleArea? HeaderRow { get; init; }

    /// <summary>The conditional formatting of the banded (alternating) rows; null means none.</summary>
    public TableStyleArea? BandRow { get; init; }

    /// <summary>The conditional formatting of the first column; null means none.</summary>
    public TableStyleArea? FirstColumn { get; init; }

    /// <summary>The conditional formatting of the last column; null means none.</summary>
    public TableStyleArea? LastColumn { get; init; }

    /// <summary>The conditional formatting of the last row; null means none.</summary>
    public TableStyleArea? LastRow { get; init; }

    private Length? _cellPadding;
}
