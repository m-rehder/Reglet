using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// Regression checks for the findings of the code review of 2026-09-25: hostile and real-world
/// input for the readers, the PDF page tools, templates, hyperlinks, fonts and the facade.
/// </summary>
internal static class ReviewFindingChecks
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    internal static int Run()
    {
        try
        {
            PdfDeepNestingIsRejected();
            PdfTjKerningKeepsWordsAndSpaces();
            PdfInlineImageIsSkipped();
            PdfFormXObjectTextIsRead();
            PdfInheritedMediaBoxIsRead();
            PdfDanglingReferenceIsNull();
            PdfBrokenXrefIsReconstructed();
            PdfExtractDoesNotCopyOtherPages();
            PdfPageToolsWriteNonSeekableStreams();
            DocxToggleOffIsNotBold();
            DocxNumIdZeroIsNoList();
            DocxNegativeValuesAreTolerated();
            DocxLocalizedHeadingStyleIsHeading();
            DocxKeywordsKeepSpaces();
            DocxAnchorHyperlinkIsInternal();
            OversizedImageIsSkipped();
            OdtLengthsInCentimetersAreRead();
            OdtListStyleDecidesBullet();
            OdtListInTableCellIsRead();
            OdtAbsorbedPageBreakIsRead();
            TemplateNestedFalseConditions();
            TemplateSplitPlaceholderInRepeatRow();
            FacadeKeepsExistingFileOnFailure();
            PdfDefaultFamilyUsesEmbeddedFont();
            InternalHyperlinksJumpToBookmarks();
            PdfUriIsAscii();
            PdfListInTableCellIsDrawn();
            PdfTabStopsAlignText();
            PdfTableOfContentsHasLeadersAndLinks();
            PdfTableHeaderIsRepeated();
            PdfImagesAndNestedTablesInCells();
            DocxNestedListKeepsSourceLevel();
            DocxReadsLevelOverrides();
            SearchCoversHeadersAndNestedTables();
            NoOpTransformsKeepDocument();
            FieldResolverKeepsListFormatting();
            UnsupportedDateFieldsStayCached();
            DateFieldKeepsItsOwnDay();
            DocxProtectionAppliesToReadDocument();
            EvenHeaderTableStyleIsWritten();
            SvgExponentIsRead();
            JsonRejectsInvalidLiteral();
            PngChunkLengthIsValidated();
            ReaderInputLimitIsEnforced();
            SourceSearchReplacesBandText();
            FieldResolveLeavesBandsOfReadSourceUnchanged();
            TableWidthPercentScalesExplicitColumns();
            Console.WriteLine("Review findings: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Review findings: " + exception);
            return 1;
        }
    }

    // ---- PDF reader ---------------------------------------------------------------------

    private static void PdfDeepNestingIsRejected()
    {
        var nested = new string('[', 200_000) + new string(']', 200_000);
        var pdf = Pdf("<< /Type /Catalog /Pages 2 0 R /Deep " + nested + " >>", "<< /Type /Pages /Kids [] /Count 0 >>");
        Expect<DocumentValidationException>(() => ReadPdf(pdf), "Deeply nested arrays fail cleanly instead of overflowing the stack.");
    }

    private static void PdfTjKerningKeepsWordsAndSpaces()
    {
        var pdf = SinglePagePdf("BT /F1 12 Tf 72 700 Td [(Hel) 20 (lo) -300 (World)] TJ ET");
        var text = DocumentReader.ReadPdfPages(new MemoryStream(pdf))[0];
        Require(text == "Hello World", $"TJ: kerning does not split words, a large gap is a space (got '{text}').");
    }

    private static void PdfInlineImageIsSkipped()
    {
        var pdf = SinglePagePdf("BT /F1 12 Tf 72 700 Td (Before) Tj ET q BI /W 2 /H 1 /BPC 8 /CS /G ID >>)\u0001 EI Q BT /F1 12 Tf 72 600 Td (After) Tj ET");
        var text = DocumentReader.ReadPdfPages(new MemoryStream(pdf))[0];
        Require(text == "Before\nAfter", $"Binary inline image data does not break the content parser (got '{text}').");
    }

    private static void PdfFormXObjectTextIsRead()
    {
        var form = "BT /F1 12 Tf 0 0 Td (InForm) Tj ET";
        var pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /X1 6 0 R >> >> /Contents 4 0 R >>",
            Stream("q 1 0 0 1 72 700 cm /X1 Do Q"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length {form.Length} >>\nstream\n{form}\nendstream");
        Require(DocumentReader.ReadPdfPages(new MemoryStream(pdf))[0] == "InForm", "Text in a form XObject is read.");
    }

    private static void PdfInheritedMediaBoxIsRead()
    {
        var pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            Stream("BT /F1 12 Tf 72 700 Td (x) Tj ET"));
        var section = ReadPdf(pdf).Document.Sections[0];
        Require(Math.Abs(section.PageSize.Width.Points - 612) < 1e-6 && Math.Abs(section.PageSize.Height.Points - 792) < 1e-6,
            "The MediaBox inherited from the page tree is read.");
    }

    private static void PdfDanglingReferenceIsNull()
    {
        var pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots 99 0 R >>",
            Stream("BT /F1 12 Tf 72 700 Td (x) Tj ET"));
        using var output = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(pdf), output);
        Require(output.Length > 0 && ReadPdf(pdf).Document.Sections.Count == 1, "A reference to an undefined object is treated as null.");
    }

    private static void PdfBrokenXrefIsReconstructed()
    {
        var pdf = SinglePagePdf("BT /F1 12 Tf 72 700 Td (Recovered) Tj ET");
        var text = Encoding.Latin1.GetString(pdf);

        // Shift every object by inserting bytes: all xref offsets are now wrong.
        var shifted = Encoding.Latin1.GetBytes(text.Replace("%PDF-1.7\n", "%PDF-1.7\n%padding padding padding\n"));
        Require(DocumentReader.ReadPdfPages(new MemoryStream(shifted))[0] == "Recovered", "Wrong xref offsets are corrected by scanning.");

        var truncated = Encoding.Latin1.GetBytes(text[..text.IndexOf("xref", StringComparison.Ordinal)]);
        Require(DocumentReader.ReadPdfPages(new MemoryStream(truncated))[0] == "Recovered", "A missing xref table is rebuilt.");
    }

    // ---- PDF page tools -----------------------------------------------------------------

    private static void PdfExtractDoesNotCopyOtherPages()
    {
        var pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            Stream("BT /F1 12 Tf 72 700 Td (PageOne) Tj ET"),
            Stream("BT /F1 12 Tf 72 700 Td (PageTwoSecret) Tj ET"),
            "<< /Type /Annot /Subtype /Link /P 3 0 R /Rect [0 0 10 10] /Dest [4 0 R /XYZ 0 0 0] >>");

        using var output = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(pdf), 1, 1, output);
        var result = Encoding.Latin1.GetString(output.ToArray());
        Require(!result.Contains("PageTwoSecret", StringComparison.Ordinal), "Links to other pages do not pull their content into the extract.");
        Require(DocumentReader.ReadPdfPages(new MemoryStream(output.ToArray())) is [var page] && page == "PageOne", "The extract has exactly the selected page.");
    }

    private static void PdfPageToolsWriteNonSeekableStreams()
    {
        var pdf = SinglePagePdf("BT /F1 12 Tf 72 700 Td (x) Tj ET");
        var sink = new NonSeekableSink();
        PdfPageOperations.Extract(new MemoryStream(pdf), 1, 1, sink);
        PdfRewriter.Rewrite(new MemoryStream(pdf), sink);
        Require(sink.Written > 0, "Page tools write into a non-seekable stream.");
    }

    // ---- DOCX reader --------------------------------------------------------------------

    private static void DocxToggleOffIsNotBold()
    {
        var result = ReadDocx("<w:p><w:r><w:rPr><w:b w:val='0'/><w:i w:val='false'/></w:rPr><w:t>plain</w:t></w:r></w:p>");
        var run = ((Paragraph)result.Document.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().Single();
        Require(run.Bold != true && run.Italic != true, "w:val='0'/'false' switches a toggle property off.");
    }

    private static void DocxNumIdZeroIsNoList()
    {
        var result = ReadDocx("<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='0'/></w:numPr></w:pPr><w:r><w:t>text</w:t></w:r></w:p>");
        Require(result.Document.Sections[0].Blocks is [Paragraph], "numId 0 removes the numbering.");
    }

    private static void DocxNegativeValuesAreTolerated()
    {
        var result = ReadDocx(
            "<w:p><w:pPr><w:spacing w:before='-40'/><w:ind w:hanging='360'/></w:pPr><w:r><w:rPr><w:sz w:val='0'/></w:rPr><w:t>x</w:t></w:r></w:p>"
            + "<w:sectPr><w:pgMar w:top='-1440' w:right='1440' w:bottom='1440' w:left='1440'/></w:sectPr>");
        var section = result.Document.Sections[0];
        var paragraph = (Paragraph)section.Blocks[0];
        Require(Math.Abs(section.Margins.Top.Points - 72) < 1e-6, "A negative top margin is read as its absolute value.");
        Require(paragraph.Style.SpaceBefore == Length.Zero, "Negative spacing is clamped.");
        Require(Math.Abs((paragraph.Style.FirstLineIndent?.Points ?? 0) + 18) < 1e-6, "A hanging indent is a negative first-line indent.");
    }

    private static void DocxLocalizedHeadingStyleIsHeading()
    {
        var styles = $"<w:styles xmlns:w='{W}'><w:style w:type='paragraph' w:styleId='berschrift2'><w:name w:val='heading 2'/></w:style>"
            + "<w:style w:type='paragraph' w:styleId='Chapter'><w:basedOn w:val='berschrift2'/></w:style></w:styles>";
        var result = ReadDocx(
            "<w:p><w:pPr><w:pStyle w:val='berschrift2'/></w:pPr><w:r><w:t>A</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Chapter'/></w:pPr><w:r><w:t>B</w:t></w:r></w:p>",
            ("word/styles.xml", styles));
        Require(result.Document.Sections[0].Blocks is [Heading { Level: HeadingLevel.Level2 }, Heading { Level: HeadingLevel.Level2 }],
            "Localized and derived heading styles are headings.");
    }

    private static void DocxKeywordsKeepSpaces()
    {
        var core = "<cp:coreProperties xmlns:cp='http://schemas.openxmlformats.org/package/2006/metadata/core-properties'>"
            + "<cp:keywords>machine learning, data</cp:keywords></cp:coreProperties>";
        var result = ReadDocx("<w:p/>", ("docProps/core.xml", core));
        Require(result.Document.Metadata.Keywords is ["machine learning", "data"], "Keywords are split at commas only.");
    }

    private static void DocxAnchorHyperlinkIsInternal()
    {
        var result = ReadDocx("<w:p><w:hyperlink w:anchor='chapter'><w:r><w:t>jump</w:t></w:r></w:hyperlink></w:p>");
        Require(((Paragraph)result.Document.Sections[0].Blocks[0]).Inlines is [Hyperlink { Target: "#chapter" }],
            "w:anchor becomes an internal hyperlink.");
    }

    private static void OversizedImageIsSkipped()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "word/document.xml",
                $"<w:document xmlns:w='{W}' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'"
                + " xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><w:body>"
                + "<w:p><w:r><w:drawing><a:blip r:embed='rId9'/></w:drawing></w:r></w:p><w:p><w:r><w:t>after</w:t></w:r></w:p></w:body></w:document>");
            WriteEntry(zip, "word/_rels/document.xml.rels",
                "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>"
                + "<Relationship Id='rId9' Type='image' Target='media/bomb.png'/></Relationships>");
            using var bomb = zip.CreateEntry("word/media/bomb.png", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1 << 20];
            for (var index = 0; index < 40; index++)
            {
                bomb.Write(zeros);
            }
        }

        buffer.Position = 0;
        var result = DocumentReader.Read(buffer, DocumentFormat.Docx);
        Require(result.Document.Sections[0].Blocks is [Paragraph], "An image above the size limit is left out.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("size limit", StringComparison.Ordinal)), "The oversized image is reported.");
    }

    // ---- ODT reader ---------------------------------------------------------------------

    private static void OdtLengthsInCentimetersAreRead()
    {
        var styles = "<office:document-styles xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
            + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0' xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
            + "<office:automatic-styles><style:page-layout style:name='pm1'><style:page-layout-properties fo:page-width='21.59cm'"
            + " fo:page-height='27.94cm' fo:margin-top='2cm'/></style:page-layout></office:automatic-styles>"
            + "<office:master-styles><style:master-page style:name='Standard' style:page-layout-name='pm1'/></office:master-styles></office:document-styles>";
        var section = ReadOdt("<text:p>x</text:p>", styles: styles).Document.Sections[0];
        Require(Math.Abs(section.PageSize.Width.Points - 612) < 0.1 && Math.Abs(section.Margins.Top.Points - 56.69) < 0.1,
            "Page lengths in cm are read (LibreOffice).");
    }

    private static void OdtListStyleDecidesBullet()
    {
        var result = ReadOdt(
            "<text:list text:style-name='L1'><text:list-item><text:p>a</text:p></text:list-item></text:list>",
            automaticStyles: "<text:list-style style:name='L1'><text:list-level-style-bullet text:level='1' text:bullet-char='•'/></text:list-style>");
        var list = (ListBlock)result.Document.Sections[0].Blocks[0];
        Require(
            TestLists.Level(result.Document, list).Kind == ListKind.Bullet,
            "The list style, not its name, decides a bullet list.");
    }

    private static void OdtListInTableCellIsRead()
    {
        var result = ReadOdt(
            "<table:table><table:table-row><table:table-cell><text:list><text:list-item><text:p>inside</text:p></text:list-item></text:list>"
            + "</table:table-cell></table:table-row></table:table>");
        var cell = ((TableBlock)result.Document.Sections[0].Blocks[0]).Rows[0].Cells[0];
        Require(cell.Blocks is [ListBlock], "A list in a table cell is read.");
    }

    private static void OdtAbsorbedPageBreakIsRead()
    {
        var document = PortableDocument.Create().Paragraph("one").PageBreak().Paragraph("two").Build();
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var blocks = DocumentReader.Read(buffer, DocumentFormat.Odt).Document.Sections[0].Blocks;
        Require(blocks is [Paragraph, PageBreak, Paragraph], "A page break written as break-before is read back.");
    }

    // ---- templates and facade -----------------------------------------------------------

    private static void TemplateNestedFalseConditions()
    {
        var template = DocumentTemplate.FromBytes(Docx("<w:p><w:r><w:t>A{{If:x}}B{{If:y}}C{{EndIf}}D{{EndIf}}E</w:t></w:r></w:p>"))
            .Condition("x", false)
            .Condition("y", false);
        Require(TemplateText(template) == "AE", "Nested false regions are removed once (no overlapping deletions).");
    }

    private static void TemplateSplitPlaceholderInRepeatRow()
    {
        var row = "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>{{Row}}{{Na</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>me}}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
        var template = DocumentTemplate.FromBytes(Docx(row)).RepeatRows("Row", new[]
        {
            new Dictionary<string, string?> { ["Name"] = "Alice" },
            new Dictionary<string, string?> { ["Name"] = "Bob" },
        });
        var text = TemplateText(template);
        Require(text.Contains("Alice", StringComparison.Ordinal) && text.Contains("Bob", StringComparison.Ordinal) && !text.Contains("{{", StringComparison.Ordinal),
            $"A placeholder split over runs is replaced in repeated rows (got '{text}').");
    }

    private static void FacadeKeepsExistingFileOnFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "reglet-review-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "existing");
        try
        {
            var broken = new PortableDocument(new DocumentMetadata(), new Section(new ImageBlock(new byte[] { 1, 2, 3 }, ImageMediaType.Png)));
            Expect<ImageException>(() => DocumentWriter.WriteAsync(broken, path).GetAwaiter().GetResult(), "The invalid image fails.");
            Require(File.ReadAllText(path) == "existing", "A failed write keeps the existing file.");
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*").Length == 0, "No temporary file is left behind.");

            DocumentWriter.WriteAsync(PortableDocument.Create().Paragraph("ok").Build(), path).GetAwaiter().GetResult();
            Require(File.ReadAllBytes(path).AsSpan(0, 5).SequenceEqual("%PDF-"u8), "A successful write replaces the file.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- fonts and hyperlinks -----------------------------------------------------------

    private static void PdfDefaultFamilyUsesEmbeddedFont()
    {
        var font = FontFace.Load(SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("name", SyntheticTrueTypeFont.Name("DefaultProbe")),
            ("cmap", SyntheticTrueTypeFont.Cmap4())));
        var document = PortableDocument.Create()
            .Defaults(new DocumentDefaults { FontFamily = "Probe" })
            .Paragraph("A")
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { FontCollection = FontCollection.Empty.Add("Probe", font) })
            .GetAwaiter().GetResult();
        Require(Encoding.Latin1.GetString(buffer.ToArray()).Contains("DefaultProbe", StringComparison.Ordinal),
            "Text without its own family uses the embedded font of DocumentDefaults.FontFamily.");
    }

    private static void InternalHyperlinksJumpToBookmarks()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[] { new Bookmark("zeta"), new Bookmark("alpha"), new TextRun("Target") }),
            new Paragraph(new Hyperlink("#alpha", "jump")),
        }));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        var body = ZipText(docx.ToArray(), "word/document.xml");
        var rels = ZipText(docx.ToArray(), "word/_rels/document.xml.rels");
        Require(body.Contains("w:anchor=\"alpha\"", StringComparison.Ordinal) && !rels.Contains("#alpha", StringComparison.Ordinal),
            "DOCX: an internal link uses w:anchor, not an external relationship.");

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(pdf.ToArray());
        Require(text.Contains("/Dest (alpha)", StringComparison.Ordinal) && !text.Contains("/URI (#alpha)", StringComparison.Ordinal),
            "PDF: an internal link is a named destination.");
        Require(text.IndexOf("(alpha)", text.IndexOf("/Names [", StringComparison.Ordinal), StringComparison.Ordinal)
                < text.IndexOf("(zeta)", text.IndexOf("/Names [", StringComparison.Ordinal), StringComparison.Ordinal),
            "PDF: the name tree is sorted.");
        Require(Regex.IsMatch(text, @"/Subtype /Link .*?/F 4 ", RegexOptions.Singleline), "PDF: link annotations carry the print flag (PDF/A).");
    }

    private static void PdfUriIsAscii()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Hyperlink("https://café.example/crème", "link"))));
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(pdf.ToArray());
        Require(text.Contains("/URI (https://xn--caf-dma.example/cr%C3%A8me)", StringComparison.Ordinal), "PDF: URIs are 7-bit ASCII (punycode, percent-encoding).");
    }

    private static void PdfListInTableCellIsDrawn()
    {
        var list = TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("first"))), new ListItem(new Paragraph(new TextRun("second")))]);
        var table = new TableBlock([new TableRow(new TableCell(list))]);
        using var pdf = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(TestLists.Document(table), pdf).GetAwaiter().GetResult();
        var text = DocumentReader.ReadPdfPages(new MemoryStream(pdf.ToArray()))[0];
        Require(text.Contains("1. first", StringComparison.Ordinal) && text.Contains("2. second", StringComparison.Ordinal),
            $"PDF: a list in a table cell is drawn with its markers (got '{text}').");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("ListBlock", StringComparison.Ordinal)), "PDF: no warning for a list in a cell.");
    }

    private static void PdfTabStopsAlignText()
    {
        var paragraph = new Paragraph(new TextRun("Name\t1"))
        {
            Style = ParagraphStyle.Default with
            {
                TabStops = [new TabStop(Length.FromPoints(100), TabStopAlignment.Right, TabStopLeader.Dot)],
            },
        };
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(paragraph)), pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        var x = TextX(content, "(1)");
        Require(x is > 160 and < 172, $"PDF: text after a right tab ends at the tab stop (x = {x}).");
    }

    private static void PdfTableOfContentsHasLeadersAndLinks()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfContents(),
            new PageBreak(),
            new Heading(HeadingLevel.Level1, new TextRun("Chapter")),
        }));
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var bytes = pdf.ToArray();
        var content = PdfStreams.Decompress(bytes);
        Require(TextX(content, "(2)") > 500, "PDF: the TOC page number is right-aligned at the content edge.");
        Require(Encoding.Latin1.GetString(bytes).Contains("/Dest (_Toc0)", StringComparison.Ordinal), "PDF: the TOC entry links to its heading.");
    }

    private static void PdfTableHeaderIsRepeated()
    {
        var rows = new List<TableRow> { new(new TableCell("HeadA"), new TableCell("HeadB")) { IsHeader = true } };
        rows.AddRange(Enumerable.Range(0, 120).Select(index => new TableRow(new TableCell("r" + index), new TableCell("v" + index))));
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new TableBlock(rows))), pdf).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(pdf.ToArray()));
        Require(pages.Count > 1 && pages.All(page => page.StartsWith("HeadA HeadB", StringComparison.Ordinal)),
            "PDF: the header row is repeated on every page of the table.");
        Require(Encoding.Latin1.GetString(pdf.ToArray()).Contains("/Artifact", StringComparison.Ordinal) || PdfStreams.Decompress(pdf.ToArray()).Contains("/Artifact", StringComparison.Ordinal),
            "PDF: repeated header rows are artifacts (not read twice by assistive technology).");
    }

    private static void PdfImagesAndNestedTablesInCells()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        var nested = new TableBlock([new TableRow(new TableCell("InnerA"), new TableCell("InnerB"))]);
        var table = new TableBlock([new TableRow(
            new TableCell(new Block[] { new Paragraph(new TextRun("Picture")), new ImageBlock(png, ImageMediaType.Png) { Width = Length.FromPoints(40), Height = Length.FromPoints(20) } }),
            new TableCell(nested))]);
        using var pdf = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(TestLists.Document(table), pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains(" Do", StringComparison.Ordinal), "PDF: an image in a table cell is drawn.");
        Require(content.Contains("(InnerA)", StringComparison.Ordinal) && content.Contains("(InnerB)", StringComparison.Ordinal), "PDF: a nested table is drawn.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("table cells", StringComparison.Ordinal)), "PDF: no warning for images or tables in cells.");
    }

    /// <summary>The x position of the text-positioning operator that precedes a shown string.</summary>
    private static double TextX(string content, string shown)
    {
        var index = content.IndexOf(shown + " Tj", StringComparison.Ordinal);
        Require(index >= 0, $"PDF: {shown} is drawn.");
        var matches = Regex.Matches(content[..index], @"([-\d.]+) [-\d.]+ Td");
        return double.Parse(matches[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static string TemplateText(DocumentTemplate template)
    {
        using var output = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(PortableDocument.Create().Build(), output, new DocxWriteOptions { Template = template })
            .GetAwaiter().GetResult();
        var xml = ZipText(output.ToArray(), "word/document.xml");
        return string.Concat(Regex.Matches(xml, "<w:t[^>]*>([^<]*)</w:t>").Select(match => match.Groups[1].Value));
    }

    private static string ZipText(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static ReadResult ReadPdf(byte[] pdf) => DocumentReader.Read(new MemoryStream(pdf), DocumentFormat.Pdf);

    private static byte[] SinglePagePdf(string content) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        Stream(content),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static string Stream(string content) => $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] Pdf(params string[] objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static void DocxNestedListKeepsSourceLevel()
    {
        var numbering = $"<w:numbering xmlns:w='{W}'>"
            + "<w:abstractNum w:abstractNumId='0'>"
            + "<w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/></w:lvl>"
            + "<w:lvl w:ilvl='1'><w:start w:val='1'/><w:numFmt w:val='upperRoman'/></w:lvl>"
            + "<w:lvl w:ilvl='2'><w:start w:val='1'/><w:numFmt w:val='lowerLetter'/></w:lvl>"
            + "</w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num></w:numbering>";
        var body = "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>A</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:numPr><w:ilvl w:val='2'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>B</w:t></w:r></w:p>";

        var read = ReadDocx(body, ("word/numbering.xml", numbering)).Document;
        var section = read.Sections[0];
        var outer = (ListBlock)section.Blocks[0];
        var nested = (ListBlock)outer.Items[0].Blocks[1];
        Require(
            TestLists.Level(read, nested).Format == ListNumberFormat.LowerLetter,
            "A skipped list level keeps its own number format.");
    }

    /// <summary>OF-B01: w:lvlOverride restarts a level of a shared definition.</summary>
    private static void DocxReadsLevelOverrides()
    {
        var numbering = $"<w:numbering xmlns:w='{W}'>"
            + "<w:abstractNum w:abstractNumId='0'>"
            + "<w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/></w:lvl>"
            + "</w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num>"
            + "<w:num w:numId='2'><w:abstractNumId w:val='0'/>"
            + "<w:lvlOverride w:ilvl='0'><w:startOverride w:val='5'/></w:lvlOverride></w:num></w:numbering>";
        var body = "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>A</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='2'/></w:numPr></w:pPr><w:r><w:t>B</w:t></w:r></w:p>";

        var read = ReadDocx(body, ("word/numbering.xml", numbering)).Document;
        var blocks = read.Sections[0].Blocks.OfType<ListBlock>().ToList();
        Require(blocks.Count == 2, "Both lists are read.");
        Require(blocks[0].NumberingName != blocks[1].NumberingName, "The override is its own definition.");
        Require(TestLists.Level(read, blocks[0]).Start == 1, "The first definition starts at 1.");
        Require(TestLists.Level(read, blocks[1]).Start == 5, "w:startOverride restarts the level.");
    }

    private static void SearchCoversHeadersAndNestedTables()
    {
        var list = TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("old list")))], numberColor: Color.FromHex("#FF0000"), numberBold: true);
        var table = new TableBlock(
        [
            new TableRow([new TableCell(new Paragraph(new TextRun("old table"))) { Padding = Length.FromPoints(3) }]),
        ])
        {
            WidthPercent = 50,
            CellPadding = Length.FromPoints(7),
        };
        var nestedTable = new TableBlock(
        [
            new TableRow([new TableCell(new Paragraph(new TextRun("old nested")))]),
        ]);
        var listWithTable = TestLists.List(ListKind.Bullet,
        [
            new ListItem(new Block[] { new Paragraph(new TextRun("item")), nestedTable }),
        ]);
        var document = TestLists.Fix(new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("old body")), list, table, listWithTable })
            {
                Header = new HeaderFooter(new Paragraph(new TextRun("old header"))),
            }));

        Require(DocumentSearch.Find(document, new TextQuery("old nested")).Count == 1, "Find searches tables inside list items.");

        var replaced = DocumentSearch.Replace(document, new TextQuery("old"), "new");
        var section = replaced.Sections[0];
        var headerText = string.Concat(((Paragraph)section.Header!.Blocks[0]).Inlines.OfType<TextRun>().Select(run => run.Text));
        Require(headerText == "new header", "Replace changes header text.");

        var replacedList = (ListBlock)section.Blocks[1];
        var replacedLevel = TestLists.Level(replaced, replacedList);
        Require(
            replacedLevel.NumberColor == Color.FromHex("#FF0000") && replacedLevel.NumberBold,
            "Replace keeps list number colour and weight.");

        var replacedTable = (TableBlock)section.Blocks[2];
        Require(
            replacedTable.WidthPercent == 50
                && replacedTable.CellPadding == Length.FromPoints(7)
                && replacedTable.Rows[0].Cells[0].Padding == Length.FromPoints(3),
            "Replace keeps table width and cell padding.");
        Require(DocumentSearch.Find(replaced, new TextQuery("old")).Count == 0, "Replace changes all occurrences, including nested tables and headers.");
    }

    private static void NoOpTransformsKeepDocument()
    {
        var document = PortableDocument.Create().Paragraph("text").Build();
        Require(
            ReferenceEquals(DocumentSearch.Replace(document, new TextQuery("missing"), "x"), document),
            "Replace without a match returns the original document.");
        Require(
            ReferenceEquals(FieldResolver.Resolve(document, FieldContext.Empty), document),
            "Resolve without a field returns the original document.");
    }

    private static void FieldResolverKeepsListFormatting()
    {
        var document = TestLists.Fix(new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("one")))], numberColor: Color.FromHex("#00FF00"), numberBold: true),
                new Paragraph(new Field("QUOTE x")),
            ])));

        var resolved = FieldResolver.Resolve(document, FieldContext.Create());
        var list = (ListBlock)resolved.Sections[0].Blocks[0];
        var level = TestLists.Level(resolved, list);
        Require(
            level.NumberColor == Color.FromHex("#00FF00") && level.NumberBold,
            "Resolve keeps list number colour and weight.");
        Require(((Field)((Paragraph)resolved.Sections[0].Blocks[1]).Inlines[0]).Result == "x", "Resolve still calculates a field.");
    }

    private static void UnsupportedDateFieldsStayCached()
    {
        Require(FieldEvaluator.Evaluate("SECTIONPAGES", FieldContext.Empty) is null, "SECTIONPAGES is unknown without a section page count.");
        Require(FieldEvaluator.Evaluate("CREATEDATE", FieldContext.Empty) is null, "CREATEDATE is unknown without a creation date.");
        Require(FieldEvaluator.Evaluate("PRINTDATE", FieldContext.Empty) is null, "PRINTDATE is unknown without a print date.");

        var created = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var context = new FieldContext { Created = created, Saved = created.AddDays(1), Printed = created.AddDays(2), SectionPages = 3 };
        Require(FieldEvaluator.Evaluate("SECTIONPAGES", context) == "3", "SECTIONPAGES uses the section page count.");
        Require(FieldEvaluator.Evaluate("CREATEDATE", context) == "2024-01-02 03:04:05", "CREATEDATE uses the creation date.");
        Require(FieldEvaluator.Evaluate("SAVEDATE", context) == "2024-01-03 03:04:05", "SAVEDATE uses the save date.");
        Require(FieldEvaluator.Evaluate("PRINTDATE", context) == "2024-01-04 03:04:05", "PRINTDATE uses the print date.");
        Require(
            FieldEvaluator.Evaluate("CREATEDATE", FieldContext.FromMetadata(new DocumentMetadata { Created = created }))
                == "2024-01-02 03:04:05",
            "CREATEDATE reads the metadata creation date.");
        Require(
            FieldEvaluator.Evaluate("DATE", new FieldContext { Now = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero) })
                == "2026-05-06 00:00:00",
            "DATE still uses the configured date.");
    }

    private static void DateFieldKeepsItsOwnDay()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new DateField(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.FromHours(2))))));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        var documentXml = ReadTextEntry(docx.ToArray(), "word/document.xml");
        Require(
            documentXml.Contains("2026-01-01", StringComparison.Ordinal) && !documentXml.Contains("2025-12-31", StringComparison.Ordinal),
            "DOCX shows the date in the value's own offset.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        var contentXml = ReadTextEntry(odt.ToArray(), "content.xml");
        Require(
            contentXml.Contains("2026-01-01", StringComparison.Ordinal) && contentXml.Contains("+02:00", StringComparison.Ordinal),
            "ODT writes the date with its real offset.");

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Require(PdfStreams.ExtractText(pdf.ToArray()).Contains("2026-01-01", StringComparison.Ordinal), "PDF shows the date in the value's own offset.");
    }

    private static void DocxProtectionAppliesToReadDocument()
    {
        var original = PortableDocument.Create().Paragraph("Body").Build();
        using var first = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(original, first).GetAwaiter().GetResult();

        first.Position = 0;
        var read = new DocxDocumentReader().Read(first).Document;
        using var second = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(read, second, new DocxWriteOptions { Protection = DocxProtection.ReadOnly, ProtectionPassword = "secret" })
            .GetAwaiter()
            .GetResult();

        var settings = ReadTextEntry(second.ToArray(), "word/settings.xml");
        Require(
            settings.Contains("documentProtection", StringComparison.Ordinal)
                && settings.Contains("hashValue", StringComparison.Ordinal),
            "Writing a read document applies the requested protection.");
    }

    private static void EvenHeaderTableStyleIsWritten()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("body")))
            {
                EvenHeader = new HeaderFooter(
                    new TableBlock([new TableRow([new TableCell(new Paragraph(new TextRun("table")))])])
                    {
                        Style = new TableStyle("EvenTable"),
                    }),
            });

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var styles = ReadTextEntry(buffer.ToArray(), "word/styles.xml");
        Require(styles.Contains("EvenTable", StringComparison.Ordinal), "A table style in the even header is written to styles.xml.");
        Require(document.Statistics().Tables == 1, "Statistics see a table in the even header.");
    }

    private static void SvgExponentIsRead()
    {
        var size = SvgMetadata.Read(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1e2' height='1e2'/>"));
        Require(
            size is { } value && Math.Abs(value.Width - 75) < 0.001 && Math.Abs(value.Height - 75) < 0.001,
            "An SVG length with an exponent is read as 75pt, not 0.75pt.");
    }

    private static void JsonRejectsInvalidLiteral()
    {
        Expect<InvalidDataException>(
            () => MergeDataSource.FromJson(new StringReader("[{\"a\": tru}]")),
            "An invalid JSON literal is rejected.");
    }

    private static void PngChunkLengthIsValidated()
    {
        var png = new byte[20];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8), int.MaxValue);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(png, 12);
        Expect<ImageException>(() => PdfPngDecoder.Decode(png), "A PNG chunk length overflow fails cleanly.");
    }


    private static void ReaderInputLimitIsEnforced()
    {
        Expect<DocumentValidationException>(
            () => ReaderGuard.ReadAllBounded(new MemoryStream(new byte[8]), "Test", 4),
            "A seekable input above the limit is rejected before allocation.");
        var data = ReaderGuard.ReadAllBounded(new MemoryStream(new byte[] { 1, 2, 3 }), "Test", 4);
        Require(data is [1, 2, 3], "An input below the limit is read completely.");
        var stream = new NonSeekableReadStream(new byte[] { 1, 2, 3, 4, 5 });
        Expect<DocumentValidationException>(
            () => ReaderGuard.ReadAllBounded(stream, "Test", 4),
            "A non-seekable input above the limit is rejected.");
        Require(
            ReaderGuard.ReadAllBounded(new NonSeekableReadStream(new byte[] { 1, 2, 3 }), "Test", 4) is [1, 2, 3],
            "A non-seekable input below the limit is read completely.");
    }


    /// <summary>
    /// OF-B08: Replace covers the bands of a read document, so the header text of a document that
    /// was read from a package is replaced as well.
    /// </summary>
    private static void SourceSearchReplacesBandText()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("old body")))
            {
                Header = new HeaderFooter(new Paragraph(new TextRun("old header"))),
            });
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var read = TestPackages.Read(TestPackages.Write(document, format), format);
            var replaced = DocumentSearch.Replace(read, new TextQuery("old"), "new");
            var section = replaced.Sections[0];
            var body = string.Concat(((Paragraph)section.Blocks[0]).Inlines.OfType<TextRun>().Select(run => run.Text));
            var header = string.Concat(((Paragraph)section.Header!.Blocks[0]).Inlines.OfType<TextRun>().Select(run => run.Text));
            Require(body == "new body", $"{format}: body text is replaced in a read source.");
            Require(header == "new header", $"{format}: band text is replaced in a read source.");
            Require(DocumentSearch.Find(read, new TextQuery("old header")).Count == 1, $"{format}: Find searches the header of a read source.");

            var written = TestPackages.Write(replaced, format);
            var readBack = TestPackages.Read(written, format);
            var writtenHeader = string.Concat(
                ((Paragraph)readBack.Sections[0].Header!.Blocks[0]).Inlines.OfType<TextRun>().Select(run => run.Text));
            Require(writtenHeader == "new header", $"{format}: the replaced band text survives writing and reading.");
        }
    }

    private static void TableWidthPercentScalesExplicitColumns()
    {
        var table = new TableBlock(
        [
            new TableRow([new TableCell("A"), new TableCell("B")]),
        ])
        {
            WidthPercent = 50,
        };
        var widths = TableLayout.ResolveColumnWidthsWithPercent(table, 2, 500);
        Require(Math.Abs(widths.Sum() - 250) < 0.001, "Percent width scales a table without explicit columns to 50% of the page.");

        var explicitTable = new TableBlock(
        [
            new TableRow([new TableCell("A"), new TableCell("B")]),
        ])
        {
            ColumnWidths = new[] { Length.FromPoints(100), Length.FromPoints(120) },
            WidthPercent = 50,
        };
        var explicitWidths = TableLayout.ResolveColumnWidthsWithPercent(explicitTable, 2, 500);
        Require(Math.Abs(explicitWidths.Sum() - 220) < 0.001, "Explicit column widths within the target win over the percent width.");

        var wideTable = new TableBlock(
        [
            new TableRow([new TableCell("A"), new TableCell("B")]),
        ])
        {
            ColumnWidths = new[] { Length.FromPoints(400), Length.FromPoints(400) },
            WidthPercent = 50,
        };
        var wideWidths = TableLayout.ResolveColumnWidthsWithPercent(wideTable, 2, 500);
        Require(Math.Abs(wideWidths.Sum() - 250) < 0.001, "Wide explicit columns are scaled down to the percent width.");

        var partialTable = new TableBlock(
        [
            new TableRow([new TableCell("A"), new TableCell("B"), new TableCell("C")]),
        ])
        {
            ColumnWidths = new[] { Length.FromPoints(100) },
            WidthPercent = 50,
        };
        var partialWidths = TableLayout.ResolveColumnWidthsWithPercent(partialTable, 3, 500);
        Require(Math.Abs(partialWidths.Sum() - 250) < 0.001,
            "Percent width also bounds the remaining columns next to explicit widths.");
        Require(Math.Abs(partialWidths[0] - 100) < 0.001
            && Math.Abs(partialWidths[1] - 75) < 0.001
            && Math.Abs(partialWidths[2] - 75) < 0.001,
            "A partial explicit width is kept and the remaining columns share the rest of the percent target.");

        var document = new PortableDocument(new DocumentMetadata(), new Section(wideTable));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            _ = TestPackages.Write(document, format);
        }
    }


    private static void FieldResolveLeavesBandsOfReadSourceUnchanged()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("body")))
            {
                Header = new HeaderFooter(new Paragraph(new Field("QUOTE header"))),
            })
        {
            SourcePackage = new byte[] { 1 },
        };

        var resolved = FieldResolver.Resolve(document, FieldContext.Create());
        Require(ReferenceEquals(resolved.Sections[0].Header, document.Sections[0].Header), "A read source keeps its header when fields are resolved.");
    }

    private static string ReadTextEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] Docx(string bodyXml, params (string Path, string Xml)[] extraParts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "word/document.xml", $"<w:document xmlns:w='{W}'><w:body>{bodyXml}</w:body></w:document>");
            foreach (var (path, xml) in extraParts)
            {
                WriteEntry(zip, path, xml);
            }
        }

        return buffer.ToArray();
    }

    private static ReadResult ReadDocx(string bodyXml, params (string Path, string Xml)[] extraParts)
        => DocumentReader.Read(new MemoryStream(Docx(bodyXml, extraParts)), DocumentFormat.Docx);

    private static ReadResult ReadOdt(string bodyXml, string? styles = null, string automaticStyles = "")
    {
        const string Namespaces = " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
            + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
            + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
            + " xmlns:table='urn:oasis:names:tc:opendocument:xmlns:table:1.0'";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "content.xml",
                $"<office:document-content{Namespaces}><office:automatic-styles>{automaticStyles}</office:automatic-styles>"
                + $"<office:body><office:text>{bodyXml}</office:text></office:body></office:document-content>");
            if (styles is not null)
            {
                WriteEntry(zip, "styles.xml", styles);
            }
        }

        buffer.Position = 0;
        return DocumentReader.Read(buffer, DocumentFormat.Odt);
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
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

    /// <summary>A write-only stream without seeking (like a network or HTTP response stream).</summary>
    private sealed class NonSeekableReadStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableSink : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written += count;
    }
}
