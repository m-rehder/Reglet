namespace Reglet;

/// <summary>
/// An embedded TrueType style. The PDF objects are reserved when the font is first used and
/// written at the end of the document (<see cref="PdfTrueTypeFontManager.Finish"/>): only then
/// is known which glyphs were actually drawn, so the subset can never miss a glyph.
/// </summary>
internal sealed class PdfEmbeddedFont
{
    private readonly HashSet<ushort> _usedGlyphs = [];
    private readonly Dictionary<ushort, string> _glyphText = [];

    public FontFace Face { get; init; } = null!;

    public TrueTypeFont Font { get; init; } = null!;

    public OpenTypeLayout? Layout { get; init; }

    public TextShaper? Shaper { get; init; }

    public int FontFileObject { get; init; }

    public int DescriptorObject { get; init; }

    public int CidFontObject { get; init; }

    public int Type0Object { get; init; }

    public int ToUnicodeObject { get; init; }

    public string ResourceName { get; init; } = string.Empty;

    /// <summary>The glyphs written into content streams.</summary>
    public IReadOnlySet<ushort> UsedGlyphs => _usedGlyphs;

    /// <summary>The text each used glyph stands for (ToUnicode); ligatures map to several characters.</summary>
    public IReadOnlyDictionary<ushort, string> GlyphText => _glyphText;

    /// <summary>Records a drawn glyph and the text it represents (the first mapping wins).</summary>
    public void Record(ushort glyph, string text)
    {
        _usedGlyphs.Add(glyph);
        if (glyph != 0 && text.Length > 0)
        {
            _glyphText.TryAdd(glyph, text);
        }
    }

    /// <summary>
    /// Records shaped glyphs. Glyphs of one cluster share their source text: the first glyph of
    /// a cluster maps to the cluster's characters (a ligature maps to all of them).
    /// </summary>
    public void RecordShaped(string text, IReadOnlyList<ShapedGlyph> glyphs)
    {
        var codePoints = new List<string>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            codePoints.Add(rune.ToString());
        }

        var clusterStarts = glyphs.Select(glyph => glyph.Cluster).Distinct().Order().ToList();
        var mapped = new HashSet<int>();
        foreach (var glyph in glyphs)
        {
            var clusterText = string.Empty;
            if (mapped.Add(glyph.Cluster) && glyph.Cluster >= 0 && glyph.Cluster < codePoints.Count)
            {
                var next = clusterStarts.FirstOrDefault(start => start > glyph.Cluster, codePoints.Count);
                clusterText = string.Concat(codePoints.Skip(glyph.Cluster).Take(Math.Max(1, next - glyph.Cluster)));

                // A mirrored glyph (UAX #9 L4: ")" drawn as "(") maps to its own character, so the
                // same glyph in left-to-right text does not copy as the opposite bracket.
                var source = char.ConvertToUtf32(clusterText, 0);
                if (next - glyph.Cluster == 1
                    && BidiData.TryGetMirror(source, out var mirror)
                    && Font.GetGlyphId(source) != glyph.GlyphId
                    && Font.GetGlyphId(mirror) == glyph.GlyphId)
                {
                    clusterText = char.ConvertFromUtf32(mirror);
                }
            }

            Record(glyph.GlyphId, clusterText);
        }
    }
}
