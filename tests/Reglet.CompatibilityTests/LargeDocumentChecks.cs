using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// large documents (phase U, U02): 10,000+ pages/blocks and large images are produced within the
/// configured limits.
/// </summary>
internal static class LargeDocumentChecks
{
    private const int Pages = 10_000;

    internal static int Run()
    {
        try
        {
            WritesManyPagesPdf();
            WritesManyBlocksDocxAndOdt();
            EmbedsLargeImage();
            Console.WriteLine("Large documents (U02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Large documents (U02): " + exception);
            return 1;
        }
    }

    private static void WritesManyPagesPdf()
    {
        var builder = PortableDocument.Create();
        for (var index = 0; index < Pages; index++)
        {
            builder.PageBreak().Paragraph("Page " + index);
        }

        var document = builder.Build();

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { CollectMetrics = true })
            .GetAwaiter().GetResult();

        Require(result.Metrics is not null, "PDF: metrics are reported.");
        Require(result.Metrics!.Pages >= Pages, $"PDF: at least {Pages} pages expected, were {result.Metrics.Pages}.");
        Require(result.Metrics.Bytes == buffer.Length && buffer.Length > 0, "PDF: bytes match the output.");
        Require(
            Encoding.Latin1.GetString(buffer.ToArray()).StartsWith("%PDF-1.7", StringComparison.Ordinal),
            "PDF: valid header.");
    }

    private static void WritesManyBlocksDocxAndOdt()
    {
        var builder = PortableDocument.Create();
        for (var index = 0; index < Pages; index++)
        {
            builder.Paragraph("Paragraph " + index);
        }

        var document = builder.Build();

        foreach (var (format, options) in new (DocumentFormat Format, WriteOptions Options)[]
        {
            (DocumentFormat.Docx, new DocxWriteOptions { CollectMetrics = true }),
            (DocumentFormat.Odt, new OdtWriteOptions { CollectMetrics = true }),
        })
        {
            using var buffer = new MemoryStream();
            var result = DocumentWriter.WriteAsync(document, buffer, format, options).GetAwaiter().GetResult();
            Require(result.Metrics!.Bytes == buffer.Length && buffer.Length > 0, $"{format}: bytes match the output.");

            var bytes = buffer.ToArray();
            var errors = format == DocumentFormat.Docx
                ? DocxPackageValidator.Validate(bytes)
                : OdtPackageValidator.Validate(bytes);
            Require(errors.Count == 0, $"{format}: package is valid: {string.Join("; ", errors)}");
        }
    }

    private static void EmbedsLargeImage()
    {
        var png = TestImages.RgbPng(1200, 1200, 10, 120, 200);
        var document = PortableDocument.Create()
            .Paragraph("Large image")
            .Image(png, ImageMediaType.Png, altText: "Large image")
            .Build();

        using (var buffer = new MemoryStream())
        {
            DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Docx).GetAwaiter().GetResult();
            var bytes = buffer.ToArray();
            Require(HasEntry(bytes, "word/media/image1.png"), "DOCX: the large image is embedded.");
            Require(DocxPackageValidator.Validate(bytes).Count == 0, "DOCX: package is valid.");
        }

        using (var buffer = new MemoryStream())
        {
            DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Odt).GetAwaiter().GetResult();
            var bytes = buffer.ToArray();
            Require(HasEntry(bytes, "Pictures/image1.png"), "ODT: the large image is embedded.");
            Require(OdtPackageValidator.Validate(bytes).Count == 0, "ODT: package is valid.");
        }

        using (var buffer = new MemoryStream())
        {
            DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Pdf).GetAwaiter().GetResult();
            var bytes = buffer.ToArray();
            var text = Encoding.Latin1.GetString(bytes);
            Require(text.StartsWith("%PDF-1.7", StringComparison.Ordinal), "PDF: valid header.");
            Require(text.Contains("/Subtype /Image", StringComparison.Ordinal), "PDF: the large image is embedded as an XObject.");
        }
    }

    private static bool HasEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.GetEntry(name) is not null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
