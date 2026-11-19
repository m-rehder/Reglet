using Reglet;

/// <summary>
/// diagnostics and operation (phase AE): content statistics (AE02) and the write progress
/// report (AE01). Auto hyphenation (AE03) is a documented boundary.
/// </summary>
internal static class DiagnosticsChecks
{
    internal static int Run()
    {
        try
        {
            CountsStatistics();
            ReportsProgress();
            Console.WriteLine("Diagnostics (AE): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Diagnostics (AE): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
        => PortableDocument.Create()
            .Heading("H", 1)
            .Paragraph("one two")
            .BulletList("a", "b")
            .Table(t => t.Header("A").Row("1"))
            .Build();

    private static void CountsStatistics()
    {
        var stats = Document().Statistics();
        Require(stats.Sections == 1, "AE02: sections.");
        Require(stats.Headings == 1, "AE02: headings.");
        Require(stats.Paragraphs >= 1, "AE02: paragraphs.");
        Require(stats.Lists == 1, "AE02: lists.");
        Require(stats.ListItems == 2, "AE02: list items.");
        Require(stats.Tables == 1, "AE02: tables.");
        Require(stats.TableRows == 2, "AE02: table rows.");
        Require(stats.TableCells == 2, "AE02: table cells.");
        Require(stats.Images == 0, "AE02: images.");
        Require(stats.Words >= 6, "AE02: words.");
        Require(stats.Characters > 0, "AE02: characters.");
    }

    private static void ReportsProgress()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new[]
            {
                new Section(new Block[] { new Paragraph(new TextRun("one")) }),
                new Section(new Block[] { new Paragraph(new TextRun("two")) }),
            });

        using (var buffer = new MemoryStream())
        {
            var collector = new Collector();
            new DocxDocumentWriter().WriteAsync(document, buffer, new DocxWriteOptions { Progress = collector })
                .GetAwaiter().GetResult();
            Require(collector.Items.Count == 2, "AE01: DOCX reports two sections.");
            Require(collector.Items[^1] == new WriteProgress("section", 2, 2), "AE01: DOCX final report.");
        }

        using (var buffer = new MemoryStream())
        {
            var collector = new Collector();
            new OdtDocumentWriter().WriteAsync(document, buffer, new OdtWriteOptions { Progress = collector })
                .GetAwaiter().GetResult();
            Require(collector.Items.Count == 2, "AE01: ODT reports two sections.");
        }

        using (var buffer = new MemoryStream())
        {
            var collector = new Collector();
            new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Progress = collector })
                .GetAwaiter().GetResult();
            Require(collector.Items.Count == 2, "AE01: PDF reports two sections.");
        }
    }

    private sealed class Collector : IProgress<WriteProgress>
    {
        public List<WriteProgress> Items { get; } = [];

        public void Report(WriteProgress value) => Items.Add(value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
