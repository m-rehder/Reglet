using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// ADR-0044 stage 1: the document validator accepts what Reglet writes and finds the faults Word, Excel, LibreOffice
/// and PDF readers reject: broken relationships, missing content types, malformed XML, schema errors, ODF packaging and
/// PDF cross-references.
/// </summary>
internal static class ValidationChecks
{
    internal static int Run()
    {
        try
        {
            AcceptsReglesOutput();
            FindsOoxmlFaults();
            AppliesMarkupCompatibility();
            FindsOdfFaults();
            FindsPdfFaults();
            Console.WriteLine("Validation: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Validation: " + exception);
            return 1;
        }
    }

    private static PortableDocument Sample() => PortableDocument.Create()
        .Heading("Report")
        .Paragraph("Body text.")
        .Table(table => table.Header("A", "B").Row("1", "2"))
        .Build();

    private static byte[] Write(IDocumentWriter writer, PortableDocument document)
    {
        using var buffer = new MemoryStream();
        writer.WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static ValidationReport Validate(byte[] data) => new DocumentValidator().Validate(new MemoryStream(data));

    private static void AcceptsReglesOutput()
    {
        var docx = Validate(Write(new DocxDocumentWriter(), Sample()));
        Require(docx.Format == "DOCX" && docx.Issues.Count == 0, "Reglet's DOCX is valid: " + string.Join("; ", docx.Issues));
        Require(Validate(Write(new OdtDocumentWriter(), Sample())) is { Format: "ODT", Issues.Count: 0 }, "Reglet's ODT is valid.");
        Require(Validate(Write(new PdfDocumentWriter(), Sample())) is { Format: "PDF", Issues.Count: 0 }, "Reglet's PDF is valid.");

        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("S");
        sheet.Cell("A1").Value = "x";
        sheet.Cell("A2").Formula = "SUM(B1:B3)";
        sheet.Merge("C1:D1");
        sheet.PageSetup = new SheetPageSetup { FitToPagesWide = 1, FitToPagesTall = 0, PrintArea = "A1:D5", RepeatRows = (1, 1) };
        using var xlsx = new MemoryStream();
        new XlsxWorkbookWriter().WriteAsync(workbook, xlsx).GetAwaiter().GetResult();
        var report = Validate(xlsx.ToArray());
        Require(report.Format == "XLSX" && report.Issues.Count == 0, "Reglet's XLSX is valid: " + string.Join("; ", report.Issues));

        Require(Validate(Encoding.ASCII.GetBytes("plain text")) is { IsValid: false, Format: "unknown" }, "Plain text is no document.");
    }

    private static void FindsOoxmlFaults()
    {
        var docx = Write(new DocxDocumentWriter(), Sample());

        var schema = Validate(Rewrite(docx, (name, text) => name == "word/document.xml" ? text.Replace("<w:body>", "<w:body><w:bogus/>", StringComparison.Ordinal) : text));
        Require(schema.Errors.Any(issue => issue.Part == "word/document.xml" && issue.Line is not null && issue.Message.Contains("bogus", StringComparison.Ordinal)),
            "An element the schema does not allow is an error with its line: " + string.Join("; ", schema.Issues));

        var value = Validate(Rewrite(docx, (name, text) => name == "word/document.xml" ? text.Replace("w:val=\"Heading1\"", "w:val=\"Heading1\" w:bad=\"1\"", StringComparison.Ordinal) : text));
        Require(value.Errors.Any(issue => issue.Message.Contains("bad", StringComparison.Ordinal)), "An undeclared attribute is an error.");

        var broken = Validate(Rewrite(docx, (name, text) => name == "word/styles.xml" ? text[..(text.Length / 2)] : text));
        Require(broken.Errors.Any(issue => issue.Part == "word/styles.xml" && issue.Message.Contains("well-formed", StringComparison.Ordinal)), "Malformed XML is an error.");

        var target = Validate(Rewrite(docx, (name, text) => name == "word/_rels/document.xml.rels" ? text.Replace("styles.xml", "missing.xml", StringComparison.Ordinal) : text));
        Require(target.Errors.Any(issue => issue.Message.Contains("word/missing.xml", StringComparison.Ordinal)), "A relationship to a missing part is an error.");

        var duplicate = Validate(Rewrite(docx, (name, text) => name == "word/_rels/document.xml.rels" ? text.Replace("Id=\"rId2\"", "Id=\"rId1\"", StringComparison.Ordinal) : text));
        Require(duplicate.Errors.Any(issue => issue.Message.Contains("occurs twice", StringComparison.Ordinal)), "A duplicate relationship id is an error.");

        var untyped = Validate(Rewrite(docx, (_, text) => text, ("word/extra.bin", "x")));
        Require(untyped.Errors.Any(issue => issue.Part == "word/extra.bin" && issue.Message.Contains("content type", StringComparison.Ordinal)), "A part without a content type is an error.");

        var noMain = Validate(Rewrite(docx, (name, text) => name == "_rels/.rels" ? text.Replace("officeDocument\"", "other\"", StringComparison.Ordinal) : text));
        Require(noMain.Errors.Any(issue => issue.Message.Contains("main part", StringComparison.Ordinal)), "A package without a main part is an error.");

        var cell = WorkbookBytes();
        var cellType = Validate(Rewrite(cell, (name, text) => name == "xl/worksheets/sheet1.xml" ? text.Replace("t=\"s\"", "t=\"zz\"", StringComparison.Ordinal) : text));
        Require(cellType.Format == "XLSX" && cellType.Errors.Any(issue => issue.Part == "xl/worksheets/sheet1.xml"), "An unknown cell type is an error.");
    }

    private static void AppliesMarkupCompatibility()
    {
        var docx = Write(new DocxDocumentWriter(), Sample());
        // Reglet declares w14, wps and wpg ignorable at the root; the checks below change that list.
        string Root(string text, string ignorable) => text
            .Replace("<w:document ", "<w:document xmlns:x=\"urn:example\" ", StringComparison.Ordinal)
            .Replace("mc:Ignorable=\"w14 wps wpg\"", $"mc:Ignorable=\"{ignorable}\"", StringComparison.Ordinal);

        var ignorable = Validate(Rewrite(docx, (name, text) => name == "word/document.xml"
            ? Root(text, "x w14 wps wpg").Replace("<w:body>", "<w:body><x:unknown/><w:p w14:paraId=\"1\"/>", StringComparison.Ordinal)
            : text));
        Require(ignorable.Issues.Count == 0, "Ignorable and Office extension markup is accepted: " + string.Join("; ", ignorable.Issues));

        var alternate = Validate(Rewrite(docx, (name, text) => name == "word/document.xml"
            ? Root(text, "w14 wps wpg").Replace("<w:body>", "<w:body><mc:AlternateContent><mc:Choice Requires=\"x\"><x:new/></mc:Choice><mc:Fallback><w:p/></mc:Fallback></mc:AlternateContent>", StringComparison.Ordinal)
            : text));
        Require(alternate.Issues.Count == 0, "AlternateContent falls back when a choice needs an unknown namespace: " + string.Join("; ", alternate.Issues));

        var strict = Validate(Rewrite(docx, (name, text) => name == "word/document.xml"
            ? Root(text, "wps wpg").Replace("<w:body>", "<w:body><x:unknown/><w:p w14:paraId=\"1\"/>", StringComparison.Ordinal)
            : text));
        Require(strict.Errors.Any(issue => issue.Message.Contains("urn:example", StringComparison.Ordinal))
            && strict.Issues.Any(issue => issue.Severity == ValidationSeverity.Warning && issue.Message.Contains("2010/wordml", StringComparison.Ordinal)),
            "An unknown namespace that is not ignorable is an error; an undeclared Office extension is a warning.");
    }

    private static void FindsOdfFaults()
    {
        var odt = Write(new OdtDocumentWriter(), Sample());
        var entries = Entries(odt);

        Require(Validate(Zip(entries.Skip(1).Append(entries[0]))).Errors.Any(issue => issue.Part == "mimetype" && issue.Message.Contains("first", StringComparison.Ordinal)),
            "mimetype must come first.");
        Require(Validate(Zip(entries, compressMimetype: true)).Errors.Any(issue => issue.Message.Contains("uncompressed", StringComparison.Ordinal)),
            "mimetype must be stored.");
        Require(Validate(Zip(entries.Append(("Pictures/extra.png", [1, 2, 3])))).Errors.Any(issue => issue.Message.Contains("not in the manifest", StringComparison.Ordinal)),
            "A file missing from the manifest is an error.");
        Require(Validate(Zip(entries.Where(entry => entry.Name != "styles.xml"))).Errors.Any(issue => issue.Message.Contains("'styles.xml', which is not in the package", StringComparison.Ordinal)),
            "A manifest entry without its file is an error.");
        Require(Validate(Zip(entries.Select(entry => entry.Name == "content.xml" ? (entry.Name, Encoding.UTF8.GetBytes("<office:document-content")) : entry)))
            .Errors.Any(issue => issue.Part == "content.xml"), "Malformed content.xml is an error.");
    }

    private static void FindsPdfFaults()
    {
        var pdf = Write(new PdfDocumentWriter(), Sample());
        var text = Encoding.Latin1.GetString(pdf);

        // The first in-use entry of the cross-reference table, 3 bytes off.
        var entry = text.IndexOf(" 65535 f", StringComparison.Ordinal) + " 65535 f \n".Length;
        var offset = long.Parse(text.AsSpan(entry, 10), System.Globalization.CultureInfo.InvariantCulture);
        var shifted = Validate(Encoding.Latin1.GetBytes(text[..entry] + (offset + 3).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + text[(entry + 10)..]));
        Require(shifted.Errors.Any(issue => issue.Part == "object 1" && issue.Message.Contains("does not point", StringComparison.Ordinal)),
            "An offset that misses its object is an error: " + string.Join("; ", shifted.Issues));

        var truncated = Validate(pdf[..(pdf.Length - 20)]);
        Require(truncated.Errors.Any(issue => issue.Message.Contains("%%EOF", StringComparison.Ordinal)), "A missing %%EOF is an error.");

        var noXref = Validate(Encoding.Latin1.GetBytes(text.Replace("startxref", "startxrex", StringComparison.Ordinal)));
        Require(noXref.Errors.Any(issue => issue.Message.Contains("cross-reference", StringComparison.Ordinal)), "Unreadable cross-references are an error.");

        using var encrypted = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Sample(), encrypted, new PdfWriteOptions { Encryption = new PdfEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" } }).GetAwaiter().GetResult();
        Require(Validate(encrypted.ToArray()).IsValid, "An encrypted PDF is checked without its password.");
    }

    private static byte[] WorkbookBytes()
    {
        var workbook = new Workbook();
        workbook.AddWorksheet("S").Cell("A1").Value = "x";
        using var buffer = new MemoryStream();
        new XlsxWorkbookWriter().WriteAsync(workbook, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static List<(string Name, byte[] Data)> Entries(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.Entries.Select(entry =>
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (entry.FullName, copy.ToArray());
        }).ToList();
    }

    private static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, bool compressMimetype = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var level = name == "mimetype" && !compressMimetype ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(data);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Rewrite(byte[] package, Func<string, string, string> change, params (string Name, string Text)[] added)
        => Zip(Entries(package)
            .Select(entry => entry.Name.EndsWith(".xml", StringComparison.Ordinal) || entry.Name.EndsWith(".rels", StringComparison.Ordinal)
                ? (entry.Name, Encoding.UTF8.GetBytes(change(entry.Name, Encoding.UTF8.GetString(entry.Data))))
                : entry)
            .Concat(added.Select(item => (item.Name, Encoding.UTF8.GetBytes(item.Text)))));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
