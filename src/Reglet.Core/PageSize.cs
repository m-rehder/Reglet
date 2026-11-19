namespace Reglet;

/// <summary>Paper size of a page.</summary>
public sealed record PageSize
{
    /// <summary>Creates a paper size from width and height.</summary>
    /// <exception cref="ArgumentException">Width or height is not positive.</exception>
    public PageSize(Length width, Length height)
    {
        if (width.Points <= 0 || height.Points <= 0)
        {
            throw new ArgumentException("Paper width and height must be positive.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>Width of the page.</summary>
    public Length Width { get; }

    /// <summary>Height of the page.</summary>
    public Length Height { get; }

    /// <summary>DIN A4 (210 × 297 mm).</summary>
    public static PageSize A4 { get; } = new(Length.FromMillimeters(210), Length.FromMillimeters(297));

    /// <summary>DIN A5 (148 × 210 mm).</summary>
    public static PageSize A5 { get; } = new(Length.FromMillimeters(148), Length.FromMillimeters(210));

    /// <summary>DIN A3 (297 × 420 mm).</summary>
    public static PageSize A3 { get; } = new(Length.FromMillimeters(297), Length.FromMillimeters(420));

    /// <summary>US Letter (8,5 × 11 Zoll).</summary>
    public static PageSize Letter { get; } = new(Length.FromInches(8.5), Length.FromInches(11));

    /// <summary>US Legal (8,5 × 14 Zoll).</summary>
    public static PageSize Legal { get; } = new(Length.FromInches(8.5), Length.FromInches(14));
}
