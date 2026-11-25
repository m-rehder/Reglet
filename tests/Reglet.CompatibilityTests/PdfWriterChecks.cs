using System.IO.Compression;
using System.Globalization;
using System.Text;
using Reglet;

internal static class PdfWriterChecks
{
    internal static int Run()
    {
        try
        {
            WritesTokens();
            ManagesObjectNumbers();
            CompressesStreams();
            WritesCrossReferenceTable();
            WritesTrailer();
            WritesPageTree();
            WritesResources();
            WritesContentOperators();
            WritesStandardFonts();
            ReadsTrueTypeDirectory();
            ReadsTrueTypeMetrics();
            ReadsTrueTypeCmap();
            EmbedsTrueTypeFont();
            MeasuresText();
            LaysOutPages();
            NumbersPagesAcrossSections();
            RendersBlocks();
            UsesTypographyBaseline();
            UsesSameRgbColors();
            UsesSameImageScale();
            PreservesParagraphSpacingAndHardBreaks();
            WrapsLongParagraphToken();
            DrawsUnderlines();
            UsesEmbeddedFontVerticalMetrics();
            RendersParagraphIndents();
            AppliesDocumentParagraphDefaults();
            JustifiesParagraphLines();
            JustifiesTableCells();
            JustifiesBandText();
            WritesImageAltTextAsMarkedContent();
            WritesMetadataLanguageAndCreationDate();
            RendersBodyPageNumberField();
            DrawsTableBorders();
            SynthesizesBoldAndItalicForEmbeddedFonts();
            EmbedsImages();
            FitsOversizedImageOnPage();
            WrapsLongTableCells();
            SplitsTallTableRowsAcrossPages();
            RespectsTableCellParagraphsAndFontSizes();
            RejectsPagesWithoutContentArea();
            KeepsCallerStreamOpen();
            ProducesDeterministicBytes();
            Console.WriteLine("PDF writer: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF-Writer: " + exception);
            return 1;
        }
    }

    private static void WritesTokens()
    {
        using var buffer = new MemoryStream();
        var writer = new PdfTokenWriter(buffer);

        writer.WriteDictionary(w =>
        {
            w.WriteName("Type");
            w.WriteName("Catalog");
            w.WriteName("Pages");
            w.WriteReference(2);
            w.WriteName("Count");
            w.WriteInteger(1);
            w.WriteName("Ratio");
            w.WriteNumber(0.5);
            w.WriteName("Title");
            w.WriteLiteralString("Example (Test)\\");
            w.WriteName("Bytes");
            w.WriteHexString(new byte[] { 0xDE, 0xAD });
            w.WriteName("Items");
            w.WriteArray(a =>
            {
                a.WriteInteger(1);
                a.WriteInteger(2);
            });
        });

        var text = Encoding.ASCII.GetString(buffer.ToArray());

        AssertContains(text, "<<");
        AssertContains(text, "/Type /Catalog ");
        AssertContains(text, "/Pages 2 0 R ");
        AssertContains(text, "/Count 1 ");
        AssertContains(text, "/Ratio 0.5 ");
        AssertContains(text, "(Example \\(Test\\)\\\\) ");
        AssertContains(text, "<DEAD> ");
        AssertContains(text, "[1 2 ]");
        AssertContains(text, ">>");

        using var unicodeBuffer = new MemoryStream();
        var unicodeWriter = new PdfTokenWriter(unicodeBuffer);
        unicodeWriter.WriteName("Munich");
        unicodeWriter.WriteLiteralString("Greetings 🌍");
        var unicodeTokens = Encoding.ASCII.GetString(unicodeBuffer.ToArray());
        AssertContains(unicodeTokens, "/Munich ");
        AssertContains(unicodeTokens, "<FEFF004700720065006500740069006E006700730020D83CDF0D> ");
    }

    private static void ManagesObjectNumbers()
    {
        var registry = new PdfObjectRegistry();

        Require(registry.Reserve() == 1, "First object number is 1.");
        Require(registry.Reserve() == 2, "Second object number is 2.");
        Require(registry.Reserve() == 3, "Third object number is 3.");

        var created = 0;
        var first = registry.GetOrAdd("font:Helvetica", () =>
        {
            created++;
            return registry.Reserve();
        });
        var second = registry.GetOrAdd("font:Helvetica", () =>
        {
            created++;
            return registry.Reserve();
        });

        Require(first == second, "The same key yields the same number.");
        Require(created == 1, "The object is created only once.");
    }

    private static void CompressesStreams()
    {
        var data = Encoding.ASCII.GetBytes(new string('a', 1000));
        var compressed = PdfStreamCompressor.Compress(data);

        Require(compressed.Length < data.Length, "Compressed data is smaller.");

        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        Require(output.ToArray().SequenceEqual(data), "Round trip yields the original data.");
    }

    private static void WritesCrossReferenceTable()
    {
        using var buffer = new MemoryStream();
        var file = new PdfFileWriter(buffer);

        var catalog = file.ReserveObject();
        var pages = file.ReserveObject();
        file.WriteObject(catalog, w =>
        {
            w.WriteName("Type");
            w.WriteName("Catalog");
        });
        file.WriteObject(pages, w =>
        {
            w.WriteName("Type");
            w.WriteName("Pages");
        });

        file.WriteCrossReferenceTable();

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        Require(text.Contains("xref\n0 3\n", StringComparison.Ordinal), "xref header present.");

        var offsets = ParseCrossReference(text);
        Require(offsets.Count == 3, "Three xref entries.");
        Require(offsets[0] == -1, "First entry is free.");
        Require(text.AsSpan((int)offsets[1]).StartsWith("1 0 obj"), "Offset of object 1 points to '1 0 obj'.");
        Require(text.AsSpan((int)offsets[2]).StartsWith("2 0 obj"), "Offset of object 2 points to '2 0 obj'.");
    }

