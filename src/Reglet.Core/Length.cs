using System.Globalization;

namespace Reglet;

/// <summary>Unit for specifying a length.</summary>
public enum LengthUnit
{
    /// <summary>Points (1/72 inch), the canonical unit.</summary>
    Points,

    /// <summary>Millimeter.</summary>
    Millimeters,

    /// <summary>Zentimeter.</summary>
    Centimeters,

    /// <summary>Zoll.</summary>
    Inches,
}

/// <summary>
/// canonical length value. Stored internally in points; other units are
/// converted with low loss (1 inch = 72 points). Negative values are allowed (e.g. for
/// indents); non-finite values are rejected.
/// </summary>
public readonly record struct Length
{
    private const double PointsPerInch = 72.0;
    private const double MillimetersPerInch = 25.4;
    private const double PointsPerMillimeter = PointsPerInch / MillimetersPerInch;
    private const double PointsPerCentimeter = PointsPerMillimeter * 10.0;

    /// <summary>Creates a length from a point value.</summary>
    /// <param name="points">Length in points; must be finite.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public Length(double points)
    {
        if (double.IsNaN(points) || double.IsInfinity(points))
        {
            throw new ArgumentOutOfRangeException(nameof(points), points, "Length must be a finite value.");
        }

        Points = points;
    }

    /// <summary>Value in points (1/72 inch).</summary>
    public double Points { get; }

    /// <summary>Length of zero points.</summary>
    public static Length Zero => new(0.0);

    /// <summary>Creates a length from points.</summary>
    public static Length FromPoints(double points) => new(points);

    /// <summary>Creates a length from millimeters.</summary>
    public static Length FromMillimeters(double millimeters) => new(millimeters * PointsPerMillimeter);

    /// <summary>Creates a length from centimeters.</summary>
    public static Length FromCentimeters(double centimeters) => new(centimeters * PointsPerCentimeter);

    /// <summary>Creates a length from inches.</summary>
    public static Length FromInches(double inches) => new(inches * PointsPerInch);

    /// <summary>Creates a length from value and unit.</summary>
    public static Length From(double value, LengthUnit unit) => unit switch
    {
        LengthUnit.Points => FromPoints(value),
        LengthUnit.Millimeters => FromMillimeters(value),
        LengthUnit.Centimeters => FromCentimeters(value),
        LengthUnit.Inches => FromInches(value),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>Converts the length into the specified unit.</summary>
    public double To(LengthUnit unit) => unit switch
    {
        LengthUnit.Points => Points,
        LengthUnit.Millimeters => Points / PointsPerMillimeter,
        LengthUnit.Centimeters => Points / PointsPerCentimeter,
        LengthUnit.Inches => Points / PointsPerInch,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <inheritdoc />
    public override string ToString() => Points.ToString("0.###", CultureInfo.InvariantCulture) + "pt";
}
