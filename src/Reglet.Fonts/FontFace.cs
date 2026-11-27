namespace Reglet;

/// <summary>
/// a validated, caller-provided TrueType font (one style, e.g. "Noto Sans Bold"). Supported are glyf-based TrueType
/// fonts, also from a collection (<c>.ttc</c>), of which only the chosen font is kept and embedded; CFF-based OpenType
/// fonts (OTTO) are rejected. The instance is immutable and can be used in parallel.
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

    /// <summary>Loads and validates a font from bytes; of a collection (<c>.ttc</c>) its first font. The bytes are copied.</summary>
    /// <exception cref="FontException">The file is corrupted.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format is not supported.</exception>
    public static FontFace Load(ReadOnlySpan<byte> data) => First(data.ToArray());

    /// <summary>
    /// Loads the font of a family from bytes: of a collection (<c>.ttc</c>) the font whose <see cref="FamilyName"/> is
    /// <paramref name="familyName"/> (case-insensitive), such as "Microsoft YaHei" beside "Microsoft YaHei UI".
    /// </summary>
    /// <exception cref="FontException">The file is corrupted, or no font of it has the family; the message names those it has.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format is not supported.</exception>
    public static FontFace Load(ReadOnlySpan<byte> data, string familyName) => Pick(data.ToArray(), familyName, "the data");

    /// <summary>All fonts of a collection (<c>.ttc</c>) in their order there; a single font file gives one.</summary>
    /// <exception cref="FontException">The file is corrupted.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format of one of the fonts is not supported.</exception>
    public static IReadOnlyList<FontFace> LoadCollection(ReadOnlySpan<byte> data)
        => TrueTypeFont.CollectionMembers(data.ToArray()).Select(Of).ToList();

    /// <summary>Loads and validates a font from a file (<c>.ttf</c>; of a <c>.ttc</c> its first font).</summary>
    /// <exception cref="FontException">The file is corrupted or unreadable.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format is not supported.</exception>
    public static FontFace LoadFile(string path) => First(ReadFile(path));

    /// <summary>Loads the font of a family from a file, for example <c>LoadFile(@"C:\Windows\Fonts\msyh.ttc", "Microsoft YaHei")</c>.</summary>
    /// <exception cref="FontException">The file is corrupted or unreadable, or no font of it has the family.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format is not supported.</exception>
    public static FontFace LoadFile(string path, string familyName) => Pick(ReadFile(path), familyName, Path.GetFileName(path));

    /// <summary>All fonts of a collection file (<c>.ttc</c>); a single font file gives one.</summary>
    /// <exception cref="FontException">The file is corrupted or unreadable.</exception>
    /// <exception cref="UnsupportedFeatureException">CFF or cmap format of one of the fonts is not supported.</exception>
    public static IReadOnlyList<FontFace> LoadCollectionFile(string path)
        => TrueTypeFont.CollectionMembers(ReadFile(path)).Select(Of).ToList();

    private static FontFace Of(byte[] data) => new(data, TrueTypeFont.Load(data));

    /// <summary>The font of a file, of a collection its first one (only that one is copied out).</summary>
    private static FontFace First(byte[] data)
        => TrueTypeFont.CollectionOffsets(data) is { } offsets ? Of(TrueTypeFont.ExtractMember(data, offsets[0])) : Of(data);

    /// <summary>
    /// The font of a family: the fonts of a collection are read where they are, and only the chosen one is copied out.
    /// A font that cannot be read is no candidate.
    /// </summary>
    private static FontFace Pick(byte[] data, string familyName, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(familyName);
        var names = new List<string>();
        var offsets = TrueTypeFont.CollectionOffsets(data);
        foreach (var start in offsets ?? [0])
        {
            string family;
            try
            {
                family = TrueTypeFont.Load(data, start).FamilyName;
            }
            catch (Exception exception) when (exception is FontException or UnsupportedFeatureException)
            {
                continue;
            }

            if (string.Equals(family, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return offsets is null ? Of(data) : Of(TrueTypeFont.ExtractMember(data, start));
            }

            if (!names.Contains(family, StringComparer.Ordinal))
            {
                names.Add(family);
            }
        }

        throw new FontException($"{source} has no font of the family '{familyName}' (it has: {string.Join(", ", names)}).");
    }

    private static byte[] ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FontException($"The font file '{Path.GetFileName(path)}' could not be read.", exception);
        }
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
