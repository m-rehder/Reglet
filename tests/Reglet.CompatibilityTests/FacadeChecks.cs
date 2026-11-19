using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Reglet;

internal static class FacadeChecks
{
    internal static int Run()
    {
        try
        {
            WritesToPath();
            WritesToStream();
            WritesPdfToNonSeekableStream();
            StreamsNonSeekableOutput();
            DefaultMarginsMatchAcrossFormats();
            HeadingSizeBaseline();
            ColorMappingMatchesAcrossFormats();
            TableWidthsMatchAcrossFormats();
            ImageScaleMatchesAcrossFormats();
            ReportsUnsupportedFeatures();
            ReportsNoWarningsForSupportedContent();
            CleansInvalidXmlCharacters();
            EnforcesImageLimits();
            EnforcesDocumentLimits();
            EnforcesNestingDepth();
            HonorsCancellation();
            CreatesNoTemporaryFiles();
            WritesInParallel();
            ErrorMessagesAvoidDocumentContent();
            PassesFormatOptions();
            ReportsMetrics();
            Console.WriteLine("Facade (E04/E05/E07): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Facade (E04): " + exception);
            return 1;
        }
    }

    private static void ReportsMetrics()
    {
        var document = PortableDocument.Create().Paragraph("Hello").Build();

        foreach (var (format, options) in new (DocumentFormat Format, WriteOptions Options)[]
        {
            (DocumentFormat.Docx, new DocxWriteOptions { CollectMetrics = true }),
            (DocumentFormat.Odt, new OdtWriteOptions { CollectMetrics = true }),
            (DocumentFormat.Pdf, new PdfWriteOptions { CollectMetrics = true }),
        })
        {
            using var buffer = new MemoryStream();
            var result = DocumentWriter.WriteAsync(document, buffer, format, options).GetAwaiter().GetResult();
            var metrics = result.Metrics;

            Require(metrics is not null, $"{format}: metrics are reported.");
            Require(metrics!.Bytes == buffer.Length, $"{format}: bytes match the output.");
            Require(metrics.Duration >= TimeSpan.Zero, $"{format}: duration is set.");
            if (format == DocumentFormat.Pdf)
            {
                Require(metrics.Pages >= 1, "PDF: page count is reported.");
            }
            else
            {
                Require(metrics.Pages is null, $"{format}: page count is unknown (null).");
            }
        }

        using var plain = new MemoryStream();
        var without = DocumentWriter.WriteAsync(document, plain, DocumentFormat.Pdf).GetAwaiter().GetResult();
        Require(without.Metrics is null, "Without CollectMetrics no metrics are reported.");
    }

