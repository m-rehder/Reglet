using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// phase P (P05): TrueType subsetting. The fonts are synthetic (glyf/loca/cmap) so that
/// the reduction can be checked without real font files.
/// </summary>
internal static class SubsetChecks
{
    internal static int Run()
    {
        try
        {
            SubsetsToUsedGlyphs();
            RemapsCompositeComponents();
            SkipsFontsWithLayoutTables();
            RejectsNegativeGlyphOffsets();
            CollectsUsedCodepointsFromDocument();
            EmbedsSubsetFont();
            Console.WriteLine("Subsetting (P05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Subsetting (P05): " + exception);
            return 1;
        }
    }

    /// <summary>P05: only the used glyphs keep their outline; the glyph IDs stay unchanged.</summary>
    private static void SubsetsToUsedGlyphs()
    {
        var fontData = BuildFont();
        var font = TrueTypeFont.Load(fontData);
        var result = TrueTypeSubsetter.TrySubset(font, new HashSet<ushort> { 3, 5 })
            ?? throw new InvalidOperationException("Subset missing.");

        Require(result.Length < fontData.Length, "Subset is smaller than the original font.");

        var subset = TrueTypeFont.Load(result);
        Require(subset.NumGlyphs == font.NumGlyphs, "The glyph IDs are retained (same glyph count).");
        Require(subset.GetGlyphId('A') == 3, "'A' keeps glyph 3.");
        Require(subset.GetGlyphId('B') == 5, "'B' keeps glyph 5.");
        Require(subset.GetGlyphId(0x1F600) == 0, "Unused emoji is removed from the cmap.");
        Require(GlyphLength(result, 4) == 0, "An unused glyph has no outline.");
        Require(GlyphLength(result, 3) > 0, "A used glyph keeps its outline.");
        Require(Math.Abs(subset.GetAdvanceWidth(3) - 600) < 1e-9, "Advance is preserved.");
    }

    /// <summary>P05: composite glyphs keep their components (with unchanged IDs).</summary>
    private static void RemapsCompositeComponents()
    {
        var result = TrueTypeSubsetter.TrySubset(TrueTypeFont.Load(BuildFont()), new HashSet<ushort> { 5 })
            ?? throw new InvalidOperationException("Subset missing.");

        Require(FirstComponent(result, 5) == 3, "The composite still references glyph 3.");
        Require(GlyphLength(result, 3) > 0, "The component glyph 3 is kept although only glyph 5 was drawn.");
    }

