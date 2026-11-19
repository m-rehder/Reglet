namespace Reglet;

/// <summary>Metrics of an embedded TrueType font (widths from hmtx, vertical metrics from hhea).</summary>
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
    public double AscentEm => (double)_font.Ascender / _font.UnitsPerEm;

    /// <summary>Line height from hhea: ascender + descender + line gap (in em).</summary>
    public double LineHeightEm => (double)(_font.Ascender - _font.Descender + _font.LineGap) / _font.UnitsPerEm;
}
