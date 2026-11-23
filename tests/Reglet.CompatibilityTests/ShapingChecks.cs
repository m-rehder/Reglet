using System.Buffers.Binary;
using Reglet;

/// <summary>
/// phase P: OpenType layout (P01) and text shaping. The fonts are synthetic; the
/// layout tables are assembled in the test so that no real font files are needed.
/// </summary>
internal static class ShapingChecks
{
    internal static int Run()
    {
        try
        {
            ParsesAndAppliesLigatureSubstitution();
            ParsesAndAppliesKerningPairs();
            ReadsGlyphClassesFromGdef();
            ReadsGdefRangeUpToLastGlyph();
            RejectsMalformedLayoutTable();
            ReportsMissingLayoutTables();
            ShapesLatinLigatureAndKerning();
            KernsRightToLeftPairs();
            AppliesArabicContextForms();
            MeasuresShapedTextWithLigature();
            WritesShapedGlyphsAsTextArray();
            BidiResolvesCommonCases();
            WritesRightToLeftWordsInVisualOrder();
            ReordersRightToLeftWithoutLayoutTables();
            MirrorsRightToLeftParagraphs();
            MirrorsRightToLeftListMarkers();
            ShapesWordsWithLineLevels();
            MapsMirroredGlyphsToTheirOwnCharacter();
            UsesLevelBulletsWhenTheFontHasThem();
            JoiningTypesFollowUnicode();
            BidiCharacterConformance();
            Console.WriteLine("Shaping (P01/P02/P03/P04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Shaping (P): " + exception);
            return 1;
        }
    }

    /// <summary>P01: GSUB with single and ligature lookup is detected and applied.</summary>
    private static void ParsesAndAppliesLigatureSubstitution()
    {
        // latn/liga: glyphs 3 + 4 become ligature 5.
        var gsub = Gsub(script: "latn", feature: "liga", lookupType: 4, lookupData: LigatureSubtable(first: 3, second: 4, ligature: 5));
        var layout = OpenTypeLayout.TryCreate(LoadFont(gsub: gsub)) ?? throw new InvalidOperationException("Layout missing.");

        Require(layout.HasGsub, "GSUB recognised.");
        var lookups = layout.GetFeatureLookups(gpos: false, OpenTypeLayout.Tag("latn"), OpenTypeLayout.Tag("liga"));
        Require(lookups.Count == 1 && lookups[0] == 0, "Ligature lookup found.");

        ushort[] glyphs = [3, 4, 4];
        Require(
            layout.TryLigature(glyphs, 0, 0, out var ligature, out var components),
            "Ligature applied.");
        Require(ligature == 5 && components == 2, "The ligature replaces 3+4 with 5.");
        Require(!layout.TryLigature(glyphs, 1, 0, out _, out _), "No ligature at position 1.");
    }

    /// <summary>P01: GPOS pair adjustment (kerning) is detected and applied.</summary>
    private static void ParsesAndAppliesKerningPairs()
    {
        var gpos = Gpos(script: "latn", feature: "kern", lookupData: PairPosSubtable(first: 3, second: 4, xAdvance: -80, xPlacement: 5));
        var layout = OpenTypeLayout.TryCreate(LoadFont(gpos: gpos)) ?? throw new InvalidOperationException("Layout missing.");

        Require(layout.HasGpos, "GPOS recognised.");
        var lookups = layout.GetFeatureLookups(gpos: true, OpenTypeLayout.Tag("latn"), OpenTypeLayout.Tag("kern"));
        Require(lookups.Count == 1 && lookups[0] == 0, "Kerning lookup found.");

        Require(layout.TryKern(0, 3, 4, out var advance, out var placement), "Kerning applied.");
        Require(Math.Abs(advance - (-80)) < 1e-9, "XAdvance = -80.");
        Require(Math.Abs(placement - 5) < 1e-9, "XPlacement = 5.");
        Require(!layout.TryKern(0, 4, 3, out _, out _), "No kerning for the reversed pair.");
    }

    /// <summary>P01: glyph classes from GDEF are read.</summary>
    private static void ReadsGlyphClassesFromGdef()
    {
        var layout = OpenTypeLayout.TryCreate(LoadFont(gdef: Gdef((3, 1), (4, 3)))) ?? throw new InvalidOperationException("Layout missing.");
        Require(layout.HasGlyphClasses, "GDEF recognised.");
        Require(layout.GetGlyphClass(3) == 1, "Glyph 3 is a base.");
        Require(layout.GetGlyphClass(4) == 3, "Glyph 4 is a mark.");
        Require(layout.GetGlyphClass(5) == 0, "Glyph 5 without a class.");
    }

