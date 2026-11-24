namespace Reglet;

/// <summary>How a worksheet prints: paper, orientation, scaling, margins, print area and repeated rows.</summary>
public sealed record SheetPageSetup
{
    /// <summary>Excel's default margins: 0.75 in top and bottom, 0.7 in left and right.</summary>
    public static PageMargins DefaultMargins { get; } = new(Length.FromInches(0.75), Length.FromInches(0.7), Length.FromInches(0.75), Length.FromInches(0.7));

    /// <summary>Portrait or landscape.</summary>
    public PageOrientation Orientation { get; init; }

    /// <summary>The paper size.</summary>
    public SheetPaperSize PaperSize { get; init; }

    /// <summary>
    /// Fits the print to this many pages wide; null prints at 100 %. With <see cref="FitToPagesTall"/> 0 the sheet is
    /// one page wide and as many pages tall as it needs ("fit all columns on one page").
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? FitToPagesWide
    {
        get;
        init => field = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(FitToPagesWide));
    }

    /// <summary>Fits the print to this many pages tall, 0 meaning as many as needed; null with a width means 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? FitToPagesTall
    {
        get;
        init => field = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(FitToPagesTall));
    }

    /// <summary>The page margins.</summary>
    public PageMargins Margins { get; init; } = DefaultMargins;

    /// <summary>The printed range, such as <c>A1:F40</c>; null prints the used range.</summary>
    /// <exception cref="ArgumentException">The reference is not a valid A1 range.</exception>
    public string? PrintArea
    {
        get;
        init => field = value is null ? null : CellRange.Parse(value).ToString();
    }

    /// <summary>The first and last 1-based row repeated at the top of every printed page; null means none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rows are not 1 ≤ first ≤ last ≤ 1048576.</exception>
    public (int First, int Last)? RepeatRows
    {
        get;
        init => field = value is not { } rows || (rows.First >= 1 && rows.First <= rows.Last && rows.Last <= CellReference.MaxRow)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RepeatRows));
    }
}

/// <summary>The paper a worksheet prints on.</summary>
public enum SheetPaperSize
{
    /// <summary>A4 (210 × 297 mm).</summary>
    A4,

    /// <summary>US Letter (8.5 × 11 in).</summary>
    Letter,
}
