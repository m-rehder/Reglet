namespace Reglet;

/// <summary>
/// the conditional formatting of a table area (a header row, a banded row, the first or last
/// column, the last row). A table style can define an area per
/// <see cref="TableStyle"/>; the direct values of the table and the cells win.
/// </summary>
public sealed class TableStyleArea
{
    /// <summary>The border of the cells in the area; null means no border.</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>Bold text; null means unchanged.</summary>
    public bool? Bold { get; init; }

    /// <summary>Italic text; null means unchanged.</summary>
    public bool? Italic { get; init; }

    /// <summary>The text color; null means unchanged.</summary>
    public Color? TextColor { get; init; }
}
