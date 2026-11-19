namespace Reglet;

/// <summary>
/// a shaped glyph with advance and offset in 1000ths of an em
/// (phase P). <see cref="Kern"/> is the adjustment after this glyph. <see cref="Cluster"/> is the
/// index of the source code point in the shaped text (for a ligature: its first component); it
/// maps glyphs back to text for the ToUnicode CMap also after bidi reordering.
/// </summary>
internal readonly record struct ShapedGlyph(
    ushort GlyphId,
    double Advance,
    double Kern,
    double OffsetX,
    double OffsetY,
    int Cluster);
