using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// hardening of the readers against malformed and hostile input as well as correctness for
/// real files (incremental PDF updates, Word-typical run structures) and in
/// output into foreign streams.
/// </summary>
internal static class MalformedInputChecks
{
    internal static int Run()
    {
        try
        {
            PdfPageTreeCycleIsRejected();
            PdfSelfContainedObjectStreamIsRejected();
            PdfStrayDelimiterDoesNotHang();
            PdfHugeBfRangeWithSurrogateTargetIsBounded();
            PdfFlateBombIsRejected();
            PdfInvalidPredictorIsRejected();
            PdfIncrementalUpdateUsesNewestObject();
            PdfStreamWithoutLengthDropsCrLf();
            PdfFlippedCtmKeepsLineOrder();
            PdfWrittenAtStreamOffsetIsReadable();
            PngSecondIhdrIsRejected();
            ForeignXmlPartsUseTheXmlGuard();
            DeeplyNestedSignatureOctetsAreRejected();
            KeyDerivationCostIsBounded();
            PngPaletteTransparencyBecomesSoftMask();
            DocxWithHeaderIsReadable();
            DocxReadsTextInRunContainersAndBreaks();
            DocxPageFieldWithCachedResultIsNotDuplicated();
            DocxFlatListHasNoNestingWarning();
            DocxDuplicateNumIdIsTolerated();
            DocxNumberingWithTenLevelsIsRead();
            GarbageInputRaisesValidationException();
            DocxReportsDanglingNumberingId();
            OdtWithoutSectionsIsRead();
            OdtHugeSpaceCountIsRejected();
            OdtAnnotationsAndImagesAreNotInlined();
            NonSeekableOutputIsWrittenAsynchronously();
            Console.WriteLine("Malformed input: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Malformed input: " + exception);
            return 1;
        }
    }

    private static void PdfPageTreeCycleIsRejected()
    {
        var pdf = Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [2 0 R] /Count 1 >>");
        Expect<DocumentValidationException>(() => ReadPdf(pdf), "A cycle in the page tree is rejected (no stack overflow).");
    }

    private static void PdfSelfContainedObjectStreamIsRejected()
    {
        // xref stream: object 1 lies compressed in object stream 1 (so inside itself).
        var builder = new StringBuilder("%PDF-1.7\n");
        var xrefOffset = builder.Length;
        var entries = new byte[] { 2, 0, 0, 0, 1, 0, 0 };
        builder.Append("2 0 obj\n<< /Type /XRef /W [1 4 2] /Index [1 1] /Size 3 /Root 1 0 R /Length 7 >>\nstream\n");
        var head = Encoding.Latin1.GetBytes(builder.ToString());
        var tail = Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        var pdf = head.Concat(entries).Concat(tail).ToArray();
        Expect<DocumentValidationException>(() => ReadPdf(pdf), "A self-referential object stream is rejected.");
    }

    private static void PdfStrayDelimiterDoesNotHang()
    {
        var pdf = SinglePagePdf("BT } { ) ET");
        var task = Task.Run(() => ReadPdf(pdf));
        Require(task.Wait(TimeSpan.FromSeconds(10)), "Individual separators in the content stream do not lead to an infinite loop.");
    }

    private static void PdfHugeBfRangeWithSurrogateTargetIsBounded()
    {
        var cmap = "begincmap 1 beginbfrange <0000> <FFFFFFFF> <D83DDE00> endbfrange endcmap";
        var pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("BT /F1 12 Tf <00000001> Tj ET"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /X /ToUnicode 6 0 R >>",
            Stream(cmap));
        var text = PlainText(ReadPdf(pdf));
        Require(text == "\U0001F600\U0001F601", "bfrange with a surrogate pair target is incremented per code.");
    }