    /// <summary>A GDEF range ending at 0xFFFF must terminate (a ushort counter wrapped around forever).</summary>
    private static void ReadsGdefRangeUpToLastGlyph()
    {
        var gdef = new byte[12 + 10];
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(0, 2), 1);       // Major
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(4, 2), 12);      // GlyphClassDef
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(12, 2), 2);      // Format 2
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(14, 2), 1);      // rangeCount
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(18, 2), 0xFFFF); // end
        BinaryPrimitives.WriteUInt16BigEndian(gdef.AsSpan(20, 2), 1);      // class

        var font = LoadFont(gdef: gdef);
        var task = Task.Run(() => OpenTypeLayout.TryCreate(font));
        Require(task.Wait(TimeSpan.FromSeconds(10)), "GDEF range up to 0xFFFF terminates.");
        Require(task.Result?.GetGlyphClass(0xFFFF) == 1, "The last glyph of the range has its class.");
    }

    /// <summary>P01: damaged tables are reported as FontException.</summary>
    private static void RejectsMalformedLayoutTable()
    {
        // Header points to a ScriptList beyond the table.
        var broken = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(broken.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(broken.AsSpan(4, 2), 0xFFFF);

        Throws<FontException>(() => OpenTypeLayout.TryCreate(LoadFont(gsub: broken)));
    }

    /// <summary>P01: a font without layout tables yields no layout.</summary>
    private static void ReportsMissingLayoutTables()
        => Require(OpenTypeLayout.TryCreate(LoadFont()) is null, "Without layout tables no layout.");

    /// <summary>Right to left, the kerning of a pair sits between exactly those two glyphs.</summary>
    private static void KernsRightToLeftPairs()
    {
        // Hebrew alef, bet, gimel; only the pair alef-bet is kerned.
        var font = LoadFont(
            cmap: Cmap12Mapped((0x05D0, 3), (0x05D1, 4), (0x05D2, 5)),
            gpos: Gpos("hebr", "kern", PairPosSubtable(3, 4, -80, 0)));
        var shaper = new TextShaper(font, OpenTypeLayout.TryCreate(font)!);

        var visual = shaper.Shape("\u05D0\u05D1\u05D2") ?? throw new InvalidOperationException("No shaping.");
        Require(visual.Select(glyph => glyph.GlyphId).SequenceEqual(new ushort[] { 5, 4, 3 }), "Hebrew is laid out right to left.");
        Require(visual[0].Kern == 0, "No kerning between gimel and bet.");
        Require(visual[1].Kern < 0, $"The alef-bet kerning sits after bet, before alef (got {visual[1].Kern}).");
        Require(visual[2].Kern == 0, "Nothing is kerned after the last glyph.");
    }

    /// <summary>P02/P03: ligature and kerning are applied during shaping.</summary>
    private static void ShapesLatinLigatureAndKerning()
    {
        var font = LoadFont(
            cmap: Cmap12Mapped(('f', 3), ('i', 4), ('A', 3)),
            gsub: Gsub("latn", "liga", 4, LigatureSubtable(3, 4, 5)),
            gpos: Gpos("latn", "kern", PairPosSubtable(3, 3, -80, 0)));
        var layout = OpenTypeLayout.TryCreate(font)!;
        var kernLookups = layout.GetFeatureLookups(gpos: true, OpenTypeLayout.ScriptLatin, OpenTypeLayout.FeatureKern);
        Require(kernLookups.Count == 1, $"Kerning lookup for latn/kern (is {kernLookups.Count}).");
        Require(layout.TryKern(0, 3, 3, out var directAdvance, out _) && Math.Abs(directAdvance - (-80)) < 1e-9, "Direct kerning -80.");
        var shaper = new TextShaper(font, layout);

        var ligature = shaper.Shape("fi") ?? throw new InvalidOperationException("No shaping.");
        Require(ligature.Count == 1 && ligature[0].GlyphId == 5, "fi becomes ligature 5.");

        // Clusters index the source code points (a ligature keeps its first component), so the
        // ToUnicode map gets the full text of every glyph.
        var clustered = shaper.Shape("Afi") ?? throw new InvalidOperationException("No shaping.");
        Require(clustered.Select(glyph => glyph.Cluster).SequenceEqual([0, 1]), "Clusters are source code point indices.");
        var embedded = new PdfEmbeddedFont { Font = font };
        embedded.RecordShaped("Afi", clustered);
        Require(embedded.GlyphText[5] == "fi" && embedded.GlyphText[3] == "A", "A ligature glyph maps to all its characters.");

        var kerned = shaper.Shape("AA") ?? throw new InvalidOperationException("No shaping.");
        Require(kerned.Count == 2, "AA remains two glyphs.");
        Require(Math.Abs(kerned[0].Kern - (-80)) < 1e-9, $"Kerning -80 after the first A (is {kerned[0].Kern}).");
    }

    /// <summary>P03: Arabic contextual forms are chosen via the joining analysis.</summary>
    private static void AppliesArabicContextForms()
    {
        // BEH (dual) at the word end gets the final form (feature 'fina').
        var font = LoadFont(
            cmap: Cmap12Mapped((0x0628, 3)),
            gsub: Gsub("arab", "fina", 1, SingleSubtable(3, 7)));
        var shaper = new TextShaper(font, OpenTypeLayout.TryCreate(font)!);

        var glyphs = shaper.Shape("\u0628\u0628") ?? throw new InvalidOperationException("No shaping.");
        Require(glyphs.Count == 2, "Two glyphs.");
        // Visual order (RTL): the final form stands on the left, the initial form on the right.
        Require(glyphs[0].GlyphId == 7, "The final form comes first in visual order.");
        Require(glyphs[1].GlyphId == 3, "Initial form after.");

        // Shape analysis directly: initial, final, initial.
        var forms = ArabicJoining.ResolveForms([0x0628, 0x0627, 0x0628]);
        Require(forms[0] == ArabicJoining.ContextForm.Initial, "BEH initial.");
        Require(forms[1] == ArabicJoining.ContextForm.Final, "ALEF final.");
        Require(forms[2] == ArabicJoining.ContextForm.Isolated, "BEH after ALEF isolated.");
        Require(ArabicJoining.ResolveForms([0x0628])[0] == ArabicJoining.ContextForm.Isolated, "Single BEH isolated.");
    }

    /// <summary>P02: measurement uses the ligature width instead of the individual glyphs.</summary>
    private static void MeasuresShapedTextWithLigature()
    {
        var font = LoadFont(
            cmap: Cmap12Mapped(('f', 3), ('i', 4)),
            gsub: Gsub("latn", "liga", 4, LigatureSubtable(3, 4, 5)));
        var shaper = new TextShaper(font, OpenTypeLayout.TryCreate(font)!);

        // hmtx: f=600, i=300, ligature=700 -> with the ligature 700, without 900.
        var handle = new PdfFontHandle(
            () => "F1",
            new PdfEmbeddedFontMetrics(font),
            isEmbedded: true,
            text => [],
            text => true,
            shaper: shaper);

        Require(Math.Abs(handle.Measure("fi", 1000) - 700) < 1e-9, "Ligature width 700.");
        Require(Math.Abs(handle.Measure("if", 1000) - 900) < 1e-9, "Without ligature 600+300.");
    }

    /// <summary>P02: shaped glyphs are written as a TJ array with kerning.</summary>
    private static void WritesShapedGlyphsAsTextArray()
    {
        var content = new PdfContentWriter();
        content.BeginText();
        content.SetFont("F1", 12);
        content.MoveText(72, 700);
        content.ShowShapedGlyphs(
        [
            new ShapedGlyph(5, 700, 0, 0, 0, 0),
            new ShapedGlyph(9, 600, -80, 0, 0, 1),
        ]);
        content.EndText();

        var text = System.Text.Encoding.ASCII.GetString(content.ToArray());
        Require(text.Contains("[ <0005> <0009> 80 ] TJ\n", StringComparison.Ordinal), "TJ array with kerning 80.");
    }

    /// <summary>P04: basic cases of the bidi resolution and visual order.</summary>
    private static void BidiResolvesCommonCases()
    {
        // "abc ABC" mixed with Hebrew alef (05D0) and Arabic beh (0628).
        Require(BidiAlgorithm.GetParagraphLevel([0x05D0]) == 1, "Hebrew is RTL.");
        Require(BidiAlgorithm.GetParagraphLevel([0x0628]) == 1, "Arabic is RTL.");
        Require(BidiAlgorithm.GetParagraphLevel([0x0041]) == 0, "Latin is LTR.");

        // LTR paragraph with a Hebrew word: levels 0 around the word, 1 in the word.
        int[] cps = [0x0041, 0x0020, 0x05D0, 0x05D1, 0x0020, 0x0042];
        var result = BidiAlgorithm.Resolve(cps, paragraphLevel: 0);
        Require(result.Levels[0] == 0 && result.Levels[5] == 0, "Latin characters at level 0.");
        Require(result.Levels[2] == 1 && result.Levels[3] == 1, "Hebrew word at level 1.");

        // The visual order reverses the Hebrew run.
        var visual = BidiAlgorithm.ReorderVisual(result.Levels);
        Require(visual[2] == 3 && visual[3] == 2, "Hebrew run is reversed.");

        // A ZWNJ inside a Persian word must not split the right-to-left run (X9 retained).
        int[] persian = [0x0041, 0x0020, 0x0645, 0x06CC, 0x200C, 0x062E, 0x0648];
        var persianOrder = BidiAlgorithm.ReorderVisual(BidiAlgorithm.Resolve(persian, paragraphLevel: 0).Levels);
        Require(persianOrder.SequenceEqual(new[] { 0, 1, 6, 5, 4, 3, 2 }), $"ZWNJ keeps the word whole, got {string.Join(' ', persianOrder)}.");

        // P2: isolated text does not decide the paragraph direction.
        Require(BidiAlgorithm.GetParagraphLevel([0x2068, 0x05D0, 0x2069, 0x0041]) == 0, "An isolated Hebrew name keeps an LTR paragraph.");
        Require(BidiAlgorithm.GetParagraphLevel([0x2067, 0x05D0]) == 0, "An unmatched isolate runs to the end of the paragraph.");
    }

    /// <summary>Joining types follow ArabicShaping.txt, including Arabic Supplement.</summary>
    private static void JoiningTypesFollowUnicode()
    {
        Require(ArabicJoining.GetJoiningType(0x063B) == ArabicJoining.JoiningType.D, "U+063B KEHEH WITH TWO DOTS joins both sides.");
        Require(ArabicJoining.GetJoiningType(0x0750) == ArabicJoining.JoiningType.D, "Arabic Supplement letters join.");
        Require(ArabicJoining.GetJoiningType(0x0759) == ArabicJoining.JoiningType.R, "U+0759 DAL WITH TWO DOTS joins to the right only.");
        Require(ArabicJoining.GetJoiningType(0x0600) == ArabicJoining.JoiningType.U, "The prepended number sign does not join.");
    }

    /// <summary>A font without GSUB/GPOS/GDEF still gets the bidi order of a right-to-left word.</summary>
    private static void ReordersRightToLeftWithoutLayoutTables()
    {
        var shaper = new TextShaper(LoadFont(cmap: Cmap12Mapped((0x05D0, 3), (0x05D1, 4))), layout: null);
        var glyphs = shaper.Shape("\u05D0\u05D1") ?? throw new InvalidOperationException("No shaping.");
        Require(glyphs.Select(glyph => glyph.GlyphId).SequenceEqual(new ushort[] { 4, 3 }), "The Hebrew word is reversed without layout tables.");
        Require(shaper.Shape("ab") is null, "Latin text keeps the fast path.");

        // L4: right to left "(" takes the glyph of ")".
        var mirroring = new TextShaper(LoadFont(cmap: Cmap12Mapped((0x05D0, 3), (0x05D1, 4), ('(', 5), (')', 2))), layout: null);
        var mirrored = mirroring.Shape("\u05D0(\u05D1") ?? throw new InvalidOperationException("No shaping.");
        Require(mirrored.Select(glyph => glyph.GlyphId).SequenceEqual(new ushort[] { 4, 2, 3 }), "A parenthesis in RTL text is mirrored.");
    }

    /// <summary>
    /// A right-to-left paragraph (w:bidi) is mirrored: start alignment is the right edge, end
    /// alignment the left, and tab segments run from the right.
    /// </summary>
    private static void MirrorsRightToLeftParagraphs()
    {
        var font = FontBytes(cmap: Cmap12Mapped((0x05D0, 3), (0x05D1, 4)));
        var rtl = new ParagraphStyle { RightToLeft = true };

        var start = TextPositions(font, p => p.Style(rtl).Run(new TextRun("\u05D0\u05D0") { FontFamily = "Heb" }));
        Require(start.Count == 1 && start[0].X > 300, $"Start alignment of an RTL paragraph is the right edge (x = {start.FirstOrDefault().X}).");

        var end = TextPositions(font, p => p.Style(rtl with { Alignment = TextAlignment.Right }).Run(new TextRun("\u05D0\u05D0") { FontFamily = "Heb" }));
        Require(end.Count == 1 && Math.Abs(end[0].X - 72) < 1, $"End alignment of an RTL paragraph is the left edge (x = {end.FirstOrDefault().X}).");

        var tabbed = TextPositions(font, p => p.Style(rtl).Run(new TextRun("\u05D0\u05D0\t\u05D1\u05D1") { FontFamily = "Heb" }));
        var first = tabbed.Single(item => item.Glyphs.Contains("<0003>")).X;
        var second = tabbed.Single(item => item.Glyphs.Contains("<0004>")).X;
        Require(first > second, "In an RTL paragraph the text before the tab is on the right.");

        var ltrTabbed = TextPositions(font, p => p.Run(new TextRun("\u05D0\u05D0 \u05D1\u05D1\t\u05D0") { FontFamily = "Heb" }));
        var words = ltrTabbed.Where(item => item.Glyphs.Contains("<0003> <0003>") || item.Glyphs.Contains("<0004> <0004>")).ToList();
        Require(words.Count == 2 && words[0].Glyphs.Contains("<0004>"), "Hebrew words before a tab are ordered visually as well.");
    }

    /// <summary>
    /// A word is shaped with the levels of its line: in an RTL paragraph "abc." at the end has its
    /// period on the left (eos is R), which the word alone (LTR) would not show.
    /// </summary>
    private static void ShapesWordsWithLineLevels()
    {
        var font = FontBytes(cmap: Cmap12Mapped(('a', 3), ('.', 4), (0x05D0, 5)));
        var pieces = TextPositions(font, p => p.Style(new ParagraphStyle { RightToLeft = true }).Run(new TextRun("\u05D0 a.") { FontFamily = "Heb" }));
        var word = pieces.Single(piece => piece.Glyphs.Contains("<0003>")).Glyphs;
        Require(word.IndexOf("<0004>", StringComparison.Ordinal) < word.IndexOf("<0003>", StringComparison.Ordinal),
            $"The final period of an RTL line sits left of the Latin word ({word.Trim()}).");
    }

    /// <summary>A mirrored bracket glyph copies as the bracket it shows, also where the glyph is used in LTR text.</summary>
    private static void MapsMirroredGlyphsToTheirOwnCharacter()
    {
        var font = FontBytes(cmap: Cmap12Mapped((0x05D0, 3), ('(', 4), (')', 5)));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Style(new ParagraphStyle { RightToLeft = true }).Run(new TextRun("\u05D0(\u05D0)") { FontFamily = "Heb" }))
            .Paragraph(p => p.Run(new TextRun("()") { FontFamily = "Heb" }))
            .Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = font } })
            .GetAwaiter().GetResult();
        buffer.Position = 0;
        var lines = DocumentReader.ReadPdfPages(buffer)[0].Split('\n');
        Require(lines[^1].Trim() == "()", $"An LTR bracket pair copies unchanged after mirrored use ({lines[^1]}).");
    }

    /// <summary>A right-to-left list item has its marker on the right, read in the paragraph direction.</summary>
    private static void MirrorsRightToLeftListMarkers()
    {
        Require(PdfBlockRenderer.VisualText("1.", rightToLeft: true) == ".1", "An RTL number marker reads \".1\".");
        Require(PdfBlockRenderer.VisualText("1.", rightToLeft: false) == "1.", "An LTR number marker stays \"1.\".");
        Require(PdfBlockRenderer.VisualText("1)", rightToLeft: true) == "(1", "An RTL marker mirrors its parenthesis (L4).");

        var font = FontBytes(cmap: Cmap12Mapped((0x05D0, 3)));
        var item = new Paragraph(new TextRun("\u05D0") { FontFamily = "Heb" }) { Style = new ParagraphStyle { RightToLeft = true } };
        var document = new PortableDocument(new DocumentMetadata(), new Section(new ListBlock("Num", [new ListItem(item)])))
        {
            Numbering = [new NumberingDefinition("Num", [new NumberingLevel { Kind = ListKind.Number }])],
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = font } })
            .GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        var positions = System.Text.RegularExpressions.Regex.Matches(content, @"(?<x>-?[\d.]+) -?[\d.]+ Td(?:(?!Td).)*?(?<show>\[[^\]]*\] TJ|\([^)]*\) Tj|<[0-9A-F]+> Tj)", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(match => (X: double.Parse(match.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture), Show: match.Groups["show"].Value))
            .ToList();
        var text = positions.Single(position => position.Show.Contains("<0003>")).X;
        var marker = positions.Single(position => !position.Show.Contains("<0003>")).X;
        Require(marker > text && marker > 450, $"The marker of an RTL item is right of its text (marker {marker}, text {text}).");
    }

    /// <summary>A level-2 bullet is ◦ as in DOCX/ODT when the font has it; the WinAnsi standard fonts keep •.</summary>
    private static void UsesLevelBulletsWhenTheFontHasThem()
    {
        var nested = TestLists.List(ListKind.Bullet, [new ListItem(new Paragraph(new TextRun("b")))], nestingLevel: 2);
        var document = TestLists.Document(TestLists.List(ListKind.Bullet, [new ListItem([new Paragraph(new TextRun("a")), nested])]));

        var font = FontFace.Load(FontBytes(cmap: Cmap12Mapped((0x2022, 3), (0x25E6, 4))));
        using var embedded = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, embedded, new PdfWriteOptions { FontCollection = FontCollection.Empty.AddDefault(font) })
            .GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(embedded.ToArray()).Contains("<0004>", StringComparison.Ordinal), "With an embedded font level 2 uses ◦.");

        using var standard = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, standard).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(standard.ToArray());
        Require(System.Text.RegularExpressions.Regex.Matches(content, @"\(\\225\) Tj").Count == 2 && !content.Contains("(?) Tj", StringComparison.Ordinal),
            "Standard fonts keep • on every level.");
    }

    /// <summary>The x position and glyphs of every text piece written for the paragraph.</summary>
    private static List<(double X, string Glyphs)> TextPositions(byte[] font, Action<ParagraphBuilder> paragraph)
    {
        var document = PortableDocument.Create().Paragraph(paragraph).Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = font } })
            .GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        return System.Text.RegularExpressions.Regex.Matches(content, @"(?<x>-?[\d.]+) -?[\d.]+ Td(?:(?!Td).)*?\[(?<g>[^\]]*)\] TJ", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(match => (double.Parse(match.Groups["x"].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups["g"].Value))
            .ToList();
    }

    /// <summary>The PDF writer places the words of a right-to-left line from the right.</summary>
    private static void WritesRightToLeftWordsInVisualOrder()
    {
        var font = FontBytes(
            cmap: Cmap12Mapped((0x05D0, 3), (0x05D1, 4)),
            gpos: Gpos("hebr", "kern", PairPosSubtable(5, 5, -10, 0)));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Run(new TextRun("\u05D0\u05D0 \u05D1\u05D1") { FontFamily = "Heb" }))
            .Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = font } })
            .GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        var first = content.IndexOf("<0003> <0003>", StringComparison.Ordinal);
        var second = content.IndexOf("<0004> <0004>", StringComparison.Ordinal);
        Require(first >= 0 && second >= 0, "Both Hebrew words are written.");
        Require(second < first, "The second Hebrew word is placed left of the first (written first).");
    }

    /// <summary>
    /// P04: check against an excerpt from BidiCharacterTest.txt (UAX #9). With the
    /// environment variable REGLET_BIDI_TEST the complete UCD file can be checked.
    /// </summary>
    private static void BidiCharacterConformance()
    {
        var path = Environment.GetEnvironmentVariable("REGLET_BIDI_TEST");
        if (string.IsNullOrEmpty(path))
        {
            path = Path.Combine(GoldenPaths.RepositoryRoot, "tests", "Reglet.CompatibilityTests", "fixtures", "bidi", "BidiCharacterTest.txt");
        }

        if (!File.Exists(path))
        {
            return;
        }

        var total = 0;
        var failures = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(';');
            var cps = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Convert.ToInt32(value, 16)).ToArray();
            var direction = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            var paragraphLevel = int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture);
            var levelTokens = fields[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expectedLevels = levelTokens.Select(token => token is "x" or "X" ? -1 : int.Parse(token)).ToArray();
            var expectedOrder = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

            total++;
            if (direction == 2 && BidiAlgorithm.GetParagraphLevel(cps) != paragraphLevel)
            {
                failures++;
                if (failures <= 5)
                {
                    Console.Error.WriteLine($"Bidi P2/P3: {line}");
                }

                continue;
            }

            var resolved = BidiAlgorithm.Resolve(cps, paragraphLevel);
            var visual = BidiAlgorithm.ReorderVisual(resolved.Levels).Where(index => expectedLevels[index] >= 0).ToArray();
            var levelsOk = true;
            for (var i = 0; i < cps.Length; i++)
            {
                if (expectedLevels[i] >= 0 && resolved.Levels[i] != expectedLevels[i])
                {
                    levelsOk = false;
                    break;
                }
            }

            var orderOk = visual.SequenceEqual(expectedOrder);
            if (!levelsOk || !orderOk)
            {
                failures++;
                if (failures <= 10)
                {
                    Console.Error.WriteLine($"FAIL {line}");
                    Console.Error.WriteLine($"  got={string.Join(' ', resolved.Levels)} exp={fields[3]}");
                }
            }
        }

        if (failures > 0)
        {
            throw new InvalidOperationException($"Bidi conformance: {failures} of {total} cases incorrect.");
        }

        Console.WriteLine($"Bidi conformance: {total} cases passed.");
    }

    private static TrueTypeFont LoadFont(byte[]? gsub = null, byte[]? gpos = null, byte[]? gdef = null, byte[]? cmap = null)
        => TrueTypeFont.Load(FontBytes(gsub, gpos, gdef, cmap));

    private static byte[] FontBytes(byte[]? gsub = null, byte[]? gpos = null, byte[]? gdef = null, byte[]? cmap = null)
    {
        var tables = new List<(string, byte[])>
        {
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 6)),
            ("maxp", SyntheticTrueTypeFont.Maxp(6)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0), (500, 0), (500, 0), (600, 0), (300, 0), (700, 0))),
            ("name", SyntheticTrueTypeFont.Name("ShapingTest")),
            ("cmap", cmap ?? SyntheticTrueTypeFont.Cmap4()),
        };

        if (gsub is not null)
        {
            tables.Add(("GSUB", gsub));
        }

        if (gpos is not null)
        {
            tables.Add(("GPOS", gpos));
        }

        if (gdef is not null)
        {
            tables.Add(("GDEF", gdef));
        }

        return SyntheticTrueTypeFont.Build(tables.ToArray());
    }

    // --- Synthetic layout tables ---------------------------------------------------

    private static byte[] Gsub(string script, string feature, int lookupType, byte[] lookupData)
        => LayoutTable(script, feature, lookupType, lookupData);

    private static byte[] Gpos(string script, string feature, byte[] lookupData)
        => LayoutTable(script, feature, lookupType: 2, lookupData);

    /// <summary>Builds a GSUB/GPOS table with a script, a feature and a lookup.</summary>
    private static byte[] LayoutTable(string script, string feature, int lookupType, byte[] lookupData)
    {
        var b = new OtBuilder();
        b.U32(0x00010000);
        var pScript = b.U16(0);
        var pFeature = b.U16(0);
        var pLookup = b.U16(0);

        // ScriptList
        b.Patch(pScript, b.Position);
        var scriptList = b.Position;
        b.U16(1);
        b.Tag(script);
        var pScriptOff = b.U16(0);
        b.Patch(pScriptOff, b.Position - scriptList);

        // Script
        var scriptTable = b.Position;
        var pDefault = b.U16(0);
        b.U16(0);
        b.Patch(pDefault, b.Position - scriptTable);

        // LangSys: lookupOrder, requiredFeatureIndex, featureCount, featureIndex[1]
        b.U16(0);
        b.U16(0xFFFF);
        b.U16(1);
        b.U16(0);

        // FeatureList
        b.Patch(pFeature, b.Position);
        var featureList = b.Position;
        b.U16(1);
        b.Tag(feature);
        var pFeatureOff = b.U16(0);
        b.Patch(pFeatureOff, b.Position - featureList);

        // Feature: featureParams, lookupCount, lookupIndex[1]
        b.U16(0);
        b.U16(1);
        b.U16(0);

        // LookupList
        b.Patch(pLookup, b.Position);
        var lookupList = b.Position;
        b.U16(1);
        var pLookupOff = b.U16(0);
        b.Patch(pLookupOff, b.Position - lookupList);

        // Lookup
        var lookup = b.Position;
        b.U16(lookupType);
        b.U16(0);
        b.U16(1);
        var pSubtable = b.U16(0);
        b.Patch(pSubtable, b.Position - lookup);
        b.Bytes(lookupData);

        return b.ToArray();
    }

    /// <summary>LigatureSubst (GSUB 4) for exactly one pair.</summary>
    private static byte[] LigatureSubtable(ushort first, ushort second, ushort ligature)
    {
        var b = new OtBuilder();
        var subtable = b.Position;
        b.U16(1); // Format 1
        var pCoverage = b.U16(0);
        b.U16(1); // LigatureSet-Anzahl
        var pSet = b.U16(0);
        b.Patch(pCoverage, b.Position - subtable);
        b.U16(1); // Coverage-Format 1
        b.U16(1);
        b.U16(first);
        b.Patch(pSet, b.Position - subtable);

        var set = b.Position;
        b.U16(1);
        var pLigature = b.U16(0);
        b.Patch(pLigature, b.Position - set);
        b.U16(ligature);
        b.U16(2);
        b.U16(second);
        return b.ToArray();
    }

    /// <summary>SingleSubst (GSUB 1, format 2) for exactly one glyph.</summary>
    private static byte[] SingleSubtable(ushort from, ushort to)
    {
        var b = new OtBuilder();
        var subtable = b.Position;
        b.U16(2); // Format 2
        var pCoverage = b.U16(0);
        b.U16(1); // glyph count
        b.U16(to);
        b.Patch(pCoverage, b.Position - subtable);
        b.U16(1); // Coverage-Format 1
        b.U16(1);
        b.U16(from);
        return b.ToArray();
    }

    /// <summary>cmap format 12 with individual code point/glyph groups.</summary>
    private static byte[] Cmap12Mapped(params (int Code, ushort Glyph)[] mappings)
    {
        var ordered = mappings.OrderBy(mapping => mapping.Code).ToArray();
        var b = new OtBuilder();
        b.U16(0); // Version
        b.U16(1); // table
        b.U16(3); // Windows
        b.U16(10); // full Unicode
        b.U32(12); // offset of the subtable
        b.U16(12); // Format 12
        b.U16(0);
        b.U32(16 + ordered.Length * 12);
        b.U32(0);
        b.U32(ordered.Length);
        foreach (var (code, glyph) in ordered)
        {
            b.U32(code);
            b.U32(code);
            b.U32(glyph);
        }

        return b.ToArray();
    }

    /// <summary>PairPos (GPOS 2, format 1) for exactly one pair.</summary>
    private static byte[] PairPosSubtable(ushort first, ushort second, short xAdvance, short xPlacement)
    {
        var b = new OtBuilder();
        var subtable = b.Position;
        b.U16(1); // Format 1
        var pCoverage = b.U16(0);
        b.U16(0x0005); // ValueFormat1: XPlacement + XAdvance
        b.U16(0x0000); // ValueFormat2
        b.U16(1); // PairSet-Anzahl
        var pPairSet = b.U16(0);
        b.Patch(pCoverage, b.Position - subtable);
        b.U16(1); // Coverage-Format 1
        b.U16(1);
        b.U16(first);
        b.Patch(pPairSet, b.Position - subtable);

        b.U16(1); // PairValue-Anzahl
        b.U16(second);
        b.I16(xPlacement);
        b.I16(xAdvance);
        return b.ToArray();
    }

    /// <summary>GDEF with a GlyphClassDef (format 1) of glyph/class pairs.</summary>
    private static byte[] Gdef(params (ushort Glyph, ushort Class)[] classes)
    {
        var b = new OtBuilder();
        b.U16(1); // Major
        b.U16(0); // Minor
        var pClassDef = b.U16(0);
        b.U16(0); // AttachList
        b.U16(0); // LigCaretList
        b.U16(0); // MarkAttachClassDef
        b.Patch(pClassDef, b.Position);

        var maxGlyph = classes.Max(entry => (int)entry.Glyph);
        b.U16(1); // Format 1
        b.U16(0); // StartGlyph
        b.U16(maxGlyph + 1);
        for (var glyph = 0; glyph <= maxGlyph; glyph++)
        {
            var entry = classes.FirstOrDefault(pair => pair.Glyph == glyph);
            b.U16(entry.Glyph == glyph ? entry.Class : (ushort)0);
        }

        return b.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    /// <summary>Small big-endian buffer with offset patches for the table assembly.</summary>
    private sealed class OtBuilder
    {
        private readonly List<byte> _bytes = [];

        public int Position => _bytes.Count;

        public int U16(int value)
        {
            var position = _bytes.Count;
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
            return position;
        }

        public void I16(short value) => U16((ushort)value);

        public void U32(long value)
        {
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void Tag(string tag)
        {
            foreach (var character in tag)
            {
                _bytes.Add((byte)character);
            }
        }

        public void Bytes(byte[] data) => _bytes.AddRange(data);

        public void Patch(int position, int value)
        {
            _bytes[position] = (byte)(value >> 8);
            _bytes[position + 1] = (byte)value;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
