using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// Reglet.Fonts: font information, embedding rights, font collections and real
/// bold/italic variants in PDF (N11). The fonts are synthetic (glyf/loca/cmap/OS/2).
/// </summary>
internal static class FontChecks
{
    internal static int Run()
    {
        try
        {
            ReadsFaceInformation();
            DefaultsWithoutOs2Table();
            MapsEmbeddingPermissions();
            ReportsCharacterCoverage();
            RejectsUnsupportedAndBrokenFiles();
            CopiesInputBytes();
            CollectionIsImmutableAndCaseInsensitive();
            RejectsDuplicateSlot();
            MatchesVariantsWithFallback();
            LoadsDirectoryDeterministically();
            LoadsSystemDirectoriesRecursively();
            EmbedsRealBoldVariant();
            SynthesizesOnlyMissingStyle();
            LegacyFontsStaySynthetic();
            RejectsFamilyInBothOptions();
            ReportsRestrictedFont();
            KeepsFullFontWhenSubsettingIsForbidden();
            SubsetKeepsDefaultFontAndHeadingGlyphs();
            Console.WriteLine("Fonts (N11): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Fonts (N11): " + exception);
            return 1;
        }
    }

    private static void ReadsFaceInformation()
    {
        var face = FontFace.Load(BuildFont("Probe Sans", "Bold Italic", weight: 700, fsSelection: 0x21));
        Require(face.FamilyName == "Probe Sans", "Family name from name ID 1.");
        Require(face.StyleName == "Bold Italic", "Style name from name ID 2.");
        Require(face.PostScriptName == "ProbeSans-BoldItalic", "PostScript name from name ID 6.");
        Require(face.Weight == 700 && face.IsBold && face.IsItalic, "Weight and style from OS/2.");
        Require(face.GlyphCount == GlyphCount, "Glyph count from maxp.");
        Require(face.ToString() == "Probe Sans Bold Italic", "ToString names family and style.");
    }

    private static void DefaultsWithoutOs2Table()
    {
        var face = FontFace.Load(BuildFont("Plain", "Regular", withOs2: false));
        Require(face.Weight == 400 && !face.IsBold && !face.IsItalic, "Without OS/2: normal style.");
        Require(face.Embedding == FontEmbeddingPermission.Installable && face.CanEmbed && face.AllowsSubsetting,
            "Without OS/2: installable, subsetting allowed.");
    }

    private static void MapsEmbeddingPermissions()
    {
        FontFace Face(ushort fsType) => FontFace.Load(BuildFont("P", "Regular", fsType: fsType));

        Require(Face(0x0002) is { Embedding: FontEmbeddingPermission.Restricted, CanEmbed: false }, "fsType 2: restricted.");
        Require(Face(0x0004).Embedding == FontEmbeddingPermission.PreviewAndPrint, "fsType 4: preview and print.");
        Require(Face(0x0008).Embedding == FontEmbeddingPermission.Editable, "fsType 8: editable.");
        Require(Face(0x0006).Embedding == FontEmbeddingPermission.PreviewAndPrint, "Several bits: the least restrictive applies.");
        Require(!Face(0x0100).AllowsSubsetting, "fsType bit 8: no subsetting.");
        Require(!Face(0x0200).CanEmbed, "fsType bit 9: only bitmaps, no outlines.");
    }

    private static void ReportsCharacterCoverage()
    {
        var face = FontFace.Load(BuildFont("P", "Regular"));
        Require(face.Supports('A') && !face.Supports('a'), "Supports checks the cmap.");
        Require(face.CanRender("ABBA"), "Covered text.");
        Require(face.GetMissingCodePoints("AaBaä").SequenceEqual(new[] { (int)'a', 0xE4 }),
            "Missing code points deduplicated in text order.");
    }

    private static void RejectsUnsupportedAndBrokenFiles()
    {
        Expect<UnsupportedFeatureException>(() => FontFace.Load(SyntheticTrueTypeFont.WithVersion(0x4F54544F)), "CFF (OTTO) is rejected.");
        Expect<FontException>(() => FontFace.Load(new byte[40]), "Damaged file yields FontException.");
        Expect<FontException>(() => FontFace.LoadFile(Path.Combine(Path.GetTempPath(), "reglet-missing-" + Guid.NewGuid() + ".ttf")),
            "A missing file yields FontException.");
        var shortOs2 = BuildFont("P", "Regular", os2Length: 40);
        Expect<FontException>(() => FontFace.Load(shortOs2), "An OS/2 table that is too short is rejected.");
    }

    private static void CopiesInputBytes()
    {
        var data = BuildFont("Copy", "Regular");
        var face = FontFace.Load(data);
        Array.Clear(data);
        Require(face.FamilyName == "Copy" && face.Supports('A'), "Load copies the bytes.");
    }

