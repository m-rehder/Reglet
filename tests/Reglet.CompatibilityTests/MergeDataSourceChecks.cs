using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// data sources of a mail merge (phase AJ, AJ03): CSV, XML, JSON and Excel.
/// </summary>
internal static class MergeDataSourceChecks
{
    internal static int Run()
    {
        try
        {
            ReadsCsv();
            ReadsCsvWithQuotedFields();
            ReadsCsvWithEscapedQuotes();
            ReadsXml();
            ReadsJson();
            ReadsExcel();
            ReadsFirstExcelSheetInWorkbookOrder();
            ReadsExcelNamedRange();
            WideExcelRowsStayCheap();
            RejectsInvalidInput();
            Console.WriteLine("Merge data sources (AJ03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Merge data sources (AJ03): " + exception);
            return 1;
        }
    }

    private static void ReadsCsv()
    {
        using var reader = new StringReader("Name,City\nAda,London\nGrace,New York\n");
        var rows = MergeDataSource.FromCsv(reader);

        Require(rows.Count == 2, "CSV: two rows are read.");
        Require(rows[0]["Name"] == "Ada", "CSV: the first value is read.");
        Require(rows[1]["City"] == "New York", "CSV: the second row is read.");
        Require(!rows[0].ContainsKey("Missing"), "CSV: an unknown name is not in the row.");
    }

    private static void ReadsCsvWithQuotedFields()
    {
        using var reader = new StringReader("Name,Note\n\"Ada, Countess\",\"line 1\nline 2\"\n");
        var rows = MergeDataSource.FromCsv(reader);

        Require(rows.Count == 1, "CSV: a quoted field with a line break is one row.");
        Require(rows[0]["Name"] == "Ada, Countess", "CSV: a quoted field may contain the separator.");
        Require(rows[0]["Note"] == "line 1\nline 2", "CSV: a quoted field may contain a line break.");
    }

    private static void ReadsCsvWithEscapedQuotes()
    {
        using var reader = new StringReader("A,B\n\"a\"\",b\",\"x\"\"\"\n");
        var rows = MergeDataSource.FromCsv(reader);

        Require(rows[0]["A"] == "a\",b", "CSV: an escaped quote does not end the quoted field.");
        Require(rows[0]["B"] == "x\"", "CSV: an escaped quote before the closing quote is one quote.");
    }

    private static void ReadsXml()
    {
        using var reader = new StringReader(
            "<customers><customer id=\"1\"><name>Ada</name><city>London</city></customer>"
            + "<customer id=\"2\"><name>Grace</name><city>New York</city></customer></customers>");

        var rows = MergeDataSource.FromXml(reader);

        Require(rows.Count == 2, "XML: two rows are read.");
        Require(rows[0]["name"] == "Ada", "XML: a child element is a field.");
        Require(rows[1]["id"] == "2", "XML: an attribute is a field.");
    }

    private static void ReadsJson()
    {
        using var reader = new StringReader("[{\"Name\":\"Ada\",\"Age\":36,\"Active\":true,\"Note\":null}]");
        var rows = MergeDataSource.FromJson(reader);

        Require(rows.Count == 1, "JSON: one row is read.");
        Require(rows[0]["Name"] == "Ada", "JSON: a string value is read.");
        Require(rows[0]["Age"] == "36", "JSON: a number is written as text.");
        Require(rows[0]["Active"] == "true", "JSON: a boolean is written as text.");
        Require(rows[0]["Note"] is null, "JSON: a null value stays null.");
    }

