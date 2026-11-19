using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// searching and replacing text in the model (phase AH): <see cref="DocumentSearch"/> finds the
/// occurrences and returns a new document with the replacements.
/// </summary>
internal static class SearchChecks
{
    internal static int Run()
    {
        try
        {
            FindsOccurrences();
            ReplacesText();
            KeepsFormatting();
            KeepsCharacterStyleWhenSplittingRuns();
            SearchesNestedBlocks();
            HonoursQueryOptions();
            LimitsRegexTime();
            HonoursCancellation();
            WritesReplacedDocument();
            Console.WriteLine("Search and replace (F): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Search and replace (F): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var list = TestLists.List(
            ListKind.Bullet,
            [new ListItem(new Paragraph(new TextRun("Preisliste ohne MwSt")))]);

        var table = new TableBlock(
        [
            new TableRow([new TableCell(new Paragraph(new TextRun("Rechnung")))]),
        ]);

        return TestLists.Fix(new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                new Heading(HeadingLevel.Level1, new TextRun("Rechnung 2026")),
                new Paragraph(new TextRun("Die Rechnung enthält MwSt.")),
                list,
                table,
            ])));
    }

    private static void FindsOccurrences()
    {
        var matches = DocumentSearch.Find(Document(), new TextQuery("Rechnung"));
        Require(matches.Count == 3, "Find: three occurrences.");
        Require(matches[0].SectionIndex == 0, "Find: the section of the occurrence.");
        Require(matches[0].Index == 0, "Find: the position in the run.");
        Require(matches[0].Text == "Rechnung", "Find: the found text.");
        Require(matches[1].Block is Paragraph, "Find: the block of the occurrence.");
        Require(matches[2].Block is Paragraph, "Find: the paragraph in the table cell of the occurrence.");
    }

    private static void ReplacesText()
    {
        var document = Document();
        var replaced = DocumentSearch.Replace(document, new TextQuery("Rechnung"), "Beleg");
        Require(
            DocumentSearch.Find(replaced, new TextQuery("Rechnung")).Count == 0,
            "Replace: the old text is gone.");
        Require(
            DocumentSearch.Find(replaced, new TextQuery("Beleg")).Count == 3,
            "Replace: the new text is in all three places.");
        Require(
            DocumentSearch.Find(document, new TextQuery("Rechnung")).Count == 3,
            "Replace: the original document is unchanged.");
    }

    private static void KeepsFormatting()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                new Paragraph(new Inline[] { new TextRun("vor ") { Bold = true }, new TextRun("nach") }),
            ]));

        var replaced = DocumentSearch.Replace(document, new TextQuery("nach"), "neu");
        var paragraph = (Paragraph)replaced.Sections[0].Blocks[0];
        Require(paragraph.Inlines.Count == 2, "Replace: two runs.");
        var first = (TextRun)paragraph.Inlines[0];
        var second = (TextRun)paragraph.Inlines[1];
        Require(first.Text == "vor ", "Replace: the first run is unchanged.");
        Require(first.Bold == true, "Replace: the formatting of the first run is kept.");
        Require(second.Text == "neu", "Replace: the new text.");
        Require(second.Bold != true, "Replace: the formatting of the second run is kept.");
    }

    private static void SearchesNestedBlocks()
    {
        var document = TestLists.Fix(new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                TestLists.List(
                    ListKind.Number,
                    [new ListItem(new Paragraph(new Inline[] { new TextRun("a "), new TextRun("b") }))]),
            ])));

        var matches = DocumentSearch.Find(document, new TextQuery("b"));
        Require(matches.Count == 1, "Find: the text in the list item is found.");
    }

    private static void KeepsCharacterStyleWhenSplittingRuns()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(
            new TextRun("before old after") { CharacterStyleName = "Emphasis" })))
        {
            CharacterStyles = [new CharacterStyle("Emphasis") { Italic = true }],
        };
        var replaced = DocumentSearch.Replace(document, new TextQuery("old"), "new");
        var runs = ((Paragraph)replaced.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().ToArray();
        Require(runs.Length == 3 && runs.All(run => run.CharacterStyleName == "Emphasis"),
            "Replace: all fragments retain the named character style.");
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(replaced, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var readRuns = ((Paragraph)read.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().ToArray();
            Require(readRuns.Length > 0 && readRuns.All(run => run.CharacterStyleName == "Emphasis"),
                $"Replace: the named character style survives {format} export and import.");
        }
    }

    private static void HonoursQueryOptions()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                new Paragraph(new TextRun("Rechnung rechnungen RECHNUNG")),
            ]));

        Require(
            DocumentSearch.Find(document, new TextQuery("rechnung")).Count == 3,
            "Find: without case sensitivity every occurrence is found.");
        Require(
            DocumentSearch.Find(document, new TextQuery("rechnung") { MatchCase = true }).Count == 1,
            "Find: with case sensitivity only the exact one is found.");
        Require(
            DocumentSearch.Find(document, new TextQuery("rechnung") { WholeWord = true }).Count == 2,
            "Find: only whole words are found.");
        Require(
            DocumentSearch.Find(document, new TextQuery("Rechn(u)?ng") { IsRegex = true }).Count == 3,
            "Find: the regular expression matches.");
    }

    private static void LimitsRegexTime()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun(new string('a', 40) + "!"))));
        var query = new TextQuery("(a+)+$") { IsRegex = true };

        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var search in new Action[] { () => DocumentSearch.Find(document, query), () => DocumentSearch.Replace(document, query, "x") })
        {
            try
            {
                search();
                throw new InvalidOperationException("Search: the runaway expression was not stopped.");
            }
            catch (DocumentValidationException exception)
            {
                Require(exception.InnerException is System.Text.RegularExpressions.RegexMatchTimeoutException, "Search: the timeout is the cause.");
            }
        }

        Require(timer.Elapsed < TimeSpan.FromSeconds(20), "Search: the time limit is enforced.");
    }

    private static void HonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var search in new Action[]
        {
            () => DocumentSearch.Find(Document(), new TextQuery("Rechnung"), cancelled.Token),
            () => DocumentSearch.Replace(Document(), new TextQuery("Rechnung") { IsRegex = true }, "x", cancelled.Token),
        })
        {
            try
            {
                search();
                throw new InvalidOperationException("Search: cancellation was ignored.");
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void WritesReplacedDocument()
    {
        var replaced = DocumentSearch.Replace(Document(), new TextQuery("Rechnung"), "Beleg");

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(replaced, buffer).GetAwaiter().GetResult();

        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("document.xml missing");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var document = reader.ReadToEnd();
        Require(document.Contains("Beleg", StringComparison.Ordinal), "The replaced text is written.");
        Require(!document.Contains("Rechnung", StringComparison.Ordinal), "The old text is not written.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
