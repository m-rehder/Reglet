using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// character styles (phase AI, AI01): a named character style is a reusable set of run
/// formatting. DOCX writes the definition and the reference (<c>w:rStyle</c>), ODT writes a
/// named text style and the reference (<c>style:parent-style-name</c>), the PDF applies the
/// formatting (it has no style mechanism). Both readers read the definitions and the references.
/// </summary>
internal static class CharacterStyleChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxCharacterStyles();
            WritesOdtCharacterStyles();
            AppliesCharacterStyleInPdf();
            ReadsCharacterStylesFromDocx();
            ReadsExtendedDocxCharacterStyles();
            ConvertsExtendedCharacterFormattingToOdt();
            ReadsCharacterStylesFromOdt();
            ReadsOdtNamedTextStyles();
            CharacterStyleEditReachesTheOutput();
            ReadsOdtDuplicateStyleNames();
            ReadsOdtCharacterStyleInheritance();
            ReadsDocxCharacterStyleInheritance();
            WritesOdtCharacterStyleWithBackgroundAndHighlight();
            KeepsTheRunFormatting();
            Console.WriteLine("Character styles (AI01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Character styles (AI01): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(params TextRun[] runs)
    {
        var style = new CharacterStyle("Emphasis")
        {
            Bold = true,
            Italic = true,
            Color = Color.FromHex("CC0000"),
            FontSize = Length.FromPoints(14),
        };

        var paragraph = new Paragraph(runs);
        return new PortableDocument(
            new DocumentMetadata { Title = "Zeichenformatvorlagen" },
            [new Section([paragraph])])
        {
            CharacterStyles = [style],
        };
    }

    private static void WritesDocxCharacterStyles()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var styles = ReadText(archive, "word/styles.xml");
        Assert(styles.Contains("w:type=\"character\""), "The character style is missing in styles.xml.");
        Assert(styles.Contains("w:styleId=\"Emphasis\""), "The style id is missing in styles.xml.");
        Assert(styles.Contains("<w:b"), "The bold attribute is missing in the style.");

        var document = ReadText(archive, "word/document.xml");
        Assert(document.Contains("<w:rStyle w:val=\"Emphasis\""), "The run does not reference the style.");
    }

    private static void WritesOdtCharacterStyles()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var styles = ReadText(archive, "styles.xml");
        Assert(styles.Contains("style:name=\"Emphasis\""), "The named text style is missing.");
        Assert(styles.Contains("style:family=\"text\""), "The text style has the wrong family.");
        Assert(styles.Contains("fo:font-weight=\"bold\""), "The style does not contain the bold attribute.");

        var content = ReadText(archive, "content.xml");
        Assert(content.Contains("style:parent-style-name=\"Emphasis\""), "The run does not reference the style.");
    }

    private static void AppliesCharacterStyleInPdf()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        // The style is bold and italic: the font must be the bold italic one.
        Assert(text.Contains("/BaseFont /Helvetica-BoldOblique"), "The character style was not applied: " + text);

        buffer.Position = 0;
        var pages = new PdfDocumentReader().ReadPageTexts(buffer);
        Assert(pages.Count == 1, "The PDF has no page.");
        Assert(pages[0].Contains("Betonung", StringComparison.Ordinal), "The text is missing: " + pages[0]);
    }

    private static void ReadsCharacterStylesFromDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        var result = new DocxDocumentReader().Read(buffer);
        var document = result.Document;

        Assert(document.CharacterStyles.Count == 1, "The character style was not read.");
        var style = document.CharacterStyles[0];
        Assert(style.Name == "Emphasis", "The style name is wrong: " + style.Name);
        Assert(style.Bold == true, "The bold attribute was not read.");
        Assert(style.Italic == true, "The italic attribute was not read.");
        Assert(style.Color is { } color && color.ToString() == "#CC0000", "The color was not read: " + style.Color);

        var run = document.Sections[0].Blocks
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<TextRun>()
            .First();
        Assert(run.CharacterStyleName == "Emphasis", "The run does not reference the style.");
    }

    private static void ReadsExtendedDocxCharacterStyles()
    {
        var source = Document(new TextRun("Format") { CharacterStyleName = "Extended" }) with
        {
            CharacterStyles =
            [
                new CharacterStyle("Extended")
                {
                    UnderlineStyle = TextUnderlineStyle.Wave,
                    UnderlineColor = Color.FromHex("123456"),
                    Strikethrough = TextStrikethrough.Double,
                    Background = Color.FromHex("ABCDEF"),
                    CharacterSpacing = Length.FromPoints(-1),
                    HorizontalScaling = 85,
                    Kerning = Length.FromPoints(10),
                    Position = Length.FromPoints(2),
                    Border = new ParagraphBorder(BorderStyle.Dashed, Length.FromPoints(1), Color.FromHex("112233")),
                },
            ],
        };
        var read = TestPackages.Read(TestPackages.Write(source, DocumentFormat.Docx), DocumentFormat.Docx);
        var style = read.CharacterStyles.Single();
        Assert(style.UnderlineStyle == TextUnderlineStyle.Wave && style.UnderlineColor?.ToString() == "#123456"
            && style.Strikethrough == TextStrikethrough.Double, "DOCX: named style preserves underline and strikethrough.");
        Assert(style.Background?.ToString() == "#ABCDEF" && style.Border?.Style == BorderStyle.Dashed,
            "DOCX: named style preserves background and border.");
        Assert(style.CharacterSpacing?.Points == -1 && style.HorizontalScaling == 85 && style.Kerning?.Points == 10
            && style.Position?.Points == 2, "DOCX: named style preserves character spacing, scaling, kerning and position.");
        Assert(RunOf(read).CharacterStyleName == "Extended", "DOCX: run retains its style reference.");

        var regenerated = new PortableDocument(read.Metadata, read.Sections) { CharacterStyles = read.CharacterStyles };
        var again = TestPackages.Read(TestPackages.Write(regenerated, DocumentFormat.Docx), DocumentFormat.Docx).CharacterStyles.Single();
        Assert(again.UnderlineStyle == TextUnderlineStyle.Wave && again.CharacterSpacing?.Points == -1
            && again.Border?.Color.ToString() == "#112233", "DOCX: extended style survives regeneration.");

        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var package = TestPackages.Write(source, DocumentFormat.Docx);
        var styles = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/styles.xml")));
        styles.Root!.Add(new System.Xml.Linq.XElement(w + "style", new System.Xml.Linq.XAttribute(w + "type", "character"),
            new System.Xml.Linq.XAttribute(w + "styleId", "Child"),
            new System.Xml.Linq.XElement(w + "basedOn", new System.Xml.Linq.XAttribute(w + "val", "Extended")),
            new System.Xml.Linq.XElement(w + "rPr", new System.Xml.Linq.XElement(w + "b"))));
        package = TestPackages.ReplacePart(package, "word/styles.xml", Encoding.UTF8.GetBytes(styles.ToString()));
        var inherited = TestPackages.Read(package, DocumentFormat.Docx).CharacterStyles.Single(item => item.Name == "Child");
        Assert(inherited.Bold == true && inherited.UnderlineStyle == TextUnderlineStyle.Wave && inherited.Position?.Points == 2,
            "DOCX: inherited character formatting combines parent properties and child overrides.");
    }

    private static void ConvertsExtendedCharacterFormattingToOdt()
    {
        var original = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("Named") { CharacterStyleName = "Extended" },
            new TextRun(" Direct") { UnderlineStyle = TextUnderlineStyle.Double, UnderlineColor = Color.FromHex("AABBCC"),
                Strikethrough = TextStrikethrough.Double, CharacterSpacing = Length.FromPoints(1), HorizontalScaling = 120,
                FontSize = Length.FromPoints(10), Position = Length.FromPoints(-1),
                Border = new ParagraphBorder(BorderStyle.Dashed, Length.FromPoints(1), Color.FromHex("112233")) },
            new TextRun(" Super") { Baseline = TextBaseline.Superscript },
            new TextRun(" Dashed") { UnderlineStyle = TextUnderlineStyle.Dashed },
        })))
        {
            CharacterStyles = [new CharacterStyle("Extended")
            {
                UnderlineStyle = TextUnderlineStyle.Wave, UnderlineColor = Color.FromHex("123456"),
                Strikethrough = TextStrikethrough.Double, Background = Color.FromHex("ABCDEF"),
                CharacterSpacing = Length.FromPoints(-1), HorizontalScaling = 85,
                FontSize = Length.FromPoints(10), Position = Length.FromPoints(2), Kerning = Length.FromPoints(10),
                Border = new ParagraphBorder(BorderStyle.Dotted, Length.FromPoints(1), Color.FromHex("223344")),
            }],
        };
        var docx = TestPackages.Read(TestPackages.Write(original, DocumentFormat.Docx), DocumentFormat.Docx);
        var converted = new PortableDocument(docx.Metadata, docx.Sections) { CharacterStyles = docx.CharacterStyles };
        var package = TestPackages.Write(converted, DocumentFormat.Odt);
        var odt = TestPackages.Read(package, DocumentFormat.Odt);
        var style = odt.CharacterStyles.Single();
        Assert(style.UnderlineStyle == TextUnderlineStyle.Wave && style.UnderlineColor?.ToString() == "#123456"
            && style.Strikethrough == TextStrikethrough.Double && style.Background?.ToString() == "#ABCDEF",
            "ODT: converted character style preserves underline, strikethrough and background.");
        Assert(style.CharacterSpacing?.Points == -1 && style.HorizontalScaling == 85 && style.Border?.Style == BorderStyle.Dotted,
            "ODT: converted character style preserves spacing, scaling and border.");
        Assert(style.Position?.Points == 2, "ODT: named character style preserves its numeric baseline shift when font size is specified.");
        var styleXml = Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml"));
        Assert(styleXml.Contains("style:letter-kerning=\"true\"", StringComparison.Ordinal),
            "ODT: a kerning threshold is represented as enabled kerning.");
        var runs = odt.Sections[0].Blocks.OfType<Paragraph>().Single().Inlines.OfType<TextRun>().ToArray();
        var direct = runs.Single(run => run.Text.Contains("Direct", StringComparison.Ordinal));
        Assert(direct.UnderlineStyle == TextUnderlineStyle.Double && direct.UnderlineColor?.ToString() == "#AABBCC"
            && direct.Strikethrough == TextStrikethrough.Double && direct.CharacterSpacing?.Points == 1
            && direct.HorizontalScaling == 120 && direct.Border?.Style == BorderStyle.Dashed && direct.Position?.Points == -1,
            "ODT: direct run formatting survives DOCX-to-ODT conversion.");
        Assert(runs.Single(run => run.Text.Contains("Super", StringComparison.Ordinal)).Baseline == TextBaseline.Superscript,
            "ODT: a superscript with a size percentage is read correctly.");
        Assert(runs.Single(run => run.Text.Contains("Dashed", StringComparison.Ordinal)).UnderlineStyle == TextUnderlineStyle.Dashed,
            "ODT: dashed underlining survives conversion.");
        var content = Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml"));
        Assert(content.Contains("style:text-underline-type=\"double\"", StringComparison.Ordinal),
            "ODT: double underline uses the standard type attribute.");
        Assert(content.Contains("style:text-underline-style=\"dash\"", StringComparison.Ordinal),
            "ODT: dashed underline uses the standard style value.");
    }

    /// <summary>ODT writes the definitions and the references; a read gets both back.</summary>
    private static void ReadsCharacterStylesFromOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        var document = ReadOdt(buffer.ToArray());

        Assert(document.CharacterStyles.Count == 1, "The character style was not read.");
        AssertStyle(document.CharacterStyles[0]);
        Assert(RunOf(document).CharacterStyleName == "Emphasis", "The run does not reference the style.");

        // The reference alone must be enough: the PDF resolves the style definition.
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Assert(Encoding.Latin1.GetString(pdf.ToArray()).Contains("Helvetica-BoldOblique"),
            "The style read from the ODT is not applied.");
    }

    /// <summary>
    /// A foreign ODT references a named text style directly from the span, through an automatic
    /// style that inherits from it (<c>style:parent-style-name</c>), or through the attribute an
    /// older version of this library wrote (<c>style:text-style-name</c>).
    /// </summary>
    private static void ReadsOdtNamedTextStyles()
    {
        var document = ReadOdt(OdtPackage(
            "<office:automatic-styles>"
            + "<style:style style:name='T1' style:family='text' style:parent-style-name='Emphasis'/>"
            + "<style:style style:name='T2' style:family='text' style:text-style-name='Emphasis'/>"
            + "</office:automatic-styles>"
            + "<office:body><office:text><text:p>"
            + "<text:span text:style-name='T1'>indirekt</text:span>"
            + "<text:span text:style-name='T2'>alt</text:span>"
            + "<text:span text:style-name='Emphasis'>direkt</text:span>"
            + "<text:span text:style-name='Spezial'>speziell</text:span>"
            + "</text:p></office:text></office:body>",
            "<office:styles>"
            + "<style:style style:name='Emphasis' style:family='text'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-style='italic'"
            + " fo:color='#CC0000' fo:font-size='14pt'/>"
            + "</style:style>"
            + "<style:style style:name='Spezial' style:family='text'>"
            + "<style:text-properties style:text-line-through-style='solid'"
            + " style:text-position='super' fo:text-transform='uppercase'"
            + " fo:font-variant='small-caps' text:display='none'/>"
            + "</style:style></office:styles>"));

        Assert(document.CharacterStyles.Count == 2, "The character styles were not read.");
        AssertStyle(document.CharacterStyles[0]);

        var special = document.CharacterStyles[1];
        Assert(special.Name == "Spezial", "The second style is wrong: " + special.Name);
        Assert(special.Strikethrough == TextStrikethrough.Single, "The strikethrough was not read.");
        Assert(special.Baseline == TextBaseline.Superscript, "The baseline was not read.");
        Assert(special.AllCaps == true && special.SmallCaps == true, "The casing was not read.");
        Assert(special.Hidden == true, "The hidden attribute was not read.");

        var runs = ((Paragraph)document.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().ToArray();
        Assert(runs.Length == 4, "The runs were not read: " + runs.Length);
        Assert(runs[0].CharacterStyleName == "Emphasis", "The parent link was not read.");
        Assert(runs[1].CharacterStyleName == "Emphasis", "The legacy reference was not read.");
        Assert(runs[2].CharacterStyleName == "Emphasis", "The direct reference was not read.");
        Assert(runs[3].CharacterStyleName == "Spezial", "The second reference was not read.");
        // Only the reference: the formatting belongs to the style, so a later style change
        // reaches the run (as in DOCX).
        Assert(runs[2].Bold != true && runs[2].Italic != true, "The style formatting was copied into the run.");
    }

    /// <summary>A style edit after reading reaches the runs.</summary>
    private static void CharacterStyleEditReachesTheOutput()
    {
        var source = ReadOdt(OdtPackage(
            "<office:body><office:text><text:p>"
            + "<text:span text:style-name='Emphasis'>Betonung</text:span>"
            + "</text:p></office:text></office:body>",
            "<office:styles><style:style style:name='Emphasis' style:family='text'>"
            + "<style:text-properties fo:font-weight='bold'/>"
            + "</style:style></office:styles>"));

        // The user turns the style bold off.
        var edited = source with { CharacterStyles = [new CharacterStyle("Emphasis")] };

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(edited, pdf).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(pdf.ToArray());
        Assert(!text.Contains("Helvetica-Bold"), "The PDF still applies the old style: " + text);

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(edited, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var styles = ReadText(archive, "styles.xml");
        Assert(styles.Contains("style:name=\"Emphasis\""), "The edited style is missing.");
        var start = styles.IndexOf("style:name=\"Emphasis\"", StringComparison.Ordinal);
        var end = styles.IndexOf("</style:style>", start, StringComparison.Ordinal);
        var definition = styles[start..end];
        Assert(!definition.Contains("font-weight"), "The old style definition was kept: " + definition);
    }

    /// <summary>Duplicate style names do not reject the file; the last definition wins.</summary>
    private static void ReadsOdtDuplicateStyleNames()
    {
        var document = ReadOdt(OdtPackage(
            "<office:body><office:text><text:p>Text</text:p></office:text></office:body>",
            "<office:styles>"
            + "<style:style style:name='A' style:family='text'>"
            + "<style:text-properties fo:font-weight='bold'/></style:style>"
            + "<style:style style:name='A' style:family='text'>"
            + "<style:text-properties fo:font-style='italic'/></style:style>"
            + "</office:styles>"));

        Assert(document.CharacterStyles.Count == 2, "A duplicate definition was dropped.");
        Assert(document.CharacterStyles[0].Bold == true, "The first definition lost its formatting.");
        Assert(document.CharacterStyles[1].Italic == true, "The second definition lost its formatting.");
    }

    /// <summary>A named text style inherits the properties of its parent style.</summary>
    private static void ReadsOdtCharacterStyleInheritance()
    {
        var document = ReadOdt(OdtPackage(
            "<office:body><office:text><text:p>Text</text:p></office:text></office:body>",
            "<office:styles>"
            + "<style:style style:name='Basis' style:family='text'>"
            + "<style:text-properties fo:font-weight='bold' fo:color='#00AA00'/></style:style>"
            + "<style:style style:name='Abgeleitet' style:family='text' style:parent-style-name='Basis'>"
            + "<style:text-properties fo:font-style='italic' fo:color='#CC0000'/></style:style>"
            + "</office:styles>"));

        var derived = document.CharacterStyles.Single(style => style.Name == "Abgeleitet");
        Assert(derived.Bold == true, "The inherited bold attribute is missing.");
        Assert(derived.Italic == true, "The own italic attribute is missing.");
        Assert(derived.Color is { } color && color.ToString() == "#CC0000", "The own color must win: " + derived.Color);
    }

    /// <summary>A character style inherits the formatting of its parent style (w:basedOn).</summary>
    private static void ReadsDocxCharacterStyleInheritance()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='character' w:styleId='Basis'><w:rPr><w:b/><w:color w:val='00AA00'/></w:rPr></w:style>"
            + "<w:style w:type='character' w:styleId='Abgeleitet'><w:basedOn w:val='Basis'/>"
            + "<w:rPr><w:i/><w:caps/></w:rPr></w:style>"
            + "</w:styles>";

        using var stream = new MemoryStream(DocxPackage(styles));
        var document = new DocxDocumentReader().Read(stream).Document;

        var derived = document.CharacterStyles.Single(style => style.Name == "Abgeleitet");
        Assert(derived.Bold == true, "The inherited bold attribute is missing.");
        Assert(derived.Italic == true, "The own italic attribute is missing.");
        Assert(derived.AllCaps == true, "The own caps attribute is missing.");
        Assert(derived.Color is { } color && color.ToString() == "#00AA00", "The inherited color is missing: " + derived.Color);
    }

    /// <summary>Builds a DOCX package with a paragraph that uses the style "Child".</summary>
    private static byte[] DocxPackage(string stylesXml)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "word/document.xml",
                "<w:document xmlns:w='" + W + "'><w:body>"
                + "<w:p><w:pPr><w:pStyle w:val='Child'/></w:pPr><w:r><w:t>Text</w:t></w:r></w:p>"
                + "</w:body></w:document>");
            WriteEntry(archive, "word/styles.xml", stylesXml);
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    /// <summary>ODF has one background color; writing a style with both must not write it twice.</summary>
    private static void WritesOdtCharacterStyleWithBackgroundAndHighlight()
    {
        var highlight = Color.FromHex("00FF00");
        var document = Document(new TextRun("Markiert") { CharacterStyleName = "Marker" }) with
        {
            CharacterStyles =
            [
                new CharacterStyle("Marker") { Background = Color.FromHex("FFFF00"), Highlight = highlight },
            ],
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var styles = ReadText(archive, "styles.xml");
        var occurrences = styles.Split("fo:background-color").Length - 1;
        Assert(occurrences == 1, "The background color was written " + occurrences + " times.");
        Assert(styles.Contains(highlight.ToString()), "The highlight color is missing.");
    }

    private static void AssertStyle(CharacterStyle style)
    {
        Assert(style.Name == "Emphasis", "The style name is wrong: " + style.Name);
        Assert(style.Bold == true, "The bold attribute was not read.");
        Assert(style.Italic == true, "The italic attribute was not read.");
        Assert(style.Color is { } color && color.ToString() == "#CC0000", "The color was not read: " + style.Color);
        Assert(style.FontSize is { } size && size.Points == 14, "The font size was not read: " + style.FontSize);
    }

    private static TextRun RunOf(PortableDocument document)
        => document.Sections[0].Blocks
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<TextRun>()
            .First();

    private static PortableDocument ReadOdt(byte[] package)
    {
        using var stream = new MemoryStream(package);
        return new OdtDocumentReader().Read(stream).Document;
    }

    /// <summary>Builds a minimal ODT package (only the parts the reader needs).</summary>
    private static byte[] OdtPackage(string body, string stylesXml)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "content.xml",
                "<office:document-content"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'>"
                + body + "</office:document-content>");
            WriteEntry(archive, "styles.xml",
                "<office:document-styles"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
                + stylesXml + "</office:document-styles>");
        }

        return buffer.ToArray();
    }


    private static void KeepsTheRunFormatting()
    {
        // The run formatting wins over the style: the style is bold, the run is not italic.
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(new TextRun("Betonung") { CharacterStyleName = "Emphasis" }), buffer)
            .GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        Assert(text.Contains("/BaseFont /Helvetica-BoldOblique"), "The style was not applied.");
    }

    private static string ReadText(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException("The part is missing: " + name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
