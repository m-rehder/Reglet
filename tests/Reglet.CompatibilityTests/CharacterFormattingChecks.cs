using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// character formatting (phase W, W01–W06): superscript/subscript, strikethrough,
/// casing, background/highlight, underline style/color,
/// kerning, character spacing, scaling and baseline shift in DOCX, ODT and PDF.
/// </summary>
internal static class CharacterFormattingChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxRunProperties();
            WritesOdtTextProperties();
            WritesPdfTextEffects();
            WritesInheritedBaselineShift();
            WritesExplicitCharacterOffsets();
            ReadsOdtCharacterFormatting();
            ReadsOdtNamedBaselineShift();
            ReportsHighlightBackgroundConflict();
            WritesDocxHighlight();
            WritesZeroKerning();
            WritesHiddenText();
            WritesCharacterBorder();
            RejectsInvalidValues();
            Console.WriteLine("Character formatting (W): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Character formatting: " + exception);
            return 1;
        }
    }

    private static TextRun DecoratedRun() => new("Test")
    {
        AllCaps = true,
        SmallCaps = true,
        Strikethrough = TextStrikethrough.Double,
        Baseline = TextBaseline.Superscript,
        Highlight = Color.FromHex("FFFF00"),
        UnderlineStyle = TextUnderlineStyle.Dashed,
        UnderlineColor = Color.FromHex("FF0000"),
        CharacterSpacing = Length.FromPoints(1.5),
        HorizontalScaling = 125,
        Kerning = Length.FromPoints(10),
    };

    private static TextRun PositionRun() => new("Pos")
    {
        FontSize = Length.FromPoints(10),
        Position = Length.FromPoints(3),
    };

    private static PortableDocument DecoratedDocument()
        => new(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { DecoratedRun(), PositionRun() })));

    private static void WritesDocxRunProperties()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(body.Contains("<w:caps", StringComparison.Ordinal), "DOCX: AllCaps.");
        Require(body.Contains("<w:smallCaps", StringComparison.Ordinal), "DOCX: SmallCaps.");
        Require(body.Contains("<w:dstrike", StringComparison.Ordinal), "DOCX: double strikethrough.");
        Require(body.Contains("w:vertAlign w:val=\"superscript\"", StringComparison.Ordinal), "DOCX: Superscript.");
        Require(body.Contains("w:position w:val=\"6\"", StringComparison.Ordinal), "DOCX: baseline shift (3 pt = 6).");
        Require(body.Contains("<w:highlight w:val=\"yellow\"", StringComparison.Ordinal) && !body.Contains("w:shd", StringComparison.Ordinal), "DOCX: highlight as Word's marker.");
        Require(body.Contains("w:u w:val=\"dash\"", StringComparison.Ordinal), "DOCX: dashed underline.");
        Require(body.Contains("w:u w:val=\"dash\" w:color=\"FF0000\"", StringComparison.Ordinal), "DOCX: underline colour.");
        Require(body.Contains("w:spacing w:val=\"30\"", StringComparison.Ordinal), "DOCX: character spacing (1.5 pt = 30).");
        Require(body.Contains("w:w w:val=\"125\"", StringComparison.Ordinal), "DOCX: horizontal scaling.");
        Require(body.Contains("w:kern w:val=\"20\"", StringComparison.Ordinal), "DOCX: Kerning (10 pt = 20).");
    }

    private static void WritesOdtTextProperties()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("fo:text-transform=\"uppercase\"", StringComparison.Ordinal), "ODT: AllCaps.");
        Require(content.Contains("fo:font-variant=\"small-caps\"", StringComparison.Ordinal), "ODT: SmallCaps.");
        Require(content.Contains("style:text-line-through-style=\"solid\"", StringComparison.Ordinal), "ODT: strikethrough.");
        Require(content.Contains("style:text-line-through-type=\"double\"", StringComparison.Ordinal), "ODT: double strikethrough.");
        Require(content.Contains("style:text-position=\"super 58%\"", StringComparison.Ordinal), "ODT: Superscript.");
        Require(content.Contains("style:text-position=\"30%\"", StringComparison.Ordinal), "ODT: baseline shift (3 pt / 10 pt = 30 %).");
        Require(content.Contains("fo:background-color=\"#FFFF00\"", StringComparison.Ordinal), "ODT: background colour.");
        Require(content.Contains("style:text-underline-style=\"dash\"", StringComparison.Ordinal), "ODT: dashed underline.");
        Require(content.Contains("style:text-underline-color=\"#FF0000\"", StringComparison.Ordinal), "ODT: underline colour.");
        Require(content.Contains("fo:letter-spacing=\"1.5pt\"", StringComparison.Ordinal), "ODT: character spacing.");
        Require(content.Contains("style:text-scale=\"125%\"", StringComparison.Ordinal), "ODT: horizontal scaling.");
        Require(content.Contains("style:letter-kerning=\"true\"", StringComparison.Ordinal), "ODT: Kerning.");
    }

    /// <summary>
    /// ODF states a baseline shift as a percentage of the font size. The run often inherits the
    /// size from its paragraph style or the document defaults, so the writer must use that size
    /// instead of assuming 11 pt.
    /// </summary>
    private static void WritesInheritedBaselineShift()
    {
        var fromParagraph = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("x")
            {
                Position = Length.FromPoints(3),
            })
            {
                Style = new ParagraphStyle { FontSize = Length.FromPoints(10) },
            }));
        using var paragraphBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(fromParagraph, paragraphBuffer).GetAwaiter().GetResult();
        var paragraphContent = ReadZipEntry(paragraphBuffer.ToArray(), "content.xml");
        Require(
            paragraphContent.Contains("style:text-position=\"30%\"", StringComparison.Ordinal),
            "ODT: an inherited paragraph font size is used for the baseline shift (expected 30 %).");

        var fromDefaults = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("x") { Position = Length.FromPoints(3) })))
        {
            Defaults = new DocumentDefaults { FontSize = Length.FromPoints(10) },
        };
        using var defaultsBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(fromDefaults, defaultsBuffer).GetAwaiter().GetResult();
        var defaultsContent = ReadZipEntry(defaultsBuffer.ToArray(), "content.xml");
        Require(
            defaultsContent.Contains("style:text-position=\"30%\"", StringComparison.Ordinal),
            "ODT: an inherited document font size is used for the baseline shift (expected 30 %).");
    }

    /// <summary>An explicit zero must reach the file so it can switch off an inherited value.</summary>
    private static void WritesExplicitCharacterOffsets()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x")
        {
            CharacterSpacing = Length.Zero,
            HorizontalScaling = 100,
            Position = Length.Zero,
        })
        {
            Style = new ParagraphStyle
            {
                FontSize = Length.FromPoints(10),
                CharacterSpacing = Length.FromPoints(2),
                HorizontalScaling = 125,
                Position = Length.FromPoints(2),
            },
        }));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("fo:letter-spacing=\"0pt\"", StringComparison.Ordinal), "ODT: explicit zero character spacing.");
        Require(content.Contains("style:text-scale=\"100%\"", StringComparison.Ordinal), "ODT: explicit 100 % scaling.");
        Require(content.Contains("style:text-position=\"0%\"", StringComparison.Ordinal), "ODT: explicit zero baseline shift.");
    }

    /// <summary>Reading an ODT resolves kerning, spacing and a numeric shift against the style chain.</summary>
    private static void ReadsOdtCharacterFormatting()
    {
        var document = ReadOdt(
            "<office:automatic-styles>"
            + "<style:style style:name='P1' style:family='paragraph'>"
            + "<style:text-properties fo:font-size='10pt'/></style:style>"
            + "<style:style style:name='T1' style:family='text'>"
            + "<style:text-properties style:text-position='30%' style:letter-kerning='true'"
            + " fo:letter-spacing='normal'/></style:style>"
            + "</office:automatic-styles>"
            + "<office:body><office:text><text:p text:style-name='P1'>"
            + "<text:span text:style-name='T1'>x</text:span>"
            + "</text:p></office:text></office:body>",
            string.Empty);

        var run = ((Paragraph)document.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().Single();
        Require(run.Position is { } position && Math.Abs(position.Points - 3) < 1e-9,
            "ODT: the numeric baseline shift uses the inherited font size.");
        Require(run.Kerning is { } kerning && kerning.Points == 0,
            "ODT: letter-kerning true becomes a threshold of 0 (kerning from every size).");
        Require(run.CharacterSpacing is { } spacing && spacing.Points == 0,
            "ODT: letter-spacing normal becomes explicit zero.");

        // Without any font size the document default is the fallback, so the shift is not dropped.
        var fallback = ReadOdt(
            "<office:automatic-styles>"
            + "<style:style style:name='T1' style:family='text'>"
            + "<style:text-properties style:text-position='30%'/></style:style>"
            + "</office:automatic-styles>"
            + "<office:body><office:text><text:p>"
            + "<text:span text:style-name='T1'>x</text:span>"
            + "</text:p></office:text></office:body>",
            string.Empty);
        var fallbackRun = ((Paragraph)fallback.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().Single();
        Require(fallbackRun.Position is { } fallbackPosition
            && Math.Abs(fallbackPosition.Points - 3.3) < 1e-9,
            "ODT: a numeric baseline shift falls back to the document font size.");
    }

    /// <summary>A named character style provides the font size for a numeric shift of its child.</summary>
    private static void ReadsOdtNamedBaselineShift()
    {
        var document = ReadOdt(
            "<office:automatic-styles>"
            + "<style:style style:name='T1' style:family='text' style:parent-style-name='Emphasis'>"
            + "<style:text-properties style:text-position='50%'/></style:style>"
            + "</office:automatic-styles>"
            + "<office:body><office:text><text:p>"
            + "<text:span text:style-name='T1'>x</text:span>"
            + "</text:p></office:text></office:body>",
            "<office:styles><style:style style:name='Emphasis' style:family='text'>"
            + "<style:text-properties fo:font-size='20pt'/></style:style></office:styles>");

        var run = ((Paragraph)document.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().Single();
        Require(run.CharacterStyleName == "Emphasis", "ODT: the named style reference was not read.");
        Require(run.Position is { } position && Math.Abs(position.Points - 10) < 1e-9,
            "ODT: the named style's font size resolves the numeric baseline shift.");
    }

    /// <summary>ODF has one background color; a highlight and a background cannot both be kept.</summary>
    private static void ReportsHighlightBackgroundConflict()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x")
        {
            Highlight = Color.FromHex("FFFF00"),
            Background = Color.FromHex("FF0000"),
        })));

        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("background", StringComparison.Ordinal)),
            "ODT: a distinct highlight and background are reported.");
    }

    /// <summary>A marker colour is w:highlight beside the shading; another colour falls back to shading.</summary>
    private static void WritesDocxHighlight()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("a") { Highlight = Color.FromHex("FF0000"), Background = Color.FromHex("00FF00") },
            new TextRun("b") { Highlight = Color.FromHex("123456") },
            new TextRun("c") { Highlight = Color.FromHex("123456"), Bold = true },
        })));

        using var docx = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        var body = ReadZipEntry(docx.ToArray(), "word/document.xml");
        var marker = body.IndexOf("<w:highlight w:val=\"red\"", StringComparison.Ordinal);
        Require(marker >= 0 && body.IndexOf("w:fill=\"00FF00\"", StringComparison.Ordinal) > marker,
            "DOCX: a marker colour and a background are both written, in schema order.");
        Require(body.Contains("w:fill=\"123456\"", StringComparison.Ordinal), "DOCX: an unnamed highlight falls back to shading.");
        Require(result.Warnings.Count(warning => warning.Message.Contains("123456", StringComparison.Ordinal)) == 1,
            "DOCX: the shading fallback is reported once, however many runs use the colour.");

        using var read = new MemoryStream(docx.ToArray());
        var runs = ((Paragraph)new DocxDocumentReader().Read(read).Document.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().ToList();
        Require(runs[0].Highlight == Color.FromHex("FF0000") && runs[0].Background == Color.FromHex("00FF00"),
            "DOCX: w:highlight is read back as the highlight, w:shd as the background.");
    }

    /// <summary>A zero kerning threshold survives the DOCX writer and reader.</summary>
    private static void WritesZeroKerning()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new Paragraph(new TextRun("x") { Kerning = Length.Zero })));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(docx.ToArray(), "word/document.xml").Contains("w:kern w:val=\"0\"", StringComparison.Ordinal),
            "DOCX: a zero kerning threshold (kerning from every size) is written.");

        using var read = new MemoryStream(docx.ToArray());
        var run = ((Paragraph)new DocxDocumentReader().Read(read).Document.Sections[0].Blocks[0])
            .Inlines.OfType<TextRun>().Single();
        Require(run.Kerning is { } kerning && kerning.Points == 0, "DOCX: a zero kerning threshold is read back.");
    }

    private static void WritesHiddenText()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("Visible"),
            new TextRun("Secret") { Hidden = true },
        })));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(docx.ToArray(), "word/document.xml").Contains("<w:vanish", StringComparison.Ordinal),
            "DOCX: hidden text (w:vanish).");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(odt.ToArray(), "content.xml").Contains("text:display=\"none\"", StringComparison.Ordinal),
            "ODT: hidden text (text:display none).");

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains("(Visible)", StringComparison.Ordinal), "PDF: visible text is rendered.");
        Require(!content.Contains("(Secret)", StringComparison.Ordinal), "PDF: hidden text is omitted.");
    }

    private static void WritesCharacterBorder()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new Paragraph(new TextRun("Boxed") { Border = new ParagraphBorder() })));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(docx.ToArray(), "word/document.xml").Contains("<w:bdr", StringComparison.Ordinal),
            "DOCX: character border (w:bdr).");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(odt.ToArray(), "content.xml").Contains("fo:border=\"0.5pt solid #000000\"", StringComparison.Ordinal),
            "ODT: character border (fo:border).");

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains("re\nS", StringComparison.Ordinal), "PDF: character border is stroked.");
    }

    private static void WritesPdfTextEffects()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("125 Tz", StringComparison.Ordinal), "PDF: horizontal scaling (Tz).");
        Require(content.Contains("1.5 Tc", StringComparison.Ordinal), "PDF: character spacing (Tc).");
        Require(content.Contains(" re\n", StringComparison.Ordinal) && content.Contains("\nf\n", StringComparison.Ordinal), "PDF: background rectangle.");
        Require(content.Contains(" d\n", StringComparison.Ordinal), "PDF: dashed line (dash pattern).");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { Baseline = (TextBaseline)7 });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { Strikethrough = (TextStrikethrough)7 });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { UnderlineStyle = (TextUnderlineStyle)7 });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { Kerning = Length.FromPoints(-1) });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { HorizontalScaling = 0 });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("x") { HorizontalScaling = double.NaN });
    }

    private static PortableDocument ReadOdt(string body, string stylesXml)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, "content.xml",
                "<office:document-content"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
                + " xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
                + body + "</office:document-content>");
            WriteZipEntry(archive, "styles.xml",
                "<office:document-styles"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
                + stylesXml + "</office:document-styles>");
        }

        using var stream = new MemoryStream(buffer.ToArray());
        return new OdtDocumentReader().Read(stream).Document;
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void Throws<TException>(Action action) where TException : Exception
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
}
