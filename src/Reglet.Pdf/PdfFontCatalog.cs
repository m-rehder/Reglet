namespace Reglet;

/// <summary>
/// resolves fonts: caller-provided TrueType fonts are embedded (matching
/// style from the <see cref="FontCollection"/>, missing attributes synthetic), otherwise
/// the default fonts with WinAnsi are used.
/// </summary>
internal sealed class PdfFontCatalog
{
    private readonly PdfStandardFontManager _standardFonts;
    private readonly PdfTrueTypeFontManager? _embeddedFonts;
    private readonly FontCollection? _embeddedSources;
    private readonly bool _requireEmbeddedFonts;
    private readonly string? _defaultFamily;
    private readonly Dictionary<string, PdfFontHandle> _handles = new(StringComparer.Ordinal);

    public PdfFontCatalog(
        PdfStandardFontManager standardFonts,
        PdfTrueTypeFontManager? embeddedFonts,
        FontCollection? embeddedSources,
        bool requireEmbeddedFonts = false,
        string? defaultFamily = null)
    {
        _defaultFamily = defaultFamily;
        ArgumentNullException.ThrowIfNull(standardFonts);
        _standardFonts = standardFonts;
        _embeddedFonts = embeddedFonts;
        _embeddedSources = embeddedSources;
        _requireEmbeddedFonts = requireEmbeddedFonts;
    }

    /// <summary>The fallback families of the embedded fonts (<see cref="FontCollection.FallbackFamilies"/>).</summary>
    public IReadOnlyList<string> FallbackFamilies => _embeddedFonts is not null ? _embeddedSources?.FallbackFamilies ?? [] : [];

    public PdfFontHandle GetFont(string? family, bool bold, bool italic)
    {
        if (_embeddedFonts is not null && _embeddedSources is not null)
        {
            // Without its own family, the default font is searched under the key ""
            // (e.g. for headers/footers in PDF/A).
            var lookup = string.IsNullOrEmpty(family) ? string.Empty : family;

            // Text without its own family uses the document's default family
            // (DocumentDefaults.FontFamily) if no font was registered under the empty key.
            if (lookup.Length == 0 && !_embeddedSources.Contains(lookup) && !string.IsNullOrEmpty(_defaultFamily))
            {
                lookup = _defaultFamily;
            }

            if (_embeddedSources.Match(lookup, bold, italic) is { } match)
            {
                // N11: the matching style (variant file) is embedded; only attributes for which
                // for which no file is present are reproduced synthetically.
                var embeddedKey = "embedded:" + lookup.ToUpperInvariant() + ":" + (bold ? "b" : "-") + (italic ? "i" : "-");
                if (!_handles.TryGetValue(embeddedKey, out var embeddedHandle))
                {
                    var embedded = _embeddedFonts.GetFont(lookup, match.Face);
                    embeddedHandle = new PdfFontHandle(
                        () => embedded.ResourceName,
                        new PdfEmbeddedFontMetrics(embedded.Font),
                        isEmbedded: true,
                        text => EncodeGlyphs(embedded, text),
                        text => CanEncodeGlyphs(embedded.Font, text),
                        syntheticBold: match.SyntheticBold,
                        syntheticItalic: match.SyntheticItalic,
                        shaper: embedded.Shaper,
                        recordShaped: embedded.RecordShaped,
                        requireGlyphs: _requireEmbeddedFonts);
                    _handles[embeddedKey] = embeddedHandle;
                }

                return embeddedHandle;
            }
        }

        if (_requireEmbeddedFonts)
        {
            throw new UnsupportedFeatureException(
                "PDF/A: text without an embedded font is not supported; provide the font family in PdfWriteOptions.Fonts or PdfWriteOptions.FontCollection (empty key for the default font).");
        }

        var baseFont = PdfStandardFonts.Resolve(string.IsNullOrEmpty(family) ? _defaultFamily : family, bold, italic);
        var standardKey = "standard:" + baseFont;
        if (!_handles.TryGetValue(standardKey, out var standardHandle))
        {
            standardHandle = new PdfFontHandle(
                () => _standardFonts.GetFont(baseFont),
                new PdfStandardFontMetrics(baseFont),
                isEmbedded: false,
                PdfWinAnsi.Encode,
                PdfWinAnsi.CanEncode);
            _handles[standardKey] = standardHandle;
        }

        return standardHandle;
    }

    private static byte[] EncodeGlyphs(PdfEmbeddedFont embedded, string text)
    {
        var bytes = new List<byte>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = embedded.Font.GetGlyphId(rune.Value);
            embedded.Record(glyph, rune.ToString());
            bytes.Add((byte)(glyph >> 8));
            bytes.Add((byte)(glyph & 0xFF));
        }

        return bytes.ToArray();
    }

    private static bool CanEncodeGlyphs(TrueTypeFont font, string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (font.GetGlyphId(rune.Value) == 0)
            {
                return false;
            }
        }

        return true;
    }
}
