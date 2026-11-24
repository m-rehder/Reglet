namespace Reglet;

/// <summary>The formatting of a worksheet cell (ADR-0043); change it with <c>with</c>.</summary>
public sealed record CellStyle
{
    /// <summary>No formatting: the workbook's default font, no fill, no border, General format.</summary>
    public static CellStyle Default { get; } = new();

    /// <summary>The font; <see cref="CellFont.Default"/> keeps the workbook's.</summary>
    public CellFont Font { get; init; } = CellFont.Default;

    /// <summary>A solid background; null means none.</summary>
    public Color? Fill { get; init; }

    /// <summary>The borders of the cell's four sides.</summary>
    public CellBorders Border { get; init; } = CellBorders.None;

    /// <summary>Horizontal alignment; General aligns text left and numbers right.</summary>
    public CellHorizontalAlignment HorizontalAlignment { get; init; }

    /// <summary>Vertical alignment; Excel's default is bottom.</summary>
    public CellVerticalAlignment VerticalAlignment { get; init; }

    /// <summary>Wraps text onto several lines inside the cell.</summary>
    public bool WrapText { get; init; }

    /// <summary>Indent in Excel's steps (about three characters each); 0 means none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The indent is not 0–250.</exception>
    public int Indent
    {
        get;
        init => field = value is >= 0 and <= 250 ? value : throw new ArgumentOutOfRangeException(nameof(Indent), "An indent must be 0–250.");
    }

    /// <summary>An Excel number format code, such as <c>#,##0.00</c> or <c>m/d/yyyy</c>; null means General.</summary>
    public string? NumberFormat { get; init; }
}

/// <summary>The font of a cell or of a rich-text run; a null property keeps the workbook's default.</summary>
public sealed record CellFont
{
    /// <summary>The workbook's default font (Calibri 11 when written).</summary>
    public static CellFont Default { get; } = new();

    /// <summary>The font family; null keeps the default.</summary>
    public string? Name { get; init; }

    /// <summary>The size in points; null keeps the default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not 1–409 points.</exception>
    public double? Size
    {
        get;
        init => field = value is null or (>= 1 and <= 409) ? value : throw new ArgumentOutOfRangeException(nameof(Size), "A font size must be 1–409 points.");
    }

    /// <summary>Bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic.</summary>
    public bool Italic { get; init; }

    /// <summary>The text colour; null keeps the default (automatic).</summary>
    public Color? Color { get; init; }
}

/// <summary>The borders of a cell's four sides; a null side has none.</summary>
public sealed record CellBorders
{
    /// <summary>No borders.</summary>
    public static CellBorders None { get; } = new();

    /// <summary>The same line on all four sides.</summary>
    public static CellBorders All(CellBorderLine line) => new() { Top = line, Bottom = line, Left = line, Right = line };

    /// <summary>The top side.</summary>
    public CellBorderLine? Top { get; init; }

    /// <summary>The bottom side.</summary>
    public CellBorderLine? Bottom { get; init; }

    /// <summary>The left side.</summary>
    public CellBorderLine? Left { get; init; }

    /// <summary>The right side.</summary>
    public CellBorderLine? Right { get; init; }
}

/// <summary>One border line of a cell.</summary>
/// <param name="Style">Thin, medium or thick.</param>
/// <param name="Color">The colour; null means automatic (black).</param>
public sealed record CellBorderLine(CellBorderStyle Style, Color? Color = null);

/// <summary>The weight of a cell border.</summary>
public enum CellBorderStyle
{
    /// <summary>Thin (Excel's default line).</summary>
    Thin,

    /// <summary>Medium.</summary>
    Medium,

    /// <summary>Thick.</summary>
    Thick,
}

/// <summary>Horizontal alignment in a cell.</summary>
public enum CellHorizontalAlignment
{
    /// <summary>Text left, numbers right.</summary>
    General,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right.</summary>
    Right,
}

/// <summary>Vertical alignment in a cell.</summary>
public enum CellVerticalAlignment
{
    /// <summary>Bottom (Excel's default).</summary>
    Bottom,

    /// <summary>Top.</summary>
    Top,

    /// <summary>Centred.</summary>
    Center,
}