    private static void ReadsExcel()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteWorkbook(archive);
            WriteEntry(archive, "xl/sharedStrings.xml",
                "<sst><si><t>Name</t></si><si><t>City</t></si><si><t>Ada</t></si><si><t>London</t></si></sst>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml",
                "<worksheet><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>"
                + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\" t=\"s\"><v>3</v></c></row>"
                + "</sheetData></worksheet>");
        }

        buffer.Position = 0;
        var rows = MergeDataSource.FromExcel(buffer);

        Require(rows.Count == 1, "Excel: one row is read.");
        Require(rows[0]["Name"] == "Ada", "Excel: the shared string is read.");
        Require(rows[0]["City"] == "London", "Excel: the second column is read.");
    }

    private static void RejectsInvalidInput()
    {
        Throws<ArgumentNullException>(() => MergeDataSource.FromCsv(null!));
        Throws<ArgumentNullException>(() => MergeDataSource.FromXml(null!));
        Throws<ArgumentNullException>(() => MergeDataSource.FromJson(null!));
        foreach (var invalid in new[] { "[{}] x", "[{\"a\":-Infinity}]", "[{\"a\":007}]", "[{\"a\":1.}]", "[{\"a\":\"\\u 41 \"}]", "[{\"a\":1}\u00A0]" })
        {
            Throws<InvalidDataException>(() => MergeDataSource.FromJson(new StringReader(invalid)));
        }

        Require(MergeDataSource.FromJson(new StringReader(" [{\"a\":-1.5e3}]\n"))[0]["a"] == "-1.5e3", "JSON: a valid number is kept as written.");
        Throws<ArgumentNullException>(() => MergeDataSource.FromExcel(null!));

        using var reader = new StringReader("[1,2]");
        Throws<InvalidDataException>(() => MergeDataSource.FromJson(reader));

        using var csv = new StringReader("A,B\n1,2,3\n");
        Throws<InvalidDataException>(() => MergeDataSource.FromCsv(csv));

        using var dtd = new StringReader("<!DOCTYPE r [<!ENTITY e \"x\">]><r><row>&e;</row></r>");
        Throws<InvalidDataException>(() => MergeDataSource.FromXml(dtd));

        // A huge column reference would otherwise allocate hundreds of millions of empty cells.
        using var excel = new MemoryStream();
        using (var archive = new ZipArchive(excel, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteWorkbook(archive);
            WriteEntry(archive, "xl/worksheets/sheet1.xml",
                "<worksheet><sheetData><row><c r=\"ZZZZZZ1\" t=\"inlineStr\"><is><t>x</t></is></c></row></sheetData></worksheet>");
        }

        excel.Position = 0;
        Throws<InvalidDataException>(() => MergeDataSource.FromExcel(excel));
    }

    private static void ReadsFirstExcelSheetInWorkbookOrder()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"First\" r:id=\"rId2\"/><sheet name=\"Second\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", "<worksheet><row><c t=\"inlineStr\"><is><t>Wrong</t></is></c></row></worksheet>");
            WriteEntry(archive, "xl/worksheets/sheet2.xml", "<worksheet><row><c t=\"inlineStr\"><is><t>Name</t></is></c></row><row><c t=\"inlineStr\"><is><t>Ada</t></is></c></row></worksheet>");
        }
        buffer.Position = 0;
        Require(MergeDataSource.FromExcel(buffer).Single()["Name"] == "Ada", "Excel: workbook order selects the first sheet.");
    }

    private static void WriteWorkbook(ZipArchive archive)
    {
        WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" r:id=\"rId1\"/></sheets></workbook>");
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
    }

    private static void ReadsExcelNamedRange()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + "<sheet name=\"Other\" r:id=\"rId1\"/><sheet name=\"Sales '26\" r:id=\"rId2\"/></sheets><definedNames>"
                + "<definedName name=\"Customers\">'Sales ''26'!$B$3:$D$5</definedName>"
                + "<definedName name=\"Bad\">[external.xlsx]Sheet1!$A$1:$A$2</definedName></definedNames></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sales.xml\"/>"
                + "</Relationships>");
            WriteEntry(archive, "xl/worksheets/sales.xml", "<worksheet><sheetData>"
                + "<row r=\"2\"><c r=\"B2\" t=\"inlineStr\"><is><t>outside</t></is></c></row>"
                + "<row r=\"3\"><c r=\"B3\" t=\"inlineStr\"><is><t>Name</t></is></c><c r=\"D3\" t=\"inlineStr\"><is><t>City</t></is></c></row>"
                + "<row r=\"4\"><c r=\"B4\" t=\"inlineStr\"><is><t>Ada</t></is></c><c r=\"D4\" t=\"inlineStr\"><is><t>London</t></is></c></row>"
                + "<row r=\"5\"><c r=\"B5\" t=\"inlineStr\"><is><t>Grace</t></is></c></row>"
                + "<row r=\"6\"><c r=\"B6\" t=\"inlineStr\"><is><t>outside</t></is></c></row>"
                + "</sheetData></worksheet>");
        }
        buffer.Position = 0;
        var rows = MergeDataSource.FromExcel(buffer, "customers");
        Require(rows.Count == 2 && rows[0]["Name"] == "Ada" && rows[0]["City"] == "London"
            && rows[1]["Name"] == "Grace" && rows[1]["City"] is null, "Excel: named range selects the sheet, columns and rows, including sparse cells.");
        var source = PortableDocument.Create().Paragraph("{{Repeat:Customers}}").Paragraph("{{Name}} - {{City}}")
            .Paragraph("{{EndRepeat}}").Build();
        using var templateBytes = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(source, templateBytes).GetAwaiter().GetResult();
        var template = DocumentTemplate.FromBytes(templateBytes.ToArray()).RepeatRegion("Customers", rows);
        using var output = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(PortableDocument.Create().Build(), output,
            new DocxWriteOptions { Template = template }).GetAwaiter().GetResult();
        var rendered = DocumentReader.Read(new MemoryStream(output.ToArray()), DocumentFormat.Docx).Document;
        var text = string.Join(" ", rendered.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<TextRun>().Select(run => run.Text));
        Require(text.Contains("Ada", StringComparison.Ordinal) && text.Contains("London", StringComparison.Ordinal)
            && text.Contains("Grace", StringComparison.Ordinal), "Excel: named range rows fill multiple repeated placeholders.");
        buffer.Position = 0;
        Throws<InvalidDataException>(() => MergeDataSource.FromExcel(buffer, "missing"));
        buffer.Position = 0;
        Throws<InvalidDataException>(() => MergeDataSource.FromExcel(buffer, "Bad"));
    }

    private static void WideExcelRowsStayCheap()
    {
        // Each row was padded up to its highest column: one cell in XFD made every row 16,384 cells wide.
        var sheet = new StringBuilder("<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Name</t></is></c></row>");
        for (var row = 2; row <= 30_000; row++)
        {
            sheet.Append($"<row r=\"{row}\"><c r=\"A{row}\" t=\"inlineStr\"><is><t>x</t></is></c><c r=\"XFD{row}\"/></row>");
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sheet1\" r:id=\"rId1\"/></sheets>"
                + "<definedNames><definedName name=\"All\">Sheet1!$A$1:$XFD$30000</definedName></definedNames></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", sheet.Append("</sheetData></worksheet>").ToString());
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        buffer.Position = 0;
        var rows = MergeDataSource.FromExcel(buffer);
        buffer.Position = 0;
        var named = MergeDataSource.FromExcel(buffer, "All");
        Require(rows.Count == 29_999 && named.Count == 29_999 && rows[0]["Name"] == "x" && named[^1]["Name"] == "x", "Excel: wide rows are read.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(2), $"Excel: wide rows cost the fields, not the sheet width ({watch.Elapsed}).");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
