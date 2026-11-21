using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// regression tests for fixed bugs. Each check guards exactly one bug that
/// previously slipped through the other suites unnoticed.
/// </summary>
internal static class RegressionChecks
{
    internal static int Run()
    {
        try
        {
            XmlTextRejectsLoneSurrogates();
            PackagePathRejectsBackslashesAndDotSegments();
            PngMetadataRejectsNonPositiveSize();
            PdfPngDecoderStopsAtExpectedSize();
            TrueTypeRejectsInvalidUnitsPerEm();
            PdfMapsSansSerifToHelvetica();
            PdfTextStaysBelowPreviousBlock();
            PdfIndentsNestedLists();
            PdfListMarkerSharesBaselineWithText();
            PdfDrawsListInHeader();
            PdfDropsWhitespaceAtSoftWraps();
            PdfRendersEmptyParagraphAsBlankLine();
            PdfAlignsTableCells();
            DocxRestartsNumberingPerList();
            DocxDerivesListLevelFromNesting();
            DocxUsesUniqueDrawingIds();
            DocxCleansAltText();
            DocxKeepsParagraphAlignmentInLeftCells();
            DocxEndsCellsWithParagraph();
            DocxWritesParagraphInEmptyHeader();
            DocxWritesStructuredBlocksInHeader();
            DocxWritesImagesInHeaderWithOwnRelationships();
            DocxReportsPageBreaksInHeader();
            DocxDoesNotInheritHeaderIntoLaterSection();
            DocxEndsCellWithParagraphAfterListTable();
            UsesSameParagraphSpacingInAllFormats();
            PdfKeepsHeadingWithNextLine();
            UsesSameTableCellPaddingInAllFormats();
            OdtHeadingsKeepWithNext();
            OdtWritesHeaderAndFooter();
            OdtPlacesBandsLikeDocx();
            OdtAttachesBreaksToFollowingBlock();
            ResolvesGenericFontFamilies();
            DropsSpaceBeforeAtTopOfContinuedPage();
            TakesTheLargerSpaceBetweenParagraphs();
            StartsColumnsBelowAContinuousBreak();
            DocxDeclaresCurrentCompatibilityMode();
            UsesSameTableBordersInAllFormats();
            OdtDeclaresFontFaces();
            OdtAppliesCellAlignment();
            FacadeDeletesPartialFileOnFailure();
            Console.WriteLine("Regression: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Regression: " + exception);
            return 1;
        }
    }

    private static void XmlTextRejectsLoneSurrogates()
    {
        const string lone = "A\uD800B";
        Require(XmlText.Clean(lone, InvalidXmlCharacterBehavior.Replace) == "A�B", "A single surrogate is replaced.");
        Throws<DocumentValidationException>(() => XmlText.Clean(lone, InvalidXmlCharacterBehavior.Fail));
        Require(XmlText.Clean("\U0001F600", InvalidXmlCharacterBehavior.Fail) == "\U0001F600", "A valid pair is preserved.");

        var document = PortableDocument.Create().Paragraph(lone).Build();
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(buffer.Length > 0, "DOCX with a single surrogate is written.");
    }

    private static void PackagePathRejectsBackslashesAndDotSegments()
    {
        Throws<DocumentValidationException>(() => PackagePath.Validate("word\\..\\..\\x.xml"));
        Throws<DocumentValidationException>(() => PackagePath.Validate("word/./document.xml"));
    }

    private static void PngMetadataRejectsNonPositiveSize()
    {
        var png = CreatePng(-5, 10);
        Throws<ImageException>(() => ImageGuard.Check(new ImageBlock(png, ImageMediaType.Png), ResourceLimits.Default));
    }

