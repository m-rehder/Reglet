using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// Run formatting of styles (OF-A04, ADR-0010): the paragraph style provides the base, a character
/// style overrides it and the formatting of the run wins. Everything is tri-state, so a level can
/// switch an inherited property off.
/// </summary>
internal static class RunFormatChecks
{
    internal static int Run()
    {
        try
        {
            ResolvesInOrder();
            ReadsTriStateFromParts();
            WritesParagraphStyleFormatting();
            WritesExplicitOffs();
            WritesCharacterStyleToggleOff();
            ReadsStylesWithoutBaking();
            EditsCharacterStyleReachesRuns();
            EditsParagraphStyleReachesParagraphs();
            KeepsExplicitOffsOnRoundTrip();
            Console.WriteLine("Run formatting of styles (OF-A04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Run formatting of styles (OF-A04): " + exception);
            return 1;
        }
    }

    private static ParagraphStyle WarnStyle()
        => ParagraphStyle.Default with
        {
            StyleName = "Warn",
            Bold = true,
            Italic = true,
            TextColor = Color.FromHex("FF0000"),
            FontSize = Length.FromPoints(13),
        };

    private static void ResolvesInOrder()
    {
        var run = new TextRun("x");
        var paragraph = WarnStyle();
        var character = new CharacterStyle("Off") { Bold = false, UnderlineStyle = TextUnderlineStyle.Dotted };

        var resolved = RunFormatResolver.Resolve(run, paragraph, character);
        Require(resolved.Bold == false, "The character style switches the bold of the paragraph style off.");
        Require(resolved.Italic == true, "The paragraph style provides the italic.");
        Require(resolved.UnderlineStyle == TextUnderlineStyle.Dotted, "The character style provides the underline.");
        Require(resolved.Color?.ToString() == Color.FromHex("FF0000").ToString(), "The paragraph style provides the color.");
        Require(resolved.FontSize?.Points == 13, "The paragraph style provides the size.");

        var own = RunFormatResolver.Resolve(
            new TextRun("x") { Bold = true, UnderlineStyle = TextUnderlineStyle.None },
            paragraph,
            character);
        Require(own.Bold == true, "The run wins over the character style.");
        Require(
            own.UnderlineStyle == TextUnderlineStyle.None,
            "The run switches the underline of the character style off.");

        // The single-underline shorthand: the style wins over the paragraph style, the run wins last.
        var shorthand = RunFormatResolver.Resolve(
            new TextRun("x") { Underline = true },
            WarnStyle() with { UnderlineStyle = TextUnderlineStyle.None },
            null);
        Require(shorthand.UnderlineStyle == TextUnderlineStyle.Single, "The run sets a single underline.");
        var inherited = RunFormatResolver.Resolve(new TextRun("x"), WarnStyle(), null);
        Require(inherited.Bold == true && inherited.UnderlineStyle == TextUnderlineStyle.None,
            "An unset property takes the style, a style without underline leaves it off.");

        // Without any base the toggles are "off", not null.
        var plain = RunFormatResolver.Resolve(new TextRun("x"), null, null);
        Require(plain.Bold == false && plain.Italic == false && plain.Hidden == false,
            "Without a base the toggles are off.");
    }

    private static void ReadsTriStateFromParts()
    {
        // A property the source does not set stays null (an explicit off is false, not null).
        var run = new TextRun("x") { Bold = false, Italic = null, Strikethrough = TextStrikethrough.None };
        Require(run.Bold == false && run.Italic is null, "The run keeps 'not set' and 'explicit off' apart.");
        Require(run.IsUnderlined == false, "A run without underline is not underlined.");
        Require(
            new TextRun("x") { UnderlineStyle = TextUnderlineStyle.None }.IsUnderlined == false,
            "An explicit underline none is not underlined.");
        Require(new TextRun("x") { Underline = true }.IsUnderlined, "The shorthand marks the run underlined.");
    }

