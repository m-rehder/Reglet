using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// OF-B02: reusable list styles. One <see cref="NumberingDefinition"/> is written once per
/// format and referenced by every list that uses it.
/// </summary>
internal static class SharedListStyleChecks
{
    internal static int Run()
    {
        try
        {
            WritesOneDefinitionInDocx();
            WritesOneDefinitionInOdt();
            WritesOdtContinuation();
            ContinuesNumberingInPdf();
            KeepsTheLevelsPerList();
            ContinuesAfterARoundTrip();
            Console.WriteLine("Shared list styles (OF-B02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Shared list styles (OF-B02): " + exception);
            return 1;
        }
    }

    /// <summary>Two lists with the same definition share one abstractNum and one num in DOCX.</summary>
    private static void WritesOneDefinitionInDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(shared: true), buffer).GetAwaiter().GetResult();

        var numbering = ReadZipEntry(buffer.ToArray(), "word/numbering.xml");
        Require(
            Regex.Matches(numbering, "<w:abstractNum ").Count == 1,
            "One abstractNum for the shared definition: " + numbering);
        Require(
            Regex.Matches(numbering, "<w:num ").Count == 1,
            "One num for the shared definition: " + numbering);

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        var numIds = Regex.Matches(body, "<w:numId w:val=\"(\\d+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Require(numIds.Count == 1, "Both lists reference the same numId: " + string.Join(", ", numIds));
    }

    /// <summary>Two lists with the same definition share one ODT list style.</summary>
    private static void WritesOneDefinitionInOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(shared: true), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        var names = Regex.Matches(content, "text:list-style style:name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
        Require(names.Count == 1, "One text:list-style for the shared definition: " + string.Join(", ", names));
        Require(
            Regex.Matches(content, $"text:list text:style-name=\"{names[0]}\"").Count == 2,
            "Both lists reference the shared style.");
    }

    /// <summary>
    /// OF-B02: the second list continues the first through <c>text:continue-list</c> and does not
    /// restart at the start value (which would make it look like a new list).
    /// </summary>
    private static void WritesOdtContinuation()
    {
        var definition = new NumberingDefinition("Shared", [new NumberingLevel { Kind = ListKind.Number, Start = 1 }]);
        var first = new ListBlock("Shared", [new ListItem(new Paragraph(new TextRun("one")))]);
        var second = new ListBlock("Shared", [new ListItem(new Paragraph(new TextRun("two")))]);
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { first, second }))
        {
            Numbering = [definition],
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");

        Require(
            content.Contains("text:continue-list=\"List1\"", StringComparison.Ordinal),
            "The second list continues the first: " + content);
        Require(
            content.Contains("xml:id=\"List1\"", StringComparison.Ordinal)
                && content.Contains("xml:id=\"List2\"", StringComparison.Ordinal),
            "Every list carries its own xml:id: " + content);
    }

    /// <summary>The shared definition and the continuation survive a read/write cycle.</summary>
    private static void ContinuesAfterARoundTrip()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(shared: true), buffer).GetAwaiter().GetResult();

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var sections = read.Sections[0];
        var lists = sections.Blocks.OfType<ListBlock>().ToList();
        Require(lists.Count == 2, "Both lists are read: " + lists.Count);
        Require(
            lists[0].NumberingName == lists[1].NumberingName,
            $"Both lists keep the shared definition ({lists[0].NumberingName} vs {lists[1].NumberingName}).");
    }

    /// <summary>The shared definition continues the numbering over both lists in the PDF.</summary>
    private static void ContinuesNumberingInPdf()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(shared: true), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(content.Contains("(3.) Tj", StringComparison.Ordinal), "The third item continues the shared numbering: " + content);
    }

    /// <summary>Without a shared definition every list starts at its own first number.</summary>
    private static void KeepsTheLevelsPerList()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(shared: false), buffer).GetAwaiter().GetResult();

        var numbering = ReadZipEntry(buffer.ToArray(), "word/numbering.xml");
        Require(
            Regex.Matches(numbering, "<w:abstractNum ").Count == 2,
            "Two separate definitions are written: " + numbering);
    }

    /// <summary>Two lists, either sharing one definition or with one definition each.</summary>
    private static PortableDocument Document(bool shared)
    {
        var first = new NumberingDefinition("First", [new NumberingLevel { Kind = ListKind.Number }]);
        var second = shared
            ? first
            : new NumberingDefinition("Second", [new NumberingLevel { Kind = ListKind.Number }]);

        var listA = new ListBlock(
            first.Name,
            [
                new ListItem(new Paragraph(new TextRun("one"))),
                new ListItem(new Paragraph(new TextRun("two"))),
            ]);
        var listB = new ListBlock(
            second.Name,
            [new ListItem(new Paragraph(new TextRun("three")))]);

        return new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { listA, listB }))
        {
            Numbering = shared ? [first] : [first, second],
        };
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
