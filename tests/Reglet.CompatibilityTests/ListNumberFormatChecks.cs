using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// the formatting of the number of a list (phase AJ, AJ04): the color and the weight in the
/// model, the DOCX and the PDF.
/// </summary>
internal static class ListNumberFormatChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxNumberFormat();
            ReadsDocxNumberFormat();
            DrawsPdfNumberFormat();
            WritesDocxBulletFormat();
            ReadsDocxBulletFormat();
            WritesOdtNumberFormat();
            WritesOneOdtStylePerDefinition();
            ReadsOdtLevels();
            ReadsNestedListWithoutStyle();
            ReadsOdtNumberFormat();
            ReadsOdtNumberFormatAndStart();
            WritesOdtBulletFormat();
            Console.WriteLine("Number formatting (AJ04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Number formatting (AJ04): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var list = TestLists.List(
            ListKind.Number,
            new[]
            {
                new ListItem(new Paragraph(new TextRun("First"))),
                new ListItem(new Paragraph(new TextRun("Second"))),
            },
            numberColor: Color.FromHex("#FF0000"),
            numberBold: true);

        return TestLists.Document(list);
    }

    private static void WritesDocxNumberFormat()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var numbering = ReadZipEntry(buffer.ToArray(), "word/numbering.xml");
        Require(numbering.Contains("w:val=\"FF0000\"", StringComparison.Ordinal), "DOCX: the color of the number is written.");
        Require(numbering.Contains("<w:b />", StringComparison.Ordinal), "DOCX: the number is written in bold.");
    }

    private static void ReadsDocxNumberFormat()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        var list = (ListBlock)read.Sections[0].Blocks[0];
        var level = TestLists.Level(read, list);

        Require(
            level.NumberColor?.ToString() == Color.FromHex("#FF0000").ToString(),
            "DOCX: the color of the number is read.");
        Require(level.NumberBold, "DOCX: the weight of the number is read.");
    }

    private static void DrawsPdfNumberFormat()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("1 0 0 rg", StringComparison.Ordinal), "PDF: the number is drawn in the color of the list.");
    }

    private static void WritesOdtNumberFormat()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("fo:color=\"#FF0000\"", StringComparison.Ordinal), "ODT: the color of the number is written.");
        Require(content.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal), "ODT: the number is written in bold.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("number", StringComparison.Ordinal)),
            "ODT: the color of the number is not reported.");
    }

    /// <summary>
    /// OF-B01: one ODT list style per numbering definition, with the color and the weight of every
    /// level in it; two lists that use the same definition share the style.
    /// </summary>
    private static void WritesOneOdtStylePerDefinition()
    {
        var definition = new NumberingDefinition(
            "Shared",
            [
                new NumberingLevel { Kind = ListKind.Number, NumberColor = Color.FromHex("#FF0000"), NumberBold = true },
                new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.LowerLetter, Start = 2 },
            ]);
        var first = new ListBlock("Shared", [new ListItem(new Paragraph(new TextRun("a")))]);
        var second = new ListBlock("Shared", [new ListItem(new Paragraph(new TextRun("b")))]);
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { first, second }))
        {
            Numbering = [definition],
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        var names = System.Text.RegularExpressions.Regex
            .Matches(content, "text:list-style style:name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
        Require(names.Count == 1 && names[0] == "Shared", "One ODT list style per definition: " + string.Join(", ", names));
        Require(
            System.Text.RegularExpressions.Regex.Matches(content, "text:style-name=\"Shared\"").Count == 2,
            "Both lists reference the shared definition.");
        Require(content.Contains("style:num-format=\"a\"", StringComparison.Ordinal), "The second level keeps its number format.");
        Require(content.Contains("text:start-value=\"2\"", StringComparison.Ordinal), "The second level keeps its start value.");
        Require(content.Contains("fo:color=\"#FF0000\"", StringComparison.Ordinal), "The first level keeps the number color.");
        Require(content.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal), "The first level keeps the bold number.");
    }

    /// <summary>A multi-level definition survives an ODT read/write cycle (OF-B01).</summary>
    private static void ReadsOdtLevels()
    {
        var definition = new NumberingDefinition(
            "Levels",
            [
                new NumberingLevel { Kind = ListKind.Number },
                new NumberingLevel { Kind = ListKind.Bullet, NumberColor = Color.FromHex("#0000FF") },
            ]);
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new ListBlock("Levels", [new ListItem(new Paragraph(new TextRun("a")))])))
        {
            Numbering = [definition],
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;

        var readDefinition = read.Numbering.Single(entry => entry.Name == "Levels");
        Require(readDefinition.Levels.Count == NumberingDefinition.MaxLevels, "All nine levels are read.");
        Require(readDefinition.Levels[0].Kind == ListKind.Number, "Level 1 keeps the number kind.");
        Require(readDefinition.Levels[1].Kind == ListKind.Bullet, "Level 2 keeps the bullet kind.");
        Require(
            readDefinition.Levels[1].NumberColor?.ToString() == Color.FromHex("#0000FF").ToString(),
            "Level 2 keeps the bullet color.");
        Require(read.Numbering.Count == 1, "One definition is read: " + string.Join(", ", read.Numbering.Select(entry => entry.Name)));
    }

    /// <summary>
    /// LibreOffice writes a nested <c>text:list</c> without a style name; it continues the list
    /// style of its parent. Such a list must not get a definition of its own (OF-B01).
    /// </summary>
    private static void ReadsNestedListWithoutStyle()
    {
        var definition = new NumberingDefinition(
            "Levels",
            [
                new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.Decimal },
                new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.LowerLetter },
            ]);
        var nested = new ListBlock("Levels", [new ListItem(new Paragraph(new TextRun("nested")))]) { NestingLevel = 2 };
        var outer = new ListBlock(
            "Levels",
            [new ListItem(new Block[] { new Paragraph(new TextRun("outer")), nested })]);
        var document = new PortableDocument(new DocumentMetadata(), new Section(outer)) { Numbering = [definition] };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        // Drop the style name of the nested list, the way LibreOffice writes it.
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        var first = content.IndexOf("<text:list text:style-name=", StringComparison.Ordinal);
        Require(first >= 0, "The written list carries a style name.");
        var start = content.IndexOf("<text:list text:style-name=", first + 1, StringComparison.Ordinal);
        Require(start >= 0, "The nested list carries a style name.");
        var end = content.IndexOf('>', start);
        content = string.Concat(content.AsSpan(0, start), "<text:list", content.AsSpan(end));
        var package = TestPackages.ReplacePart(buffer.ToArray(), "content.xml", Encoding.UTF8.GetBytes(content));

        var read = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt).Document;
        Require(read.Numbering.Count == 1, "One definition is read: " + string.Join(", ", read.Numbering.Select(entry => entry.Name)));
        Require(read.Numbering[0].Levels[1].Format == ListNumberFormat.LowerLetter, "Level 2 keeps its number format.");
        var outerList = (ListBlock)read.Sections[0].Blocks[0];
        var nestedList = (ListBlock)((ListItem)outerList.Items[0]).Blocks.OfType<ListBlock>().Single();
        Require(
            nestedList.NumberingName == outerList.NumberingName,
            $"The nested list continues the definition of its parent ({nestedList.NumberingName} vs {outerList.NumberingName}).");
    }

    private static void ReadsOdtNumberFormat()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(Document(), buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var list = (ListBlock)read.Sections[0].Blocks[0];
        var level = TestLists.Level(read, list);

        Require(
            level.NumberColor?.ToString() == Color.FromHex("#FF0000").ToString(),
            "ODT: the color of the number is read.");
        Require(level.NumberBold, "ODT: the weight of the number is read.");
    }

    private static void WritesDocxBulletFormat()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(BulletDocument(), buffer)
            .GetAwaiter()
            .GetResult();

        var numbering = ReadZipEntry(buffer.ToArray(), "word/numbering.xml");
        Require(numbering.Contains("w:val=\"0000FF\"", StringComparison.Ordinal), "DOCX: the color of the bullet is written.");
        Require(numbering.Contains("<w:b />", StringComparison.Ordinal), "DOCX: the bullet is written in bold.");
    }

    private static void ReadsDocxBulletFormat()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(BulletDocument(), buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        var list = (ListBlock)read.Sections[0].Blocks[0];
        var level = TestLists.Level(read, list);

        Require(
            level.NumberColor?.ToString() == Color.FromHex("#0000FF").ToString(),
            "DOCX: the color of the bullet is read.");
        Require(level.NumberBold, "DOCX: the weight of the bullet is read.");
    }

    private static void ReadsOdtNumberFormatAndStart()
    {
        var numberList = TestLists.List(
            ListKind.Number,
            new[]
            {
                new ListItem(new Paragraph(new TextRun("First"))),
                new ListItem(new Paragraph(new TextRun("Second"))),
            },
            ListNumberFormat.LowerRoman,
            5);
        var document = TestLists.Document(numberList);

        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var list = (ListBlock)read.Sections[0].Blocks[0];
        var level = TestLists.Level(read, list);

        Require(level.Format == ListNumberFormat.LowerRoman, "ODT: the number format is read.");
        Require(level.Start == 5, "ODT: the start value is read.");
    }

    private static void WritesOdtBulletFormat()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter()
            .WriteAsync(BulletDocument(), buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("fo:color=\"#0000FF\"", StringComparison.Ordinal), "ODT: the color of the bullet is written.");
        Require(content.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal), "ODT: the bullet is written in bold.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("bullet", StringComparison.Ordinal)),
            "ODT: the color of the bullet is not reported.");
    }

    private static PortableDocument BulletDocument()
    {
        var list = TestLists.List(
            ListKind.Bullet,
            new[] { new ListItem(new Paragraph(new TextRun("First"))) },
            numberColor: Color.FromHex("#0000FF"),
            numberBold: true);

        return TestLists.Document(list);
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
