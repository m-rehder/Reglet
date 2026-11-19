using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// golden tests for templates (phase S, S05): they record the bound output of
/// DOCX and ODT templates (including style references) as well as the PDF page template.
/// the templates are produced deterministically with the own writers.
/// </summary>
internal static class TemplateGoldenChecks
{
    internal static int Run()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";
        var failures = 0;
        var written = 0;

        foreach (var (name, summary) in Summaries())
        {
            var path = Path.Combine(GoldenPaths.Directory, name + ".golden.txt");

            if (update)
            {
                File.WriteAllText(path, summary);
                written++;
                continue;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Golden file missing: {name}.golden.txt");
                failures++;
                continue;
            }

            if (File.ReadAllText(path) != summary)
            {
                Console.Error.WriteLine($"Golden '{name}' deviates from the output.");
                failures++;
            }
        }

        if (update)
        {
            Console.WriteLine($"Template golden files updated: {written}.");
            return 0;
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"{failures} template golden check(s) failed.");
            return 1;
        }

        Console.WriteLine("Template golden (S05): all templates match.");
        return 0;
    }

    private static IEnumerable<(string Name, string Summary)> Summaries()
    {
        yield return ("template-docx", SummarizeDocx());
        yield return ("template-odt", SummarizeOdt());
        yield return ("template-pdf", SummarizePdf());
    }

    private static string SummarizeDocx()
    {
        var template = CreateDocxTemplate(PortableDocument.Create()
            .Heading("{{Title}}", 1)
            .Paragraph(p => p.Text("Date: ").Bold("{{Date}}"))
            .Paragraph("{{Content}}")
            .Build());

        var document = PortableDocument.Create()
            .Paragraph("First paragraph")
            .BulletList("Windows", "macOS")
            .Build();

        var options = new DocxWriteOptions
        {
            Template = DocumentTemplate.FromBytes(template)
                .Body("Content")
                .Bind("Title", new Paragraph(new TextRun("Monthly report")))
                .BindText("Date", "1. October 2026"),
        };

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();

        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return Normalize(ReadEntry(zip, "word/document.xml"));
    }

    private static string SummarizeOdt()
    {
        var template = CreateOdtTemplate(PortableDocument.Create()
            .Heading("{{Title}}", 1)
            .Paragraph(p => p.Text("Date: ").Bold("{{Date}}"))
            .Paragraph("{{Content}}")
            .Build());

        var document = PortableDocument.Create()
            .Paragraph("First paragraph")
            .BulletList("Windows", "macOS")
            .Build();

        var options = new OdtWriteOptions
        {
            Template = DocumentTemplate.FromBytes(template)
                .Body("Content")
                .Bind("Title", new Paragraph(new TextRun("Monthly report")))
                .BindText("Date", "1. October 2026"),
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();

        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return Normalize(ReadEntry(zip, "content.xml"));
    }

    private static string SummarizePdf()
    {
        var document = PortableDocument.Create()
            .Paragraph("Content of the page")
            .Build();

        var options = new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
                Footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() })),
                Watermark = new Watermark("ENTWURF"),
                PageFrame = new PageFrame(),
            },
        };

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        var bytes = buffer.ToArray();
        var latin = Encoding.Latin1.GetString(bytes);

        var pages = latin.Split("/Type /Page ", StringSplitOptions.None).Length - 1;
        var extGState = latin.Contains("/ExtGState", StringComparison.Ordinal) ? "yes" : "no";
        var frame = PdfStreams.Decompress(bytes).Contains(" re\nS\n", StringComparison.Ordinal) ? "yes" : "no";
        var text = PdfStreams.ExtractText(bytes);

        return $"pages={pages}; extgstate={extGState}; frame={frame}; text={text}";
    }

    private static byte[] CreateDocxTemplate(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] CreateOdtTemplate(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static string Normalize(string xml)
        => Regex.Replace(xml, @">\s+<", "><").Trim();

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
