namespace Reglet;

/// <summary>
/// a validated, caller-provided TrueType font file (one style, e.g.
/// "Noto Sans Bold"). Supported are glyf-based TrueType fonts; CFF-based
/// OpenType fonts (OTTO) and collections (TTC) are rejected. The instance is
/// immutable and can be used in parallel.
/// </summary>
public sealed class FontFace
{
    private readonly byte[] _data;

    private FontFace(byte[] data, TrueTypeFont font)
    {
        _data = data;
        Font = font;

        Embedding = (font.EmbeddingFlags & 0xF) switch
        {
            0 => FontEmbeddingPermission.Installable,
            var usage when (usage & 0x8) != 0 => FontEmbeddingPermission.Editable,
            var usage when (usage & 0x4) != 0 => FontEmbeddingPermission.PreviewAndPrint,
            var usage when (usage & 0x2) != 0 => FontEmbeddingPermission.Restricted,
            _ => FontEmbeddingPermission.Installable,
        };
    }

    /// <summary>Family name per font file (typographic family, otherwise name ID 1).</summary>
    public string FamilyName => Font.FamilyName;

    /// <summary>Style name per font file, e.g. "Bold Italic".</summary>
    public string StyleName => Font.SubfamilyName;

    /// <summary>PostScript name per font file.</summary>
    public string PostScriptName => Font.PostScriptName;

    /// <summary>Weight (100–900; 400 = normal, 700 = bold).</summary>
    public int Weight => Font.WeightClass;

    /// <summary>Whether the file contains a bold style.</summary>
    public bool IsBold => Font.IsBold;

    /// <summary>Whether the file contains an italic or oblique style.</summary>
    public bool IsItalic => Font.IsItalic;

    /// <summary>Number of glyphs.</summary>
    public int GlyphCount => Font.NumGlyphs;

    /// <summary>Font design units per em (<c>head.unitsPerEm</c>).</summary>
    public int UnitsPerEm => Font.UnitsPerEm;

    /// <summary>Ascender above the baseline in font units (<c>hhea.ascender</c>), as the PDF writer uses it.</summary>
    public int Ascent => Font.Ascender;

    /// <summary>Descender below the baseline in font units, as a positive value (<c>-hhea.descender</c>).</summary>
    public int Descent => -Font.Descender;

    /// <summary>Extra gap between lines in font units (<c>hhea.lineGap</c>).</summary>
    public int LineGap => Font.LineGap;

    /// <summary>Embedding right per OS/2 <c>fsType</c>.</summary>
    public FontEmbeddingPermission Embedding { get; }

    /// <summary>
    /// whether the outlines may be embedded: not for a restricted license and not
    /// if the font only allows bitmap embedding (fsType bit 9).
    /// </summary>
    public bool CanEmbed => Embedding != FontEmbeddingPermission.Restricted && (Font.EmbeddingFlags & 0x200) == 0;

    /// <summary>Whether the font may be reduced to the used glyphs when embedding (fsType bit 8 not set).</summary>
    public bool AllowsSubsetting => (Font.EmbeddingFlags & 0x100) == 0;

    /// <summary>The parsed font for the writers.</summary>
    internal TrueTypeFont Font { get; }

    /// <summary>The unchanged bytes of the font file.</summary>
    internal byte[] Data => _data;

    /// <summary>Loads and validates a font from bytes. The bytes are copied.</summary>
    /// <exception cref="FontException">The file is corrupted.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF, collection or cmap format is not supported.</exception>
    public static FontFace Load(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        return new FontFace(copy, TrueTypeFont.Load(copy));
    }

    /// <summary>Loads and validates a font from a file (<c>.ttf</c>).</summary>
    /// <exception cref="FontException">The file is corrupted or unreadable.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF, collection or cmap format is not supported.</exception>
    public static FontFace LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FontException($"The font file '{Path.GetFileName(path)}' could not be read.", exception);
        }

        return new FontFace(data, TrueTypeFont.Load(data));
    }

    /// <summary>Whether the font contains a glyph for the code point.</summary>
    public bool Supports(int codePoint) => Font.GetGlyphId(codePoint) != 0;

    /// <summary>Whether the font can render all characters of the text.</summary>
    public bool CanRender(string text) => GetMissingCodePoints(text).Count == 0;

    /// <summary>The code points of the text without a glyph (deduplicated, in text order).</summary>
    public IReadOnlyList<int> GetMissingCodePoints(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var missing = new List<int>();
        var seen = new HashSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Supports(rune.Value) && seen.Add(rune.Value))
            {
                missing.Add(rune.Value);
            }
        }

        return missing;
    }

    /// <inheritdoc />
    public override string ToString() => $"{FamilyName} {StyleName}";
}