    private static void CollectionIsImmutableAndCaseInsensitive()
    {
        var regular = FontFace.Load(BuildFont("Probe", "Regular"));
        var one = FontCollection.Empty.Add(regular);
        var two = one.AddDefault(regular);

        Require(FontCollection.Empty.Count == 0 && one.Count == 1 && two.Count == 2, "Add yields new collections.");
        Require(one.Contains("PROBE") && one.Match("probe", false, false)?.Face == regular, "Families case-insensitively.");
        Require(two.Match(null, false, false)?.Face == regular, "null stands for the default font.");
        Require(one.Match("Other", false, false) is null, "An unknown family gives null.");
    }

    private static void RejectsDuplicateSlot()
    {
        var regular = FontFace.Load(BuildFont("Probe", "Regular"));
        var collection = FontCollection.Empty.Add(regular);
        Expect<DocumentValidationException>(() => collection.Add(FontFace.Load(BuildFont("Probe", "Regular"))),
            "A duplicate style is rejected.");
        Require(collection.Add("Probe", regular, bold: true, italic: false).GetFaces("Probe").Count == 2,
            "An explicit style overrides the file's information.");
    }

    private static void MatchesVariantsWithFallback()
    {
        var regular = FontFace.Load(BuildFont("Probe", "Regular"));
        var bold = FontFace.Load(BuildFont("Probe", "Bold", weight: 700, fsSelection: 0x20));
        var collection = FontCollection.Empty.Add(regular).Add(bold);

        Require(collection.Match("Probe", true, false) == new FontMatch(bold, false, false), "Bold: real file.");
        Require(collection.Match("Probe", true, true) == new FontMatch(bold, false, true), "Bold-italic: bold file, italic synthetic.");
        Require(collection.Match("Probe", false, true) == new FontMatch(regular, false, true), "Italic: normal file, italic synthetic.");

        var boldOnly = FontCollection.Empty.Add(bold);
        Require(boldOnly.Match("Probe", false, false) == new FontMatch(bold, false, false),
            "Only bold present: is also used for normal text.");
    }

    private static void LoadsDirectoryDeterministically()
    {
        var directory = Directory.CreateTempSubdirectory("reglet-fonts-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "b-bold.ttf"), BuildFont("Dir", "Bold", weight: 700, fsSelection: 0x20));
            File.WriteAllBytes(Path.Combine(directory.FullName, "a-regular.ttf"), BuildFont("Dir", "Regular"));
            File.WriteAllText(Path.Combine(directory.FullName, "readme.txt"), "no font");

            var collection = FontCollection.FromDirectory(directory.FullName);
            Require(collection.Count == 2 && collection.Families.SequenceEqual(["Dir"]), "All .ttf files under their family.");
            Require(collection.GetFaces("Dir").Select(face => face.StyleName).SequenceEqual(["Regular", "Bold"]),
                "Styles in a fixed order (normal before bold).");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void LoadsSystemDirectoriesRecursively()
    {
        var root = Directory.CreateTempSubdirectory("reglet-system-fonts-");
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(root.FullName, "one.ttf"), BuildFont("SysOne", "Regular"));
            File.WriteAllBytes(Path.Combine(sub.FullName, "two.ttf"), BuildFont("SysTwo", "Bold", weight: 700, fsSelection: 0x20));
            File.WriteAllBytes(Path.Combine(sub.FullName, "broken.ttf"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(sub.FullName, "readme.txt"), "no font");

            var collection = FontCollection.FromDirectories(
                [root.FullName, root.FullName, sub.FullName]);
            Require(collection.Count == 2, "System font directories load recursively without duplicates: " + collection.Count);
            Require(collection.Families.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(["SysOne", "SysTwo"]),
                "Broken files and repeated directories are skipped.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void EmbedsRealBoldVariant()
    {
        var collection = FontCollection.Empty
            .Add(FontFace.Load(BuildFont("Probe", "Regular")))
            .Add(FontFace.Load(BuildFont("Probe", "Bold", weight: 700, fsSelection: 0x20)));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("AB", fontFamily: "Probe").Span("BA", bold: true, fontFamily: "Probe"))
            .Build();

        var pdf = WritePdf(document, new PdfWriteOptions { FontCollection = collection });
        Require(Regex.Matches(pdf.Text, "/FontFile2 ").Count == 2, "Normal and bold file are embedded separately.");
        Require(Regex.IsMatch(pdf.Text, @"/FontName /([A-Z]{6}\+)?Probe-Bold "), "The bold file is embedded.");
        Require(!pdf.Content.Contains(" Tr\n", StringComparison.Ordinal), "No synthetic bold when a bold file is present.");
    }

    private static void SynthesizesOnlyMissingStyle()
    {
        var collection = FontCollection.Empty
            .Add(FontFace.Load(BuildFont("Probe", "Regular")))
            .Add(FontFace.Load(BuildFont("Probe", "Bold", weight: 700, fsSelection: 0x20)));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("AB", bold: true, italic: true, fontFamily: "Probe"))
            .Build();

        var pdf = WritePdf(document, new PdfWriteOptions { FontCollection = collection });
        Require(!pdf.Content.Contains(" Tr\n", StringComparison.Ordinal), "Bold comes from the file.");
        Require(pdf.Content.Contains(" 0 1 0 0 cm", StringComparison.Ordinal), "Italic is synthetically slanted.");
    }

    private static void LegacyFontsStaySynthetic()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("AB", bold: true, fontFamily: "Probe"))
            .Build();
        var pdf = WritePdf(document, new PdfWriteOptions
        {
            Fonts = new Dictionary<string, byte[]> { ["Probe"] = BuildFont("Probe", "Regular") },
        });
        Require(pdf.Content.Contains("2 Tr\n", StringComparison.Ordinal), "Fonts (one file per family): bold stays synthetic.");
    }

