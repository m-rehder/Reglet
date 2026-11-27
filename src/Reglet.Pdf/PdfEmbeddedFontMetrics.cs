namespace Reglet;

/// <summary>
/// Metrics of an embedded TrueType font: widths from hmtx, vertical metrics from hhea. An East Asian font
/// (<see cref="TrueTypeFont.IsEastAsian"/>) takes Word's single line instead: 1.3 times its Windows ascent and descent
/// (OS/2 <c>usWinAscent</c> + <c>usWinDescent</c>), the extra room half above and half below the text. Measured in
/// Word for Windows with 13 Chinese, Japanese and Korean fonts, from SimSun (1.30 em) to Meiryo (1.95 em).
/// </summary>
internal sealed class PdfEmbeddedFontMetrics : IPdfFontMetrics
{
    private readonly TrueTypeFont _font;
    private readonly double _scale;

    public PdfEmbeddedFontMetrics(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        _scale = 1000.0 / font.UnitsPerEm;
    }

    public double GetWidth(int codePoint)
        => _font.GetAdvanceWidth(_font.GetGlyphId(codePoint)) * _scale;

    /// <summary>Ascender from hhea (in em); baseline as in Word and LibreOffice.</summary>
    public double AscentEm => EastAsianLine
        ? (_font.WinAscent + (EastAsianExtra * (_font.WinAscent + _font.WinDescent))) / _font.UnitsPerEm
        : (double)_font.Ascender / _font.UnitsPerEm;

    /// <summary>Descender from hhea (in em, positive).</summary>
    public double DescentEm => EastAsianLine
        ? (_font.WinDescent + (EastAsianExtra * (_font.WinAscent + _font.WinDescent))) / _font.UnitsPerEm
        : -(double)_font.Descender / _font.UnitsPerEm;

    /// <summary>Line height from hhea: ascender + descender + line gap (in em).</summary>
    public double LineHeightEm => EastAsianLine
        ? (1 + (2 * EastAsianExtra)) * (_font.WinAscent + _font.WinDescent) / _font.UnitsPerEm
        : (double)(_font.Ascender - _font.Descender + _font.LineGap) / _font.UnitsPerEm;

    /// <summary>The share of the Windows height Word adds above and below the text of an East Asian font.</summary>
    private const double EastAsianExtra = 0.15;

    private bool EastAsianLine => _font.IsEastAsian && _font.WinAscent + _font.WinDescent > 0;
}
