using System.IO.Compression;
using Reglet;

/// <summary>
/// import, round trip and conversion (phase AC): model-based conversion DOCX↔ODT and
/// PDF→DOCX/ODT (AC03/AC04) as well as preservation of unknown package parts (AC02).
/// </summary>
internal static class ConversionChecks
{
    internal static int Run()
    {
        try
        {
            ConvertsDocxToOdt();
            ConvertsOdtToDocx();
            ConvertsPdfToDocx();
            ExtractsPdfPages();
            SeparatesPdfPageLines();
            PreservesUnknownParts();
            Console.WriteLine("Conversion (AC): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Conversion (AC): " + exception);
            return 1;
        }
    }

    private static PortableDocument SourceDocument()
        => PortableDocument.Create()
            .Heading("Title", 1)
            .Paragraph("Bodytext")
            .Build();

    private static byte[] WriteDocx(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] WriteOdt(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] WritePdf(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void ConvertsDocxToOdt()
    {
        var read = DocumentReader.Read(new MemoryStream(WriteDocx(SourceDocument())), DocumentFormat.Docx);
        var content = ReadZipEntry(WriteOdt(read.Document), "content.xml");
        Require(content.Contains("Title", StringComparison.Ordinal), "AC03: DOCX→ODT heading.");
        Require(content.Contains("Bodytext", StringComparison.Ordinal), "AC03: DOCX→ODT paragraph.");
    }

    private static void ConvertsOdtToDocx()
    {
        var read = DocumentReader.Read(new MemoryStream(WriteOdt(SourceDocument())), DocumentFormat.Odt);
        var body = ReadZipEntry(WriteDocx(read.Document), "word/document.xml");
        Require(body.Contains("Title", StringComparison.Ordinal), "AC03: ODT→DOCX heading.");
        Require(body.Contains("Bodytext", StringComparison.Ordinal), "AC03: ODT→DOCX paragraph.");
    }

    private static void ConvertsPdfToDocx()
    {
        var read = DocumentReader.Read(new MemoryStream(WritePdf(SourceDocument())), DocumentFormat.Pdf);
        var body = ReadZipEntry(WriteDocx(read.Document), "word/document.xml");
        Require(body.Contains("Bodytext", StringComparison.Ordinal), "AC04: PDF→DOCX text.");
    }

    private static void ExtractsPdfPages()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("Pageone")),
                new PageBreak(),
                new Paragraph(new TextRun("Pagetwo")),
            }));

        var pages = DocumentReader.ReadPdfPages(new MemoryStream(WritePdf(document)));
        Require(pages.Count == 2, "AC05: two pages.");
        Require(pages[0].Contains("Pageone", StringComparison.Ordinal), "AC05: first page text.");
        Require(pages[1].Contains("Pagetwo", StringComparison.Ordinal), "AC05: second page text.");
    }

    private static void SeparatesPdfPageLines()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("Lineone")),
                new Paragraph(new TextRun("Linetwo")),
            }));

        var pages = DocumentReader.ReadPdfPages(new MemoryStream(WritePdf(document)));
        Require(pages.Count == 1, "AC05: one page with two lines.");
        Require(
            pages[0].Contains("Lineone\nLinetwo", StringComparison.Ordinal),
            "AC05: lines are separated by a newline, got: " + pages[0].Replace("\n", "\\n"));
    }

    private static void PreservesUnknownParts()
    {
        // A template with an extra package part: the writer adopts the package unchanged.
        var bytes = WriteDocx(PortableDocument.Create().Paragraph("Hi {{Name}}").Build());
        using (var stream = new MemoryStream())
        {
            stream.Write(bytes);
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = zip.CreateEntry("custom/extra.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("preserved");
            }

            stream.Position = 0;
            bytes = stream.ToArray();
        }

        Require(ContainsEntry(bytes, "custom/extra.txt"), "AC02: template contains the extra part.");

        var template = DocumentTemplate.FromBytes(bytes).BindText("Name", "Ada");
        var document = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(document, buffer, new DocxWriteOptions { Template = template })
            .GetAwaiter().GetResult();

        var output = buffer.ToArray();
        var body = ReadZipEntry(output, "word/document.xml");
        Require(body.Contains("Ada", StringComparison.Ordinal), "AC02: placeholder bound.");
        Require(ContainsEntry(output, "custom/extra.txt"), "AC02: unknown part preserved.");
    }

    private static bool ContainsEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.GetEntry(name) is not null;
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
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
