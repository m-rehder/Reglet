using System.Text;

namespace Reglet;

/// <summary>A resolved font: resource name, metrics and encoding.</summary>
internal sealed class PdfFontHandle
{
    private readonly Func<string, byte[]> _encode;
    private readonly Func<string, bool> _canEncode;
    private readonly TextShaper? _shaper;
    private readonly Action<string, IReadOnlyList<ShapedGlyph>>? _recordShaped;

    // resourceName registers the font resource on first use, so a font that is only measured is not written.
    public PdfFontHandle(
        Func<string> resourceName,
        IPdfFontMetrics metrics,
        bool isEmbedded,
        Func<string, byte[]> encode,
        Func<string, bool> canEncode,
        bool syntheticBold = false,
        bool syntheticItalic = false,
        TextShaper? shaper = null,
        Action<string, IReadOnlyList<ShapedGlyph>>? recordShaped = null,
        bool requireGlyphs = false)
    {
        _requireGlyphs = requireGlyphs;
        _recordShaped = recordShaped;
        ArgumentNullException.ThrowIfNull(canEncode);
        _resourceName = new Lazy<string>(resourceName, LazyThreadSafetyMode.None);
        Metrics = metrics;
        IsEmbedded = isEmbedded;
        SyntheticBold = syntheticBold;
        SyntheticItalic = syntheticItalic;
        _encode = encode;
        _canEncode = canEncode;
        _shaper = shaper;
    }

    private readonly Lazy<string> _resourceName;
    private PdfFontHandle? _withoutKerning;

    /// <summary>The same font drawn and measured without kerning (a run below its kerning threshold); itself when it does not kern.</summary>
    public PdfFontHandle WithoutKerning()
    {
        if (_shaper is null || _shaper.WithoutKerning() is var plain && ReferenceEquals(plain, _shaper))
        {
            return this;
        }

        return _withoutKerning ??= new PdfFontHandle(() => ResourceName, Metrics, IsEmbedded, _encode, _canEncode, SyntheticBold, SyntheticItalic, plain, _recordShaped, _requireGlyphs);
    }

    public string ResourceName => _resourceName.Value;

    public IPdfFontMetrics Metrics { get; }

    /// <summary>True for embedded fonts (glyph IDs), false for WinAnsi default fonts.</summary>
    public bool IsEmbedded { get; }

    /// <summary>Bold is produced synthetically (render mode 2) because no variant file is present.</summary>
    public bool SyntheticBold { get; }

    /// <summary>Italic is produced synthetically (slanting) because no variant file is present.</summary>
    public bool SyntheticItalic { get; }

    public byte[] Encode(string text)
    {
        var visible = Visible(text);
        if (_requireGlyphs && !_canEncode(visible))
        {
            throw new UnsupportedFeatureException(MissingGlyphMessage);
        }

        return _encode(visible);
    }

    public bool CanEncode(string text) => _canEncode(Visible(text));

    private const string MissingGlyphMessage =
        "PDF/A: the embedded font has no glyph for a character of the text, and PDF/A forbids drawing the replacement glyph; embed a font that contains it.";

    private readonly bool _requireGlyphs;

    /// <summary>Characters that show nothing: a font without a glyph for them would draw its replacement box.</summary>
    private static bool IsInvisible(int codePoint) => codePoint is
        < 0x20 or (>= 0x7F and <= 0x9F) or 0xAD or 0x34F or 0x61C or 0x180E
        or (>= 0x200B and <= 0x200F) or (>= 0x2028 and <= 0x202E) or (>= 0x2060 and <= 0x206F)
        or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or (>= 0xE0000 and <= 0xE0FFF);

    private bool IsDropped(Rune rune) => IsInvisible(rune.Value) && !_canEncode(rune.ToString());

    /// <summary>The text without invisible characters the font cannot draw; a font's own glyphs (joiners) stay.</summary>
    private string Visible(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsDropped(rune))
            {
                return string.Concat(text.EnumerateRunes().Where(item => !IsDropped(item)).Select(item => item.ToString()));
            }
        }

        return text;
    }

    /// <summary>True if the font can shape text (phase P).</summary>
    public bool HasShaping => _shaper is not null;

    /// <summary>Shapes text into a glyph sequence in visual order (UAX #9).</summary>
    public IReadOnlyList<ShapedGlyph>? Shape(string text, IReadOnlyList<byte>? levels = null)
        => _shaper?.Shape(Visible(text), levels is null ? null : text.EnumerateRunes().Zip(levels).Where(pair => !IsDropped(pair.First)).Select(pair => pair.Second).ToArray());

    /// <summary>Records shaped glyphs that are written into a content stream (subset, ToUnicode).</summary>
    public void RecordShaped(string text, IReadOnlyList<ShapedGlyph> glyphs)
    {
        if (_requireGlyphs && glyphs.Any(glyph => glyph.GlyphId == 0))
        {
            throw new UnsupportedFeatureException(MissingGlyphMessage);
        }

        _recordShaped?.Invoke(Visible(text), glyphs);
    }

    /// <summary>
    /// measures a text in points. If a shaper is present, ligatures and kerning are
    /// taken into account so that wrap and output agree.
    /// </summary>
    public double Measure(string text, double fontSize)
    {
        text = Visible(text);
        if (_shaper is null)
        {
            return PdfTextMeasurer.Measure(Metrics, text, fontSize);
        }

        var glyphs = _shaper.Shape(text);
        return glyphs is null
            ? PdfTextMeasurer.Measure(Metrics, text, fontSize)
            : _shaper.Measure(glyphs) * fontSize / 1000.0;
    }
}
