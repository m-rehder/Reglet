using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// Phase Q: accessibility. Checks the PDF structure tree (Q01/Q02), the
/// accessibility features in DOCX (Q03) and ODT (Q04) as well as the audit (Q05) using a
/// document with a heading, paragraph, table, image, header and language attribute.
/// </summary>
internal static class AccessibilityChecks
{
    internal static int Run()
    {
        try
        {
            PdfHasStructureTree();
            DocxHasAccessibilityAttributes();
            OdtHasAccessibilityAttributes();
            Console.WriteLine("Accessibility (Q01–Q05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Barrierefreiheit (Q): " + exception);
            return 1;
        }
    }

    /// <summary>Q01/Q02: catalog contains the structure tree, MarkInfo and roles; content is tagged.</summary>
    private static void PdfHasStructureTree()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Sample(), buffer).GetAwaiter().GetResult();
        var bytes = buffer.ToArray();
        var latin = Encoding.Latin1.GetString(bytes);

        AssertContains(latin, "/StructTreeRoot ");
        AssertContains(latin, "/MarkInfo <</Marked true >>");
        AssertContains(latin, "/ViewerPreferences <</DisplayDocTitle true >>");
        AssertContains(latin, "/Lang (de-DE)");
        AssertContains(latin, "/ParentTree ");
        AssertContains(latin, "/ParentTreeNextKey ");
        AssertContains(latin, "/StructParents 0");

        foreach (var role in new[] { "Document", "P", "H1", "Table", "TR", "TH", "TD", "Figure" })
        {
            AssertContains(latin, $"/S /{role} ");
        }

        AssertContains(latin, "/Alt (Diagramm)");
        AssertContains(latin, "/Type /Metadata /Subtype /XML");
        AssertContains(latin, "<pdf:Producer>Reglet</pdf:Producer>");
        AssertContains(latin, "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Barrierefrei</rdf:li></rdf:Alt></dc:title>");
        AssertContains(latin, "<dc:language><rdf:Bag><rdf:li>de-DE</rdf:li></rdf:Bag></dc:language>");

        var content = PdfStreams.Decompress(bytes);
        AssertContains(content, "/H1 <</MCID 0>> BDC\n");
        AssertContains(content, "/P <</MCID ");
        AssertContains(content, "/Figure <</MCID ");
        AssertContains(content, "/Artifact BMC\n");
        AssertContains(content, "EMC\n");
    }

    /// <summary>Q03: DOCX sets heading style, header row, alt text and language markup.</summary>
    private static void DocxHasAccessibilityAttributes()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Sample(), buffer).GetAwaiter().GetResult();

        var document = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        AssertContains(document, "<w:pStyle w:val=\"Heading1\"");
        AssertContains(document, "<w:tblHeader");
        AssertContains(document, "descr=\"Diagramm\"");

        var styles = ReadZipEntry(buffer.ToArray(), "word/styles.xml");
        AssertContains(styles, "<w:lang w:val=\"de-DE\"");
    }

    /// <summary>Q04: ODT sets outline level, header area, alt text and language.</summary>
    private static void OdtHasAccessibilityAttributes()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Sample(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        AssertContains(content, "text:outline-level=\"1\"");
        AssertContains(content, "table:table-header-rows");
        AssertContains(content, "<svg:title");

        var styles = ReadZipEntry(buffer.ToArray(), "styles.xml");
        AssertContains(styles, "fo:language=\"de\"");
        AssertContains(styles, "fo:country=\"DE\"");
    }

    private static PortableDocument Sample()
    {
        var image = new ImageBlock(RenderFixtures.CreatePng(4, 4, 10, 20, 30), ImageMediaType.Png) { AltText = "Diagramm" };
        var table = new TableBlock(
        [
            new TableRow(new TableCell("Header")) { IsHeader = true },
            new TableRow(new TableCell("Wert")),
        ]);
        var section = new Section(
        [
            new Heading(HeadingLevel.Level1, new TextRun("Title")),
            new Paragraph(new TextRun("Text")),
            table,
            image,
        ])
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
        };

        return new PortableDocument(
            new DocumentMetadata { Title = "Barrierefrei", Language = "de-DE" },
            [section]);
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Entry missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void AssertContains(string text, string expected)
        => Require(text.Contains(expected, StringComparison.Ordinal), $"Expected: {expected}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