    /// <summary>P05: glyph IDs are retained, so fonts with layout tables are reduced as well.</summary>
    /// <summary>loca offsets >= 2^31 turn negative; the subsetter must fall back to the full font.</summary>
    private static void RejectsNegativeGlyphOffsets()
    {
        var data = BuildFont();
        _ = TrueTypeFont.Load(data).TryGetTable("loca", out var offset, out var length);
        for (var i = 0; i + 4 <= length; i += 4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + i, 4), 0x80000000);
        }

        Require(TrueTypeSubsetter.TrySubset(TrueTypeFont.Load(data), new HashSet<ushort> { 3 }) is null,
            "Negative glyph offsets embed the complete font.");
    }

    private static void SkipsFontsWithLayoutTables()
    {
        var withGdef = BuildFont(withGdef: true);
        var result = TrueTypeSubsetter.TrySubset(TrueTypeFont.Load(withGdef), new HashSet<ushort> { 3 });
        Require(result is not null, "A font with GDEF is reduced (the glyph IDs do not change).");
        Require(!TrueTypeFont.Load(result!).TryGetTable("GDEF", out _, out _), "The layout table is dropped from the subset.");
    }

    /// <summary>P05: glyphs are recorded while drawing, including links and footnotes.</summary>
    private static void CollectsUsedCodepointsFromDocument()
    {
        var paragraph = new Paragraph([
            new TextRun("A") { FontFamily = "F" },
            new Hyperlink("https://example.com", [new TextRun("B") { FontFamily = "F" }]),
        ]);
        var document = new PortableDocument(new DocumentMetadata(), new Section(paragraph));

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["F"] = BuildFont() } })
            .GetAwaiter().GetResult();

        var embedded = TrueTypeFont.Load(EmbeddedFontFile(buffer.ToArray()));
        Require(GlyphLength(embedded.Data, 3) > 0, "Text glyph 'A' is embedded.");
        Require(GlyphLength(embedded.Data, 5) > 0, "The glyph of the hyperlink text 'B' is embedded.");
        Require(GlyphLength(embedded.Data, 4) == 0, "An unused glyph is not embedded.");
    }

    /// <summary>P05: the PDF writer embeds the reduced FontFile2 and maps the glyphs correctly.</summary>
    private static void EmbedsSubsetFont()
    {
        var fontData = BuildFont();
        var document = PortableDocument.Create()
            .Paragraph(paragraph => paragraph.Span("A", fontFamily: "F"))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["F"] = fontData } })
            .GetAwaiter().GetResult();

        var bytes = buffer.ToArray();
        var text = Encoding.Latin1.GetString(bytes);
        var embeddedData = EmbeddedFontFile(bytes);
        Require(embeddedData.Length < fontData.Length, "Embedded FontFile2 is the reduced subset.");
        Require(GlyphLength(embeddedData, 3) > 0 && GlyphLength(embeddedData, 5) == 0, "Only the drawn glyph 'A' keeps its outline.");
        Require(text.Contains("<0003> <0041>", StringComparison.Ordinal), "ToUnicode maps glyph 3 to 'A'.");
        Require(Regex.IsMatch(text, @"/BaseFont /[A-Z]{6}\+SubsetTest "), "The subset name carries a six-letter tag.");
    }

    private static byte[] EmbeddedFontFile(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var match = Regex.Match(text, @"/FontFile2 (\d+) 0 R");
        Require(match.Success, "FontFile2 reference found.");

        var objectStart = text.IndexOf($"\n{match.Groups[1].Value} 0 obj\n", StringComparison.Ordinal);
        var streamStart = text.IndexOf("stream\n", objectStart, StringComparison.Ordinal) + "stream\n".Length;
        var streamEnd = text.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);

        using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        return decompressed.ToArray();
    }

    private static int GlyphLength(byte[] fontData, int glyphId)
    {
        var font = TrueTypeFont.Load(fontData);
        _ = font.TryGetTable("head", out var headOffset, out _);
        var longFormat = BinaryPrimitives.ReadInt16BigEndian(fontData.AsSpan(headOffset + 50, 2)) != 0;
        _ = font.TryGetTable("loca", out var locaOffset, out _);
        int Offset(int glyph) => longFormat
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(locaOffset + glyph * 4, 4))
            : BinaryPrimitives.ReadUInt16BigEndian(fontData.AsSpan(locaOffset + glyph * 2, 2)) * 2;
        return Offset(glyphId + 1) - Offset(glyphId);
    }

    private static int FirstComponent(byte[] fontData, int glyphId)
    {
        var font = TrueTypeFont.Load(fontData);
        _ = font.TryGetTable("head", out var headOffset, out _);
        var longFormat = BinaryPrimitives.ReadInt16BigEndian(fontData.AsSpan(headOffset + 50, 2)) != 0;
        _ = font.TryGetTable("loca", out var locaOffset, out _);
        _ = font.TryGetTable("glyf", out var glyfOffset, out _);

        var start = longFormat
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(locaOffset + glyphId * 4, 4))
            : BinaryPrimitives.ReadUInt16BigEndian(fontData.AsSpan(locaOffset + glyphId * 2, 2)) * 2;
        return BinaryPrimitives.ReadUInt16BigEndian(fontData.AsSpan(glyfOffset + start + 12, 2));
    }

    /// <summary>Builds a small font with 40 glyphs; glyph 5 is a composite on glyph 3.</summary>
    private static byte[] BuildFont(bool withGdef = false)
    {
        const int glyphCount = 40;
        var glyphs = new byte[glyphCount][];
        glyphs[0] = [];
        for (var i = 1; i < glyphCount; i++)
        {
            glyphs[i] = i == 5 ? CompositeGlyph(3) : EmptyGlyph();
        }

        var glyf = new List<byte>();
        var offsets = new List<uint> { 0 };
        foreach (var glyph in glyphs)
        {
            glyf.AddRange(glyph);
            while (glyf.Count % 4 != 0)
            {
                glyf.Add(0);
            }

            offsets.Add((uint)glyf.Count);
        }

        var loca = new byte[offsets.Count * 4];
        for (var i = 0; i < offsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4, 4), offsets[i]);
        }

        var head = SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50, 2), 1); // indexToLocFormat = long

        var metrics = new (ushort Advance, short Lsb)[glyphCount];
        for (var i = 0; i < glyphCount; i++)
        {
            metrics[i] = (500, 0);
        }

        metrics[3] = (600, 0);
        metrics[4] = (700, 0);
        metrics[5] = (800, 0);

        var tables = new List<(string Tag, byte[] Data)>
        {
            ("head", head),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, glyphCount)),
            ("maxp", SyntheticTrueTypeFont.Maxp(glyphCount)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx(metrics)),
            ("name", SyntheticTrueTypeFont.Name("SubsetTest")),
            ("cmap", SyntheticTrueTypeFont.Cmap12(('A', 3), ('B', 5), (0x1F600, 4))),
            ("glyf", glyf.ToArray()),
            ("loca", loca),
        };

        if (withGdef)
        {
            tables.Add(("GDEF", Gdef()));
        }

        return SyntheticTrueTypeFont.Build(tables.ToArray());
    }

    private static byte[] EmptyGlyph() => new byte[10];

    private static byte[] CompositeGlyph(ushort component)
    {
        var glyph = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(glyph.AsSpan(0, 2), -1);
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(10, 2), 0x0002); // ARGS_ARE_XY_VALUES, kein weiteres Element
        BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(12, 2), component);
        return glyph;
    }

    private static byte[] Gdef()
    {
        var gdef = new byte[12 + 10];
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(4, 2), 12); // GlyphClassDef-Offset
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(12, 2), 1); // Format 1
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(14, 2), 3); // startGlyph
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(16, 2), 1); // count
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(18, 2), 1); // Klasse Basis
        return gdef;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