    private static void WritesParagraphStyleFormatting()
    {
        var document = StyledDocument();

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var styles = ReadZipEntry(docxBuffer.ToArray(), "word/styles.xml");
        var styleStart = styles.IndexOf("w:styleId=\"Warn\"", StringComparison.Ordinal);
        Require(styleStart >= 0, "DOCX: the named style is written.");
        var style = styles[styleStart..];
        style = style[..Math.Max(0, style.IndexOf("</w:style>", StringComparison.Ordinal))];
        Require(style.Contains("<w:b />", StringComparison.Ordinal), "DOCX: the style carries the bold.");
        Require(style.Contains("<w:i />", StringComparison.Ordinal), "DOCX: the style carries the italic.");
        Require(style.Contains("<w:color w:val=\"FF0000\"", StringComparison.Ordinal), "DOCX: the style carries the color.");
        Require(style.Contains("<w:sz w:val=\"26\"", StringComparison.Ordinal), "DOCX: the style carries the size.");

        var body = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(body.Contains("<w:pStyle w:val=\"Warn\" />", StringComparison.Ordinal), "DOCX: the paragraph references the style.");
        Require(!body.Contains("<w:rPr><w:b />", StringComparison.Ordinal), "DOCX: the run does not repeat the style.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var odtStyles = ReadZipEntry(odtBuffer.ToArray(), "styles.xml");
        var odtStart = odtStyles.IndexOf("style:name=\"Warn\"", StringComparison.Ordinal);
        Require(odtStart >= 0, "ODT: the named style is written.");
        var odtStyle = odtStyles[odtStart..];
        odtStyle = odtStyle[..Math.Max(0, odtStyle.IndexOf("</style:style>", StringComparison.Ordinal))];
        Require(odtStyle.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal), "ODT: the style carries the bold.");
        Require(odtStyle.Contains("fo:font-style=\"italic\"", StringComparison.Ordinal), "ODT: the style carries the italic.");
        Require(odtStyle.Contains("fo:color=\"#FF0000\"", StringComparison.Ordinal), "ODT: the style carries the color.");

        using var pdfBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(pdfBuffer.ToArray());
        Require(pdf.Contains("/BaseFont /Helvetica-BoldOblique", StringComparison.Ordinal),
            "PDF: the paragraph style makes the run bold and italic.");
        var content = PdfStreams.Decompress(pdfBuffer.ToArray());
        Require(content.Contains("1 0 0 rg", StringComparison.Ordinal), "PDF: the run takes the color of the style.");
    }

