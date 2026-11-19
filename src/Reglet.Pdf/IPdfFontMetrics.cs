namespace Reglet;

/// <summary>Provides character widths and vertical layout metrics for text measurement.</summary>
internal interface IPdfFontMetrics
{
    /// <summary>Width of a code point in 1000ths of an em.</summary>
    double GetWidth(int codePoint);

    /// <summary>
    /// ascender in em. Determines how far the baseline is below the
    /// line start; Word and LibreOffice use the same font size for this.
    /// </summary>
    double AscentEm { get; }

    /// <summary>
    /// line height relative to the font size. Embedded fonts use the metrics of the
    /// font file (ascender + descender + line gap); default fonts without
    /// font file keep the usual factor 1.2.
    /// </summary>
    double LineHeightEm { get; }
}