    private static void RejectsFamilyInBothOptions()
    {
        var document = PortableDocument.Create().Paragraph("AB").Build();
        var options = new PdfWriteOptions
        {
            Fonts = new Dictionary<string, byte[]> { ["probe"] = BuildFont("Probe", "Regular") },
            FontCollection = FontCollection.Empty.Add(FontFace.Load(BuildFont("Probe", "Regular"))),
        };
        Expect<DocumentValidationException>(() => WritePdf(document, options), "A family in both Fonts and FontCollection is rejected.");
    }

    private static void ReportsRestrictedFont()
    {
        var collection = FontCollection.Empty.Add(FontFace.Load(BuildFont("Probe", "Regular", fsType: 0x0002)));
        var document = PortableDocument.Create().Paragraph(p => p.Span("AB", fontFamily: "Probe")).Build();

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { FontCollection = collection })
            .GetAwaiter().GetResult();
        Require(result.Warnings.Any(warning => warning.Message.Contains("fsType", StringComparison.Ordinal)),
            "A restricted license is warned about.");
        Require(!System.Text.Encoding.Latin1.GetString(buffer.ToArray()).Contains("/FontFile2", StringComparison.Ordinal),
            "A font whose license forbids embedding is not embedded.");

        Expect<UnsupportedFeatureException>(
            () => WritePdf(document, new PdfWriteOptions { FontCollection = collection, UnsupportedContent = UnsupportedContentBehavior.Fail }),
            "In fail mode a restricted license aborts.");
    }

    private static void KeepsFullFontWhenSubsettingIsForbidden()
    {
        var fontData = BuildFont("Probe", "Regular", fsType: 0x0100);
        var document = PortableDocument.Create().Paragraph(p => p.Span("A", fontFamily: "Probe")).Build();
        var pdf = WritePdf(document, new PdfWriteOptions
        {
            FontCollection = FontCollection.Empty.Add(FontFace.Load(fontData)),
        });
        Require(pdf.Streams.Any(stream => stream.AsSpan().SequenceEqual(fontData)), "fsType bit 8: the font is embedded completely.");
    }

    private static void SubsetKeepsDefaultFontAndHeadingGlyphs()
    {
        Block[] blocks =
        [
            new Heading(HeadingLevel.Level1, new TextRun("A")),
            new Paragraph([new TextRun("B")]),
            new Paragraph([new TextRun("C") { FontFamily = "Named" }]),
            new Heading(HeadingLevel.Level2, new TextRun("D") { FontFamily = "Named" }),
            TestLists.List(ListKind.Bullet, [new ListItem(new Paragraph([new TextRun("B")]))]),
        ];
        var document = TestLists.Document(blocks);

        var pdf = WritePdf(document, new PdfWriteOptions
        {
            FontCollection = FontCollection.Empty.AddDefault(FontFace.Load(BuildFont("Probe", "Regular"))),
        });
        var subset = pdf.Streams.Select(TryLoadFont).OfType<TrueTypeFont>().Single();
        Require(subset.GetGlyphId('A') != 0 && subset.GetGlyphId('B') != 0 && subset.GetGlyphId('•') != 0,
            "The embedded subset of the default font contains text, heading and marker glyphs.");
    }

    // ---- Helpers ------------------------------------------------------------------------

    private const int GlyphCount = 40;

    private static TrueTypeFont? TryLoadFont(byte[] data)
    {
        try
        {
            return TrueTypeFont.Load(data);
        }
        catch (Exception exception) when (exception is FontException or UnsupportedFeatureException)
        {
            return null;
        }
    }

    private sealed record PdfBytes(string Text, string Content, IReadOnlyList<byte[]> Streams);

    /// <summary>Writes a PDF and unpacks all FlateDecode streams (content and fonts).</summary>
    private static PdfBytes WritePdf(PortableDocument document, PdfWriteOptions options)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        var bytes = buffer.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        var streams = new List<byte[]>();
        var position = 0;
        while ((position = text.IndexOf("stream\n", position, StringComparison.Ordinal)) >= 0)
        {
            var start = position + "stream\n".Length;
            var end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
            position = end + "\nendstream".Length;
            try
            {
                using var compressed = new MemoryStream(bytes[start..end]);
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                streams.Add(output.ToArray());
            }
            catch (InvalidDataException)
            {
                // non-compressed stream (e.g. XMP)
            }
        }

        var content = string.Join("\n", streams.Select(stream => Encoding.Latin1.GetString(stream)));
        return new PdfBytes(text, content, streams);
    }

    /// <summary>
    /// builds a glyf-based font with 40 empty glyphs. cmap: 'A'–'D' → 3–6,
    /// '0'–'9' → 10–19, '.' → 20, '•' → 21.
    /// </summary>
    private static byte[] BuildFont(
        string family,
        string style,
        int weight = 400,
        ushort fsSelection = 0x40,
        ushort fsType = 0,
        bool withOs2 = true,
        int os2Length = 78)
    {
        // Glyph 0 is empty, the others are 12 bytes each (header without contours); loca in long format.
        var glyf = new byte[(GlyphCount - 1) * 12];
        var loca = new byte[(GlyphCount + 1) * 4];
        for (var i = 1; i <= GlyphCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4, 4), (uint)((i - 1) * 12));
        }

        var head = SyntheticTrueTypeFont.Head(1000, 0, -200, 600, 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50, 2), 1); // indexToLocFormat = long

        var metrics = Enumerable.Repeat(((ushort)500, (short)0), GlyphCount).ToArray();
        var mappings = new List<(int Code, ushort Glyph)> { ('A', 3), ('B', 4), ('C', 5), ('D', 6), ('.', 20), ('•', 21) };
        for (var digit = 0; digit < 10; digit++)
        {
            mappings.Add(('0' + digit, (ushort)(10 + digit)));
        }

        var tables = new List<(string Tag, byte[] Data)>
        {
            ("head", head),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, GlyphCount)),
            ("maxp", SyntheticTrueTypeFont.Maxp(GlyphCount)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx(metrics)),
            ("name", Names(family, style)),
            ("cmap", SyntheticTrueTypeFont.Cmap12(mappings.OrderBy(mapping => mapping.Code).ToArray())),
            ("glyf", glyf),
            ("loca", loca),
        };

        if (withOs2)
        {
            var os2 = new byte[os2Length];
            if (os2Length >= 64)
            {
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4, 2), (ushort)weight);
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8, 2), fsType);
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62, 2), fsSelection);
            }

            tables.Add(("OS/2", os2));
        }

        return SyntheticTrueTypeFont.Build(tables.ToArray());
    }

    /// <summary>name table with family (1), style (2) and PostScript name (6), Windows/Unicode.</summary>
    private static byte[] Names(string family, string style)
    {
        var postScript = family.Replace(" ", string.Empty, StringComparison.Ordinal)
            + (style == "Regular" ? string.Empty : "-" + style.Replace(" ", string.Empty, StringComparison.Ordinal));
        (ushort Id, byte[] Text)[] entries =
        [
            (1, Encoding.BigEndianUnicode.GetBytes(family)),
            (2, Encoding.BigEndianUnicode.GetBytes(style)),
            (6, Encoding.BigEndianUnicode.GetBytes(postScript)),
        ];

        var storageOffset = 6 + entries.Length * 12;
        var span = new byte[storageOffset];
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(2, 2), (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(4, 2), (ushort)storageOffset);

        var stringOffset = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var record = 6 + i * 12;
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record, 2), 3);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record + 2, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record + 4, 2), 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record + 6, 2), entries[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record + 8, 2), (ushort)entries[i].Text.Length);
            BinaryPrimitives.WriteUInt16BigEndian(span.AsSpan(record + 10, 2), (ushort)stringOffset);
            stringOffset += entries[i].Text.Length;
        }

        return [.. span, .. entries.SelectMany(entry => entry.Text)];
    }

    private static void Expect<TException>(Action action, string message)
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
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}");
        }

        throw new InvalidOperationException($"{message} No exception was thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
