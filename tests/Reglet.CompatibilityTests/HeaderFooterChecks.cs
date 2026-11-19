using System.IO.Compression;
using Reglet;

/// <summary>
/// header/footer variants and starting page number (phase Z, Z06): first/even pages in DOCX and
/// PDF and ODT.
/// </summary>
internal static class HeaderFooterChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxVariants();
            RendersPdfVariants();
            WritesOdtVariants();
            WritesOdtSectionPageNumbers();
            RejectsInvalidValues();
            Console.WriteLine("Header/footer variants (Z06): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Header/footer variants (Z06): " + exception);
            return 1;
        }
    }

    private static PortableDocument VariantDocument()
    {
        var section = new Section(new Block[]
        {
            new Paragraph(new TextRun("Page 1")),
            new PageBreak(),
            new Paragraph(new TextRun("Page 2")),
        })
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
            FirstHeader = new HeaderFooter(new Paragraph(new TextRun("First page"))),
            EvenHeader = new HeaderFooter(new Paragraph(new TextRun("Even page"))),
            Footer = new HeaderFooter(new Paragraph(new PageNumberField())),
            PageNumberStart = 5,
        };

        return new PortableDocument(new DocumentMetadata(), section);
    }

    private static void WritesDocxVariants()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(VariantDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var body = ReadZipEntry(package, "word/document.xml");
        Require(body.Contains("<w:titlePg", StringComparison.Ordinal), "DOCX: titlePg.");
        Require(body.Contains("w:type=\"first\"", StringComparison.Ordinal), "DOCX: header of the first page.");
        Require(body.Contains("w:type=\"even\"", StringComparison.Ordinal), "DOCX: header of even pages.");
        Require(body.Contains("w:pgNumType w:start=\"5\"", StringComparison.Ordinal), "DOCX: start page number.");

        Require(ReadZipEntry(package, "word/settings.xml").Contains("w:evenAndOddHeaders", StringComparison.Ordinal), "DOCX: evenAndOddHeaders.");
    }

    private static void RendersPdfVariants()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(VariantDocument(), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(content.Contains("(First) Tj", StringComparison.Ordinal), "PDF: header of the first page.");
        Require(content.Contains("(Even) Tj", StringComparison.Ordinal), "PDF: header of even pages.");
        Require(content.Contains("(5) Tj", StringComparison.Ordinal), "PDF: start page number 5.");
        Require(content.Contains("(6) Tj", StringComparison.Ordinal), "PDF: next page 6.");
    }

    private static void WritesOdtVariants()
    {
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Default"))),
            FirstHeader = new HeaderFooter(new Block[] { new Paragraph(new TextRun("First") { Bold = true }), new ImageBlock(TestImages.RgbPng(2, 2, 255, 0, 0), ImageMediaType.Png) }),
            EvenHeader = new HeaderFooter(new Paragraph(new TextRun("Even"))),
            FirstFooter = new HeaderFooter(new Paragraph(new TextRun("First footer"))),
            EvenFooter = new HeaderFooter(Array.Empty<Block>()),
            PageNumberStart = 5,
        });
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(source, buffer).GetAwaiter().GetResult();

        var styles = ReadZipEntry(buffer.ToArray(), "styles.xml");
        Require(styles.Contains("style:header-first", StringComparison.Ordinal) && styles.Contains("style:header-left", StringComparison.Ordinal), "ODT: header variants written.");
        Require(styles.Contains("style:footer-first", StringComparison.Ordinal) && styles.Contains("style:footer-left", StringComparison.Ordinal), "ODT: footer variants written.");
        Require(ReadZipEntry(buffer.ToArray(), "content.xml").Contains("style:page-number=\"5\"", StringComparison.Ordinal), "ODT: starting page number written.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("starting page number", StringComparison.Ordinal)), "ODT: starting page number is not reported.");
        buffer.Position = 0;
        var section = DocumentReader.Read(buffer, DocumentFormat.Odt).Document.Sections[0];
        Require(((TextRun)((Paragraph)section.FirstHeader!.Blocks[0]).Inlines[0]).Text == "First", "ODT: first header roundtrip.");
        Require(((TextRun)((Paragraph)section.FirstHeader.Blocks[0]).Inlines[0]).Bold == true, "ODT: first header formatting roundtrip.");
        Require(section.FirstHeader.Blocks.OfType<ImageBlock>().Any(), "ODT: first header image roundtrip.");
        Require(((TextRun)((Paragraph)section.EvenHeader!.Blocks[0]).Inlines[0]).Text == "Even", "ODT: even header roundtrip.");
        Require(((TextRun)((Paragraph)section.FirstFooter!.Blocks[0]).Inlines[0]).Text == "First footer", "ODT: first footer roundtrip.");
        Require(section.EvenFooter is { Blocks.Count: 0 }, "ODT: empty even footer roundtrip.");
    }

    private static void WritesOdtSectionPageNumbers()
    {
        var document = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("One"))) { PageNumberStart = 5 },
            new Section(new Paragraph(new TextRun("Two"))) { PageNumberStart = 10 },
            new Section(new Paragraph(new TextRun("Three"))) { PageNumberStart = 20 },
        });

        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("style:page-number=\"5\"", StringComparison.Ordinal), "ODT: the first section starts at page 5.");
        Require(content.Contains("style:page-number=\"10\"", StringComparison.Ordinal), "ODT: the second section starts at page 10.");
        Require(content.Contains("style:page-number=\"20\"", StringComparison.Ordinal), "ODT: the third section starts at page 20.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("starting page number", StringComparison.Ordinal)),
            "ODT: section page numbers are not reported.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => new Section(new Paragraph(new TextRun("x"))) { PageNumberStart = -1 });
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
