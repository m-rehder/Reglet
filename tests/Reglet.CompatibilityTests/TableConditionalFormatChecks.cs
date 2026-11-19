using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// conditional table formats (phase AI, AI03): besides the backgrounds, a table style can
/// define the borders and the text formatting of the header row, the banded rows, the first
/// and last column and the last row.
/// </summary>
internal static class TableConditionalFormatChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxConditionalFormats();
            WritesOdtConditionalFormats();
            AppliesConditionalTextInPdf();
            Console.WriteLine("Conditional table formats (AI03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Conditional table formats (AI03): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var style = new TableStyle("Raster")
        {
            Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(0.5), Color.Black),
            HeaderBackground = Color.FromHex("DDDDDD"),
            HeaderRow = new TableStyleArea
            {
                Border = new ParagraphBorder(BorderStyle.Double, Length.FromPoints(1), Color.FromHex("FF0000")),
                Bold = true,
                TextColor = Color.FromHex("0000FF"),
            },
            BandRow = new TableStyleArea { Italic = true },
            FirstColumn = new TableStyleArea { Bold = true },
            LastRow = new TableStyleArea { TextColor = Color.FromHex("00FF00") },
        };

        var table = new TableBlock(
            [
                new TableRow([new TableCell(new Paragraph(new TextRun("Head")))]) { IsHeader = true },
                new TableRow([new TableCell(new Paragraph(new TextRun("Line")))]),
                new TableRow([new TableCell(new Paragraph(new TextRun("Last")))]),
            ])
        {
            Style = style,
        };

        return new PortableDocument(
            new DocumentMetadata { Title = "Conditional formats" },
            [new Section([table])]);
    }

    private static void WritesDocxConditionalFormats()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var styles = ReadText(archive, "word/styles.xml");

        Assert(styles.Contains("w:type=\"table\""), "The table style is missing.");
        Assert(styles.Contains("w:styleId=\"Raster\""), "The style id is missing.");
        Assert(styles.Contains("<w:tblStylePr w:type=\"firstRow\""), "The header row conditional is missing.");
        Assert(styles.Contains("<w:tcBorders"), "The conditional borders are missing.");
        Assert(styles.Contains("<w:rPr"), "The conditional text formatting is missing.");
        Assert(styles.Contains("<w:b"), "The conditional bold is missing.");

        // The order follows the schema (CT_TblStylePr): w:rPr before w:tcPr, and the
        // shading and the borders inside w:tcPr.
        var conditional = styles[styles.IndexOf("<w:tblStylePr", StringComparison.Ordinal)..];
        var runProperties = conditional.IndexOf("<w:rPr", StringComparison.Ordinal);
        var cellProperties = conditional.IndexOf("<w:tcPr", StringComparison.Ordinal);
        Assert(runProperties >= 0 && cellProperties > runProperties, "The text formatting must come before the cell properties.");
        Assert(conditional.Contains("<w:tcPr><w:tcBorders", StringComparison.Ordinal), "The borders must be inside the cell properties.");
        Assert(conditional.Contains("<w:shd", StringComparison.Ordinal), "The background is missing.");
    }

    private static void WritesOdtConditionalFormats()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var content = ReadText(archive, "content.xml");

        Assert(content.Contains("style:family=\"table-cell\""), "The cell styles are missing.");
        Assert(content.Contains("fo:border-top"), "The conditional border is missing.");
        Assert(content.Contains("fo:font-weight=\"bold\""), "The conditional bold is missing.");
        Assert(content.Contains("fo:font-style=\"italic\""), "The conditional italic is missing.");
        Assert(content.Contains("fo:color=\"#0000FF\""), "The conditional color is missing.");
    }

    private static void AppliesConditionalTextInPdf()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(buffer.ToArray());

        // The header row is bold (the bold font), the band row is italic.
        Assert(text.Contains("/BaseFont /Helvetica-Bold"), "The conditional bold was not applied.");
        Assert(text.Contains("/BaseFont /Helvetica-Oblique"), "The conditional italic was not applied.");
    }

    private static string ReadText(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException("The part is missing: " + name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