    private static void WritesExplicitOffs()
    {
        // The style sets bold, italic, caps and underline, so the run has to state all four offs.
        var style = WarnStyle() with { AllCaps = true, UnderlineStyle = TextUnderlineStyle.Single };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Inline[]
            {
                new TextRun("plain "),
                new TextRun("upright") { Bold = false, Italic = false, AllCaps = false, UnderlineStyle = TextUnderlineStyle.None },
            })
            {
                Style = style,
            }))
        {
            Styles = [style],
        };

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(body.Contains("<w:b w:val=\"0\" />", StringComparison.Ordinal), "DOCX: an explicit bold off is written.");
        Require(body.Contains("<w:i w:val=\"0\" />", StringComparison.Ordinal), "DOCX: an explicit italic off is written.");
        Require(body.Contains("<w:caps w:val=\"0\" />", StringComparison.Ordinal), "DOCX: an explicit caps off is written.");
        Require(body.Contains("<w:u w:val=\"none\" />", StringComparison.Ordinal), "DOCX: an explicit underline off is written.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(content.Contains("fo:font-weight=\"normal\"", StringComparison.Ordinal), "ODT: an explicit bold off is written.");
        Require(content.Contains("fo:font-style=\"normal\"", StringComparison.Ordinal), "ODT: an explicit italic off is written.");
        Require(content.Contains("fo:text-transform=\"none\"", StringComparison.Ordinal), "ODT: an explicit caps off is written.");
        Require(content.Contains("style:text-underline-style=\"none\"", StringComparison.Ordinal), "ODT: an explicit underline off is written.");

        using var pdfBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(pdfBuffer.ToArray());
        Require(pdf.Contains("/BaseFont /Helvetica-BoldOblique", StringComparison.Ordinal), "PDF: the plain run is bold and italic.");
        Require(pdf.Contains("/BaseFont /Helvetica ", StringComparison.Ordinal), "PDF: the upright run is neither bold nor italic.");
    }

    private static void WritesCharacterStyleToggleOff()
    {
        var character = new CharacterStyle("Upright") { Bold = false };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Inline[]
            {
                new TextRun("bold ") { CharacterStyleName = "Upright" },
            })
            {
                Style = WarnStyle(),
            }))
        {
            Styles = [WarnStyle()],
            CharacterStyles = [character],
        };

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var styles = ReadZipEntry(docxBuffer.ToArray(), "word/styles.xml");
        var start = styles.IndexOf("w:styleId=\"Upright\"", StringComparison.Ordinal);
        Require(start >= 0, "DOCX: the character style is written.");
        var style = styles[start..];
        style = style[..Math.Max(0, style.IndexOf("</w:style>", StringComparison.Ordinal))];
        Require(style.Contains("<w:b w:val=\"0\" />", StringComparison.Ordinal),
            "DOCX: the character style switches the bold off.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var odtStyles = ReadZipEntry(odtBuffer.ToArray(), "styles.xml");
        var odtStart = odtStyles.IndexOf("style:name=\"Upright\"", StringComparison.Ordinal);
        Require(odtStart >= 0, "ODT: the character style is written.");
        var odtStyle = odtStyles[odtStart..];
        odtStyle = odtStyle[..Math.Max(0, odtStyle.IndexOf("</style:style>", StringComparison.Ordinal))];
        Require(odtStyle.Contains("fo:font-weight=\"normal\"", StringComparison.Ordinal),
            "ODT: the character style switches the bold off.");

        using var pdfBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(pdfBuffer.ToArray());
        Require(pdf.Contains("/BaseFont /Helvetica-Oblique", StringComparison.Ordinal),
            "PDF: the character style makes the run italic but not bold.");
        Require(!pdf.Contains("/BaseFont /Helvetica-BoldOblique", StringComparison.Ordinal),
            "PDF: the character style switches the bold of the paragraph style off.");
    }

    /// <summary>
    /// The readers keep the style formatting on the style: a run carries its own formatting only, and
    /// resolving gives the same result as before (ADR-0010, slice 2).
    /// </summary>
    private static void ReadsStylesWithoutBaking()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Basis'><w:rPr><w:b/><w:color w:val='CC0000'/></w:rPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='Quote'><w:basedOn w:val='Basis'/>"
            + "<w:rPr><w:i/></w:rPr></w:style>"
            + "<w:style w:type='character' w:styleId='Small'><w:rPr><w:sz w:val='16'/></w:rPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Quote'/></w:pPr>"
            + "<w:r><w:t>Quote</w:t></w:r>"
            + "<w:r><w:rPr><w:rStyle w:val='Small'/></w:rPr><w:t>small</w:t></w:r>"
            + "<w:r><w:rPr><w:i w:val='0'/></w:rPr><w:t>upright</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var document = new DocxDocumentReader().Read(stream).Document;
        var paragraph = (Paragraph)document.Sections[0].Blocks[0];
        var runs = paragraph.Inlines.OfType<TextRun>().ToArray();

        Require(paragraph.Style.Bold == true && paragraph.Style.Italic == true,
            "The paragraph style carries the inherited run formatting.");
        Require(paragraph.Style.TextColor?.ToString() == "#CC0000", "The paragraph style carries the inherited color.");
        Require(runs[0].Bold is null && runs[0].Color is null, "The run took the formatting of the style.");
        Require(runs[1].CharacterStyleName == "Small" && runs[1].FontSize is null,
            "The character style formatting was baked into the run.");
        Require(document.CharacterStyles.Single().FontSize?.Points == 8, "The character style lost its formatting.");
        Require(runs[2].Italic == false, "The own italic-off of the run is read.");

        // A style of the model is written into its definition, not repeated in every run.
        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var written = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(!written.Contains("<w:b />", StringComparison.Ordinal), "DOCX: the run repeats the style formatting.");
        var writtenStyles = ReadZipEntry(docxBuffer.ToArray(), "word/styles.xml");
        var start = writtenStyles.IndexOf("w:styleId=\"Quote\"", StringComparison.Ordinal);
        var style = writtenStyles[start..];
        style = style[..Math.Max(0, style.IndexOf("</w:style>", StringComparison.Ordinal))];
        // The source definitions are adopted, so the bold stays in the parent style.
        Require(style.Contains("<w:i", StringComparison.Ordinal), "DOCX: the written style lost its run formatting.");
        Require(style.Contains("w:basedOn w:val=\"Basis\"", StringComparison.Ordinal), "DOCX: the written style lost its parent.");
        var basis = writtenStyles.IndexOf("w:styleId=\"Basis\"", StringComparison.Ordinal);
        Require(basis >= 0 && writtenStyles[basis..].Contains("<w:b", StringComparison.Ordinal),
            "DOCX: the parent style lost its run formatting.");
    }

    /// <summary>
    /// A character style is resolved by name at write time, so an edit after a read reaches the runs in
    /// all three formats (ADR-0010, slice 2).
    /// </summary>
    private static void EditsCharacterStyleReachesRuns()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='character' w:styleId='Emphasis2'><w:rPr><w:b/></w:rPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:r><w:rPr><w:rStyle w:val='Emphasis2'/></w:rPr><w:t>Text</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var read = new DocxDocumentReader().Read(stream).Document;

        // The user replaces the character style: no bold, italic instead.
        var edited = read with { CharacterStyles = [new CharacterStyle("Emphasis2") { Italic = true }] };

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(edited, docxBuffer).GetAwaiter().GetResult();
        var docxStyles = ReadZipEntry(docxBuffer.ToArray(), "word/styles.xml");
        var start = docxStyles.IndexOf("w:styleId=\"Emphasis2\"", StringComparison.Ordinal);
        var style = docxStyles[start..];
        style = style[..Math.Max(0, style.IndexOf("</w:style>", StringComparison.Ordinal))];
        Require(style.Contains("<w:i />", StringComparison.Ordinal) && !style.Contains("<w:b />", StringComparison.Ordinal),
            "DOCX: the edited character style is written.");
        var docxBody = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(docxBody.Contains("w:rStyle w:val=\"Emphasis2\"", StringComparison.Ordinal),
            "DOCX: the run still references the style.");
        Require(!docxBody.Contains("<w:b />", StringComparison.Ordinal),
            "DOCX: the run kept the old formatting of the style.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(edited, odtBuffer).GetAwaiter().GetResult();
        var odtStyles = ReadZipEntry(odtBuffer.ToArray(), "styles.xml");
        var odtStart = odtStyles.IndexOf("style:name=\"Emphasis2\"", StringComparison.Ordinal);
        var odtStyle = odtStyles[odtStart..];
        odtStyle = odtStyle[..Math.Max(0, odtStyle.IndexOf("</style:style>", StringComparison.Ordinal))];
        Require(odtStyle.Contains("fo:font-style=\"italic\"", StringComparison.Ordinal),
            "ODT: the edited character style is written.");
        var odtContent = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(odtContent.Contains("parent-style-name=\"Emphasis2\"", StringComparison.Ordinal),
            "ODT: the span references the character style.");
        Require(!odtContent.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal),
            "ODT: the span kept the old formatting of the style.");

        using var pdfBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(edited, pdfBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(pdfBuffer.ToArray());
        Require(pdf.Contains("/BaseFont /Helvetica-Oblique", StringComparison.Ordinal),
            "PDF: the edited character style reaches the run.");
        Require(!pdf.Contains("/BaseFont /Helvetica-BoldOblique", StringComparison.Ordinal),
            "PDF: the run kept the old formatting of the style.");
    }

    /// <summary>A run that switches a toggle of its style off keeps the explicit off on a round trip.</summary>
    private static void KeepsExplicitOffsOnRoundTrip()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Quote'><w:rPr><w:b/></w:rPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Quote'/></w:pPr>"
            + "<w:r><w:t>bold</w:t></w:r>"
            + "<w:r><w:rPr><w:b w:val='0'/></w:rPr><w:t>upright</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var read = new DocxDocumentReader().Read(stream).Document;
        var paragraph = (Paragraph)read.Sections[0].Blocks[0];
        Require(paragraph.Style.Bold == true, "DOCX: the style bold is missing.");
        Require(paragraph.Inlines.OfType<TextRun>().Last().Bold == false, "DOCX: the explicit bold off is not read.");

        // A changed body forces the regenerated path. The paragraph references its style, so the runs
        // inherit the style's bold and only state what they switch off.
        var edited = read with
        {
            Sections =
            [
                read.Sections[0] with
                {
                    Blocks = [new Paragraph(paragraph.Inlines) { Style = paragraph.Style with { StyleName = "Quote" } }],
                },
            ],
        };
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(edited, buffer).GetAwaiter().GetResult();

        var written = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(written.Contains("<w:pStyle w:val=\"Quote\" />", StringComparison.Ordinal),
            "DOCX: the paragraph references its style.");
        Require(written.Contains("<w:b w:val=\"0\" />", StringComparison.Ordinal),
            "DOCX: the explicit bold off of the run is kept: " + written);
        Require(!written.Contains("<w:b />", StringComparison.Ordinal),
            "DOCX: the run repeats the bold of the style.");
    }

    /// <summary>
    /// ADR-0011: a body paragraph references its named style instead of flattening it. An edit of
    /// the style after reading reaches the paragraph in all three formats, and direct formatting
    /// still wins.
    /// </summary>
    private static void EditsParagraphStyleReachesParagraphs()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Quote'>"
            + "<w:pPr><w:jc w:val='center'/><w:spacing w:before='240'/></w:pPr>"
            + "<w:rPr><w:b/></w:rPr>"
            + "</w:style></w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Quote'/></w:pPr><w:r><w:t>inherited</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Quote'/><w:jc w:val='left'/></w:pPr>"
            + "<w:r><w:t>direct</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var read = new DocxDocumentReader().Read(stream).Document;
        var paragraphs = read.Sections[0].Blocks.OfType<Paragraph>().ToArray();
        Require(paragraphs.Length == 2, "Both body paragraphs are read.");
        Require(paragraphs[0].Style.StyleName == "Quote",
            "DOCX: the body paragraph keeps the reference to its named style.");
        Require(paragraphs[0].Style.Alignment == TextAlignment.Center,
            "DOCX: the inherited alignment is resolved on the paragraph.");
        Require(paragraphs[1].Style.Alignment == TextAlignment.Left,
            "DOCX: the direct alignment is read.");

        // The user edits the style: right aligned, 12 pt before, italic instead of bold.
        var edited = read with
        {
            Styles =
            [
                new ParagraphStyle
                {
                    StyleName = "Quote",
                    Alignment = TextAlignment.Right,
                    SpaceBefore = Length.FromPoints(12),
                    Italic = true,
                },
            ],
        };

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(edited, docxBuffer).GetAwaiter().GetResult();
        XNamespace w = W;
        var docx = XDocument.Parse(ReadZipEntry(docxBuffer.ToArray(), "word/document.xml"));
        var written = docx.Descendants(w + "p").Where(p => p.Descendants(w + "t").Any()).ToArray();
        Require(written.Length == 2, "DOCX: both paragraphs are written.");
        Require(written.All(p => (string?)p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val") == "Quote"),
            "DOCX: both paragraphs reference the named style.");
        Require(!written[0].Descendants(w + "jc").Any(),
            "DOCX: the inherited alignment is not repeated as direct formatting.");
        Require(!written[0].Descendants(w + "spacing").Any(),
            "DOCX: the inherited spacing is not repeated as direct formatting.");
        Require((string?)written[1].Descendants(w + "jc").Single().Attribute(w + "val") == "left",
            "DOCX: the direct alignment wins over the edited style.");
        Require(!docx.Descendants(w + "b").Any(),
            "DOCX: the edited, no-longer-bold style is not written as direct bold.");

        var docxStyles = XDocument.Parse(ReadZipEntry(docxBuffer.ToArray(), "word/styles.xml"));
        var zitat = docxStyles.Descendants(w + "style")
            .First(style => (string?)style.Attribute(w + "styleId") == "Quote");
        Require((string?)zitat.Descendants(w + "jc").Single().Attribute(w + "val") == "right",
            "DOCX: the edited alignment is written into the style definition.");
        Require((string?)zitat.Descendants(w + "spacing").Single().Attribute(w + "before") == "240",
            "DOCX: the edited spacing is written into the style definition.");
        Require(!zitat.Descendants(w + "b").Any(), "DOCX: the edited style no longer sets bold.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(edited, odtBuffer).GetAwaiter().GetResult();
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
        XNamespace styleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var odt = XDocument.Parse(ReadZipEntry(odtBuffer.ToArray(), "content.xml"));
        var odtParagraphs = odt.Descendants(text + "p").Where(p => p.Value.Length > 0).ToArray();
        Require((string?)odtParagraphs[0].Attribute(text + "style-name") == "Quote",
            "ODT: the paragraph references the named style.");
        var automaticName = (string?)odtParagraphs[1].Attribute(text + "style-name");
        Require(automaticName is not null and not "Quote",
            "ODT: a paragraph with direct formatting gets an automatic style.");
        var automatic = odt.Descendants(styleNs + "style")
            .First(item => (string?)item.Attribute(styleNs + "name") == automaticName);
        Require((string?)automatic.Element(styleNs + "paragraph-properties")?.Attribute(fo + "text-align") == "start",
            "ODT: the direct alignment wins over the edited style: " + automatic);

        var odtStyles = XDocument.Parse(ReadZipEntry(odtBuffer.ToArray(), "styles.xml"));
        var odtQuote = odtStyles.Descendants(styleNs + "style")
            .First(item => (string?)item.Attribute(styleNs + "name") == "Quote");
        var properties = odtQuote.Element(styleNs + "paragraph-properties");
        Require((string?)properties?.Attribute(fo + "text-align") == "end",
            "ODT: the edited alignment is written into the style definition.");
        Require((string?)properties?.Attribute(fo + "margin-top") == "12pt",
            "ODT: the edited spacing is written into the style definition.");
        Require(odtQuote.Element(styleNs + "text-properties") is null
                || !odtQuote.Element(styleNs + "text-properties")!.Attributes()
                    .Any(attribute => attribute.Value.Contains("bold", StringComparison.Ordinal)),
            "ODT: the edited style no longer sets bold.");

        using var pdfBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(edited, pdfBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(pdfBuffer.ToArray());
        Require(pdf.Contains("/BaseFont /Helvetica-Oblique", StringComparison.Ordinal),
            "PDF: the edited italic style reaches the runs.");
        Require(!pdf.Contains("/BaseFont /Helvetica-BoldOblique", StringComparison.Ordinal),
            "PDF: the style no longer makes the runs bold.");
    }

    private static byte[] DocxPackage(string styles, string body)
    {
        const string header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
        var document = header
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
            + body
            + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\" />"
            + "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" />"
            + "</w:sectPr></w:body></w:document>";
        var stylesXml = header + styles.Replace("xmlns:w='", "xmlns:w=\"", StringComparison.Ordinal)
            .Replace("'", "\"", StringComparison.Ordinal);

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", header
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\" />"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\" />"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\" />"
                + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\" />"
                + "</Types>");
            Write(zip, "_rels/.rels", header
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\" />"
                + "</Relationships>");
            Write(zip, "word/_rels/document.xml.rels", header
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\" />"
                + "</Relationships>");
            Write(zip, "word/document.xml", document);
            Write(zip, "word/styles.xml", stylesXml);
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static PortableDocument StyledDocument()
        => new(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("Warning")) { Style = WarnStyle() }))
        {
            Styles = [WarnStyle()],
        };

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
