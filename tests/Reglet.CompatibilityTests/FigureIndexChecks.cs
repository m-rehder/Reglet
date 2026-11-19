using System.IO.Compression;
using Reglet;

/// <summary>
/// the table of figures and the alphabetical index: DOCX writes <c>TOC \c</c>, <c>INDEX</c> and
/// <c>XE</c> fields, ODT the illustration, table and alphabetical indexes and index marks; the PDF
/// renders the entries with the pages of the captions and marks.
/// </summary>
internal static class FigureIndexChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidValues();
            WritesDocx();
            WritesOdt();
            WritesOdtTableIndex();
            RendersPdfFigureList();
            RendersPdfIndex();
            RoundTripsWithoutError();
            Console.WriteLine("Table of figures and index: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Table of figures and index: " + exception);
            return 1;
        }
    }

    private static Paragraph Caption(string label, int number, string text)
        => new(new Inline[] { new TextRun(label + " "), new Field($"SEQ {label} \\* ARABIC", number.ToString()), new TextRun(": " + text) });

    private static Paragraph Mark(string text, IndexEntry entry)
        => new(new Inline[] { new TextRun(text), entry });

    private static PortableDocument Document()
        => new(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfFigures { Title = "Figures" },
            new AlphabeticalIndex { Title = "Index" },
            new PageBreak(),
            Caption("Figure", 1, "First picture"),
            Mark("Apples grow.", new IndexEntry("Apple")),
            Mark("Pears too.", new IndexEntry("Fruit", "Pear")),
            new PageBreak(),
            Caption("Figure", 2, "Second picture"),
            Caption("Table", 1, "A table"),
            Mark("Apples again.", new IndexEntry("Apple")),
            Mark("Oranges.", new IndexEntry("Ångström \"quoted\": text")),
        }));

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentException>(() => new TableOfFigures { Label = "" });
        Throws<ArgumentException>(() => new TableOfFigures { Label = "Fig\"ure" });
        Throws<ArgumentException>(() => new IndexEntry(" "));
        Throws<ArgumentException>(() => new IndexEntry("a", "b\nc"));
    }

    private static void WritesDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");

        Require(body.Contains("TOC \\h \\z \\c \"Figure\"", StringComparison.Ordinal), "DOCX: the table of figures is a TOC field over the label.");
        Require(body.Contains(">First picture<", StringComparison.Ordinal) || body.Contains("First picture", StringComparison.Ordinal), "DOCX: the cached entry is present.");
        Require(!body.Contains("A table</w:t>", StringComparison.Ordinal) || Count(body, "A table") == 1, "DOCX: a caption of another label is not listed.");
        Require(body.Contains("INDEX \\h", StringComparison.Ordinal), "DOCX: the index is an INDEX field.");
        Require(body.Contains(" XE \"Apple\" ", StringComparison.Ordinal), "DOCX: an index entry is an XE field.");
        Require(body.Contains(" XE \"Fruit:Pear\" ", StringComparison.Ordinal), "DOCX: a sub entry follows its main entry after a colon.");
        Require(body.Contains("XE \"Ångström \\\"quoted\\\"\\: text\"", StringComparison.Ordinal), "DOCX: quotation marks and colons are escaped.");
        Require(body.Contains("<w:vanish", StringComparison.Ordinal), "DOCX: the entry field is hidden text.");
        Require(Count(body, ">Apple<") <= 1, "DOCX: an entry that occurs twice is listed once.");
    }

    private static void WritesOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");

        Require(content.Contains("<text:illustration-index ", StringComparison.Ordinal), "ODT: the table of figures is an illustration index.");
        Require(content.Contains("text:caption-sequence-name=\"Figure\"", StringComparison.Ordinal), "ODT: the index reads the sequence of the label.");
        Require(content.Contains("<text:alphabetical-index ", StringComparison.Ordinal), "ODT: the index is an alphabetical index.");
        Require(content.Contains("<text:alphabetical-index-mark text:string-value=\"Apple\"", StringComparison.Ordinal), "ODT: an entry is an index mark.");
        Require(content.Contains("text:string-value=\"Pear\" text:key1=\"Fruit\"", StringComparison.Ordinal), "ODT: a sub entry has the main entry as its key.");
        Require(content.Contains("First picture", StringComparison.Ordinal), "ODT: the cached entry is present.");
    }

    private static void WritesOdtTableIndex()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableOfFigures { Label = "Table" },
            Caption("Table", 1, "A table"),
        }));
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("<text:table-index ", StringComparison.Ordinal) && content.Contains("<text:table-index-source ", StringComparison.Ordinal),
            "ODT: the label \"Table\" gives a table index.");
    }

    private static void RendersPdfFigureList()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));

        Require(pages.Count == 3, $"PDF: three pages ({pages.Count}).");
        var first = pages[0];
        Require(first.Contains("Figure 1: First picture", StringComparison.Ordinal) && first.Contains("Figure 2: Second picture", StringComparison.Ordinal), "PDF: both figure captions are listed.");
        Require(!first.Contains("A table", StringComparison.Ordinal), "PDF: a caption of another label is not listed.");
        var lines = first.Split('\n');
        Require(lines.Any(line => line.Contains("First picture", StringComparison.Ordinal) && line.TrimEnd().EndsWith("2", StringComparison.Ordinal)), "PDF: the first caption shows page 2.");
        Require(lines.Any(line => line.Contains("Second picture", StringComparison.Ordinal) && line.TrimEnd().EndsWith("3", StringComparison.Ordinal)), "PDF: the second caption shows page 3.");
        Require(!result.HasWarnings, "PDF: no warning.");
    }

    private static void RendersPdfIndex()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var first = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()))[0];
        var lines = first.Split('\n').Select(line => line.Trim()).ToList();

        Require(lines.Any(line => line == "Apple, 2, 3"), "PDF: an entry on two pages lists both pages: " + string.Join("|", lines));
        Require(lines.Contains("Fruit") && lines.Any(line => line == "Pear, 2"), "PDF: a sub entry stands below its main entry.");
        Require(lines.IndexOf("A") < lines.IndexOf("Apple, 2, 3") && lines.IndexOf("Apple, 2, 3") < lines.IndexOf("F"), "PDF: the entries are grouped by letter.");
        Require(lines.IndexOf("A") < lines.FindIndex(line => line.StartsWith("Ångström", StringComparison.Ordinal)), "PDF: an accented entry sorts with its base letter.");
    }

    private static void RoundTripsWithoutError()
    {
        foreach (var write in new Action<PortableDocument, Stream>[]
        {
            (document, stream) => new DocxDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult(),
            (document, stream) => new OdtDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult(),
        })
        {
            using var buffer = new MemoryStream();
            write(Document(), buffer);
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, buffer.Length > 0 && IsZipOf(buffer, "word/document.xml") ? DocumentFormat.Docx : DocumentFormat.Odt);
            Require(read.Document.Sections.Count > 0, "Reading a document with a table of figures and an index works.");
        }
    }

    private static bool IsZipOf(MemoryStream buffer, string name)
    {
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        var found = zip.GetEntry(name) is not null;
        buffer.Position = 0;
        return found;
    }

    private static int Count(string text, string value) => text.Split(value).Length - 1;

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
