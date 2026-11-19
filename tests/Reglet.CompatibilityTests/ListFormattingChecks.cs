using System.IO.Compression;
using Reglet;

/// <summary>
/// List fine control (phase Y, Y06): number format and start value of numbered lists
/// in DOCX, ODT and PDF.
/// </summary>
internal static class ListFormattingChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxNumbering();
            WritesOdtNumbering();
            RendersPdfNumbering();
            UsesSameListGeometryInAllFormats();
            PushesTextPastWideMarkers();
            IgnoresFormatForBullets();
            RejectsInvalidValues();
            Console.WriteLine("List fine control (Y06): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("List fine control (Y06): " + exception);
            return 1;
        }
    }

    private static PortableDocument NumberedDocument(ListNumberFormat format, int start)
    {
        var list = TestLists.List(
            ListKind.Number,
            new[]
            {
                new ListItem(new Paragraph(new TextRun("one"))),
                new ListItem(new Paragraph(new TextRun("two"))),
            },
            format,
            start);

        return TestLists.Document(list);
    }

    private static PortableDocument BulletDocument()
    {
        var list = TestLists.List(
            ListKind.Bullet,
            new[] { new ListItem(new Paragraph(new TextRun("one"))) },
            ListNumberFormat.UpperRoman,
            5);

        return TestLists.Document(list);
    }

    private static void WritesDocxNumbering()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(NumberedDocument(ListNumberFormat.LowerRoman, 3), buffer)
            .GetAwaiter().GetResult();

        var numbering = ReadZipEntry(buffer.ToArray(), "word/numbering.xml");
        Require(numbering.Contains("w:val=\"lowerRoman\"", StringComparison.Ordinal), "DOCX: lowerRoman number format.");
        Require(numbering.Contains("<w:start w:val=\"3\"", StringComparison.Ordinal), "DOCX: start value 3.");
    }

    private static void WritesOdtNumbering()
    {
        var document = NumberedDocument(ListNumberFormat.UpperLetter, 4);
        var list = (ListBlock)document.Sections[0].Blocks[0];
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");

        // OF-B01: the list style is the numbering definition itself, not a style derived from the
        // level it is used at.
        Require(
            content.Contains($"text:list-style style:name=\"{list.NumberingName}\"", StringComparison.Ordinal),
            "ODT: the list style is the numbering definition.");
        Require(
            content.Contains($"text:style-name=\"{list.NumberingName}\"", StringComparison.Ordinal),
            "ODT: the list references its definition.");
        Require(content.Contains("text:start-value=\"4\"", StringComparison.Ordinal), "ODT: start value.");
        Require(content.Contains("style:num-format=\"A\"", StringComparison.Ordinal), "ODT: num-format A.");
    }

    private static void RendersPdfNumbering()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(NumberedDocument(ListNumberFormat.LowerRoman, 3), buffer)
            .GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(iii.) Tj", StringComparison.Ordinal), "PDF: first marker iii.");
        Require(content.Contains("(iv.) Tj", StringComparison.Ordinal), "PDF: second marker iv.");
    }

    /// <summary>A marker wider than the hanging indent pushes the text to the next default tab stop (Word's list tab).</summary>
    private static void PushesTextPastWideMarkers()
    {
        using var buffer = new MemoryStream();
        var items = Enumerable.Range(0, 8).Select(_ => new ListItem(new Paragraph(new TextRun("Item")))).ToArray();
        new PdfDocumentWriter().WriteAsync(TestLists.Document(TestLists.List(ListKind.Number, items, ListNumberFormat.UpperRoman, 1)), buffer)
            .GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var positions = System.Text.RegularExpressions.Regex.Matches(content, @"([0-9.]+) [0-9.]+ Td\n\(Item\) Tj")
            .Select(match => match.Groups[1].Value)
            .ToList();
        Require(positions.Count == 8 && positions[0] == "108", $"PDF: a narrow marker keeps the text at 36 pt ({string.Join(", ", positions)}).");
        Require(positions[7] == "144", $"PDF: the text after VIII. moves to the next default tab stop ({positions[^1]}).");
    }

    /// <summary>PDF and ODT use the list geometry of DOCX (w:ind left 720, hanging 360): marker at 18 pt, text at 36 pt.</summary>
    private static void UsesSameListGeometryInAllFormats()
    {
        var document = PortableDocument.Create().BulletList("Item").Build();

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        var marker = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(\\225\) Tj");
        var text = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(Item\) Tj");
        Require(marker.Success && marker.Groups[1].Value == "90", $"PDF: the marker hangs 18 pt before the text (x = {marker.Groups[1].Value}).");
        Require(text.Success && text.Groups[1].Value == "108", $"PDF: the text is indented 36 pt (x = {text.Groups[1].Value}).");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(ReadZipEntry(odt.ToArray(), "content.xml").Contains(
                "text:label-followed-by=\"listtab\" text:list-tab-stop-position=\"36pt\" fo:text-indent=\"-18pt\" fo:margin-left=\"36pt\"", StringComparison.Ordinal),
            "ODT: level 1 indents the text 36 pt with an 18 pt hanging label (label-alignment).");
    }

    private static void IgnoresFormatForBullets()
    {
        var document = BulletDocument();
        var list = (ListBlock)document.Sections[0].Blocks[0];
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("text:list-level-style-bullet", StringComparison.Ordinal), "ODT: the level is a bullet level.");
        Require(!content.Contains("start-value", StringComparison.Ordinal), "ODT: bullet list has no start value.");
        Require(!content.Contains("style:num-format", StringComparison.Ordinal), "ODT: bullet list has no number format.");

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var readList = (ListBlock)read.Sections[0].Blocks[0];
        Require(TestLists.Level(read, readList).Kind == ListKind.Bullet, "ODT: the bullet kind is read.");
        Require(readList.NumberingName == list.NumberingName, "ODT: the definition keeps its name.");
    }

    private static void RejectsInvalidValues()
    {
        var items = new[] { new ListItem(new Paragraph(new TextRun("x"))) };
        Throws<ArgumentOutOfRangeException>(() => new NumberingLevel { Start = 0 });
        Throws<ArgumentOutOfRangeException>(() => new NumberingLevel { Format = (ListNumberFormat)99 });

        // The definition name is also the ODT list style name; it must be an NCName.
        Throws<ArgumentException>(() => new NumberingDefinition("1 List", [new NumberingLevel()]));
        Throws<ArgumentException>(() => new NumberingDefinition(string.Empty, [new NumberingLevel()]));
        Throws<ArgumentOutOfRangeException>(() => new NumberingDefinition("n", []));
        Throws<ArgumentOutOfRangeException>(() => new NumberingDefinition(
            "n",
            Enumerable.Repeat(new NumberingLevel(), NumberingDefinition.MaxLevels + 1)));
        Require(
            new NumberingDefinition("n", [new NumberingLevel { Start = 5 }]).Levels.Count == NumberingDefinition.MaxLevels,
            "A definition repeats its last level up to the maximum depth.");
        Throws<DocumentValidationException>(() => new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x"))))
        {
            Numbering = [new NumberingDefinition("n", [new NumberingLevel()]), new NumberingDefinition("n", [new NumberingLevel()])],
        });
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