    private static void PdfPngDecoderStopsAtExpectedSize()
    {
        // 1×1 pixel, but IDAT decompresses to 64 MiB: read only the expected 4 bytes.
        var png = CreatePng(1, 1, rawLength: 64 * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = PdfPngDecoder.Decode(png);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(decoded.Samples.Length == 3, "One RGB pixel decoded.");
        Require(allocated < 16 * 1024 * 1024, $"Decoding stays small ({allocated} bytes).");

        Throws<ImageException>(() => PdfPngDecoder.Decode(CreatePng(4, 4, rawLength: 3)));
    }

    private static void TrueTypeRejectsInvalidUnitsPerEm()
    {
        var font = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(0, 0, 0, 0, 0)),
            ("hhea", SyntheticTrueTypeFont.Empty(36)),
            ("maxp", SyntheticTrueTypeFont.Empty(6)),
            ("hmtx", SyntheticTrueTypeFont.Empty(4)),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));
        Throws<FontException>(() => TrueTypeFont.Load(font));
    }

    private static void PdfMapsSansSerifToHelvetica()
    {
        Require(PdfStandardFonts.Resolve("sans-serif", false, false) == "Helvetica", "sans-serif is Helvetica.");
        Require(PdfStandardFonts.Resolve("Microsoft Sans Serif", true, false) == "Helvetica-Bold", "Sans serif is Helvetica.");
        Require(PdfStandardFonts.Resolve("serif", false, false) == "Times-Roman", "serif remains Times.");
        Require(PdfStandardFonts.Resolve("Noto Sans Mono", false, false) == "Courier", "Mono remains Courier.");
    }

    private static void PdfTextStaysBelowPreviousBlock()
    {
        var document = PortableDocument.Create().Paragraph("First").Build();
        var content = PdfContent(document);
        var y = TextY(content, "First");
        var top = PageSize.A4.Height.Points - PageMargins.Normal.Top.Points;
        var ascent = new PdfStandardFontMetrics("Helvetica").AscentEm;
        Require(Math.Abs(y - (top - DocumentDefaults.Default.FontSize.Points * ascent)) < 0.01,
            "Baseline lies at the ascender below the top margin.");
    }

    private static void PdfIndentsNestedLists()
    {
        var nested = TestLists.List(ListKind.Bullet, new ListItem[] { new(new Paragraph(new TextRun("inner"))) });
        var list = TestLists.List(
            ListKind.Bullet,
            new ListItem[] { new(new Block[] { new Paragraph(new TextRun("outer")), nested }) });
        var content = PdfContent(TestLists.Document(list));

        var outer = TextX(content, "outer");
        var inner = TextX(content, "inner");
        Require(inner > outer + 1, "Nested list is indented further.");
    }

    private static void PdfListMarkerSharesBaselineWithText()
    {
        var item = new Paragraph(new TextRun("Entry")) { Style = new ParagraphStyle { SpaceBefore = Length.FromPoints(20) } };
        var list = TestLists.List(ListKind.Number, new ListItem[] { new(item) });
        var content = PdfContent(TestLists.Document(list));
        Require(Math.Abs(TextY(content, "1.") - TextY(content, "Entry")) < 0.01, "Marker and text share the baseline.");
    }

    private static void PdfDrawsListInHeader()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(TestLists.List(ListKind.Bullet, new ListItem[] { new(new Paragraph(new TextRun("Headeritem"))) })),
        };
        var content = PdfContent(TestLists.Fix(new PortableDocument(new DocumentMetadata(), section)));
        // The bullet marker is WinAnsi 0x95, written as the PDF octal escape \225.
        Require(
            TextX(content, "\\225") < TextX(content, "Headeritem") - 1,
            "The list in the header is drawn with its marker.");
    }

    private static void PdfDropsWhitespaceAtSoftWraps()
    {
        var text = string.Join(' ', Enumerable.Repeat("Wort", 60));
        var document = PortableDocument.Create()
            .Paragraph(p => p.Text(text).Style(new ParagraphStyle { Alignment = TextAlignment.Right }))
            .Build();
        var lines = Regex.Matches(PdfContent(document), @"(-?[0-9.]+) (-?[0-9.]+) Td\n\(([^)]*)\) Tj")
            .Select(match => (Y: match.Groups[2].Value, Text: match.Groups[3].Value))
            .GroupBy(entry => entry.Y)
            .ToList();
        Require(lines.Count > 1, "Paragraph was wrapped.");

        // whitespace at the edge of a wrapped line shifts right-aligned text.
        Require(lines.All(line => line.First().Text.Trim().Length > 0), "No line begins with whitespace.");
        Require(lines.All(line => line.Last().Text.Trim().Length > 0), "No line ends with whitespace.");
    }

    private static void PdfRendersEmptyParagraphAsBlankLine()
    {
        var withBlank = PdfContent(new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("")), new Paragraph(new TextRun("After")) })));
        var without = PdfContent(PortableDocument.Create().Paragraph("After").Build());
        var lineHeight = DocumentDefaults.Default.FontSize.Points * Typography.LineHeightFactor;
        Require(Math.Abs(TextY(without, "After") - TextY(withBlank, "After") - lineHeight - Typography.ParagraphSpaceAfter.Points) < 0.01,
            "An empty paragraph occupies one line plus paragraph spacing.");
    }

    private static void PdfAlignsTableCells()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new TableCell("L"), new TableCell("R") { Alignment = TextAlignment.Right }),
        });
        var content = PdfContent(new PortableDocument(new DocumentMetadata(), new Section(table)));
        var columnWidth = (PageSize.A4.Width.Points - 2 * PageMargins.Normal.Left.Points) / 2;
        Require(TextX(content, "R") > PageMargins.Normal.Left.Points + columnWidth * 1.5, "Right-aligned cell.");
    }

    private static void DocxRestartsNumberingPerList()
    {
        var document = PortableDocument.Create()
            .NumberedList("a", "b")
            .Paragraph("Text in between")
            .NumberedList("c", "d")
            .Build();
        var (body, numbering) = DocxParts(document);
        Require(body.Contains("w:numId w:val=\"1\"", StringComparison.Ordinal), "First list uses numId 1.");
        Require(body.Contains("w:numId w:val=\"2\"", StringComparison.Ordinal), "Second list uses numId 2.");
        Require(numbering.Contains("w:abstractNumId w:val=\"1\"", StringComparison.Ordinal), "Second list has its own counting.");
        Require(
            numbering.LastIndexOf("<w:abstractNum ", StringComparison.Ordinal)
                < numbering.IndexOf("<w:num ", StringComparison.Ordinal),
            "All abstractNum precede the num elements.");
    }

    private static void DocxDerivesListLevelFromNesting()
    {
        var nested = TestLists.List(ListKind.Bullet, new ListItem[] { new(new Paragraph(new TextRun("inner"))) });
        var list = TestLists.List(
            ListKind.Bullet,
            new ListItem[] { new(new Block[] { new Paragraph(new TextRun("outer")), nested }) });
        var (body, _) = DocxParts(TestLists.Document(list));
        Require(body.Contains("w:ilvl w:val=\"1\"", StringComparison.Ordinal), "Nested list lies at level 1.");
    }

    private static void DocxUsesUniqueDrawingIds()
    {
        var image = new ImageBlock(CreatePng(4, 4), ImageMediaType.Png);
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[] { image, image }));
        var (body, _) = DocxParts(document);
        var ids = Regex.Matches(body, "<wp:docPr id=\"([0-9]+)\"").Select(match => match.Groups[1].Value).ToList();
        Require(ids.Count == 2 && ids.Distinct().Count() == 2, "Each drawing has its own docPr ID.");
    }

    private static void DocxCleansAltText()
    {
        var image = new ImageBlock(CreatePng(4, 4), ImageMediaType.Png) { AltText = "Logo\u0001" };
        var (body, _) = DocxParts(new PortableDocument(new DocumentMetadata(), new Section(image)));
        Require(body.Contains("descr=\"Logo�\"", StringComparison.Ordinal), "Alternative text is sanitized.");
    }

    private static void DocxKeepsParagraphAlignmentInLeftCells()
    {
        var centered = new Paragraph(new TextRun("middle")) { Style = new ParagraphStyle { Alignment = TextAlignment.Center } };
        var table = new TableBlock(new[] { new TableRow(new TableCell(centered)) });
        var (body, _) = DocxParts(new PortableDocument(new DocumentMetadata(), new Section(table)));
        Require(body.Contains("<w:jc w:val=\"center\" />", StringComparison.Ordinal), "Paragraph alignment is preserved.");
    }

    private static void DocxEndsCellsWithParagraph()
    {
        var inner = new TableBlock(new[] { new TableRow(new TableCell("inner")) });
        var outer = new TableBlock(new[] { new TableRow(new TableCell(inner)) });
        var (body, _) = DocxParts(TestLists.Document(outer));
        Require(body.Contains("</w:tbl><w:p /></w:tc>", StringComparison.Ordinal), "Cell ends with a paragraph.");
    }

    private static void DocxWritesParagraphInEmptyHeader()
    {
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            Header = new HeaderFooter(Array.Empty<Block>()),
        };
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        Require(ReadEntry(zip, "word/header1.xml").Contains("<w:p />", StringComparison.Ordinal), "Header contains a paragraph.");
    }

    private static void DocxWritesStructuredBlocksInHeader()
    {
        var table = new TableBlock(new[] { new TableRow(new TableCell("Cell")) });
        var list = TestLists.List(ListKind.Number, new ListItem[] { new(new Paragraph(new TextRun("Point"))) });
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            Header = new HeaderFooter(new Block[] { new Heading(HeadingLevel.Level2, new TextRun("Title")), list, table }),
        };

        var (header, result) = DocxHeader(TestLists.Fix(new PortableDocument(new DocumentMetadata(), section)), DocxWriteOptions.Default);
        Require(header.Contains("w:val=\"Heading2\"", StringComparison.Ordinal), "Heading in the header.");
        Require(header.Contains("Point", StringComparison.Ordinal) && header.Contains("<w:numPr>", StringComparison.Ordinal),
            "List in the header.");
        Require(header.Contains("<w:tbl>", StringComparison.Ordinal), "Table in the header.");
        Require(header.EndsWith("</w:tbl><w:p /></w:hdr>", StringComparison.Ordinal), "Header ends with a paragraph.");
        Require(!result.HasWarnings, "Renderable blocks produce no warning.");
    }

    private static void DocxWritesImagesInHeaderWithOwnRelationships()
    {
        // The same image in the body and the header, a second only in the footer.
        var shared = new ImageBlock(CreatePng(4, 4), ImageMediaType.Png) { AltText = "Logo" };
        var footerOnly = new ImageBlock(CreatePng(8, 4), ImageMediaType.Png);
        var section = new Section(new Block[] { shared })
        {
            Header = new HeaderFooter(new Block[] { new Paragraph(new TextRun("Header")), shared }),
            Footer = new HeaderFooter(footerOnly),
        };

        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter()
            .WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer)
            .GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);

        var header = ReadEntry(zip, "word/header1.xml");
        var footer = ReadEntry(zip, "word/footer1.xml");
        var body = ReadEntry(zip, "word/document.xml");
        var headerRels = ReadEntry(zip, "word/_rels/header1.xml.rels");
        var footerRels = ReadEntry(zip, "word/_rels/footer1.xml.rels");
        var documentRels = ReadEntry(zip, "word/_rels/document.xml.rels");

        Require(!result.HasWarnings, "Images in the header/footer produce no warning.");
        Require(header.Contains("r:embed=\"rId1\"", StringComparison.Ordinal) && header.Contains("descr=\"Logo\"", StringComparison.Ordinal),
            "Header references the image via its own relationship rId1.");
        Require(headerRels.Contains("Id=\"rId1\"", StringComparison.Ordinal)
                && headerRels.Contains("Target=\"media/image1.png\"", StringComparison.Ordinal),
            "header1.xml.rels references the shared medium.");
        Require(footer.Contains("r:embed=\"rId1\"", StringComparison.Ordinal)
                && footerRels.Contains("Target=\"media/image2.png\"", StringComparison.Ordinal),
            "Footer has its own relationship to its medium.");
        Require(!documentRels.Contains("media/image2.png", StringComparison.Ordinal),
            "document.xml.rels contains only body images.");
        Require(zip.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)) == 2,
            "Each image lies exactly once under word/media.");

        var ids = Regex.Matches(body + header + footer, "<wp:docPr id=\"([0-9]+)\"").Select(match => match.Groups[1].Value).ToList();
        Require(ids.Count == 3 && ids.Distinct().Count() == 3, "docPr IDs are unique across all parts.");

        var errors = DocxPackageValidator.Validate(buffer.ToArray());
        Require(errors.Count == 0, "Subset-Validator: " + string.Join("; ", errors));
    }

    private static void DocxDoesNotInheritHeaderIntoLaterSection()
    {
        var first = new Section(new Paragraph(new TextRun("one")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header 1"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("Footer 1"))),
        };
        var second = new Section(new Paragraph(new TextRun("two")));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(TestLists.Fix(new PortableDocument(new DocumentMetadata(), new[] { first, second })), buffer)
            .GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        // Word inherits headers/footers to sections without their own reference.
        Require(Regex.Matches(body, "<w:headerReference ").Count == 2, "Both sections reference a header.");
        Require(Regex.Matches(body, "<w:footerReference ").Count == 2, "Both sections reference a footer.");
        Require(!ReadEntry(zip, "word/header2.xml").Contains("Header 1", StringComparison.Ordinal), "Second header is empty.");
        Require(!ReadEntry(zip, "word/footer2.xml").Contains("Footer 1", StringComparison.Ordinal), "Second footer is empty.");
    }

    private static void DocxReportsPageBreaksInHeader()
    {
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            Header = new HeaderFooter(new Block[] { new Paragraph(new TextRun("Header")), new PageBreak() }),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);

        var (header, result) = DocxHeader(document, DocxWriteOptions.Default);
        Require(!header.Contains("w:br", StringComparison.Ordinal), "No page break in the header.");
        Require(result.Warnings.Count == 1, $"Page break is reported ({result.Warnings.Count}).");

        Throws<UnsupportedFeatureException>(() => DocxHeader(
            document,
            new DocxWriteOptions { UnsupportedContent = UnsupportedContentBehavior.Fail }));
    }

    private static void DocxEndsCellWithParagraphAfterListTable()
    {
        var inner = new TableBlock(new[] { new TableRow(new TableCell("inner")) });
        var list = TestLists.List(
            ListKind.Bullet,
            new ListItem[] { new(new Block[] { new Paragraph(new TextRun("Point")), inner }) });
        var outer = new TableBlock(new[] { new TableRow(new TableCell(list)) });
        var (body, _) = DocxParts(TestLists.Document(outer));
        Require(body.Contains("</w:tbl><w:p /></w:tc>", StringComparison.Ordinal), "Cell ends after a list with a table with a paragraph.");
    }

    private static (string Header, WriteResult Result) DocxHeader(PortableDocument document, DocxWriteOptions options)
    {
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return (ReadEntry(zip, "word/header1.xml"), result);
    }

    private static void UsesSameParagraphSpacingInAllFormats()
    {
        var document = PortableDocument.Create()
            .Paragraph("Paragraph one")
            .Paragraph("Paragraph two")
            .BulletList("Point one", "Point two")
            .Paragraph("After")
            .Table(t => t.Row("Cell"))
            .Build();
        var after = Typography.ParagraphSpaceAfter;

        // DOCX: default in docDefaults, compact paragraphs set 0 explicitly.
        using (var docx = new MemoryStream())
        {
            new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
            using var zip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
            var styles = ReadEntry(zip, "word/styles.xml");
            var body = ReadEntry(zip, "word/document.xml");
            Require(styles.Contains($"<w:spacing w:after=\"{after.Points * 20}\" w:line=\"240\"", StringComparison.Ordinal),
                "DOCX: default spacing and single line spacing.");
            Require(Regex.IsMatch(body, "<w:spacing w:after=\"0\" /></w:pPr><w:r><w:t>Point one<"), "DOCX: list item compact.");
            Require(Regex.IsMatch(body, "</w:numPr></w:pPr><w:r><w:t>Point two<"), "DOCX: last list item with default spacing.");
            Require(Regex.IsMatch(body, "<w:spacing w:after=\"0\" /></w:pPr><w:r><w:t>Cell<"), "DOCX: cell compact.");
        }

        // ODT: default on the default paragraph style, compact paragraphs via an automatic style.
        using (var odt = new MemoryStream())
        {
            new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
            using var zip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
            var styles = ReadEntry(zip, "styles.xml");
            var content = ReadEntry(zip, "content.xml");
            Require(styles.Contains($"fo:margin-bottom=\"{after.Points}pt\"", StringComparison.Ordinal), "ODT: default spacing.");
            Require(content.Contains("fo:margin-bottom=\"0pt\"", StringComparison.Ordinal), "ODT: compact style present.");
            Require(content.Contains("<text:p text:style-name=\"P1\">Point<text:s />one</text:p>", StringComparison.Ordinal), "ODT: list item compact.");
            Require(content.Contains("<text:p>Point<text:s />two</text:p>", StringComparison.Ordinal), "ODT: last list item with default spacing.");
        }

        // PDF: spacing between the baselines.
        var pdf = PdfContent(document);
        var line = DocumentDefaults.Default.FontSize.Points * Typography.LineHeightFactor;
        Require(Math.Abs(TextY(pdf, "Paragraph") - TextY(pdf, "Paragraph", 1) - line - after.Points) < 0.01, "PDF: paragraph spacing.");
        Require(Math.Abs(TextY(pdf, "Point") - TextY(pdf, "Point", 1) - line) < 0.01, "PDF: list items compact.");
        Require(Math.Abs(TextY(pdf, "Point", 1) - TextY(pdf, "After") - line - after.Points) < 0.01, "PDF: space after the list.");
    }

    private static void PdfKeepsHeadingWithNextLine()
    {
        // Each fill amount pushes the heading to a different position, including exactly
        // the page margin. It must never stand on a page without a following line.
        var headingMovedToNextPage = false;
        for (var fill = 1; fill <= 70; fill++)
        {
            var builder = PortableDocument.Create();
            for (var index = 0; index < fill; index++)
            {
                builder.Paragraph("Filler");
            }

            var pages = PdfPageContents(builder.Heading("Title", level: 2).Paragraph("Continuation").Build());
            var headingPage = pages.FindIndex(page => page.Contains("(Title) Tj", StringComparison.Ordinal));
            var nextPage = pages.FindIndex(page => page.Contains("(Continuation) Tj", StringComparison.Ordinal));
            Require(headingPage == nextPage, $"Heading and following line on the same page (fill amount {fill}).");

            // Moved means: on the heading's page no filler text stands before it.
            var page = pages[headingPage];
            var filler = page.IndexOf("(Filler) Tj", StringComparison.Ordinal);
            headingMovedToNextPage |= headingPage > 0 && (filler < 0 || filler > page.IndexOf("(Title) Tj", StringComparison.Ordinal));
        }

        Require(headingMovedToNextPage, "At least one case pushes the heading to the next page.");
    }

    private static void UsesSameTableCellPaddingInAllFormats()
    {
        var document = PortableDocument.Create().Table(t => t.Row("Top").Row("Bottom")).Build();

        var (body, _) = DocxParts(document);
        var twips = Typography.TableCellPaddingHorizontal.Points * 20;
        Require(body.Contains($"<w:left w:w=\"{twips}\" w:type=\"dxa\" />", StringComparison.Ordinal), "DOCX: cell padding left.");
        Require(body.Contains("<w:top w:w=\"0\" w:type=\"dxa\" />", StringComparison.Ordinal), "DOCX: no cell padding at the top.");

        var pdf = PdfContent(document);
        var line = DocumentDefaults.Default.FontSize.Points * Typography.LineHeightFactor;
        Require(Math.Abs(TextX(pdf, "Top") - PageMargins.Normal.Left.Points - Typography.TableCellPaddingHorizontal.Points) < 0.01,
            "PDF: cell padding left like DOCX.");
        Require(Math.Abs(TextY(pdf, "Top") - TextY(pdf, "Bottom") - line - 2 * Typography.TableCellPaddingVertical.Points) < 0.01,
            "PDF: line height without additional top/bottom padding.");
    }

    private static void OdtHeadingsKeepWithNext()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(PortableDocument.Create().Heading("Title").Build(), buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var styles = ReadEntry(zip, "styles.xml");
        Require(Regex.Matches(styles, "fo:keep-with-next=\"always\"").Count == 6, "ODT: all six heading styles keep with the following paragraph.");
    }

    private static void OdtWritesHeaderAndFooter()
    {
        var logo = new ImageBlock(CreatePng(4, 4), ImageMediaType.Png);
        var section = new Section(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() }))
        {
            Header = new HeaderFooter(new Block[]
            {
                new Paragraph(new Inline[] { new TextRun("Header ") { Bold = true }, new PageNumberField() }),
                TestLists.List(ListKind.Bullet, new[] { new ListItem(new Paragraph(new TextRun("Point"))) }),
                new TableBlock(new[] { new TableRow(new TableCell("Cell")) }),
                logo,
            }),
            Footer = new HeaderFooter(new Paragraph(new TextRun("Footer"))),
        };

        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter()
            .WriteAsync(TestLists.Fix(new PortableDocument(new DocumentMetadata(), section)), buffer)
            .GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var styles = ReadEntry(zip, "styles.xml");
        var content = ReadEntry(zip, "content.xml");

        Require(!result.HasWarnings, "Header/footer and page number field produce no warning: "
            + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        var header = Regex.Match(styles, "<style:header>(.*?)</style:header>", RegexOptions.Singleline).Groups[1].Value;
        Require(header.Contains("<text:page-number text:select-page=\"current\">", StringComparison.Ordinal), "Page number in the header.");
        Require(header.Contains("<text:list ", StringComparison.Ordinal) && header.Contains("<table:table ", StringComparison.Ordinal),
            "List and table in the header.");
        Require(header.Contains("xlink:href=\"Pictures/image1.png\"", StringComparison.Ordinal), "Image in the header.");
        Require(Regex.IsMatch(header, "<text:span text:style-name=\"MT1\">Header"), "Text formatting via its own style MT1.");
        Require(styles.Contains("<style:style style:name=\"MT1\" style:family=\"text\"", StringComparison.Ordinal),
            "MT1 is defined in styles.xml.");
        Require(styles.Contains("<style:footer>", StringComparison.Ordinal) && styles.Contains(">Footer</text:p>", StringComparison.Ordinal),
            "Footer written.");
        Require(content.Contains("<text:page-number ", StringComparison.Ordinal), "Page number in the body.");

        var errors = OdtPackageValidator.Validate(buffer.ToArray());
        Require(errors.Count == 0, "ODT-Subset-Validator: " + string.Join("; ", errors));
    }

    private static void OdtPlacesBandsLikeDocx()
    {
        // header/footer lie in the page margin, at HeaderFooterDistance from the sheet edge.
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("Footer"))),
        };
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var styles = ReadEntry(zip, "styles.xml");

        var distance = Typography.HeaderFooterDistance.Points;
        var margin = PageMargins.Normal.Top.Points;
        var line = DocumentDefaults.Default.FontSize.Points * Typography.LineHeightFactor;
        static string Pt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

        Require(styles.Contains($"fo:margin-top=\"{Pt(distance)}\" fo:margin-bottom=\"{Pt(distance)}\"", StringComparison.Ordinal),
            "Page margin reduced to the header/footer spacing.");
        Require(styles.Contains($"<style:header-style><style:header-footer-properties fo:min-height=\"{Pt(margin - distance)}\" fo:margin-bottom=\"0pt\" style:dynamic-spacing=\"false\"", StringComparison.Ordinal),
            "Header fills the remaining page margin.");
        Require(styles.Contains($"<style:footer-style><style:header-footer-properties fo:min-height=\"{Pt(line)}\" fo:margin-top=\"{Pt(margin - distance - line)}\" style:dynamic-spacing=\"false\"", StringComparison.Ordinal),
            "Footer: one line plus fixed spacing.");
    }

    private static void OdtAttachesBreaksToFollowingBlock()
    {
        var shared = new Paragraph(new TextRun("double"));
        var first = new Section(new Block[]
        {
            new Paragraph(new TextRun("start")),
            new PageBreak(), new Heading(HeadingLevel.Level2, new TextRun("title")),
            new PageBreak(), new TableBlock(new[] { new TableRow(new TableCell("cell")) }),
            new PageBreak(), TestLists.List(ListKind.Bullet, new[] { new ListItem(new Paragraph(new TextRun("point"))) }),
            new PageBreak(), new ImageBlock(CreatePng(4, 4), ImageMediaType.Png),
            new PageBreak(), shared, shared,
            new PageBreak(), new PageBreak(), new Paragraph(new TextRun("after double")),
            new PageBreak(),
        });
        var second = new Section(new Paragraph(new TextRun("section")));
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(TestLists.Fix(new PortableDocument(new DocumentMetadata(), new[] { first, second })), buffer)
            .GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        bool BreaksBefore(string styleName, string? masterPage = null)
            => Regex.IsMatch(content, $"style:name=\"{styleName}\" style:family=\"(paragraph|table)\""
                + (masterPage is null ? "" : $" style:master-page-name=\"{masterPage}\"")
                + "[^>]*><style:(paragraph|table)-properties[^>]*fo:break-before=\"page\"");
        string StyleOf(string pattern)
        {
            var match = Regex.Match(content, pattern);
            Require(match.Success, $"Element found: {pattern}");
            return match.Groups[1].Value;
        }

        Require(BreaksBefore(StyleOf("<text:h text:outline-level=\"2\" text:style-name=\"(P[0-9]+)\">title")), "Heading carries the break.");
        Require(Regex.IsMatch(content, "style:name=\"P[0-9]+\" style:family=\"paragraph\" style:parent-style-name=\"Heading2\""),
            "The heading's break style inherits the heading style.");
        Require(BreaksBefore(StyleOf("<table:table table:name=\"Table1\" table:style-name=\"(TB1)\"")), "Table carries the break.");
        Require(BreaksBefore(StyleOf("<text:list-item><text:p text:style-name=\"(P[0-9]+)\">point")), "First list paragraph carries the break.");
        Require(BreaksBefore(StyleOf("<text:p text:style-name=\"(P[0-9]+)\"><draw:frame")), "Image paragraph carries the break.");
        Require(BreaksBefore(StyleOf("<text:p text:style-name=\"(P[0-9]+)\">after<text:s />double")), "Paragraph after a double break carries the second.");
        Require(BreaksBefore(StyleOf("<text:p text:style-name=\"(P[0-9]+)\">section"), "Standard2"), "Section break at the first paragraph.");

        // fallbacks: instance used multiple times, double break, break before the section end.
        Require(Regex.Matches(content, "<text:p>double</text:p>").Count == 2, "A paragraph used multiple times stays without a break style.");
        Require(Regex.Matches(content, "<text:p text:style-name=\"PageBreak\" />").Count == 3,
            "Empty break paragraph only before the multiple paragraph, at the double break and at the end.");
        Require(!content.Contains("text:style-name=\"SectBreak2\"", StringComparison.Ordinal), "No empty section break.");

        var errors = OdtPackageValidator.Validate(buffer.ToArray());
        Require(errors.Count == 0, "ODT-Subset-Validator: " + string.Join("; ", errors));
    }

    private static void ResolvesGenericFontFamilies()
    {
        // Otherwise Word and Pages search for a font literally named "sans-serif".
        var document = PortableDocument.Create()
            .Paragraph(p => p
                .Span("a", fontFamily: "sans-serif")
                .Span("b", fontFamily: "Serif")
                .Span("c", fontFamily: "monospace")
                .Span("d", fontFamily: "Georgia"))
            .Build();

        var (body, _) = DocxParts(document);
        foreach (var name in new[] { "Arial", "Times New Roman", "Courier New", "Georgia" })
        {
            Require(body.Contains($"<w:rFonts w:ascii=\"{name}\"", StringComparison.Ordinal), $"DOCX: {name}.");
        }

        Require(!Regex.IsMatch(body, "w:ascii=\"(sans-serif|Serif|monospace)\""), "DOCX: no generic names.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("<style:font-face style:name=\"Arial\" svg:font-family=\"Arial\" style:font-family-generic=\"swiss\"", StringComparison.Ordinal),
            "ODT: sans-serif as Arial with category swiss.");
        Require(content.Contains("style:name=\"Times New Roman\" svg:font-family=\"'Times New Roman'\" style:font-family-generic=\"roman\"", StringComparison.Ordinal),
            "ODT: serif as Times New Roman with category roman.");
        Require(content.Contains("style:font-family-generic=\"modern\"", StringComparison.Ordinal), "ODT: monospace with category modern.");
        Require(Regex.IsMatch(content, "<style:font-face style:name=\"Georgia\" svg:font-family=\"Georgia\"[^>]*/>")
                && !Regex.IsMatch(content, "style:name=\"Georgia\"[^>]*font-family-generic"),
            "ODT: concrete names unchanged and without a category.");
        Require(content.Contains("style:font-name=\"Arial\"", StringComparison.Ordinal), "ODT: text style references Arial.");
        Require(!content.Contains("sans-serif", StringComparison.Ordinal), "ODT: no generic name any more.");
    }

    private static void StartsColumnsBelowAContinuousBreak()
    {
        // A headline across the page, then a continuous section in two columns: both columns start below the headline
        // (the right one used to start at the top of the page and overlapped it).
        var head = new Section(new Block[] { new Paragraph(new TextRun("Headline of the article")), new Paragraph(new TextRun("Standfirst across the full width.")) });
        var columns = new Section(Enumerable.Range(0, 12).Select(i => (Block)new Paragraph(new TextRun($"Column{i:D2} " + string.Join(' ', Enumerable.Repeat("word", 60))))).ToList())
        {
            TextColumns = 2,
            StartType = SectionStartType.Continuous,
        };
        var page = PdfPageContents(new PortableDocument(new DocumentMetadata(), [head, columns]))[0];
        var below = TextY(page, "Standfirst");
        // Every text line in the right column, including the continuation of a paragraph at its top: paragraph starts
        // alone do not show the overlap.
        var right = Regex.Matches(page, @"(-?[0-9.]+) (-?[0-9.]+) Td\n\(")
            .Select(match => (X: double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Y: double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Where(position => position.X > 280).ToList();
        Require(right.Count > 0, "PDF: some text reaches the right column on the first page.");
        Require(right.All(position => position.Y < below), $"PDF: the right column starts below the full-width text ({right.Max(position => position.Y)} vs {below}).");
    }

    private static void TakesTheLargerSpaceBetweenParagraphs()
    {
        // Word (confirmed by screenshot, 2026-11-18) and LibreOffice use the larger of "after" and "before" between two
        // paragraphs, not their sum: 6 pt after and 12 pt before give the same gap as 0 pt after and 12 pt before.
        double Gap(double after)
        {
            var page = PdfPageContents(new PortableDocument(new DocumentMetadata(), new Section(new Block[]
            {
                new Paragraph(new TextRun("top")) { Style = ParagraphStyle.Default with { SpaceAfter = Length.FromPoints(after) } },
                new Heading(HeadingLevel.Level2, new TextRun("bottom")) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(12) } },
            })))[0];
            return TextY(page, "top") - TextY(page, "bottom");
        }

        Require(Math.Abs(Gap(6) - Gap(0)) < 0.01, "PDF: the gap between paragraphs is the larger space, not the sum.");
        Require(Gap(20) > Gap(0) + 7.9, "PDF: a larger space after still counts.");
    }

    private static void DropsSpaceBeforeAtTopOfContinuedPage()
    {
        // Like Word (confirmed by screenshot): after a manual break and after overflow
        // the space before is dropped; after a section break it remains.
        var top = PageSize.A4.Height.Points - PageMargins.Normal.Top.Points;
        var size = Typography.HeadingSize(HeadingLevel.Level2).Points;
        var before = Typography.HeadingSpaceBefore.Points;
        var ascent = new PdfStandardFontMetrics("Helvetica-Bold").AscentEm;

        var afterBreak = PdfPageContents(PortableDocument.Create()
            .Paragraph("start").PageBreak().Heading("Break", level: 2).Paragraph("x").Build());
        Require(Math.Abs(TextY(afterBreak[1], "Break") - (top - size * ascent)) < 0.01, "PDF: no space before after a manual break.");

        var sections = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("one"))),
            new Section(new Block[] { new Heading(HeadingLevel.Level2, new TextRun("Section")) }),
        });
        // After a section break Word/LibreOffice reduce the space before by a
        // fixed amount (see Typography.SpaceBeforeReductionAtSectionStart; in Word about 5 pt
        // at a 12-pt heading, measured in LibreOffice as max(0, before − 6)).
        Require(Math.Abs(TextY(PdfPageContents(sections)[1], "Section")
            - (top - (before - Typography.SpaceBeforeReductionAtSectionStart.Points) - size * ascent)) < 0.01,
            "PDF: space before is reduced after a section break (like Word).");

        var overflowChecked = false;
        for (var fill = 30; fill <= 60 && !overflowChecked; fill++)
        {
            var builder = PortableDocument.Create();
            for (var index = 0; index < fill; index++)
            {
                builder.Paragraph("Filler");
            }

            var pages = PdfPageContents(builder.Heading("Overflow", level: 2).Paragraph("y").Build());
            var page = pages.FindIndex(content => content.Contains("(Overflow) Tj", StringComparison.Ordinal));
            if (page > 0 && !pages[page].Contains("(Filler) Tj", StringComparison.Ordinal))
            {
                Require(Math.Abs(TextY(pages[page], "Overflow") - (top - size * ascent)) < 0.01, $"PDF: no space before after overflow (fill amount {fill}).");
                overflowChecked = true;
            }
        }

        Require(overflowChecked, "A fill amount puts the heading at the top of a new page.");

        // ODT: the style at the manual break sets the space before to 0, that at the
        // section break is shortened by the measured reduction (like Word/LibreOffice).
        var document = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Block[] { new Paragraph(new TextRun("start")), new PageBreak(), new Heading(HeadingLevel.Level2, new TextRun("break")) }),
            new Section(new Block[] { new Heading(HeadingLevel.Level2, new TextRun("section")) }),
        });
        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        string StyleBody(string text)
        {
            var name = Regex.Match(content, $"text:style-name=\"(P[0-9]+)\">{text}<").Groups[1].Value;
            return Regex.Match(content, $"style:name=\"{name}\".*?</style:style>").Value;
        }

        Require(StyleBody("break").Contains("fo:margin-top=\"0pt\"", StringComparison.Ordinal), "ODT: no space before after a manual break.");
        var reduced = (Typography.HeadingSpaceBefore.Points - Typography.SpaceBeforeReductionAtSectionStart.Points)
            .ToString("0.##", CultureInfo.InvariantCulture);
        Require(StyleBody("section").Contains($"fo:margin-top=\"{reduced}pt\"", StringComparison.Ordinal),
            "ODT: space before is reduced after a section break (like Word).");
    }

    private static void DocxDeclaresCurrentCompatibilityMode()
    {
        // Without settings.xml Word opened every file in compatibility mode (finding from Word for Mac).
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(PortableDocument.Create().Paragraph("x").Build(), buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);

        var settings = ReadEntry(zip, "word/settings.xml");
        Require(settings.Contains("<w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\" />",
            StringComparison.Ordinal), "settings.xml sets compatibility level 15.");
        Require(ReadEntry(zip, "word/_rels/document.xml.rels").Contains("Target=\"settings.xml\"", StringComparison.Ordinal),
            "document.xml references settings.xml.");
        Require(ReadEntry(zip, "[Content_Types].xml").Contains("PartName=\"/word/settings.xml\"", StringComparison.Ordinal),
            "Content type for settings.xml.");

        var errors = DocxPackageValidator.Validate(buffer.ToArray());
        Require(errors.Count == 0, "DOCX-Subset-Validator: " + string.Join("; ", errors));
    }

    private static void UsesSameTableBordersInAllFormats()
    {
        // Two rows, the second with only one cell (irregular table).
        var table = new TableBlock(new[]
        {
            new TableRow(new TableCell("a"), new TableCell("b")),
            new TableRow(new TableCell("c")),
        });
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));
        var border = Typography.TableBorderWidth.Points;

        var (body, _) = DocxParts(document);
        Require(body.Contains($"<w:insideV w:val=\"single\" w:sz=\"{border * 8}\"", StringComparison.Ordinal), "DOCX: border width from Typography.");

        // ODT: always left/top, right at the last cell of a row, bottom in the last row.
        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        string CellStyleOf(string text) => Regex.Match(content, $"<table:table-cell table:style-name=\"(Cell[RU]*)\"[^>]*><text:p[^>]*>{text}<").Groups[1].Value;
        Require(CellStyleOf("a") == "Cell" && CellStyleOf("b") == "CellR" && CellStyleOf("c") == "CellRU", "ODT: cell styles by position.");
        var solid = $"{border.ToString(CultureInfo.InvariantCulture)}pt solid #000000";
        Require(Regex.IsMatch(content, $"style:name=\"CellRU\" style:family=\"table-cell\"[^>]*><style:table-cell-properties fo:padding-left=\"5.4pt\" fo:padding-right=\"5.4pt\" fo:padding-top=\"0pt\" fo:padding-bottom=\"0pt\" fo:border-left=\"{solid}\" fo:border-top=\"{solid}\" fo:border-right=\"{solid}\" fo:border-bottom=\"{solid}\""),
            "ODT: corner cell with borders all around and padding like DOCX.");
        Require(Regex.IsMatch(content, "style:name=\"Cell\" style:family=\"table-cell\"[^>]*><style:table-cell-properties[^>]*fo:border-right=\"none\" fo:border-bottom=\"none\""),
            "ODT: inner cell without double lines.");
        var errors = OdtPackageValidator.Validate(odt.ToArray());
        Require(errors.Count == 0, "ODT-Subset-Validator: " + string.Join("; ", errors));

        // PDF: one rectangle per cell, with the same line width.
        var pdf = PdfContent(document);
        Require(Regex.Matches(pdf, " re\nS\n").Count == 3, "PDF: three cell borders.");
        Require(pdf.Contains($"{border.ToString(CultureInfo.InvariantCulture)} w\n", StringComparison.Ordinal), "PDF: line width from Typography.");

        // PDF: a row across two pages is bordered on both pages.
        var tall = new TableBlock(new[] { new TableRow(new TableCell(string.Join("\n", Enumerable.Repeat("Row", 70)))) });
        var pages = PdfPageContents(new PortableDocument(new DocumentMetadata(), new Section(tall)));
        Require(pages.Count == 2 && pages.All(page => Regex.Matches(page, " re\nS\n").Count == 1), "PDF: each part of a line has a border.");
    }

    /// <summary>The decompressed content streams per page (without images and fonts).</summary>
    private static List<string> PdfPageContents(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = buffer.ToArray();
        var latin = Encoding.Latin1.GetString(pdf);
        var pages = new List<string>();
        var index = 0;
        while ((index = latin.IndexOf("/Filter /FlateDecode", index, StringComparison.Ordinal)) >= 0)
        {
            var streamStart = latin.IndexOf("stream\n", index, StringComparison.Ordinal) + "stream\n".Length;
            var streamEnd = latin.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var content = new MemoryStream();
            zlib.CopyTo(content);
            pages.Add(Encoding.Latin1.GetString(content.ToArray()));
            index = streamEnd;
        }

        return pages;
    }

    private static void OdtDeclaresFontFaces()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("x", fontFamily: "Times New Roman"))
            .Build();
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        var styles = ReadEntry(zip, "styles.xml");
        Require(content.Contains("<style:font-face style:name=\"Times New Roman\" svg:font-family=\"'Times New Roman'\"", StringComparison.Ordinal),
            "content.xml declares the font.");
        Require(content.IndexOf("office:font-face-decls", StringComparison.Ordinal)
                < content.IndexOf("office:automatic-styles", StringComparison.Ordinal),
            "font-face-decls precede the automatic styles.");
        Require(styles.Contains("<style:font-face style:name=\"" + DocumentDefaults.Default.FontFamily + "\"", StringComparison.Ordinal),
            "styles.xml declares the default font.");
    }

    private static void OdtAppliesCellAlignment()
    {
        var table = new TableBlock(new[] { new TableRow(new TableCell("R") { Alignment = TextAlignment.Right }) });
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(table)), buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("fo:text-align=\"end\"", StringComparison.Ordinal), "Right-aligned paragraph style present.");
        Require(Regex.IsMatch(content, "<table:table-cell[^>]*><text:p text:style-name=\"P1\">R</text:p>"), "Cell uses the style.");
    }

    private static void FacadeDeletesPartialFileOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reglet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "broken.docx");
            var document = PortableDocument.Create().Paragraph("A\u0001B").Build();
            Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(
                document,
                path,
                new DocxWriteOptions { InvalidXmlCharacters = InvalidXmlCharacterBehavior.Fail }).GetAwaiter().GetResult());
            Require(!File.Exists(path), "No half-written file is left behind.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (string Body, string Numbering) DocxParts(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return (ReadEntry(zip, "word/document.xml"), ReadEntry(zip, "word/numbering.xml"));
    }

    private static string PdfContent(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = buffer.ToArray();
        var latin = Encoding.Latin1.GetString(pdf);
        var output = new StringBuilder();
        var index = 0;
        while ((index = latin.IndexOf("/Filter /FlateDecode", index, StringComparison.Ordinal)) >= 0)
        {
            var streamStart = latin.IndexOf("stream\n", index, StringComparison.Ordinal) + "stream\n".Length;
            var streamEnd = latin.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var content = new MemoryStream();
            zlib.CopyTo(content);
            output.Append(Encoding.Latin1.GetString(content.ToArray()));
            index = streamEnd;
        }

        return output.ToString();
    }

    private static double TextX(string content, string text) => TextPosition(content, text).X;

    private static double TextY(string content, string text, int occurrence = 0) => TextPosition(content, text, occurrence).Y;

    private static (double X, double Y) TextPosition(string content, string text, int occurrence = 0)
    {
        var matches = Regex.Matches(content, @"(-?[0-9.]+) (-?[0-9.]+) Td\n\(" + Regex.Escape(text) + @"\) Tj");
        Require(matches.Count > occurrence, $"Text '{text}' found in the PDF.");
        var match = matches[occurrence];
        return (
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static byte[] CreatePng(int width, int height, int? rawLength = null)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(bytes, "IHDR", ihdr);
        var pixels = new byte[rawLength ?? Math.Max(0, height * (1 + width * 3))];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        AppendChunk(bytes, "IDAT", compressed.ToArray());
        AppendChunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(Encoding.ASCII.GetBytes(type));
        target.AddRange(data);
        target.AddRange(new byte[4]);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Entry '{name}' is missing.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
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