    private static void WritesTrailer()
    {
        using var buffer = new MemoryStream();
        var file = new PdfFileWriter(buffer);

        var catalog = file.ReserveObject();
        var info = file.ReserveObject();
        file.WriteObject(catalog, w =>
        {
            w.WriteName("Type");
            w.WriteName("Catalog");
        });
        file.WriteObject(info, w =>
        {
            w.WriteName("Producer");
            w.WriteLiteralString("Reglet");
        });
        file.Complete(catalog, info);

        var text = Encoding.Latin1.GetString(buffer.ToArray());

        Require(text.StartsWith("%PDF-1.7", StringComparison.Ordinal), "Header begins with %PDF-1.7.");
        Require(text.Contains("trailer\n", StringComparison.Ordinal), "Trailer present.");
        Require(text.Contains("/Size 3 ", StringComparison.Ordinal), "Trailer contains /Size.");
        Require(text.Contains("/Root 1 0 R ", StringComparison.Ordinal), "Trailer contains /Root.");
        Require(text.Contains("/Info 2 0 R ", StringComparison.Ordinal), "Trailer contains /Info.");
        Require(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "File ends with %%EOF.");

        var startXrefIndex = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var startXref = long.Parse(
            text[(startXrefIndex + "startxref".Length)..].Trim().Split('\n')[0], CultureInfo.InvariantCulture);
        Require(
            text.AsSpan((int)startXref).StartsWith("xref"),
            "startxref points to the xref table.");
    }

    private static void WritesPageTree()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(buffer.ToArray());