    private static void WritesToPath()
    {
        var document = PortableDocument.Create().Paragraph("Hello").Build();
        var directory = Path.Combine(Path.GetTempPath(), "reglet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            foreach (var (extension, magic) in new[] { ("docx", "PK"), ("odt", "PK"), ("pdf", "%PDF-1.7") })
            {
                var path = Path.Combine(directory, "report." + extension);
                DocumentWriter.WriteAsync(document, path).GetAwaiter().GetResult();

                var bytes = File.ReadAllBytes(path);
                Require(bytes.Length > 0, $"{extension}: file was written.");
                Require(
                    Encoding.Latin1.GetString(bytes).StartsWith(magic, StringComparison.Ordinal),
                    $"{extension}: file begins with {magic}.");
            }

            Require(DocumentWriter.FormatFromPath("REPORT.DOCX") == DocumentFormat.Docx, "Extension is normalized.");
            Require(DocumentWriter.FormatFromPath("a.odt") == DocumentFormat.Odt, "odt recognised.");
            Require(DocumentWriter.FormatFromPath("a.pdf") == DocumentFormat.Pdf, "pdf recognised.");
            Throws<NotSupportedException>(() => DocumentWriter.FormatFromPath("a.txt"));
            Throws<UnsupportedFeatureException>(() => DocumentWriter.FormatFromPath("report"));
            Throws<UnsupportedFeatureException>(() => DocumentWriter.FormatFromPath("report.txt"));
            Require(
                DocumentWriter.FormatFromPath("report.docx").Equals(DocumentFormat.Docx),
                "Extension with a leading dot is recognized.");

            var existing = Path.Combine(directory, "existing.docx");
            File.WriteAllText(existing, "Original");
            Throws<ArgumentException>(() => DocumentWriter.WriteAsync(
                document, existing, new OdtWriteOptions()).GetAwaiter().GetResult());
            Require(File.ReadAllText(existing) == "Original", "Invalid options do not overwrite a file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WritesToStream()
    {
        var document = PortableDocument.Create().Paragraph("Stream").Build();

        using var pdf = new TrackingStream();
        DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        Require(!pdf.Disposed, "The foreign stream remains open.");
        Require(
            Encoding.Latin1.GetString(pdf.ToArray()).StartsWith("%PDF-1.7", StringComparison.Ordinal),
            "PDF header in the stream.");

        using var odt = new TrackingStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt, new OdtWriteOptions()).GetAwaiter().GetResult();
        Require(odt.Length > 0, "ODT written with options.");

        // A format error must remain visible and must not modify the stream.
        using var docx = new TrackingStream();
        Throws<ArgumentException>(() => DocumentWriter.WriteAsync(
            document, docx, DocumentFormat.Docx, new OdtWriteOptions()).GetAwaiter().GetResult());
        Require(docx.Length == 0, "Foreign options write no data.");
    }

    /// <summary>
    /// E04: PDF internally needs the stream position. A non-seekable target stream is
    /// buffered and the finished PDF is then transferred.
    /// </summary>
    private static void WritesPdfToNonSeekableStream()
    {
        var document = PortableDocument.Create().Heading("Title", 1).Paragraph("Not seekable").Build();

        using var target = new NonSeekableStream();
        DocumentWriter.WriteAsync(document, target, DocumentFormat.Pdf).GetAwaiter().GetResult();

        var bytes = target.ToArray();
        Require(bytes.Length > 0, "PDF written into a non-seekable stream.");
        Require(
            Encoding.Latin1.GetString(bytes).StartsWith("%PDF-1.7", StringComparison.Ordinal),
            "PDF header in the non-seekable stream.");
    }

    private static void StreamsNonSeekableOutput()
    {
        var document = PortableDocument.Create().Heading("Title", 1).Paragraph("Streaming").Build();

        foreach (var (format, options, magic) in new (DocumentFormat Format, WriteOptions Options, string Magic)[]
        {
            (DocumentFormat.Docx, new DocxWriteOptions { StreamNonSeekableOutput = true, CollectMetrics = true }, "PK"),
            (DocumentFormat.Odt, new OdtWriteOptions { StreamNonSeekableOutput = true, CollectMetrics = true }, "PK"),
            (DocumentFormat.Pdf, new PdfWriteOptions { StreamNonSeekableOutput = true, CollectMetrics = true }, "%PDF-1.7"),
        })
        {
            using var target = new NonSeekableStream();
            var result = DocumentWriter.WriteAsync(document, target, format, options).GetAwaiter().GetResult();
            var bytes = target.ToArray();

            Require(
                Encoding.Latin1.GetString(bytes).StartsWith(magic, StringComparison.Ordinal),
                $"{format}: header in the directly written, non-seekable target.");
            Require(result.Metrics is { Bytes: > 0 }, $"{format}: bytes are reported.");
            Require(result.Metrics!.Bytes == bytes.Length, $"{format}: reported bytes match the target.");
        }
    }

    private static void DefaultMarginsMatchAcrossFormats()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();

        using var docx = new MemoryStream();
        DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
        var body = ReadEntry(docxZip, "word/document.xml");
        Require(body.Contains("w:left=\"1440\"", StringComparison.Ordinal), "DOCX: 1 in = 1440 twips.");
        Require(body.Contains("w:w=\"11906\"", StringComparison.Ordinal), "DOCX: A4 width.");

        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var styles = ReadEntry(odtZip, "styles.xml");
        Require(styles.Contains("fo:margin-left=\"72pt\"", StringComparison.Ordinal), "ODT: 1 in = 72pt.");
        Require(styles.Contains("fo:page-width=\"595.28pt\"", StringComparison.Ordinal), "ODT: A4 width.");

        using var pdf = new MemoryStream();
        DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        var pdfText = Encoding.Latin1.GetString(pdf.ToArray());
        Require(pdfText.Contains("595.27559", StringComparison.Ordinal), "PDF: A4 width.");
    }

    private static void HeadingSizeBaseline()
    {
        Require(Typography.HeadingSize(HeadingLevel.Level2).Points == 14, "Baseline for level 2 is 14 pt.");
        Require(Typography.HeadingSize(HeadingLevel.Level1).Points == 16, "Baseline for level 1 is 16 pt.");

        var document = PortableDocument.Create().Heading("Title", level: 2).Build();

        using var docx = new MemoryStream();
        DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
        Require(
            ReadEntry(docxZip, "word/styles.xml").Contains("w:sz w:val=\"28\"", StringComparison.Ordinal),
            "DOCX: 14pt = 28 half-points.");

        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        Require(
            ReadEntry(odtZip, "styles.xml").Contains("fo:font-size=\"14pt\"", StringComparison.Ordinal),
            "ODT: 14 pt from the baseline.");

        using var pdf = new MemoryStream();
        DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        var content = ReadPdfContent(pdf.ToArray());
        Require(content.Contains("/F", StringComparison.Ordinal) && content.Contains(" 14 Tf", StringComparison.Ordinal),
            "PDF: 14 pt from the baseline.");
        var headingPosition = System.Text.RegularExpressions.Regex.Match(content,
            @"[0-9.]+ ([0-9.]+) Td\n\(Title\) Tj");
        Require(headingPosition.Success, "PDF: heading was positioned.");
        var y = double.Parse(headingPosition.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // The baseline lies at the font's ascender below the line top.
        var expectedY = PageSize.A4.Height.Points - PageMargins.Normal.Top.Points
            - Typography.HeadingSpaceBefore.Points
            - new PdfStandardFontMetrics("Helvetica-Bold").AscentEm * Typography.HeadingSize(HeadingLevel.Level2).Points;
        Require(Math.Abs(y - expectedY) < 0.01, "PDF: heading spacing from the baseline.");
    }

    private static void ColorMappingMatchesAcrossFormats()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("x", color: Color.FromHex("#1A2B3C")))
            .Build();

        using var docx = new MemoryStream();
        DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
        Require(
            ReadEntry(docxZip, "word/document.xml").Contains("w:color w:val=\"1A2B3C\"", StringComparison.Ordinal),
            "DOCX: color as RRGGBB.");

        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        Require(
            ReadEntry(odtZip, "content.xml").Contains("fo:color=\"#1A2B3C\"", StringComparison.Ordinal),
            "ODT: color as #RRGGBB.");

        using var pdf = new MemoryStream();
        DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        var content = ReadPdfContent(pdf.ToArray());
        var color = System.Text.RegularExpressions.Regex.Match(content,
            @"([0-9.]+) ([0-9.]+) ([0-9.]+) rg");
        Require(color.Success, "PDF: RGB color in the content stream.");
        var channels = new[] { 0x1A, 0x2B, 0x3C };
        for (var index = 0; index < channels.Length; index++)
        {
            var channel = double.Parse(color.Groups[index + 1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Require(Math.Abs(channel - channels[index] / 255.0) <= 0.0005,
                $"PDF: RGB channel {index} is {channel}, expected {channels[index] / 255.0}.");
        }
    }

    private static void ImageScaleMatchesAcrossFormats()
    {
        var png = CreatePng(96, 48);
        foreach (var (mode, boxWidth, boxHeight, expectedWidth, expectedHeight) in new[]
        {
            (ImageScaleMode.Original, (Length?)null, (Length?)null, 72.0, 36.0),
            (ImageScaleMode.FitWidth, Length.FromPoints(144), (Length?)null, 144.0, 72.0),
            (ImageScaleMode.Contain, Length.FromPoints(100), Length.FromPoints(30), 60.0, 30.0),
        })
        {
            var document = PortableDocument.Create()
                .Image(png, ImageMediaType.Png, width: boxWidth, height: boxHeight, scaleMode: mode)
                .Build();

            using var docx = new MemoryStream();
            DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
            using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
            var body = ReadEntry(docxZip, "word/document.xml");
            Require(body.Contains($"cx=\"{(long)(expectedWidth * 12700)}\"", StringComparison.Ordinal), $"DOCX: {mode} width.");
            Require(body.Contains($"cy=\"{(long)(expectedHeight * 12700)}\"", StringComparison.Ordinal), $"DOCX: {mode} height.");

            using var odt = new MemoryStream();
            DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
            using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
            var content = ReadEntry(odtZip, "content.xml");
            var expectedWidthCm = (expectedWidth * 2.54 / 72).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var expectedHeightCm = (expectedHeight * 2.54 / 72).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Require(content.Contains($"svg:width=\"{expectedWidthCm}cm\"", StringComparison.Ordinal), $"ODT: {mode} width.");
            Require(content.Contains($"svg:height=\"{expectedHeightCm}cm\"", StringComparison.Ordinal), $"ODT: {mode} height.");

            using var pdf = new MemoryStream();
            DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
            var pdfContent = ReadPdfContent(pdf.ToArray());
            Require(pdfContent.Contains($"{expectedWidth} 0 0 {expectedHeight} ", StringComparison.Ordinal),
                $"PDF: {mode}-Transformation.");
        }
    }

    private static void TableWidthsMatchAcrossFormats()
    {
        var document = PortableDocument.Create()
            .Page(new PageSize(Length.FromPoints(300), Length.FromPoints(300)),
                margins: new PageMargins(Length.FromPoints(10)))
            .Table(table => table.Row("A", "B", "C").ColumnWidths(Length.FromPoints(80)))
            .Build();

        using var docx = new MemoryStream();
        DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
        var body = ReadEntry(docxZip, "word/document.xml");
        Require(body.Contains("gridCol w:w=\"1600\"", StringComparison.Ordinal), "DOCX: first column is 80 pt.");
        Require(CountOccurrences(body, "gridCol w:w=\"2000\"") == 2, "DOCX: remaining columns are 100 pt each.");

        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(odtZip, "content.xml");
        Require(content.Contains("style:column-width=\"2.822cm\"", StringComparison.Ordinal), "ODT: first column is 80 pt.");
        Require(content.Contains("style:column-width=\"3.528cm\"", StringComparison.Ordinal), "ODT: remaining columns are 100 pt each.");

        using var pdf = new MemoryStream();
        DocumentWriter.WriteAsync(document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        var pdfContent = ReadPdfContent(pdf.ToArray());
        // left margin 10 pt plus shared cell padding, then the column widths 80/100/100 pt.
        var padding = Typography.TableCellPaddingHorizontal.Points;
        foreach (var (letter, expectedX) in new[] { ("A", 10 + padding), ("B", 90 + padding), ("C", 190 + padding) })
        {
            var position = System.Text.RegularExpressions.Regex.Match(pdfContent,
                $@"([0-9.]+) [0-9.]+ Td\n\({letter}\) Tj");
            Require(position.Success, $"PDF: cell {letter} present.");
            var x = double.Parse(position.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Require(Math.Abs(x - expectedX) < 0.01, $"PDF: cell {letter} uses the shared column width.");
        }
    }

    private static int CountOccurrences(string text, string needle)
        => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static byte[] CreatePng(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(bytes, "IHDR", ihdr);
        var pixels = new byte[height * (1 + width * 3)];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        AppendChunk(bytes, "IDAT", compressed.ToArray());
        AppendChunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static string ReadPdfContent(byte[] pdf)
    {
        var latin = Encoding.Latin1.GetString(pdf);
        var output = new StringBuilder();
        var index = 0;
        while ((index = latin.IndexOf("/Filter /FlateDecode", index, StringComparison.Ordinal)) >= 0)
        {
            var streamMarker = latin.IndexOf("stream\n", index, StringComparison.Ordinal);
            var streamStart = streamMarker + "stream\n".Length;
            var streamEnd = latin.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            Require(streamMarker >= 0 && streamEnd > streamStart, "PDF stream present.");
            using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var content = new MemoryStream();
            zlib.CopyTo(content);
            output.Append(Encoding.Latin1.GetString(content.ToArray()));
            index = streamEnd;
        }

        return output.ToString();
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

    private static void ReportsUnsupportedFeatures()
    {
        // Underline has been drawn as a line since N05; it produces no warning.
        var underlined = PortableDocument.Create()
            .Paragraph(p => p.Span("x", underline: true))
            .Build();

        using var pdf = new MemoryStream();
        var warnResult = DocumentWriter
            .WriteAsync(underlined, pdf, DocumentFormat.Pdf, new PdfWriteOptions())
            .GetAwaiter().GetResult();
        Require(!warnResult.HasWarnings, "PDF writes underline without a warning.");

        var content = ReadPdfContent(pdf.ToArray());
        Require(content.Contains("m\n", StringComparison.Ordinal) && content.Contains("S\n", StringComparison.Ordinal),
            "PDF draws the underline.");

        using var failStream = new MemoryStream();
        DocumentWriter
            .WriteAsync(
                underlined,
                failStream,
                DocumentFormat.Pdf,
                new PdfWriteOptions { UnsupportedContent = UnsupportedContentBehavior.Fail })
            .GetAwaiter().GetResult();

        // ODT has written headers and page number fields since N03; a page break in the
        // header remains unrenderable and is reported.
        var section = new Section(new Paragraph(new TextRun("x")))
        {
            Header = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() }), new PageBreak() }),
        };
        var withHeader = new PortableDocument(new DocumentMetadata(), section);
        using var odt = new MemoryStream();
        var odtResult = DocumentWriter
            .WriteAsync(withHeader, odt, DocumentFormat.Odt, new OdtWriteOptions())
            .GetAwaiter().GetResult();
        Require(odtResult.Warnings.Count == 1, "ODT reports only the page break.");
        Require(
            odtResult.Warnings[0].Message.Contains("page breaks in the header", StringComparison.Ordinal),
            "ODT warns about an unrenderable page break in the header.");

        var imageWithAltText = PortableDocument.Create()
            .Image(CreatePng(2, 1), ImageMediaType.Png, altText: "Chart")
            .Build();
        using var pdfImageStream = new MemoryStream();
        var imageResult = DocumentWriter.WriteAsync(imageWithAltText, pdfImageStream, DocumentFormat.Pdf)
            .GetAwaiter().GetResult();
        Require(!imageResult.Warnings.Any(warning => warning.Message.Contains("Alternative text", StringComparison.Ordinal)),
            "PDF writes image alternative text without a warning (marked content).");
        var pdfContent = ReadPdfContent(pdfImageStream.ToArray());
        Require(pdfContent.Contains("BDC", StringComparison.Ordinal) && pdfContent.Contains("EMC", StringComparison.Ordinal),
            "PDF encloses the image with marked content.");

        var unsupportedText = PortableDocument.Create().Paragraph("中文").Build();
        using var pdfTextStream = new MemoryStream();
        var textResult = DocumentWriter.WriteAsync(unsupportedText, pdfTextStream, DocumentFormat.Pdf)
            .GetAwaiter().GetResult();
        Require(textResult.Warnings.Any(warning => warning.Message.Contains("WinAnsi", StringComparison.Ordinal)),
            "PDF reports replaced characters outside WinAnsi.");

        using var pdfFailStream = new MemoryStream();
        Throws<NotSupportedException>(() => DocumentWriter.WriteAsync(unsupportedText, pdfFailStream,
            DocumentFormat.Pdf, new PdfWriteOptions { UnsupportedContent = UnsupportedContentBehavior.Fail })
            .GetAwaiter().GetResult());

        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));
        var missingGlyph = PortableDocument.Create()
            .Paragraph(paragraph => paragraph.Span("AB", fontFamily: "TestFont"))
            .Build();
        using var embeddedStream = new MemoryStream();
        var embeddedResult = DocumentWriter.WriteAsync(missingGlyph, embeddedStream, DocumentFormat.Pdf,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["TestFont"] = fontData } })
            .GetAwaiter().GetResult();
        Require(embeddedResult.Warnings.Any(warning => warning.Message.Contains("glyphs", StringComparison.Ordinal)),
            "PDF reports missing glyphs of an embedded font.");

        var boldEmbedded = PortableDocument.Create()
            .Paragraph(paragraph => paragraph.Span("A", bold: true, fontFamily: "TestFont"))
            .Build();
        using var boldStream = new MemoryStream();
        var boldResult = DocumentWriter.WriteAsync(boldEmbedded, boldStream, DocumentFormat.Pdf,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["TestFont"] = fontData } })
            .GetAwaiter().GetResult();
        Require(!boldResult.Warnings.Any(warning => warning.Message.Contains("embedded fonts", StringComparison.Ordinal)),
            "PDF produces bold for embedded fonts synthetically (without a warning).");
        Require(ReadPdfContent(boldStream.ToArray()).Contains("2 Tr", StringComparison.Ordinal),
            "PDF uses render mode 2 for synthetic bold.");

        var headerWithUnsupportedText = new Section(new Paragraph(new TextRun("x")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("中文"))),
        };
        using var pdfHeaderStream = new MemoryStream();
        var headerResult = DocumentWriter.WriteAsync(
            new PortableDocument(new DocumentMetadata(), headerWithUnsupportedText),
            pdfHeaderStream, DocumentFormat.Pdf).GetAwaiter().GetResult();
        Require(headerResult.Warnings.Any(warning => warning.Message.Contains("replaced with '?'", StringComparison.Ordinal)),
            "PDF reports replaced characters in the header.");
    }

    private static void ReportsNoWarningsForSupportedContent()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();
        using var stream = new MemoryStream();
        var result = DocumentWriter
            .WriteAsync(document, stream, DocumentFormat.Pdf, new PdfWriteOptions())
            .GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Supported content produces no warnings.");
        Require(WriteResult.Empty.Warnings.Count == 0, "WriteResult.Empty is empty.");
    }

    private static void CleansInvalidXmlCharacters()
    {
        var document = PortableDocument.Create().Paragraph("A\u0001B").Build();

        using var docx = new MemoryStream();
        DocumentWriter.WriteAsync(document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var docxZip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read);
        var body = ReadEntry(docxZip, "word/document.xml");
        Require(!body.Contains('\u0001'), "DOCX contains no invalid XML character.");
        Require(body.Contains("A\uFFFDB", StringComparison.Ordinal), "DOCX replaces the character with U+FFFD.");

        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(document, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var odtZip = new ZipArchive(new MemoryStream(odt.ToArray()), ZipArchiveMode.Read);
        var content = ReadEntry(odtZip, "content.xml");
        Require(!content.Contains('\u0001'), "ODT contains no invalid XML character.");

        using var failStream = new MemoryStream();
        Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(
            document,
            failStream,
            DocumentFormat.Docx,
            new DocxWriteOptions { InvalidXmlCharacters = InvalidXmlCharacterBehavior.Fail }).GetAwaiter().GetResult());
    }

    private static void EnforcesImageLimits()
    {
        var png = CreatePng(96, 48);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();

        using var bytesStream = new MemoryStream();
        Throws<ImageException>(() => DocumentWriter.WriteAsync(
            document,
            bytesStream,
            DocumentFormat.Docx,
            new DocxWriteOptions { Limits = new ResourceLimits { MaxImageBytes = 1 } }).GetAwaiter().GetResult());

        using var pixelsStream = new MemoryStream();
        Throws<ImageException>(() => DocumentWriter.WriteAsync(
            document,
            pixelsStream,
            DocumentFormat.Pdf,
            new PdfWriteOptions { Limits = new ResourceLimits { MaxImagePixels = 1 } }).GetAwaiter().GetResult());
    }

    private static void EnforcesDocumentLimits()
    {
        var manyBlocks = PortableDocument.Create().Paragraph("a").Paragraph("b").Build();
        using var blocksStream = new MemoryStream();
        Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(
            manyBlocks,
            blocksStream,
            DocumentFormat.Odt,
            new OdtWriteOptions { Limits = new ResourceLimits { MaxBlocks = 1 } }).GetAwaiter().GetResult());

        var manyCells = PortableDocument.Create().Table(t => t.Row("a", "b")).Build();
        using var cellsStream = new MemoryStream();
        Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(
            manyCells,
            cellsStream,
            DocumentFormat.Pdf,
            new PdfWriteOptions { Limits = new ResourceLimits { MaxTableCells = 1 } }).GetAwaiter().GetResult());

        var longText = PortableDocument.Create().Paragraph(new string('x', 10)).Build();
        using var textStream = new MemoryStream();
        Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(
            longText,
            textStream,
            DocumentFormat.Docx,
            new DocxWriteOptions { Limits = new ResourceLimits { MaxTextLength = 3 } }).GetAwaiter().GetResult());
    }

    private static Block NestedTables(int depth)
    {
        Block block = new Paragraph(new TextRun("x"));
        for (var level = 0; level < depth; level++)
        {
            block = new TableBlock([new TableRow(new TableCell(block))]);
        }

        return block;
    }

    /// <summary>Deep nesting is an error, not a stack overflow that ends the process.</summary>
    private static void EnforcesNestingDepth()
    {
        var deep = new PortableDocument(new DocumentMetadata(), new Section(NestedTables(100_000)));
        var inHeader = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("x"))) { Header = new HeaderFooter(NestedTables(300)) });
        Block mixed = new Paragraph(new TextRun("x"));
        for (var level = 0; level < 100; level++)
        {
            mixed = new ListBlock("Bullets", [new ListItem(new TableBlock([new TableRow(new TableCell(
                new Paragraph([new TextRun("n"), new Footnote(mixed)])))]))]);
        }

        var throughListsAndNotes = new PortableDocument(new DocumentMetadata(), new Section(mixed));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            foreach (var document in new[] { deep, inHeader, throughListsAndNotes })
            {
                using var stream = new MemoryStream();
                Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(document, stream, format).GetAwaiter().GetResult());
            }

            using var allowed = new MemoryStream();
            DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(NestedTables(256))), allowed, format)
                .GetAwaiter().GetResult();
        }
    }

    private static void HonorsCancellation()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();
        using var source = new CancellationTokenSource();
        source.Cancel();

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            using var stream = new MemoryStream();
            Throws<OperationCanceledException>(() => DocumentWriter
                .WriteAsync(document, stream, format, options: null, source.Token)
                .GetAwaiter().GetResult());
            Require(stream.Length == 0, $"{format}: on cancellation no data is written.");
        }
    }

    private static void CreatesNoTemporaryFiles()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();
        var directory = Path.Combine(Path.GetTempPath(), "reglet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            DocumentWriter.WriteAsync(document, Path.Combine(directory, "out.docx")).GetAwaiter().GetResult();
            var files = Directory.GetFiles(directory);
            Require(files.Length == 1 && Path.GetFileName(files[0]) == "out.docx",
                "Only the target file is created.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WritesInParallel()
    {
        var document = PortableDocument.Create()
            .Metadata(title: "Parallel")
            .Heading("Title", level: 1)
            .Paragraph(p => p.Text("Text"))
            .BulletList("a", "b")
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .Build();

        var formats = new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf };
        var expected = formats.ToDictionary(format => format, format => WriteBytes(document, format));

        var results = new byte[formats.Length * 8][];
        Parallel.For(0, results.Length, index =>
        {
            results[index] = WriteBytes(document, formats[index % formats.Length]);
        });

        for (var index = 0; index < results.Length; index++)
        {
            Require(
                results[index].SequenceEqual(expected[formats[index % formats.Length]]),
                "Parallel output is identical to sequential.");
        }
    }

    private static byte[] WriteBytes(PortableDocument document, DocumentFormat format)
    {
        using var stream = new MemoryStream();
        DocumentWriter.WriteAsync(document, stream, format).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static void ErrorMessagesAvoidDocumentContent()
    {
        const string secret = "Confidential-XYZ-42";
        var document = PortableDocument.Create()
            .Paragraph(secret)
            .Paragraph("\u4E2D\u6587")
            .Build();

        using var stream = new MemoryStream();
        var result = DocumentWriter
            .WriteAsync(document, stream, DocumentFormat.Pdf, new PdfWriteOptions())
            .GetAwaiter().GetResult();

        Require(result.HasWarnings, "At least one warning is produced.");
        Require(
            result.Warnings.All(warning => !warning.Message.Contains(secret, StringComparison.Ordinal)),
            "Warnings contain no document content.");
        Require(
            result.Warnings.All(warning => warning.Message.StartsWith("PDF:", StringComparison.Ordinal)),
            "Warnings name the affected format.");
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void PassesFormatOptions()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("AA", fontFamily: "TestFont"))
            .Build();

        using var stream = new MemoryStream();
        var options = new PdfWriteOptions
        {
            Fonts = new Dictionary<string, byte[]> { ["TestFont"] = fontData },
        };

        DocumentWriter.WriteAsync(document, stream, DocumentFormat.Pdf, options).GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(stream.ToArray());
        Require(text.Contains("/Subtype /Type0", StringComparison.Ordinal), "The provided font is embedded as Type0.");
        Require(text.Contains("/FontFile2", StringComparison.Ordinal), "FontFile2 is present.");
        Require(text.Contains("/Encoding /Identity-H", StringComparison.Ordinal), "Identity-H is used.");
    }

    /// <summary>Writable, non-seekable stream (e.g. network/pipe target).</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
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

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}
