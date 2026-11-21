namespace Reglet;

/// <summary>
/// Length conversions into OOXML units, kept in the ranges Word reads: a model length may be any finite value, a
/// twip, half-point or eighth-point value is an Int32 and an EMU coordinate at most 27,273,042,316,900 (ST_Coordinate).
/// </summary>
internal static class DocxUnits
{
    private const double MaxCoordinate = 27_273_042_316_900;

    /// <summary><paramref name="points"/> × <paramref name="factor"/>, rounded and clamped to ±Int32.MaxValue (symmetric, so a hanging indent may take the magnitude).</summary>
    public static long Scaled(double points, double factor)
        => (long)Math.Clamp(Math.Round(points * factor, MidpointRounding.AwayFromZero), -int.MaxValue, int.MaxValue);

    /// <summary>Points as EMU (12,700 per point), clamped to ST_Coordinate.</summary>
    public static long Emu(double points)
        => (long)Math.Clamp(Math.Round(points * 12700.0, MidpointRounding.AwayFromZero), -MaxCoordinate, MaxCoordinate);
}
