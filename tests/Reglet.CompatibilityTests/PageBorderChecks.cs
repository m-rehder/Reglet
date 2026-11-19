using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// page border of a section (phase AJ, AJ05): the model, the DOCX (write and read) and the PDF.
/// </summary>
internal static class PageBorderChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxPageBorders();
            ReadsDocxPageBorders();
            DrawsPdfPageBorder();
            WritesOdtPageBorder();
            ReadsOdtPageBorder();
            Console.WriteLine("Page border (AJ05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Page border (AJ05): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            PageBorders = new PageBorders
            {
                Top = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(2), Color.FromHex("#FF0000")),
                Bottom = new ParagraphBorder(BorderStyle.Double),
                Offset = Length.FromPoints(12),
            },
        };

        return new PortableDocument(new DocumentMetadata(), section);
    }

    private static void WritesDocxPageBorders()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(content.Contains("w:pgBorders", StringComparison.Ordinal), "DOCX: the page border is written.");
        Require(content.Contains("w:color=\"FF0000\"", StringComparison.Ordinal), "DOCX: the color of the border is written.");
        Require(content.Contains("w:val=\"nil\"", StringComparison.Ordinal), "DOCX: a side without a border is nil.");
    }

    private static void ReadsDocxPageBorders()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        var borders = read.Sections[0].PageBorders;

        Require(borders is not null, "DOCX: the page border is read.");
        Require(borders!.Top?.Style == BorderStyle.Single, "DOCX: the style of the border is read.");
        Require(borders.Top?.Width.Points == 2, "DOCX: the width of the border is read.");
        Require(borders.Bottom?.Style == BorderStyle.Double, "DOCX: the second side is read.");
        Require(borders.Left is null, "DOCX: a side without a border stays empty.");
        Require(borders.Offset?.Points == 12, "DOCX: the offset is read.");
    }

    private static void DrawsPdfPageBorder()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains(" l\n", StringComparison.Ordinal), "PDF: the page border is drawn as a line.");
        Require(content.Contains("\nS\n", StringComparison.Ordinal), "PDF: the border line is stroked.");
    }

    private static void WritesOdtPageBorder()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var styles = ReadZipEntry(buffer.ToArray(), "styles.xml");
        Require(styles.Contains("fo:border-top", StringComparison.Ordinal), "ODT: the page border is written.");
        Require(styles.Contains("2pt solid #FF0000", StringComparison.Ordinal), "ODT: the border carries width, style and color.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("page border", StringComparison.Ordinal)),
            "ODT: the page border is not reported.");
    }

    private static void ReadsOdtPageBorder()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var borders = read.Sections[0].PageBorders;

        Require(borders is not null, "ODT: the page border is read.");
        Require(borders!.Top?.Style == BorderStyle.Single, "ODT: the style of the border is read.");
        Require(borders.Top?.Width.Points == 2, "ODT: the width of the border is read.");
        Require(
            borders.Top?.Color.ToString() == Color.FromHex("#FF0000").ToString(),
            "ODT: the color of the border is read.");
        Require(borders.Bottom?.Style == BorderStyle.Double, "ODT: the second side is read.");
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
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
