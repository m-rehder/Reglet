using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// the start type of a section (phase AI, AI02): next page (the default), continuous, odd page
/// or even page. DOCX writes <c>w:type</c>, ODT omits the master page change, the PDF keeps the
/// section on the page or inserts a blank page.
/// </summary>
internal static class SectionStartChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxStartType();
            ReadsDocxStartType();
            KeepsTheOdtSectionOnThePage();
            KeepsThePdfSectionOnThePage();
            KeepsThePdfColumnInContinuousSection();
            InsertsABlankPageForOddPages();
            DefaultsToNextPage();
            AbsorbsAPageBreakBeforeANewSection();
            Console.WriteLine("Section start type (AI02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Section start type (AI02): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(SectionStartType startType, string text = "Text")
        => new(
            new DocumentMetadata { Title = "Abschnittsstart" },
            [
                new Section([new Paragraph(new TextRun("Erster"))]),
                new Section([new Paragraph(new TextRun(text))]) { StartType = startType },
            ]);

    private static void WritesDocxStartType()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(SectionStartType.Continuous), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var document = ReadText(archive, "word/document.xml");
        Assert(document.Contains("<w:type w:val=\"continuous\""), "The section type is missing: " + document);

        // The order follows the schema (CT_SectPr): the header and footer references come
        // before w:type, w:titlePg comes after w:type.
        using var buffer2 = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(
                new PortableDocument(
                    new DocumentMetadata(),
                    new Section([new Paragraph(new TextRun("Text"))])
                    {
                        StartType = SectionStartType.Continuous,
                        FirstHeader = new HeaderFooter(new Paragraph(new TextRun("Kopf"))),
                    }),
                buffer2)
            .GetAwaiter()
            .GetResult();

        buffer2.Position = 0;
        using var archive2 = new ZipArchive(buffer2, ZipArchiveMode.Read, leaveOpen: true);
        var withHeader = ReadText(archive2, "word/document.xml");
        var section = withHeader[withHeader.IndexOf("<w:sectPr", StringComparison.Ordinal)..];
        var reference = section.IndexOf("<w:headerReference", StringComparison.Ordinal);
        var type = section.IndexOf("<w:type", StringComparison.Ordinal);
        var titlePage = section.IndexOf("<w:titlePg", StringComparison.Ordinal);
        Assert(reference >= 0 && type > reference, "The header reference must come before the section type.");
        Assert(titlePage > type, "The title page must come after the section type.");
    }

    private static void ReadsDocxStartType()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(SectionStartType.OddPage), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        var result = new DocxDocumentReader().Read(buffer);
        var sections = result.Document.Sections;

        Assert(sections.Count == 2, "The sections were not read: " + sections.Count);
        Assert(sections[0].StartType == SectionStartType.NextPage, "The default is wrong.");
        Assert(sections[1].StartType == SectionStartType.OddPage, "The start type was not read: " + sections[1].StartType);
    }

    private static void KeepsTheOdtSectionOnThePage()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(Document(SectionStartType.Continuous), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var content = ReadText(archive, "content.xml");

        // A section break with a master page starts a new page; a continuous section has no
        // break paragraph.
        Assert(!content.Contains("text:style-name=\"SectBreak2\""), "The continuous section starts a new page.");
    }

    private static void KeepsThePdfSectionOnThePage()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(SectionStartType.Continuous), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        var pages = new PdfDocumentReader().ReadPageTexts(buffer);
        Assert(pages.Count == 1, "The continuous section starts a new page: " + pages.Count);
        Assert(pages[0].Contains("Erster", StringComparison.Ordinal), "The first text is missing.");
        Assert(pages[0].Contains("Text", StringComparison.Ordinal), "The second text is missing.");
    }

    private static void KeepsThePdfColumnInContinuousSection()
    {
        var first = new Section(new Block[]
        {
            new Paragraph(new TextRun("FIRSTCOLUMN")), new ColumnBreak(), new Paragraph(new TextRun("SECONDCOLUMN")),
        }) { TextColumns = 2 };
        var second = first with { StartType = SectionStartType.Continuous, Blocks = [new Paragraph(new TextRun("CONTINUED"))] };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), [first, second]), buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        var content = PdfStreams.Decompress(buffer.ToArray());
        var previous = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(SECONDCOLUMN\) Tj");
        var continued = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(CONTINUED\) Tj");
        Assert(pages.Count == 1 && previous.Success && continued.Success && previous.Groups[1].Value == continued.Groups[1].Value,
            "A continuous section must remain in the current PDF column.");
    }

    private static void InsertsABlankPageForOddPages()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(SectionStartType.OddPage), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        var pages = new PdfDocumentReader().ReadPageTexts(buffer);

        // The first section fills a page, the second starts on an odd page: a blank page is
        // inserted when the parity does not match.
        Assert(pages.Count == 3, "The odd page start is wrong: " + pages.Count);
        Assert(pages[1].Trim().Length == 0, "The blank page is not empty: " + pages[1]);
        Assert(pages[2].Contains("Text", StringComparison.Ordinal), "The section text is missing.");
    }

    private static void DefaultsToNextPage()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(SectionStartType.NextPage), buffer)
            .GetAwaiter().GetResult();

        buffer.Position = 0;
        var pages = new PdfDocumentReader().ReadPageTexts(buffer);
        Assert(pages.Count == 2, "The next page start is wrong: " + pages.Count);
    }

    private static void AbsorbsAPageBreakBeforeANewSection()
    {
        // Word shows no blank page when a section ends with a page break and the next section
        // starts a new page; a continuous section keeps the break.
        foreach (var (startType, expected) in new[]
        {
            (SectionStartType.NextPage, 2),
            (SectionStartType.OddPage, 3),
            (SectionStartType.Continuous, 2),
        })
        {
            var document = new PortableDocument(
                new DocumentMetadata(),
                [
                    new Section([new Paragraph(new TextRun("Erster")), new PageBreak()]),
                    new Section([new Paragraph(new TextRun("Text"))]) { StartType = startType },
                ]);
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            buffer.Position = 0;
            var pages = new PdfDocumentReader().ReadPageTexts(buffer);
            Assert(pages.Count == expected, $"{startType}: expected {expected} pages, got {pages.Count}.");
            Assert(pages[^1].Contains("Text", StringComparison.Ordinal), startType + ": the section text is missing.");
        }

        // A page break at the end of the document stays.
        using var last = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(new PortableDocument(new DocumentMetadata(), new Section([new Paragraph(new TextRun("Erster")), new PageBreak()])), last)
            .GetAwaiter().GetResult();
        last.Position = 0;
        Assert(new PdfDocumentReader().ReadPageTexts(last).Count == 2, "The final page break was dropped.");
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
