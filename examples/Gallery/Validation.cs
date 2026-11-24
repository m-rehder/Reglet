using System.IO.Compression;
using System.Text;
using Reglet;

namespace Gallery;

/// <summary>The document validator (package Reglet.Validation).</summary>
public static class Validation
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("validation", "validate-files", "Checks a DOCX, ODT, PDF and XLSX for what Word, LibreOffice and PDF readers reject, and finds a planted fault.")
        {
            Run = ValidateAsync,
        };
    }

    private static async Task<IReadOnlyList<string>> ValidateAsync(string output)
    {
        var document = PortableDocument.Create()
            .Heading("Delivery note")
            .Paragraph("Two items, one parcel.")
            .Table(table => table.Header("Item", "Quantity").Row("Monitor arm", "4").Row("Docking station", "4"))
            .Build();

        var files = new List<(string Name, byte[] Data)>();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            using var buffer = new MemoryStream();
            await DocumentWriter.WriteAsync(document, buffer, format);
            files.Add(("delivery-note." + format.ToString().ToLowerInvariant(), buffer.ToArray()));
        }

        var workbook = new Workbook();
        workbook.AddWorksheet("Items").Cell("A1").Value = "Monitor arm";
        using (var buffer = new MemoryStream())
        {
            await new XlsxWorkbookWriter().WriteAsync(workbook, buffer);
            files.Add(("items.xlsx", buffer.ToArray()));
        }

        // A planted fault: an element Word does not know, in the body of the DOCX.
        files.Add(("broken.docx", Plant(files[0].Data, "word/document.xml", "<w:body>", "<w:body><w:unknown/>")));

        var validator = new DocumentValidator();
        var report = new StringBuilder();
        foreach (var (name, data) in files)
        {
            var result = validator.Validate(new MemoryStream(data));
            report.AppendLine($"{name}: {result.Format}, {(result.IsValid ? "valid" : "not valid")}");
            foreach (var issue in result.Issues)
            {
                report.AppendLine("  " + issue);
            }
        }

        var path = Path.Combine(output, "validation-validate-files.txt");
        await File.WriteAllTextAsync(path, report.ToString());
        return [path];
    }

    private static byte[] Plant(byte[] package, string part, string find, string replace)
    {
        using var source = new ZipArchive(new MemoryStream(package));
        using var buffer = new MemoryStream();
        using (var target = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = new StreamReader(entry.Open());
                var text = input.ReadToEnd();
                using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open());
                writer.Write(entry.FullName == part ? text.Replace(find, replace, StringComparison.Ordinal) : text);
            }
        }

        return buffer.ToArray();
    }
}