        Require(text.StartsWith("%PDF-1.7", StringComparison.Ordinal), "Header present.");
        Require(text.Contains("/Type /Catalog ", StringComparison.Ordinal), "Catalog present.");
        Require(text.Contains("/Type /Pages ", StringComparison.Ordinal), "Pages present.");
        Require(text.Contains("/Type /Page ", StringComparison.Ordinal), "Page present.");
        Require(text.Contains("/Count 1 ", StringComparison.Ordinal), "/Count 1 present.");
        Require(text.Contains("/MediaBox", StringComparison.Ordinal), "MediaBox present.");
        Require(text.Contains("/Producer", StringComparison.Ordinal), "Info with producer present.");
    }

    private static void WritesResources()
    {
        var manager = new PdfResourceManager();
        Require(manager.AddFont("font:Helvetica", 10) == "F1", "First font is named F1.");
        Require(manager.AddFont("font:Helvetica", 10) == "F1", "The same font yields F1.");
        Require(manager.AddFont("font:Times", 11) == "F2", "Second font is named F2.");
        Require(manager.AddImage("image:1", 20) == "Im1", "First image is named Im1.");

        using var buffer = new MemoryStream();
        var tokens = new PdfTokenWriter(buffer);
        manager.Write(tokens);
        var text = Encoding.Latin1.GetString(buffer.ToArray());

        Require(text.Contains("/Font", StringComparison.Ordinal), "Font group present.");
        Require(text.Contains("/F1 10 0 R", StringComparison.Ordinal), "F1 referenced.");
        Require(text.Contains("/F2 11 0 R", StringComparison.Ordinal), "F2 referenced.");
        Require(text.Contains("/XObject", StringComparison.Ordinal), "XObject group present.");
        Require(text.Contains("/Im1 20 0 R", StringComparison.Ordinal), "Im1 referenced.");

        var document = PortableDocument.Create().Paragraph("x").Build();
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Require(Encoding.Latin1.GetString(pdf.ToArray()).Contains("/Resources", StringComparison.Ordinal), "Page references Resources.");
    }

    private static void WritesContentOperators()
    {
        var content = new PdfContentWriter();
        content.SaveState();
        content.ConcatMatrix(1, 0, 0, 1, 0, 0);
        content.SetStrokeColor(Color.FromHex("#FF0000"));
        content.SetLineWidth(1);
        content.MoveTo(10, 10);
        content.LineTo(100, 10);
        content.Stroke();
        content.Rectangle(10, 20, 50, 30);
        content.SetFillColor(Color.Black);
        content.Fill();
        content.BeginText();
        content.SetFont("F1", 12);
        content.MoveText(72, 700);
        content.ShowText("Hello (World)"u8);
        content.EndText();
        content.RestoreState();

        var text = Encoding.ASCII.GetString(content.ToArray());
        AssertContains(text, "q\n1 0 0 1 0 0 cm\n");
        AssertContains(text, "1 0 0 RG\n");
        AssertContains(text, "1 w\n");
        AssertContains(text, "10 10 m\n");
        AssertContains(text, "100 10 l\n");
        AssertContains(text, "S\n");
        AssertContains(text, "10 20 50 30 re\n");
        AssertContains(text, "0 0 0 rg\n");
        AssertContains(text, "f\n");
        AssertContains(text, "BT\n/F1 12 Tf\n72 700 Td\n(Hello \\(World\\)) Tj\nET\n");
        AssertContains(text, "Q\n");
    }

    private static void WritesStandardFonts()
    {
        Require(PdfWinAnsi.Encode("\u00FC")[0] == 0xFC, "ue becomes 0xFC.");
        Require(PdfWinAnsi.Encode("\u2013")[0] == 0x96, "Em dash becomes 0x96.");
        Require(PdfWinAnsi.Encode("\u20AC")[0] == 0x80, "Euro becomes 0x80.");
        Require(PdfWinAnsi.Encode("\u4E2D")[0] == (byte)'?', "Non-WinAnsi becomes ?.");

        Require(PdfStandardFonts.Resolve("Times New Roman", bold: true, italic: false) == "Times-Bold", "Times-Bold.");
        Require(PdfStandardFonts.Resolve(null, bold: false, italic: false) == "Helvetica", "Helvetica.");
        Require(PdfStandardFonts.Resolve("Courier New", bold: false, italic: true) == "Courier-Oblique", "Courier-Oblique.");

        using var buffer = new MemoryStream();
        var file = new PdfFileWriter(buffer);
        var resources = new PdfResourceManager();
        var fonts = new PdfStandardFontManager(file, resources);

        Require(fonts.GetFont("Helvetica") == "F1", "Helvetica becomes F1.");
        Require(fonts.GetFont("Helvetica") == "F1", "Helvetica remains F1.");

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        AssertContains(text, "/BaseFont /Helvetica ");
        AssertContains(text, "/Encoding /WinAnsiEncoding ");
    }

    private static List<long> ParseCrossReference(string text)
    {
        var result = new List<long>();
        var start = text.IndexOf("xref\n", StringComparison.Ordinal);
        var end = text.IndexOf("trailer", StringComparison.Ordinal);
        var block = text[(start + "xref\n".Length)..(end < 0 ? text.Length : end)];
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1))
        {
            var type = line[17];
            result.Add(type == 'n' ? long.Parse(line[..10], CultureInfo.InvariantCulture) : -1);
        }

        return result;
    }

    private static void ReadsTrueTypeDirectory()
    {
        var font = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 0, 0)),
            ("hhea", SyntheticTrueTypeFont.Empty(36)),
            ("maxp", SyntheticTrueTypeFont.Empty(6)),
            ("hmtx", SyntheticTrueTypeFont.Empty(4)),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        var parsed = TrueTypeFont.Load(font);
        Require(parsed.TableTags.Count == 5, "Five tables read.");
        Require(parsed.TryGetTable("head", out _, out var length), "head found.");
        Require(length == 54, "head length read.");
        Require(!parsed.TryGetTable("glyf", out _, out _), "glyf is not present.");

        Throws<NotSupportedException>(() => TrueTypeFont.Load(SyntheticTrueTypeFont.WithVersion(0x4F54544F)));
        Throws<NotSupportedException>(() => TrueTypeFont.Load(SyntheticTrueTypeFont.WithVersion(0x74746366)));
        Throws<FontException>(() => TrueTypeFont.Load(new byte[4]));
        Throws<FontException>(() => TrueTypeFont.Load(SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Empty(20)),
            ("hhea", SyntheticTrueTypeFont.Empty(36)),
            ("maxp", SyntheticTrueTypeFont.Empty(6)),
            ("hmtx", SyntheticTrueTypeFont.Empty(4)),
            ("cmap", SyntheticTrueTypeFont.Cmap4()))));
        Throws<FontException>(() => TrueTypeFont.Load(SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 2)),
            ("maxp", SyntheticTrueTypeFont.Maxp(3)),
            ("hmtx", SyntheticTrueTypeFont.Empty(4)),
            ("cmap", SyntheticTrueTypeFont.Cmap4()))));
    }

    private static void ReadsTrueTypeMetrics()
    {
        var font = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, -10, -20, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(750, -250, 50, 2)),
            ("maxp", SyntheticTrueTypeFont.Maxp(3)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0), (600, 0))),
            ("name", SyntheticTrueTypeFont.Name("TestFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        var parsed = TrueTypeFont.Load(font);
        Require(parsed.UnitsPerEm == 1000, "UnitsPerEm read.");
        Require(parsed.NumGlyphs == 3, "NumGlyphs read.");
        Require(parsed.Ascender == 750, "Ascender read.");
        Require(parsed.Descender == -250, "Descender read.");
        Require(parsed.LineGap == 50, "LineGap read.");
        Require(parsed.XMin == -10 && parsed.YMax == 800, "BoundingBox read.");
        Require(parsed.PostScriptName == "TestFont", "PostScript name read.");
        Require(parsed.GetAdvanceWidth(0) == 500, "Width of glyph 0.");
        Require(parsed.GetAdvanceWidth(1) == 600, "Width of glyph 1.");
        Require(parsed.GetAdvanceWidth(2) == 600, "The last width continues to apply.");
    }

    private static void ReadsTrueTypeCmap()
    {
        var font4 = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));
        var parsed4 = TrueTypeFont.Load(font4);
        Require(parsed4.GetGlyphId('A') == 3, "Format 4 with idRangeOffset yields glyph 3.");
        Require(parsed4.GetGlyphId('B') == 0, "An unknown character yields glyph 0.");

        var font12 = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap12()));
        var parsed12 = TrueTypeFont.Load(font12);
        Require(parsed12.GetGlyphId('A') == 3, "Format 12 yields glyph 3 for A.");
        Require(parsed12.GetGlyphId(0x1F600) == 4, "Format 12 yields glyph 4 for U+1F600.");
    }

    private static void EmbedsTrueTypeFont()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, -10, -20, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("name", SyntheticTrueTypeFont.Name("TestFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        using var buffer = new MemoryStream();
        var file = new PdfFileWriter(buffer);
        var resources = new PdfResourceManager();
        var manager = new PdfTrueTypeFontManager(file, resources);

        var face = FontFace.Load(fontData);
        var embedded = manager.GetFont("embedded:TestFont", face);
        Require(embedded.DescriptorObject > 0, "Descriptor was assigned.");
        Require(ReferenceEquals(manager.GetFont("embedded:TestFont", face), embedded), "Deduplication works.");
        embedded.Record(embedded.Font.GetGlyphId('A'), "A");
        manager.Finish();

        var bytes = buffer.ToArray();
        var text = Encoding.Latin1.GetString(bytes);
        AssertContains(text, "/Type /FontDescriptor ");
        AssertContains(text, "/FontName /TestFont ");
        AssertContains(text, "/FontFile2 ");
        AssertContains(text, "/Length1 ");
        AssertContains(text, "/Subtype /CIDFontType2 ");
        AssertContains(text, "/Encoding /Identity-H ");
        AssertContains(text, "/CIDToGIDMap /Identity ");
        AssertContains(text, "/DescendantFonts [");
        AssertContains(text, "/Registry (Adobe)");
        AssertContains(text, "/W [");
        Require(embedded.ResourceName == "F1", "Font is registered as F1.");
        AssertContains(text, "/ToUnicode ");

        var toUnicodeMarker = $"{embedded.ToUnicodeObject} 0 obj\n";
        var toUnicodeStart = text.IndexOf(toUnicodeMarker, StringComparison.Ordinal);
        var cmapStart = text.IndexOf("stream\n", toUnicodeStart, StringComparison.Ordinal) + "stream\n".Length;
        var cmapEnd = text.IndexOf("\nendstream", cmapStart, StringComparison.Ordinal);
        var cmap = text[cmapStart..cmapEnd];
        AssertContains(cmap, "beginbfchar");
        AssertContains(cmap, "<0003> <0041>");

        var objectMarker = $"{embedded.FontFileObject} 0 obj\n";
        var objectStart = text.IndexOf(objectMarker, StringComparison.Ordinal);
        Require(objectStart >= 0, "FontFile2 object found.");
        var streamStart = text.IndexOf("stream\n", objectStart, StringComparison.Ordinal) + "stream\n".Length;
        var streamEnd = text.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
        var compressed = bytes[streamStart..streamEnd];

        using var compressedStream = new MemoryStream(compressed);
        using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        Require(decompressed.ToArray().SequenceEqual(fontData), "FontFile2 contains the original bytes.");
    }

    private static void MeasuresText()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        var embedded = new PdfEmbeddedFontMetrics(TrueTypeFont.Load(fontData));
        Require(
            Math.Abs(PdfTextMeasurer.Measure(embedded, "AAAA", 10) - 20) < 1e-9,
            "Four glyphs at 500 units at 10 pt yield 20 pt.");

        var unicodeFont = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap12()));
        var unicodeMetrics = new PdfEmbeddedFontMetrics(TrueTypeFont.Load(unicodeFont));
        Require(Math.Abs(PdfTextMeasurer.Measure(unicodeMetrics, "😀", 10) - 5) < 1e-9,
            "A supplementary Unicode character is measured as one code point.");

        var courier = new PdfStandardFontMetrics("Courier");
        Require(
            Math.Abs(PdfTextMeasurer.Measure(courier, "ii", 10) - 12) < 1e-9,
            "Courier measures 600 units per character.");

        var helvetica = new PdfStandardFontMetrics("Helvetica");
        Require(helvetica.GetWidth('i') == 222, "Helvetica width for i.");
        Require(helvetica.GetWidth(' ') == 278, "Helvetica width for space.");
        Require(helvetica.GetWidth('A') == 667, "Helvetica width for A.");
    }

    private static void LaysOutPages()
    {
        var section = new Section(new Paragraph(new TextRun("Content")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
            Footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() })),
        };

        using var buffer = new MemoryStream();
        var file = new PdfFileWriter(buffer);
        var catalog = file.ReserveObject();
        var pages = file.ReserveObject();
        var info = file.ReserveObject();
        var resourcesObject = file.ReserveObject();
        var resources = new PdfResourceManager();
        var fonts = new PdfFontCatalog(
            new PdfStandardFontManager(file, resources),
            new PdfTrueTypeFontManager(file, resources),
            embeddedSources: null);
        var renderer = new PdfPageRenderer(file, resources, fonts, new PdfStructureTree(file), pages, resourcesObject);

        renderer.BeginSection(section);
        Require(Math.Abs(renderer.ContentLeft - 72) < 1e-9, "Default margin left is 72 pt.");
        var lineFont = fonts.GetFont(null, bold: false, italic: false);
        for (var i = 0; i < 200; i++)
        {
            renderer.EnsureSpace(12);
            renderer.WriteLine(lineFont, $"Row {i}", 10, Color.Black, renderer.ContentLeft);
            renderer.Advance(12);
        }

        var pageObjects = renderer.Finish();
        Require(pageObjects.Count >= 2, "Several pages were produced.");

        file.WriteObject(pages, tokens => tokens.WriteDictionary(p =>
        {
            p.WriteName("Type");
            p.WriteName("Pages");
            p.WriteName("Count");
            p.WriteInteger(pageObjects.Count);
        }));
        file.WriteObject(catalog, tokens => tokens.WriteDictionary(c =>
        {
            c.WriteName("Type");
            c.WriteName("Catalog");
        }));
        file.WriteObject(resourcesObject, resources.Write);
        file.Complete(catalog, info);

        // Without a band renderer (the block layout draws the bands, see PdfBandChecks) the page
        // renderer leaves the header and footer empty and keeps the page margins.
        var decompressed = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(decompressed, "Row 0");
    }

    private static void NumbersPagesAcrossSections()
    {
        var footer = new HeaderFooter(new Paragraph(new Inline[]
        {
            new TextRun("Page "),
            new PageNumberField(),
        }));
        var first = new Section(new Paragraph(new TextRun("One"))) { Footer = footer };
        var second = new Section(new Paragraph(new TextRun("Two"))) { Footer = footer };
        var document = new PortableDocument(new DocumentMetadata(), new[] { first, second });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(content, "(1) Tj");
        AssertContains(content, "(2) Tj");
    }

    private static void RendersBlocks()
    {
        var document = PortableDocument.Create()
            .Heading("Title", level: 1)
            .Paragraph(p => p.Text("One " ).Bold("bold"))
            .BulletList("one", "two")
            .NumberedList("first", "second")
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .PageBreak()
            .Paragraph("After the break")
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var decompressed = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(decompressed, "Title");
        AssertContains(decompressed, "One");
        AssertContains(decompressed, "bold");
        AssertContains(decompressed, "one");
        AssertContains(decompressed, "first");
        AssertContains(decompressed, "After");
        AssertContains(decompressed, "break");

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        Require(CountOccurrences(text, "/Type /Page ") >= 2, "The page break produces a second page.");
    }

    private static void UsesTypographyBaseline()
    {
        var document = PortableDocument.Create()
            .Heading("Title", level: 2)
            .Paragraph("Text")
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        AssertContains(content, "14 Tf");
    }

    private static void UsesSameRgbColors()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("x", color: Color.FromHex("#1A2B3C")))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        AssertContains(content, "0.102 0.169 0.235 rg");
    }

    private static void UsesSameImageScale()
    {
        var png = TestImages.RgbPng(96, 48, 255, 0, 0);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        AssertContains(content, "72 0 0 36 ");
    }

    private static void PreservesParagraphSpacingAndHardBreaks()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Text("A  ").Bold("B\r\nC"))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        AssertContains(content, "(  ) Tj");
        AssertContains(content, "(B) Tj");
        AssertContains(content, "(C) Tj");
        var before = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(B\) Tj");
        var after = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(C\) Tj");
        Require(before.Success && after.Success, "Both sides of the break are drawn.");
        var beforeY = double.Parse(before.Groups[1].Value, CultureInfo.InvariantCulture);
        var afterY = double.Parse(after.Groups[1].Value, CultureInfo.InvariantCulture);
        Require(afterY < beforeY, "CRLF produces exactly one new PDF line.");
    }

    private static void WrapsLongParagraphToken()
    {
        const string word = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var document = PortableDocument.Create()
            .Page(new PageSize(Length.FromPoints(90), Length.FromPoints(200)),
                margins: new PageMargins(Length.FromPoints(10)))
            .Paragraph(word)
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var fragments = System.Text.RegularExpressions.Regex.Matches(content, @"\(([^)]*)\) Tj");
        Require(fragments.Count > 1, "A long word is split into several PDF lines.");
        Require(string.Concat(fragments.Select(match => match.Groups[1].Value)) == word, "A long word remains complete.");
    }

    /// <summary>N05: underline is drawn as a line below the baseline.</summary>
    private static void DrawsUnderlines()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Text("normal ").Span("underlined", underline: true, fontSize: Length.FromPoints(10)))
            .Build();

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Underlined text produces no warning.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        // 10 pt: stroke width 0.5 (0.05 em); the line lies 1 pt below the baseline (0.1 em).
        AssertContains(content, "0.5 w\n");
        var text = System.Text.RegularExpressions.Regex.Match(content, "([0-9.]+) Td\\n\\(underlined\\) Tj");
        var line = System.Text.RegularExpressions.Regex.Match(content, "([0-9.]+) ([0-9.]+) m\\n[0-9.]+ ([0-9.]+) l\\nS\\n");
        Require(text.Success && line.Success, "An underlined segment is drawn with a line.");
        var baseline = double.Parse(text.Groups[1].Value, CultureInfo.InvariantCulture);
        var lineY = double.Parse(line.Groups[2].Value, CultureInfo.InvariantCulture);
        Require(Math.Abs(lineY - (baseline - 1)) < 0.01, "The line lies 0.1 em below the baseline.");
        var endY = double.Parse(line.Groups[3].Value, CultureInfo.InvariantCulture);
        Require(Math.Abs(endY - lineY) < 0.01, "The line is horizontal.");

        // Without underline no line is produced.
        var plain = PortableDocument.Create().Paragraph("without").Build();
        using var plainBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(plain, plainBuffer).GetAwaiter().GetResult();
        Require(!PdfStreams.Decompress(plainBuffer.ToArray()).Contains(" w\n", StringComparison.Ordinal),
            "Non-underlined text draws no line.");
    }

    /// <summary>
    /// line height and baseline of embedded fonts follow the hhea metrics of the
    /// file (ascender + descender + line gap or ascender), as in Word and
    /// LibreOffice; the standard fonts keep the factor 1.2.
    /// </summary>
    private static void UsesEmbeddedFontVerticalMetrics()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("name", SyntheticTrueTypeFont.Name("MetrikFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        // hheaAsc 800 / upm 1000 -> ascender 0.8 em; line height (800 + 200) / 1000 = 1.0 em.
        // The paragraph mark is in the same font, or its 11 pt default would raise the line.
        var metricStyle = ParagraphStyle.Default with { FontFamily = "Test", FontSize = Length.FromPoints(10) };
        var document = new PortableDocument(new DocumentMetadata(), new Section([
            new Paragraph(new TextRun("erst")) { Style = metricStyle },
            new Paragraph(new TextRun("two")) { Style = metricStyle }]));

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Test"] = fontData } })
            .GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        var top = PageSize.A4.Height.Points - PageMargins.Normal.Top.Points;
        var baselines = System.Text.RegularExpressions.Regex
            .Matches(content, "[0-9.]+ ([0-9.]+) Td\\n")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .OrderByDescending(value => value)
            .Take(2)
            .ToArray();
        Require(baselines.Length == 2, "Embedded font draws both lines (embedded fonts write hex strings).");
        Require(Math.Abs(baselines[0] - (top - 0.8 * 10)) < 0.01, "Baseline lies at the ascender.");
        Require(Math.Abs(baselines[0] - baselines[1] - (1.0 * 10 + 6)) < 0.01, "Line height from hhea (1.0 em) plus paragraph spacing.");
    }

    /// <summary>N06: left, right and first-line indent shift the start position and shorten the wrap.</summary>
    private static void RendersParagraphIndents()
    {
        var style = ParagraphStyle.Default with
        {
            LeftIndent = Length.FromPoints(20),
            RightIndent = Length.FromPoints(10),
            FirstLineIndent = Length.FromPoints(5),
        };
        var longWord = new string('W', 44);
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun(longWord)) { Style = style }));

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Paragraph indents produce no warning.");
        var content = PdfStreams.Decompress(buffer.ToArray());

        var first = System.Text.RegularExpressions.Regex.Match(content, "([0-9.]+) ([0-9.]+) Td\\n\\(W+\\) Tj");
        Require(first.Success, "First line is drawn.");
        var x = double.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
        // 20 pt left indent + 5 pt first-line indent to the right of the default margin (72 pt).
        Require(Math.Abs(x - (PageMargins.Normal.Left.Points + 20 + 5)) < 0.01,
            "First line starts after the left indent plus first-line indent.");

        // At 11 pt Helvetica, 44 × W without indents is < 44.8 cm and not wrapped; with the
        // 30 pt indents it no longer fits -> wrap into several fragments.
        var fragments = System.Text.RegularExpressions.Regex.Matches(content, @"\(W+\) Tj").Count;
        Require(fragments > 1, "Right and left indent shorten the wrap width.");
    }

    /// <summary>The paragraph formatting of DocumentDefaults.ParagraphStyle reaches a built document.</summary>
    private static void AppliesDocumentParagraphDefaults()
    {
        var defaults = new DocumentDefaults
        {
            ParagraphStyle = new ParagraphStyle
            {
                Alignment = TextAlignment.Center,
                LeftIndent = Length.FromPoints(36),
            },
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("centered"))))
        {
            Defaults = defaults,
        };

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var match = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(centered\) Tj");
        Require(match.Success, "The default-styled paragraph is drawn.");
        var x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Require(x > 200, "PDF: the document paragraph defaults are applied (x = " + x + ").");
    }

    /// <summary>N07: justification distributes the extra width across the word gaps (last line left).</summary>
    private static void JustifiesParagraphLines()
    {
        var text = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj";
        var style = ParagraphStyle.Default with { Alignment = TextAlignment.Justify };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun(text) { FontFamily = "Courier New", FontSize = Length.FromPoints(10) }) { Style = style })
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
            });

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Justification produces no warning.");
        var content = PdfStreams.Decompress(buffer.ToArray());

        // Courier 10 pt: 6 pt per character; 180 pt wrap width -> line 1 is
        // "aaaa bbbb cccc dddd eeee ffff" (174 pt), ffff its last word.
        var ffff = System.Text.RegularExpressions.Regex.Match(content, "([0-9.]+) ([0-9.]+) Td\\n\\(ffff\\) Tj");
        var gggg = System.Text.RegularExpressions.Regex.Match(content, "([0-9.]+) ([0-9.]+) Td\\n\\(gggg\\) Tj");
        Require(ffff.Success && gggg.Success, "Justified paragraph is wrapped into lines.");
        var ffffX = double.Parse(ffff.Groups[1].Value, CultureInfo.InvariantCulture);
        var ggggX = double.Parse(gggg.Groups[1].Value, CultureInfo.InvariantCulture);
        // Right limit = 10 + 180 = 190; ffff (4 characters = 24 pt) ends there, so it starts
        Require(Math.Abs(ffffX - 166) < 0.01, "Line 1 is justified up to the right limit.");
        Require(Math.Abs(ggggX - 10) < 0.01, "The last line remains left-aligned.");
    }


    /// <summary>OF-B05: justification also fills the line width inside a table cell.</summary>
    private static void JustifiesTableCells()
    {
        var left = TableTextX(TextAlignment.Left, "eeee");
        var justified = TableTextX(TextAlignment.Justify, "eeee");
        Require(justified > left + 1, "A justified table cell line fills the cell width.");
        var leftTail = TableTextX(TextAlignment.Left, "end", withTail: true);
        var justifiedTail = TableTextX(TextAlignment.Justify, "end", withTail: true);
        Require(Math.Abs(justifiedTail - leftTail) < 0.1, "The last line of a cell paragraph stays left-aligned.");
    }

    private static double TableTextX(TextAlignment alignment, string word, bool withTail = false)
    {
        var paragraph = new Paragraph(new TextRun("aaaa bbbb cccc dddd eeee ffff gggg hhhh end")
        {
            FontFamily = "Courier New",
            FontSize = Length.FromPoints(10),
        })
        {
            Style = ParagraphStyle.Default with { Alignment = alignment },
        };
        var cell = withTail
            ? new TableCell(new Block[] { paragraph, new Paragraph(new TextRun("tail")) })
            : new TableCell(paragraph);
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new TableBlock([new TableRow([cell])]))
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
            });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var match = System.Text.RegularExpressions.Regex.Match(
            content,
            "([0-9.]+) [0-9.]+ Td\\n\\(" + word + "\\) Tj");
        Require(match.Success, "The table text is found in the PDF content.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>OF-B05: justification also fills the line width in a header and in a footer.</summary>
    private static void JustifiesBandText()
    {
        var leftFooter = BandTextX(TextAlignment.Left, "ffff", footer: true);
        var justifiedFooter = BandTextX(TextAlignment.Justify, "ffff", footer: true);
        Require(justifiedFooter > leftFooter + 1, "A justified footer line fills the band width.");

        var leftHeader = BandTextX(TextAlignment.Left, "ffff", footer: false);
        var justifiedHeader = BandTextX(TextAlignment.Justify, "ffff", footer: false);
        Require(justifiedHeader > leftHeader + 1, "A justified header line fills the band width.");

        // the last line of the paragraph is not stretched, as in the body text.
        var leftTail = BandTextX(TextAlignment.Left, "end", footer: true, withTail: true);
        var justifiedTail = BandTextX(TextAlignment.Justify, "end", footer: true, withTail: true);
        Require(Math.Abs(justifiedTail - leftTail) < 0.1, "The last line of a band paragraph stays left-aligned.");
    }

    private static double BandTextX(TextAlignment alignment, string word, bool footer, bool withTail = false)
    {
        var paragraph = new Paragraph(new TextRun("aaaa bbbb cccc dddd eeee ffff gggg hhhh end")
        {
            FontFamily = "Courier New",
            FontSize = Length.FromPoints(10),
        })
        {
            Style = ParagraphStyle.Default with { Alignment = alignment },
        };
        var bandContent = withTail
            ? new Block[] { paragraph, new Paragraph(new TextRun("tail")) }
            : [paragraph];
        var band = new HeaderFooter(bandContent);
        var section = footer
            ? new Section(new Paragraph(new TextRun("body")))
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
                Footer = band,
            }
            : new Section(new Paragraph(new TextRun("body")))
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
                Header = band,
            };

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer)
            .GetAwaiter().GetResult();
        Require(!result.HasWarnings, "A justified band paragraph produces no warning.");
        var content = PdfStreams.Decompress(buffer.ToArray());
        var match = System.Text.RegularExpressions.Regex.Match(
            content,
            "([0-9.]+) [0-9.]+ Td\\n\\(" + word + "\\) Tj");
        Require(match.Success, "The band text is found in the PDF content.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>N13: language and creation time land in the catalog (/Lang) and info (/CreationDate).</summary>
    private static void WritesMetadataLanguageAndCreationDate()
    {
        var document = PortableDocument.Create()
            .Metadata(language: "de-DE", created: new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)))
            .Paragraph("x")
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(buffer.ToArray());

        AssertContains(text, "/Lang (de-DE) ");
        AssertContains(text, "/CreationDate (D:20260924143005+02'00') ");

        // Without language and creation time the output remains unchanged.
        using var plainBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(PortableDocument.Create().Paragraph("x").Build(), plainBuffer)
            .GetAwaiter().GetResult();
        var plainText = Encoding.Latin1.GetString(plainBuffer.ToArray());
        Require(!plainText.Contains("/Lang ", StringComparison.Ordinal), "Without a language no /Lang.");
        Require(!plainText.Contains("/CreationDate ", StringComparison.Ordinal), "Without a creation time no /CreationDate.");

        var withCustom = PortableDocument.Create().Paragraph("x").Build() with
        {
            Metadata = new DocumentMetadata
            {
                CustomProperties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Department"] = "Engineering",
                    ["Review"] = "OK",
                },
            },
        };
        using var customBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(withCustom, customBuffer).GetAwaiter().GetResult();
        var customText = Encoding.Latin1.GetString(customBuffer.ToArray());
        AssertContains(customText, "/Department (Engineering) ");
        AssertContains(customText, "/Review (OK) ");

        var reserved = PortableDocument.Create().Paragraph("x").Build() with
        {
            Metadata = new DocumentMetadata
            {
                CustomProperties = new Dictionary<string, string>(StringComparer.Ordinal) { ["Title"] = "reserved" },
            },
        };
        using var reservedBuffer = new MemoryStream();
        Throws<DocumentValidationException>(() =>
            new PdfDocumentWriter().WriteAsync(reserved, reservedBuffer).GetAwaiter().GetResult());
    }

    /// <summary>Q01/Q02: image is tagged as a /Figure structure element with /Alt (marked content /Figure &lt;&lt;/MCID&gt;&gt; BDC … EMC).</summary>
    private static void WritesImageAltTextAsMarkedContent()
    {
        var png = TestImages.RgbPng(4, 2, 255, 0, 0);
        var withAlt = PortableDocument.Create().Image(png, ImageMediaType.Png, altText: "Chart").Build();
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(withAlt, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Image alternative text produces no warning.");

        var bytes = buffer.ToArray();
        var content = PdfStreams.Decompress(bytes);
        AssertContains(content, "/Figure <</MCID 0>> BDC\n");
        AssertContains(content, "EMC\n");
        var bdcIndex = content.IndexOf("BDC", StringComparison.Ordinal);
        var emcIndex = content.IndexOf("EMC", StringComparison.Ordinal);
        var doIndex = content.IndexOf("/Im1 Do", StringComparison.Ordinal);
        Require(bdcIndex >= 0 && emcIndex > bdcIndex && doIndex > bdcIndex && doIndex < emcIndex,
            "The image lies within the marked content.");

        var latin = Encoding.Latin1.GetString(bytes);
        AssertContains(latin, "/S /Figure ");
        AssertContains(latin, "/Alt (Chart)");

        // Without alternative text the image stays tagged but carries no /Alt.
        var withoutAlt = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();
        using var plainBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(withoutAlt, plainBuffer).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(plainBuffer.ToArray()).Contains("/Figure", StringComparison.Ordinal),
            "Image without alternative text stays tagged.");
        Require(!Encoding.Latin1.GetString(plainBuffer.ToArray()).Contains("/Alt (", StringComparison.Ordinal),
            "Without alternative text no /Alt.");
    }

    /// <summary>N09: page number field in the body is resolved in the PDF to the current page.</summary>
    private static void RendersBodyPageNumberField()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() })));

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Page number field in the body produces no warning.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(content, "(Page) Tj");
        AssertContains(content, "(1) Tj");

        // On the second page the field yields page 2.
        var fill = Enumerable.Range(0, 60).Select(index => (Block)new Paragraph(new TextRun("Filler text"))).ToList();
        fill.Add(new Paragraph(new Inline[] { new TextRun("Sheet "), new PageNumberField() }));
        var twoPages = new PortableDocument(new DocumentMetadata(), new Section(fill));
        using var page2 = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(twoPages, page2).GetAwaiter().GetResult();
        AssertContains(PdfStreams.Decompress(page2.ToArray()), "(2) Tj");
    }

    /// <summary>N10: PDF draws the table borders (0.5 pt rectangles per cell).</summary>
    private static void DrawsTableBorders()
    {
        var document = PortableDocument.Create()
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(content, "0.5 w\n");
        // Four cells -> four outlined rectangles (re ... S).
        var rectangles = System.Text.RegularExpressions.Regex.Matches(content, " re\n").Count;
        Require(rectangles >= 4, "Each cell gets a border (re).");
        Require(content.Contains("S\n", StringComparison.Ordinal), "The borders are drawn (S).");
    }

    /// <summary>N11: embedded fonts are synthetically bold (render mode 2) and italic (slant).</summary>
    private static void SynthesizesBoldAndItalicForEmbeddedFonts()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("name", SyntheticTrueTypeFont.Name("SyntheticFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("A", bold: true, fontFamily: "Test"))
            .Paragraph(p => p.Span("A", italic: true, fontFamily: "Test"))
            .Build();

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Test"] = fontData } })
            .GetAwaiter().GetResult();
                Require(!result.HasWarnings, "Synthetic variants produce no warning.");
        var content = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(content, "2 Tr\n");
        AssertContains(content, "0.176 0 1 0 0 cm");
    }

    private static void EmbedsImages()
    {
        var png = TestImages.RgbPng(4, 2, 255, 0, 0);
        var decoded = PdfPngDecoder.Decode(png);
        Require(decoded.Width == 4 && decoded.Height == 2, "PNG dimensions decoded.");
        Require(decoded.ColorSpace == "/DeviceRGB", "PNG-Farbraum DeviceRGB.");
        Require(decoded.Samples.Length == 4 * 2 * 3, "PNG samples complete.");
        Require(decoded.Samples[0] == 255 && decoded.Samples[1] == 0, "First pixel is red.");

        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        AssertContains(text, "/Subtype /Image ");
        AssertContains(text, "/ColorSpace /DeviceRGB ");
        AssertContains(text, "/Filter /FlateDecode ");

        var decompressed = PdfStreams.Decompress(buffer.ToArray());
        AssertContains(decompressed, "/Im1 Do");
    }

    private static void FitsOversizedImageOnPage()
    {
        var png = TestImages.RgbPng(4, 2, 255, 0, 0);
        var document = PortableDocument.Create()
            .Page(new PageSize(Length.FromPoints(100), Length.FromPoints(100)),
                margins: new PageMargins(Length.FromPoints(10)))
            .Image(png, ImageMediaType.Png,
                width: Length.FromPoints(300), height: Length.FromPoints(300))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var matrix = System.Text.RegularExpressions.Regex.Match(
            content, @"([0-9.]+) 0 0 ([0-9.]+) ([0-9.]+) ([0-9.]+) cm\n/Im1 Do");
        Require(matrix.Success, "Image transformation present.");
        var width = double.Parse(matrix.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = double.Parse(matrix.Groups[2].Value, CultureInfo.InvariantCulture);
        Require(width <= 80 && height <= 80, "Image fits into the content area.");
    }

    private static void WrapsLongTableCells()
    {
        var document = PortableDocument.Create()
            .Page(new PageSize(Length.FromPoints(200), Length.FromPoints(200)), margins: new PageMargins(Length.FromPoints(10)))
            // 44 pt minus 2 × cell padding leaves room for exactly one word per line.
            .Table(t => t.Row("AAAA BBBB CCCC DDDD").ColumnWidths(Length.FromPoints(44)))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        var positions = new Dictionary<string, double>(StringComparer.Ordinal);
        var matches = System.Text.RegularExpressions.Regex.Matches(content, @"([0-9.]+) ([0-9.]+) Td\n\(([^)]*)\) Tj");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            positions[match.Groups[3].Value] = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        foreach (var word in new[] { "AAAA", "BBBB", "CCCC", "DDDD" })
        {
            Require(positions.ContainsKey(word), $"Cell word {word} was drawn.");
        }

        var baselines = new[] { "AAAA", "BBBB", "CCCC", "DDDD" }.Select(word => positions[word]).Distinct().Count();
        Require(baselines > 1, "Long cell content is wrapped to several lines.");
    }

    private static void SplitsTallTableRowsAcrossPages()
    {
        var words = Enumerable.Range(1, 30).Select(index => $"W{index:00}").ToArray();
        var document = PortableDocument.Create()
            .Page(new PageSize(Length.FromPoints(100), Length.FromPoints(100)),
                margins: new PageMargins(Length.FromPoints(10)))
            .Table(table => table.Row(string.Join(' ', words)).ColumnWidths(Length.FromPoints(50)))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = buffer.ToArray();
        var content = PdfStreams.Decompress(pdf);
        Require(CountOccurrences(Encoding.Latin1.GetString(pdf), "/Type /Page ") > 1,
            "A tall table row is distributed over several pages.");
        foreach (var word in words)
        {
            AssertContains(content, $"({word}) Tj");
        }

        var baselines = System.Text.RegularExpressions.Regex.Matches(content, @"[0-9.]+ ([0-9.]+) Td\n")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Require(baselines.All(y => y >= 10), "Table content does not extend below the page margin.");
    }

    private static void RespectsTableCellParagraphsAndFontSizes()
    {
        var firstCell = new TableCell(new Block[]
        {
            new Paragraph(new TextRun("First")),
            new Paragraph(new TextRun("Second") { FontSize = Length.FromPoints(24) }),
        });
        var table = new TableBlock(new[]
        {
            new TableRow(firstCell),
            new TableRow(new TableCell("After")),
        });
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var first = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(First\) Tj");
        var second = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(Second\) Tj");
        var nextRow = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(After\) Tj");
        Require(first.Success && second.Success && nextRow.Success, "All cell paragraphs and rows are drawn.");
        var firstY = double.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
        var secondY = double.Parse(second.Groups[1].Value, CultureInfo.InvariantCulture);
        var nextY = double.Parse(nextRow.Groups[1].Value, CultureInfo.InvariantCulture);
        Require(firstY > secondY && secondY > nextY, "Cell paragraphs and the following row stand one below the other.");
    }

    private static void RejectsPagesWithoutContentArea()
    {
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            PageSize = new PageSize(Length.FromPoints(100), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(60)),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);
        using var buffer = new MemoryStream();
        Throws<ArgumentException>(() => new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult());
    }

    private static int CountOccurrences(string text, string needle)
        => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static void KeepsCallerStreamOpen()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();

        var pdf = new TrackingStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Require(!pdf.Disposed, "PDF: the foreign stream remains open.");
        Require(pdf.Length > 0, "PDF: bytes were written.");
        pdf.Dispose();

        var odt = new TrackingStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(!odt.Disposed, "ODT: the foreign stream remains open.");
        odt.Dispose();

        var docx = new TrackingStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        Require(!docx.Disposed, "DOCX: the foreign stream remains open.");
        docx.Dispose();
    }

    private static void ProducesDeterministicBytes()
    {
        var document = PortableDocument.Create()
            .Metadata(title: "Deterministic", author: "Author")
            .Heading("Title", level: 1)
            .Paragraph("One paragraph")
            .BulletList("a", "b")
            .Build();

        Require(
            WriteWith(new PdfDocumentWriter(), document).SequenceEqual(WriteWith(new PdfDocumentWriter(), document)),
            "PDF: identical input produces identical bytes.");
        Require(
            WriteWith(new DocxDocumentWriter(), document).SequenceEqual(WriteWith(new DocxDocumentWriter(), document)),
            "DOCX: identical input produces identical bytes.");
        Require(
            WriteWith(new OdtDocumentWriter(), document).SequenceEqual(WriteWith(new OdtDocumentWriter(), document)),
            "ODT: identical input produces identical bytes.");
    }

    private static byte[] WriteWith(IDocumentWriter writer, PortableDocument document)
    {
        using var buffer = new MemoryStream();
        writer.WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{expected}' in: {text}");
        }
    }
}