    private static void PdfFlateBombIsRejected()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (var index = 0; index < 300; index++)
            {
                zlib.Write(zeros);
            }
        }

        Expect<DocumentValidationException>(
            () => PdfStreamFilters.Decode(compressed.ToArray(), [new PdfFilter("FlateDecode", null)]),
            "A FlateDecode bomb is aborted at the limit.");
    }

    private static void PdfInvalidPredictorIsRejected()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write([1, 2, 3]);
        }

        var parameters = new PdfDictionary(new Dictionary<string, PdfValue>
        {
            ["Predictor"] = new PdfInteger(2),
            ["Columns"] = new PdfInteger(0),
        });
        Expect<DocumentValidationException>(
            () => PdfStreamFilters.Decode(compressed.ToArray(), [new PdfFilter("FlateDecode", parameters)]),
            "A predictor with /Columns 0 is rejected.");
    }

    private static void PdfIncrementalUpdateUsesNewestObject()
    {
        var original = SinglePagePdf("BT /F1 12 Tf (OLD) Tj ET");
        var text = Encoding.Latin1.GetString(original);
        var previous = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0];

        var update = new StringBuilder();
        var objectOffset = original.Length;
        update.Append($"4 0 obj\n{Stream("BT /F1 12 Tf (NEW) Tj ET")}\nendobj\n");
        var xrefOffset = objectOffset + update.Length;
        update.Append($"xref\n4 1\n{objectOffset:D10} 00000 n \ntrailer\n<< /Size 5 /Root 1 0 R /Prev {previous} >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        var updated = original.Concat(Encoding.Latin1.GetBytes(update.ToString())).ToArray();
        Require(PlainText(ReadPdf(updated)) == "NEW", "Incremental update: the newest object wins.");
    }

    private static void PdfStreamWithoutLengthDropsCrLf()
    {
        // /Length as an indirect reference: the reader searches for "endstream"; CRLF before it belongs
        // not part of the data.
        var pdf = Pdf("<< /Type /Catalog >>", "<< /Length 3 0 R >>\nstream\r\nabc\r\nendstream", "3");
        var file = PdfFile.Load(pdf);
        Require(file.GetObject(2) is PdfStreamValue stream && stream.Raw.AsSpan().SequenceEqual("abc"u8),
            "CRLF before endstream is removed completely.");
    }

    private static void PdfFlippedCtmKeepsLineOrder()
    {
        var pdf = SinglePagePdf(
            "1 0 0 -1 0 792 cm BT /F1 12 Tf 1 0 0 -1 72 100 Tm (First) Tj 1 0 0 -1 72 114 Tm (Second) Tj ET");
        var blocks = ReadPdf(pdf).Document.Sections[0].Blocks;
        Require(blocks.Count == 1 && PlainText(blocks[0]) == "First Second",
            "Trm = Tm × CTM: a mirrored coordinate system yields a paragraph in the correct order.");
    }

    private static void PdfWrittenAtStreamOffsetIsReadable()
    {
        var document = PortableDocument.Create().Paragraph("hello").Build();
        using var output = new MemoryStream();
        output.Write(new byte[100]);
        DocumentWriter.WriteAsync(document, output, DocumentFormat.Pdf).GetAwaiter().GetResult();

        var pdf = output.ToArray()[100..];
        Require(PlainText(ReadPdf(pdf)) == "hello", "PDF offsets count from the start of the PDF in the stream.");
    }

    private static void PngSecondIhdrIsRejected()
    {
        var png = Png(1, 1, colorType: 0, [0, 0], extraChunks: [("IHDR", Ihdr(60000, 60000, 0))]);
        Expect<ImageException>(() => PdfPngDecoder.Decode(png), "A second IHDR is rejected.");
    }

    private static void PngPaletteTransparencyBecomesSoftMask()
    {
        // One pixel with palette entry 0 (black), fully transparent: the color stays, the alpha
        // becomes the soft mask (instead of laying the pixel over white).
        var png = Png(1, 1, colorType: 3, [0, 0], extraChunks: [("PLTE", [0, 0, 0]), ("tRNS", [0])]);
        var decoded = PdfPngDecoder.Decode(png);
        Require(decoded.Samples.AsSpan().SequenceEqual(new byte[] { 0, 0, 0 }), "tRNS for palette images keeps the palette color.");
        Require(decoded.Alpha is [0], "tRNS for palette images becomes a soft mask.");
    }

    private static void DocxWithHeaderIsReadable()
    {
        var section = new Section(new Paragraph([new TextRun("body")]))
        {
            Header = new HeaderFooter(new Paragraph([new TextRun("head")])),
        };
        var document = new PortableDocument(new DocumentMetadata(), [section]);
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        buffer.Position = 0;

        var result = DocumentReader.Read(buffer, DocumentFormat.Docx);
        var readSection = result.Document.Sections[0];
        Require(
            readSection.Header is { } header
                && header.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<TextRun>().Any(r => r.Text == "head")),
            "DOCX with a header is read and the header is mapped.");
    }

    private static void DocxReadsTextInRunContainersAndBreaks()
    {
        var result = ReadDocxBody(
            "<w:p><w:r><w:t xml:space='preserve'>Visit </w:t></w:r><w:hyperlink><w:r><w:t>example.com</w:t></w:r></w:hyperlink>"
            + "<w:ins><w:r><w:t xml:space='preserve'> now</w:t></w:r></w:ins><w:del><w:r><w:delText>gone</w:delText></w:r></w:del></w:p>"
            + "<w:p><w:r><w:br w:type='page'/><w:t>After break</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:drawing/></w:r></w:p>"
            + "<w:sdt><w:sdtContent><w:p><w:r><w:t>In control</w:t></w:r></w:p></w:sdtContent></w:sdt>");

        var blocks = result.Document.Sections[0].Blocks;
        Require(blocks.Count == 5, $"DOCX: five blocks expected, read {blocks.Count}.");
        Require(PlainText(blocks[0]) == "Visit example.com nowgone", "Text in a hyperlink, an insertion and a deletion is read (the changes as markers).");
        Require(blocks[1] is PageBreak, "A page break before text is read as a page break.");
        Require(PlainText(blocks[2]) == "After break", "Text after the page break in the same run is preserved.");
        Require(PlainText(blocks[3]) == "a\tb\nc", "w:tab and w:br are read as \\t and \\n.");
        Require(PlainText(blocks[4]) == "In control", "Paragraphs in content controls are read.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("drawing", StringComparison.Ordinal)),
            "A drawing without a media part is reported.");
    }

    private static void DocxPageFieldWithCachedResultIsNotDuplicated()
    {
        var result = ReadDocxBody(
            "<w:p><w:r><w:t xml:space='preserve'>Page </w:t></w:r>"
            + "<w:r><w:fldChar w:fldCharType='begin'/></w:r><w:r><w:instrText> PAGE </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType='separate'/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType='end'/></w:r>"
            + "<w:r><w:t xml:space='preserve'> of </w:t></w:r>"
            + "<w:r><w:fldChar w:fldCharType='begin'/></w:r><w:r><w:instrText> NUMPAGES </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType='separate'/></w:r><w:r><w:t>5</w:t></w:r><w:r><w:fldChar w:fldCharType='end'/></w:r></w:p>");

        var inlines = ((Paragraph)result.Document.Sections[0].Blocks[0]).Inlines;
        Require(inlines.OfType<PageNumberField>().Count() == 1, "Exactly one page number field.");
        Require(inlines.OfType<PageCountField>().Count() == 1, "Exactly one page count field.");
        Require(PlainText(result.Document.Sections[0].Blocks[0]) == "Page  of ",
            "The cached PAGE and NUMPAGES results are dropped (the fields represent them).");
    }

    private static void DocxFlatListHasNoNestingWarning()
    {
        var document = PortableDocument.Create().BulletList("a", "b", "c").Build();
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        buffer.Position = 0;

        var result = DocumentReader.Read(buffer, DocumentFormat.Docx);
        Require(result.Warnings.Count == 0, "A flat list produces no nesting warning.");
        Require(result.Document.Sections[0].Blocks is [ListBlock { Items.Count: 3 }], "The list has three items.");
    }

    private static void DocxDuplicateNumIdIsTolerated()
    {
        var numbering = "<w:numbering xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>"
            + "<w:abstractNum w:abstractNumId='0'><w:lvl w:ilvl='0'><w:numFmt w:val='bullet'/></w:lvl></w:abstractNum>"
            + "<w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num><w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num>"
            + "</w:numbering>";
        var result = ReadDocxBody(
            "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>",
            ("word/numbering.xml", numbering));
        var list = result.Document.Sections[0].Blocks.OfType<ListBlock>().Single();
        Require(
            TestLists.Level(result.Document, list).Kind == ListKind.Bullet,
            "A duplicate numId does not abort reading.");
    }

    private static void DocxNumberingWithTenLevelsIsRead()
    {
        var levels = string.Concat(Enumerable.Range(0, 10).Select(level =>
            $"<w:lvl w:ilvl='{level}'><w:numFmt w:val='{(level == 9 ? "bullet" : "decimal")}'/></w:lvl>"));
        var numbering = "<w:numbering xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>"
            + "<w:abstractNum w:abstractNumId='0'>" + levels + "</w:abstractNum>"
            + "<w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num></w:numbering>";
        var result = ReadDocxBody(
            "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>",
            ("word/numbering.xml", numbering));
        var definition = result.Document.Numbering.Single();
        Require(
            definition.Levels.All(level => level.Kind == ListKind.Number),
            "A definition with ten levels is read; the tenth is dropped.");
    }

    private static void DocxReportsDanglingNumberingId()
    {
        var result = ReadDocxBody(
            "<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='4'/></w:numPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>");

        Require(result.Document.Sections[0].Blocks is [Paragraph], "A dangling numbering id is read as text.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("numbering id '4'", StringComparison.Ordinal)),
            "A dangling numbering id is reported.");
    }

    private static void GarbageInputRaisesValidationException()
    {
        var garbage = Encoding.ASCII.GetBytes("this is not a document");
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            Expect<DocumentValidationException>(
                () => DocumentReader.Read(new MemoryStream(garbage), format),
                $"{format}: invalid input yields DocumentValidationException.");
        }

        var brokenObjectStream = Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] >>", "<< /Type /Page /Contents [ 99999999999 0 R ] >>");
        Expect<DocumentValidationException>(() => ReadPdf(brokenObjectStream), "A malformed PDF structure gives DocumentValidationException.");
    }

    private static void OdtWithoutSectionsIsRead()
    {
        // LibreOffice puts content directly into office:text, without text:section.
        var odt = Odt("<text:sequence-decls/><text:h text:outline-level='1'>Title</text:h><text:p>Paragraph</text:p>");
        var result = DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt);
        Require(result.Document.Sections is [{ Blocks: [Heading, Paragraph] }],
            "ODT without text:section: content forms a section.");
    }

    private static void OdtHugeSpaceCountIsRejected()
    {
        var odt = Odt("<text:p>a<text:s text:c='2000000000'/>b</text:p>");
        Expect<DocumentValidationException>(() => DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt),
            "text:s with a huge counter is rejected at the text limit.",
            messageContains: "limit");
    }

    private static void OdtAnnotationsAndImagesAreNotInlined()
    {
        var odt = Odt(
            "<text:p>Body<office:annotation><text:p>Comment</text:p></office:annotation>"
            + "<draw:frame><draw:image/></draw:frame><text:a>link<text:s text:c='2'/>text</text:a></text:p>");
        var result = DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt);
        Require(PlainText(result.Document.Sections[0].Blocks[0]) == "Bodylink  text",
            "Comment text is not adopted into the paragraph; spaces in links are preserved.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("image", StringComparison.Ordinal)), "An image without a part is reported.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("comment", StringComparison.Ordinal))
            && result.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<Comment>().Count() == 1,
            "A comment is modelled as a comment, not reported.");
    }

    private static void NonSeekableOutputIsWrittenAsynchronously()
    {
        var document = PortableDocument.Create().Paragraph("async").Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            var output = new AsyncOnlyStream();
            DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
            Require(output.Length > 0, $"{format}: a non-seekable stream is written exclusively asynchronously.");
        }
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static ReadResult ReadPdf(byte[] pdf) => DocumentReader.Read(new MemoryStream(pdf), DocumentFormat.Pdf);

    private static byte[] SinglePagePdf(string content) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
        Stream(content));

    private static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Builds a PDF with a classic xref table from the given objects (number = index + 1).</summary>
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

    private static string PlainText(ReadResult result)
        => string.Join(" ", result.Document.Sections.SelectMany(section => section.Blocks).Select(PlainText));

    private static string PlainText(Block block) => block switch
    {
        Paragraph paragraph => string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text)),
        Heading heading => string.Concat(heading.Inlines.OfType<TextRun>().Select(run => run.Text)),
        _ => string.Empty,
    };

    private static ReadResult ReadDocxBody(string bodyXml, params (string Path, string Xml)[] extraParts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "word/document.xml",
                "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body>"
                + bodyXml + "</w:body></w:document>");
            foreach (var (path, xml) in extraParts)
            {
                WriteEntry(zip, path, xml);
            }
        }

        buffer.Position = 0;
        return DocumentReader.Read(buffer, DocumentFormat.Docx);
    }

    private static byte[] Odt(string bodyXml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "content.xml",
                "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:draw='urn:oasis:names:tc:opendocument:xmlns:drawing:1.0'>"
                + "<office:body><office:text>" + bodyXml + "</office:text></office:body></office:document-content>");
        }

        return buffer.ToArray();
    }

    private static void ForeignXmlPartsUseTheXmlGuard()
    {
        // Chart parts, ODT objects and custom XML were loaded without the guard: a DTD was accepted and the depth was
        // unbounded (loading time grows with the square of the depth, so a deep part blocks a thread for hours).
        var payloads = new[]
        {
            "<!DOCTYPE x [<!ENTITY e \"text\">]><x>&e;</x>",
            string.Concat(Enumerable.Repeat("<x>", ReaderGuard.MaxXmlDepth + 100)) + string.Concat(Enumerable.Repeat("</x>", ReaderGuard.MaxXmlDepth + 100)),
        };
        var chart = new Chart(ChartKind.Column, [new ChartSeries("s", [1.0, 2.0])], ["a", "b"]);
        var withChart = new PortableDocument(new DocumentMetadata(), new Section(chart));
        var withCustomXml = new PortableDocument(
            new DocumentMetadata { CustomXmlParts = [new CustomXmlPart { Xml = "<data/>" }] },
            new Section(new Paragraph(new TextRun("Body"))));
        foreach (var payload in payloads)
        {
            var docx = ReplaceEntries(Write(withChart, DocumentFormat.Docx), name => name.StartsWith("word/charts/chart", StringComparison.Ordinal), payload);
            var read = DocumentReader.Read(new MemoryStream(docx), DocumentFormat.Docx);
            Require(!read.Document.Sections[0].Blocks.OfType<Chart>().Any() && read.Warnings.Any(warning => warning.Message.Contains("chart part", StringComparison.Ordinal)),
                "DOCX: a chart part with a DTD or deep nesting is skipped with a warning.");

            var odt = ReplaceEntries(Write(withChart, DocumentFormat.Odt), name => name.EndsWith("/content.xml", StringComparison.Ordinal), payload);
            read = DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt);
            Require(!read.Document.Sections[0].Blocks.OfType<Chart>().Any() && read.Warnings.Any(warning => warning.Message.Contains("cannot be read", StringComparison.Ordinal)),
                "ODT: an embedded object with a DTD or deep nesting is skipped with a warning.");

            var custom = ReplaceEntries(Write(withCustomXml, DocumentFormat.Docx), name => name.StartsWith("customXml/item", StringComparison.Ordinal) && !name.Contains("Props", StringComparison.Ordinal), payload);
            Require(DocumentReader.Read(new MemoryStream(custom), DocumentFormat.Docx).Document.Metadata.CustomXmlParts is not { Count: > 0 },
                "DOCX: a custom XML part with a DTD or deep nesting is skipped.");
        }

        Expect<ArgumentException>(() => _ = new CustomXmlPart { Xml = payloads[0] }, "A custom XML part with a DTD is rejected.");
    }

    private static void DeeplyNestedSignatureOctetsAreRejected()
    {
        // A constructed OCTET STRING nested 200,000 times (1.2 MB): joining it recursed once per level and overflowed
        // the stack, which ends the whole process instead of marking the signature invalid.
        const int levels = 200_000;
        var der = new byte[(levels * 6) + 3];
        for (var level = 0; level < levels; level++)
        {
            der[level * 6] = 0x24;
            der[(level * 6) + 1] = 0x84;
            BinaryPrimitives.WriteInt32BigEndian(der.AsSpan((level * 6) + 2), ((levels - 1 - level) * 6) + 3);
        }

        der[^3] = 0x04;
        der[^2] = 0x01;
        der[^1] = 0x41;
        Expect<DocumentValidationException>(() => DerNode.Parse(der, 0, der.Length).OctetValue(), "A deeply nested OCTET STRING is rejected.", "nested too deeply");
    }

    private static void KeyDerivationCostIsBounded()
    {
        // The file sets how many hash rounds derive the key: 2^31 rounds kept a core busy for many minutes per read.
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(
            new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x")))),
            buffer,
            new DocxWriteOptions { Encryption = new DocxEncryptionOptions("pw") { SpinCount = 1000 } }).GetAwaiter().GetResult();
        var info = CompoundFile.Read(buffer.ToArray(), "EncryptionInfo")!;
        var xml = Encoding.UTF8.GetString(info, 8, info.Length - 8);
        Require(xml.Contains("spinCount=\"1000\"", StringComparison.Ordinal), "Test setup: the spin count is in the encryption information.");
        Expect<DocumentValidationException>(
            () => DocxAgileEncryption.ParseDescriptor(xml.Replace("spinCount=\"1000\"", "spinCount=\"2000000000\"", StringComparison.Ordinal)),
            "DOCX: a spin count above 10,000,000 is rejected.");
        Expect<ArgumentOutOfRangeException>(() => _ = new DocxEncryptionOptions("pw") { SpinCount = DocxAgileEncryption.MaxSpinCount + 1 }, "DOCX: the writer accepts no spin count above 10,000,000.");

        XNamespace manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        XElement Data(int iterations, int keySize = 32) => new(manifest + "encryption-data",
            new XElement(manifest + "key-derivation", new XAttribute(manifest + "iteration-count", iterations), new XAttribute(manifest + "key-size", keySize)));
        Expect<DocumentValidationException>(() => OdtEncryption.ParseInfo(Data(2_000_000_000)), "ODT: an iteration count above 1,000,000 is rejected.");
        Expect<DocumentValidationException>(() => OdtEncryption.ParseInfo(Data(1000, int.MaxValue)), "ODT: a huge key size is rejected.");

        // Each entry within the limit, but sixty of them: the total is bounded before any key is derived.
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = Enumerable.Range(0, 60).Select(index => new XElement(manifest + "file-entry",
                new XAttribute(manifest + "full-path", $"part{index}.xml"), Data(OdtEncryption.MaxIterations)));
            WriteEntry(zip, "META-INF/manifest.xml", new XElement(manifest + "manifest", entries).ToString());
        }

        Expect<DocumentValidationException>(() => OdtEncryption.TryDecryptPackage(package.ToArray(), "pw"), "ODT: the total key derivation cost is bounded.", "in total");
    }

    private static byte[] Write(PortableDocument document, DocumentFormat format)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>Copies a package and replaces the content of every entry whose name matches.</summary>
    private static byte[] ReplaceEntries(byte[] package, Func<string, bool> match, string content)
    {
        using var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        var replaced = 0;
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var to = target.CreateEntry(entry.FullName).Open();
                if (match(entry.FullName))
                {
                    replaced++;
                    to.Write(Encoding.UTF8.GetBytes(content));
                }
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }

        Require(replaced > 0, "Test setup: the package has the entry to replace.");
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static byte[] Ihdr(int width, int height, byte colorType)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data, width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8;
        data[9] = colorType;
        return data;
    }

    /// <summary>Builds an 8-bit PNG; <paramref name="extraChunks"/> stand between IHDR and IDAT.</summary>
    private static byte[] Png(int width, int height, byte colorType, byte[] rawScanlines, (string Type, byte[] Data)[] extraChunks)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", Ihdr(width, height, colorType));
        foreach (var (type, data) in extraChunks)
        {
            WriteChunk(output, type, data);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rawScanlines);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(Encoding.ASCII.GetBytes(type));
        output.Write(data);
        output.Write(new byte[4]); // the CRC is not checked by the decoder
    }

    private static void Expect<TException>(Action action, string message, string? messageContains = null)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception) when (messageContains is null || exception.Message.Contains(messageContains, StringComparison.Ordinal))
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

    /// <summary>Non-seekable stream that forbids synchronous writing (like ASP.NET Core).</summary>
    private sealed class AsyncOnlyStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override void Write(ReadOnlySpan<byte> buffer)
            => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
