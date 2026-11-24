using System.IO.Compression;
using Reglet;

/// <summary>
/// reusable table styles and conditional formats (phase Y, Y05): named DOCX table style,
/// header-row and banded-row backgrounds in DOCX, ODT and PDF.
/// </summary>
internal static class TableStyleChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxStyle();
            RendersOdtStyle();
            RendersPdfStyle();
            BandsWithoutHeader();
            RejectsInvalidValues();
            Console.WriteLine("Table styles (Y05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Table styles (Y05): " + exception);
            return 1;
        }
    }

    private static PortableDocument StyledDocument()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B"), new TableCell("C") }) { IsHeader = true },
            new TableRow(new[] { new TableCell("1"), new TableCell("2"), new TableCell("3") }),
            new TableRow(new[] { new TableCell("4"), new TableCell("5"), new TableCell("6") }),
        })
        {
            Style = new TableStyle("Fancy")
            {
                Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Color.FromHex("0000FF")),
                CellPadding = Length.FromPoints(3),
                HeaderBackground = Color.FromHex("CCCCCC"),
                BandBackground = Color.FromHex("EEEEEE"),
                FirstColumnBackground = Color.FromHex("AAAAAA"),
                LastColumnBackground = Color.FromHex("BBBBBB"),
                LastRowBackground = Color.FromHex("DDDDDD"),
            },
        };

        return new PortableDocument(new DocumentMetadata(), new Section(table));
    }

    private static void WritesDocxStyle()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(StyledDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var styles = ReadZipEntry(package, "word/styles.xml");
        Require(styles.Contains("w:styleId=\"Fancy\"", StringComparison.Ordinal), "DOCX: named table style.");
        Require(styles.Contains("w:tblStylePr w:type=\"firstRow\"", StringComparison.Ordinal), "DOCX: firstRow conditional format.");
        Require(styles.Contains("w:tblStylePr w:type=\"band1Horz\"", StringComparison.Ordinal), "DOCX: band1Horz conditional format.");
        Require(styles.Contains("w:tblStylePr w:type=\"firstCol\"", StringComparison.Ordinal), "DOCX: firstCol conditional format.");
        Require(styles.Contains("w:tblStylePr w:type=\"lastCol\"", StringComparison.Ordinal), "DOCX: lastCol conditional format.");
        Require(styles.Contains("w:tblStylePr w:type=\"lastRow\"", StringComparison.Ordinal), "DOCX: lastRow conditional format.");

        var body = ReadZipEntry(package, "word/document.xml");
        Require(body.Contains("<w:tblStyle w:val=\"Fancy\"", StringComparison.Ordinal), "DOCX: table references the style.");
        Require(body.Contains("w:fill=\"CCCCCC\"", StringComparison.Ordinal), "DOCX: header background.");
        Require(body.Contains("w:fill=\"EEEEEE\"", StringComparison.Ordinal), "DOCX: banded row background.");
    }

    private static void RendersOdtStyle()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(StyledDocument(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("fo:background-color=\"#CCCCCC\"", StringComparison.Ordinal), "ODT: header background.");
        Require(content.Contains("fo:background-color=\"#EEEEEE\"", StringComparison.Ordinal), "ODT: banded row background.");
        Require(content.Contains("fo:background-color=\"#AAAAAA\"", StringComparison.Ordinal), "ODT: first column background.");
        Require(content.Contains("fo:background-color=\"#BBBBBB\"", StringComparison.Ordinal), "ODT: last column background.");
        Require(content.Contains("fo:background-color=\"#DDDDDD\"", StringComparison.Ordinal), "ODT: last row background.");
        Require(content.Contains("reglet:border=\"1pt solid #0000FF\"", StringComparison.Ordinal),
            "ODT: style border, kept as a Reglet attribute for reading back (ODF has no table border).");
    }

    private static void RendersPdfStyle()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(StyledDocument(), buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("0.8 0.8 0.8 rg", StringComparison.Ordinal), "PDF: header background.");
        Require(content.Contains("0.933 0.933 0.933 rg", StringComparison.Ordinal), "PDF: banded row background.");
    }

    private static void BandsWithoutHeader()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A") }),
            new TableRow(new[] { new TableCell("B") }),
        })
        {
            Style = new TableStyle("Banded") { BandBackground = Color.FromHex("EEEEEE") },
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(
            ReadZipEntry(buffer.ToArray(), "content.xml").Contains("fo:background-color=\"#EEEEEE\"", StringComparison.Ordinal),
            "ODT: the first data row is banded without a header.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentException>(() => new TableStyle(" "));
        Throws<ArgumentOutOfRangeException>(() => new TableStyle("x") { CellPadding = Length.FromPoints(-1) });
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
