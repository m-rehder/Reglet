namespace Reglet;

/// <summary>
/// metrics of the PDF default fonts. For the ASCII range the AFM widths
/// used; outside it the family-specific default applies. The ascender is the `hhea` value of the
/// font Word and LibreOffice draw instead (Arial, Times New Roman, Courier New); the line height remains
/// the usual factor 1.2, because default fonts have no font file with layout metrics
/// carry along.
/// </summary>
internal sealed class PdfStandardFontMetrics : IPdfFontMetrics
{
    private readonly int[] _widths;
    private readonly double _fallback;
    private readonly double _ascent;

    public double GetWidth(int codePoint)
        => codePoint is >= 32 and <= 126 ? _widths[codePoint - 32] : _fallback;

    /// <summary>
    /// Ascender in em (Helvetica 0.905, Times 0.891, Courier 0.833). The AFM ascender (0.718) is the
    /// height of a lowercase "d"; with it capitals and umlauts touched or crossed a cell's top border.
    /// </summary>
    public double AscentEm => _ascent;

    /// <summary>
    /// default fonts have no font file and thus no layout metrics; the
    /// line height keeps the usual factor 1.2 (see Typography.LineHeightFactor).
    /// </summary>
    public double LineHeightEm => Typography.LineHeightFactor;

    public PdfStandardFontMetrics(string baseFont)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseFont);

        if (baseFont.StartsWith("Courier", StringComparison.Ordinal))
        {
            _widths = CourierWidths;
            _fallback = 600;
            _ascent = 0.833;
        }
        else if (baseFont.StartsWith("Times", StringComparison.Ordinal))
        {
            _widths = baseFont.Contains("Bold", StringComparison.Ordinal) ? TimesBoldWidths : TimesWidths;
            _fallback = 500;
            _ascent = 0.891;
        }
        else
        {
            _widths = baseFont.Contains("Bold", StringComparison.Ordinal) ? HelveticaBoldWidths : HelveticaWidths;
            _fallback = 556;
            _ascent = 0.905;
        }
    }

    private static readonly int[] CourierWidths = CreateUniform(600);

    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015,
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        278, 278, 278, 469, 556, 333,
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
        334, 260, 334, 584,
    ];

    private static readonly int[] HelveticaBoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975,
        722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
        333, 278, 333, 584, 556, 333,
        556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500,
        389, 280, 389, 584,
    ];

    private static readonly int[] TimesWidths =
    [
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 278, 278, 564, 564, 564, 444, 921,
        722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, 556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611,
        333, 278, 333, 469, 500, 333,
        444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, 500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444,
        480, 200, 480, 541,
    ];

    private static readonly int[] TimesBoldWidths =
    [
        250, 333, 555, 500, 500, 1000, 833, 278, 333, 333, 500, 570, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 333, 333, 570, 570, 570, 500, 930,
        722, 667, 722, 722, 667, 611, 778, 778, 389, 500, 778, 667, 944, 722, 778, 611, 778, 722, 556, 667, 722, 722, 1000, 722, 722, 667,
        333, 278, 333, 581, 500, 333,
        500, 556, 444, 556, 444, 333, 500, 556, 278, 333, 556, 278, 833, 556, 500, 556, 556, 444, 389, 333, 556, 500, 722, 500, 500, 444,
        394, 220, 394, 520,
    ];

    private static int[] CreateUniform(int width)
    {
        var widths = new int[95];
        Array.Fill(widths, width);
        return widths;
    }
}
