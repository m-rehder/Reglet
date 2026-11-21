using System.IO.Compression;
using Reglet;

/// <summary>
/// hyperlinks (phase Z, Z02): target and text in DOCX (relationship) and ODT (`text:a`); PDF
/// draws the text and emits a `/Link` annotation with a `/URI` action.
/// </summary>
internal static class HyperlinkChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxHyperlink();
            ReadsDocxHyperlink();
            RoundTripsDocxBandHyperlinks();
            WritesOdtHyperlink();
            ReadsOdtHyperlink();
            ReadsBookmarksInsideHyperlinks();
            RendersPdfHyperlinkAsText();
            WritesPdfHyperlinkAnnotation();
            RejectsInvalidValues();
            ClassifiesLinkTargets();
            RejectsScriptTargets();
            DropsDisallowedTargetsWhenReading(DocumentFormat.Docx);
            DropsDisallowedTargetsWhenReading(DocumentFormat.Odt);
            Console.WriteLine("Hyperlinks (Z02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Hyperlinks (Z02): " + exception);
            return 1;
        }
    }

    private static PortableDocument LinkDocument()
        => new(new DocumentMetadata(), new Section(new Paragraph(
            new Hyperlink("https://example.com/", "Example"))));

    private static void WritesDocxHyperlink()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var body = ReadZipEntry(package, "word/document.xml");
        Require(body.Contains("<w:hyperlink r:id=", StringComparison.Ordinal), "DOCX: Hyperlink-Element.");
        Require(body.Contains("Example", StringComparison.Ordinal), "DOCX: link text present.");

        var rels = ReadZipEntry(package, "word/_rels/document.xml.rels");
        Require(rels.Contains("relationships/hyperlink", StringComparison.Ordinal), "DOCX: relationship type hyperlink.");
        Require(rels.Contains("Target=\"https://example.com/\"", StringComparison.Ordinal), "DOCX: target.");
        Require(rels.Contains("TargetMode=\"External\"", StringComparison.Ordinal), "DOCX: external target.");
    }

    private static void ReadsDocxHyperlink()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;

        var hyperlink = read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<Hyperlink>()
            .FirstOrDefault();

        Require(hyperlink is not null, "DOCX: the hyperlink is read.");
        Require(hyperlink!.Target == "https://example.com/", "DOCX: the hyperlink target is read.");
        Require(
            hyperlink.Inlines.OfType<TextRun>().Any(run => run.Text == "Example"),
            "DOCX: the hyperlink text is read.");
    }

    private static void RoundTripsDocxBandHyperlinks()
    {
        var image = new ImageBlock(TestImages.RgbPng(2, 2, 0, 0, 255), ImageMediaType.Png);
        var section = new Section(new Paragraph(new Hyperlink("https://body.example/", "BodyLink")))
        {
            Header = new HeaderFooter(new Block[] { image, new Paragraph(new Hyperlink("https://header.example/", "HeaderLink")) }),
            Footer = new HeaderFooter(new Paragraph(new Hyperlink("https://footer.example/", "FooterLink"))),
        };
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        Require(!result.Warnings.Any(warning => warning.Message.Contains("hyperlink", StringComparison.OrdinalIgnoreCase)),
            "DOCX: header and footer links receive part-local relationships.");
        var rels = System.Xml.Linq.XDocument.Parse(ReadZipEntry(buffer.ToArray(), "word/_rels/header1.xml.rels"));
        var ids = rels.Root!.Elements().Select(node => (string?)node.Attribute("Id")).ToArray();
        Require(ids.Length == 2 && ids.Distinct().Count() == 2, "DOCX: header image and hyperlink relationship IDs are distinct.");
        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Docx).Document.Sections[0];
        var headerLink = read.Header!.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<Hyperlink>().Single();
        var footerLink = read.Footer!.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<Hyperlink>().Single();
        Require(headerLink.Target == "https://header.example/" && footerLink.Target == "https://footer.example/"
            && read.Header.Blocks.OfType<ImageBlock>().Count() == 1, "DOCX: band hyperlinks and images survive reading.");
    }

    private static void WritesOdtHyperlink()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");

        Require(content.Contains("<text:a ", StringComparison.Ordinal), "ODT: text:a.");
        Require(content.Contains("xlink:href=\"https://example.com/\"", StringComparison.Ordinal), "ODT: target.");
        Require(content.Contains("Example", StringComparison.Ordinal), "ODT: Linktext.");
    }

    private static void ReadsOdtHyperlink()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;

        var hyperlink = read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<Hyperlink>()
            .FirstOrDefault();

        Require(hyperlink is not null, "ODT: the hyperlink is read.");
        Require(hyperlink!.Target == "https://example.com/", "ODT: the hyperlink target is read.");
        Require(
            hyperlink.Inlines.OfType<TextRun>().Any(run => run.Text == "Example"),
            "ODT: the hyperlink text is read.");
    }

    private static void RendersPdfHyperlinkAsText()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(content.Contains("Example", StringComparison.Ordinal), "PDF: link text is drawn.");
    }

    private static void ReadsBookmarksInsideHyperlinks()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("before"),
            new Hyperlink("https://example.com/", [new Bookmark("Target"), new TextRun("link")]),
            new Bookmark("After"),
        })));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var paragraph = (Paragraph)DocumentReader.Read(buffer, format).Document.Sections[0].Blocks[0];
            var hyperlink = paragraph.Inlines.OfType<Hyperlink>().Single();
            Require(hyperlink.Inlines.OfType<Bookmark>().Single().Name == "Target", $"{format}: bookmark inside hyperlink is read.");
            Require(paragraph.Inlines.OfType<Bookmark>().Single().Name == "After", $"{format}: bookmark after hyperlink is read.");
            Require(hyperlink.Inlines.OfType<TextRun>().Any(run => run.Text == "link"), $"{format}: link text is retained.");
            using var pdf = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(paragraph)), pdf).GetAwaiter().GetResult();
            Require(System.Text.Encoding.Latin1.GetString(pdf.ToArray()).Contains("(Target)", StringComparison.Ordinal),
                $"{format}: imported bookmark becomes a PDF destination.");
        }
    }

    private static void WritesPdfHyperlinkAnnotation()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(LinkDocument(), buffer).GetAwaiter().GetResult();
        var latin = System.Text.Encoding.Latin1.GetString(buffer.ToArray());

        Require(latin.Contains("/Subtype /Link", StringComparison.Ordinal), "PDF: link annotation.");
        Require(latin.Contains("/S /URI", StringComparison.Ordinal), "PDF: URI action.");
        Require(latin.Contains("/URI (https://example.com/)", StringComparison.Ordinal), "PDF: the target is stored.");
        Require(latin.Contains("/Annots [", StringComparison.Ordinal), "PDF: the page references its annotations.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("link annotation", StringComparison.Ordinal)),
            "PDF: no missing-annotation warning.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentException>(() => new Hyperlink("  ", "x"));
        Throws<ArgumentNullException>(() => new Hyperlink("https://x", (string)null!));
        Throws<ArgumentNullException>(() => new Hyperlink("https://x", (IEnumerable<Inline>)null!));
    }

    private static void ClassifiesLinkTargets()
    {
        foreach (var allowed in new[] { "https://example.com", "HTTP://example.com", "mailto:a@example.com", "#Anchor", "../annex.pdf", "annex.pdf?a=b:c", "docs/a:b" })
        {
            Require(ReaderGuard.IsAllowedLinkTarget(allowed), $"Allowed link target rejected: {allowed}");
        }

        foreach (var denied in new[]
        {
            "javascript:alert(1)", "JaVaScRiPt:alert(1)", "java\tscript:alert(1)", " javascript:alert(1)", "vbscript:x", "data:text/html,x",
            "file:///C:/x", "smb://host/x", "ms-msdt:/id x", "C:\\x", "\\\\host\\share", "//host/x", "/\\host/x", "/ /host/x", ":x",
        })
        {
            Require(!ReaderGuard.IsAllowedLinkTarget(denied), $"Disallowed link target accepted: {denied}");
        }
    }

    private static void RejectsScriptTargets()
    {
        // Only the HTML writer filtered: DOCX, ODT and PDF wrote a javascript: link set through the API.
        foreach (var script in new[] { "javascript:alert(1)", "JaVaScRiPt:alert(1)", "java\tscript:alert(1)", " javascript:x", "vbscript:x", "data:text/html,x" })
        {
            try
            {
                _ = new Hyperlink(script, "x");
                throw new InvalidOperationException($"A script link target is accepted: {script}");
            }
            catch (ArgumentException)
            {
            }
        }

        foreach (var allowed in new[] { "https://example.com", "tel:+49", "file:///C:/x", "#Anchor", "docs/data:x" })
        {
            _ = new Hyperlink(allowed, "x");
        }
    }

    private static void DropsDisallowedTargetsWhenReading(DocumentFormat format)
    {
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Hyperlink("file:///C:/x", "Bad")),
            new Paragraph(new Hyperlink("https://example.com/", "Good")),
        }));

        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = DocumentReader.Read(buffer, format);

        var paragraphs = read.Document.Sections[0].Blocks.OfType<Paragraph>().ToList();
        Require(paragraphs[0].Inlines.OfType<TextRun>().Any(run => run.Text == "Bad"), $"{format}: text of a dropped link is kept.");
        Require(!paragraphs[0].Inlines.OfType<Hyperlink>().Any(), $"{format}: a disallowed link is dropped.");
        Require(paragraphs[1].Inlines.OfType<Hyperlink>().Single().Target == "https://example.com/", $"{format}: an allowed link is kept.");
        Require(read.Warnings.Any(warning => warning.Message.Contains("disallowed target", StringComparison.Ordinal)), $"{format}: a dropped link is reported.");

        using var pdf = new MemoryStream();
        DocumentWriter.WriteAsync(read.Document, pdf, DocumentFormat.Pdf).GetAwaiter().GetResult();
        var raw = System.Text.Encoding.Latin1.GetString(pdf.ToArray());
        Require(!raw.Contains("file:///C:/x", StringComparison.OrdinalIgnoreCase), $"{format}: a disallowed link does not reach the PDF.");
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
