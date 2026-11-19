using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// table of contents (phase Z, Z04): DOCX and ODT write a field so the application fills in
/// the page numbers; PDF renders the entries statically (reported without page numbers).
/// </summary>
internal static class TableOfContentsChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxField();
            WritesOdtIndex();
            IncludesTableHeadingsInOfficeIndexes();
            RendersPdfEntries();
            RendersPdfTableHeadings();
            RendersPdfHeadingsAfterLongTable();
            RendersPdfNestedTableHeadings();
            UsesStartPageForSplitHeading();
            RejectsInvalidValues();
            Console.WriteLine("Table of contents (Z04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Table of contents (Z04): " + exception);
            return 1;
        }
    }

    private static PortableDocument ContentsDocument()
        => new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new TableOfContents { Title = "Contents", MaxLevel = 2 },
                new Heading(HeadingLevel.Level1, new TextRun("First")),
                new Paragraph(new TextRun("body")),
                new Heading(HeadingLevel.Level2, new TextRun("Second")),
                new Heading(HeadingLevel.Level4, new TextRun("Deep")),
            }));

    private static void WritesDocxField()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(ContentsDocument(), buffer).GetAwaiter().GetResult();

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(body.Contains("w:fldChar", StringComparison.Ordinal), "DOCX: field char.");
        Require(body.Contains("w:instrText", StringComparison.Ordinal), "DOCX: field instruction.");
        Require(body.Contains("TOC", StringComparison.Ordinal), "DOCX: TOC instruction.");
        Require(body.Contains(">First<", StringComparison.Ordinal), "DOCX: first entry.");
        Require(body.Contains(">Second<", StringComparison.Ordinal), "DOCX: second entry.");
        Require(body.Contains(">Contents<", StringComparison.Ordinal), "DOCX: title.");
        Require(Count(body, ">Deep<") == 1, "DOCX: level above MaxLevel is excluded (only the heading itself).");
    }

    private static void WritesOdtIndex()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(ContentsDocument(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("<text:table-of-content", StringComparison.Ordinal), "ODT: table of content.");
        Require(content.Contains("<text:index-body>", StringComparison.Ordinal), "ODT: index body.");
        Require(content.Contains(">First<", StringComparison.Ordinal), "ODT: first entry.");
        Require(content.Contains(">Second<", StringComparison.Ordinal), "ODT: second entry.");
        Require(Count(content, ">Deep<") == 1, "ODT: level above MaxLevel is excluded (only the heading itself).");
    }

    private static void RendersPdfEntries()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(ContentsDocument(), buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(First)", StringComparison.Ordinal), "PDF: first entry.");
        Require(content.Contains("(Second)", StringComparison.Ordinal), "PDF: second entry.");
        Require(Count(content, "(Deep)") == 1, "PDF: level above MaxLevel is excluded (only the heading itself).");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("without page numbers", StringComparison.Ordinal)),
            "PDF: page numbers rendered.");
        Require(content.Contains("(1)", StringComparison.Ordinal), "PDF: a table of contents page number is rendered.");
    }

    private static void IncludesTableHeadingsInOfficeIndexes()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfContents { MaxLevel = 2 },
            new TableBlock([new TableRow(new TableCell(new Heading(HeadingLevel.Level2, new TextRun("Cell heading"))))]),
        }));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            var content = ReadZipEntry(buffer.ToArray(), format == DocumentFormat.Docx ? "word/document.xml" : "content.xml");
            var xml = XDocument.Parse(content);
            var index = format == DocumentFormat.Odt
                ? xml.Descendants().Single(element => element.Name.LocalName == "index-body")
                : xml.Root!;
            Require(index.Value.Contains("Cell heading", StringComparison.Ordinal), $"{format}: table heading appears in the index.");
            if (format == DocumentFormat.Docx)
            {
                Require(Count(content, ">Cell heading<") == 2, "DOCX: heading appears in the index and cell.");
            }
        }
    }

    private static void RendersPdfTableHeadings()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfContents(),
            new Heading(HeadingLevel.Level1, new TextRun("Before")),
            new PageBreak(),
            new TableBlock([new TableRow(new TableCell(new Heading(HeadingLevel.Level2, new TextRun("In cell"))))]),
            new Heading(HeadingLevel.Level1, new TextRun("After")),
        }));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = System.Text.Encoding.Latin1.GetString(buffer.ToArray());
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages.Count == 2 && pages[1].Contains("In cell", StringComparison.Ordinal), "PDF: cell heading appears in the table.");
        Require(pdf.Contains("/Title (In cell)", StringComparison.Ordinal), "PDF: cell heading appears in the outline.");
        Require(pdf.Contains("(_Toc1)", StringComparison.Ordinal), "PDF: cell heading has a contents destination.");
        Require(pages[0].Contains("In cell", StringComparison.Ordinal) && pages[0].Contains("2", StringComparison.Ordinal),
            "PDF: contents shows the cell heading's page.");
    }

    private static void RendersPdfHeadingsAfterLongTable()
    {
        var rows = Enumerable.Range(0, 100).Select(_ => new TableRow(new TableCell("row"))).ToList();
        rows.Add(new TableRow(new TableCell(new Heading(HeadingLevel.Level2, new TextRun("Late heading")))));
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfContents(),
            new TableBlock(rows),
            new Heading(HeadingLevel.Level1, new TextRun("Following")),
        }));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = System.Text.Encoding.Latin1.GetString(buffer.ToArray());
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        var headingPage = Enumerable.Range(1, pages.Count - 1).FirstOrDefault(index => pages[index].Contains("Late heading", StringComparison.Ordinal));
        Require(headingPage > 0, "PDF: heading is on a later table page.");
        Require(pages[0].Contains((headingPage + 1).ToString(), StringComparison.Ordinal), "PDF: contents shows the later page number.");
        Require(pdf.Contains("(_Toc0)", StringComparison.Ordinal) && pdf.Contains("(_Toc1)", StringComparison.Ordinal),
            "PDF: table heading and following heading have distinct destinations.");
        Require(pdf.Contains("/Title (Late heading)", StringComparison.Ordinal) && pdf.Contains("/Title (Following)", StringComparison.Ordinal),
            "PDF: both headings appear in the outline.");
    }

    private static void RendersPdfNestedTableHeadings()
    {
        var nested = new TableBlock(new[]
        {
            new TableRow(new TableCell(new TableBlock(new[]
            {
                new TableRow(new TableCell(new Heading(HeadingLevel.Level2, new TextRun("Nested")))),
            }))),
        });
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfContents(),
            new Heading(HeadingLevel.Level1, new TextRun("Before")),
            new PageBreak(),
            nested,
            new Heading(HeadingLevel.Level1, new TextRun("After")),
        }));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        Require(pdf.Contains("/Title (Nested)", StringComparison.Ordinal),
            "PDF: a heading in a nested table appears in the outline.");
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages[0].Contains("Nested", StringComparison.Ordinal),
            "PDF: the contents lists a heading in a nested table.");
    }

    private static void UsesStartPageForSplitHeading()
    {
        var heading = new Heading(HeadingLevel.Level1, new TextRun("ONE TWO THREE FOUR FIVE SIX")
        {
            FontSize = Length.FromPoints(30),
        });
        var section = new Section(new Block[] { new TableOfContents(), new PageBreak(), heading })
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        var firstPage = Enumerable.Range(1, pages.Count - 1).FirstOrDefault(index => pages[index].Contains("ONE", StringComparison.Ordinal));
        Require(firstPage > 0 && firstPage < pages.Count - 1, "PDF: heading starts on one page and continues on a later page.");
        Require(pages.Take(firstPage).Any(page => page.Contains((firstPage + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            "PDF: contents reports the page where the heading starts.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => new TableOfContents { MaxLevel = 0 });
        Throws<ArgumentOutOfRangeException>(() => new TableOfContents { MaxLevel = 7 });
    }

    private static int Count(string text, string value)
        => text.Split(value).Length - 1;

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
