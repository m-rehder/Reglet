using Reglet;

namespace Gallery;

/// <summary>The first steps: create, write, read and convert documents.</summary>
public static class Basics
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("basics", "hello-world", "The smallest document: one paragraph, written as DOCX, ODT and PDF.")
        {
            Document = () => PortableDocument.Create().Paragraph("Hello World!").Build(),
        };

        yield return new Example("basics", "one-model-four-formats", "One document model written to Word, LibreOffice, PDF and HTML.")
        {
            Document = OneModel,
            Formats = ["docx", "odt", "pdf", "html"],
        };

        yield return new Example("basics", "document-properties", "Title, author, subject, keywords, language and dates of a document.")
        {
            Document = Properties,
        };

        yield return new Example("basics", "read-a-document", "Reads a DOCX, walks through its blocks and writes what it found.")
        {
            Run = ReadAsync,
        };

        yield return new Example("basics", "convert-docx-to-pdf", "Opens a Word document and saves it as PDF in one call each.")
        {
            Run = ConvertAsync,
        };

        yield return new Example("basics", "pdf-to-docx", "Turns a PDF back into an editable document: headings, lists, tables and formulas.")
        {
            Run = PdfToDocxAsync,
        };
    }

    private static PortableDocument OneModel()
        => PortableDocument.Create()
            .Metadata(title: "One model, four formats")
            .Heading("One model, four formats")
            .Paragraph(p => p.Text("The same ").Bold("PortableDocument").Text(" is written as DOCX for Word, ODT for LibreOffice, PDF for print and HTML for the web."))
            .BulletList("Write once.", "Choose the format by file extension.", "Get a list of everything a format cannot show.")
            .Table(t => t.Header("Format", "Opens in").Row("DOCX", "Microsoft Word").Row("ODT", "LibreOffice").Row("PDF", "Any PDF viewer").Row("HTML", "Any web browser"))
            .Build();

    private static PortableDocument Properties()
        => PortableDocument.Create()
            .Metadata(
                title: "Quarterly figures",
                author: "Reglet examples",
                subject: "How document properties are set",
                keywords: ["properties", "metadata", "example"],
                language: "en-GB",
                created: new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero))
            .Heading("Document properties")
            .Paragraph("Open File > Properties in Word, LibreOffice or your PDF viewer to see the title, author, subject and keywords of this file.")
            .Build();

    private static async Task<IReadOnlyList<string>> ReadAsync(string output)
    {
        // Something to read: a small report written to a DOCX first.
        var source = Path.Combine(output, "basics-read-a-document.source.docx");
        await DocumentWriter.WriteAsync(
            PortableDocument.Create()
                .Heading("Site visit")
                .Paragraph("The roof is finished; the windows arrive next week.")
                .BulletList("Scaffolding until Friday", "Electrician on Monday")
                .Table(t => t.Header("Trade", "Done").Row("Roof", "100 %").Row("Windows", "0 %"))
                .Build(),
            source);

        var result = DocumentReader.Read(source);
        var lines = new List<string> { $"Read {Path.GetFileName(source)}: {result.Warnings.Count} warning(s)." };
        foreach (var block in result.Document.Sections.SelectMany(section => section.Blocks))
        {
            lines.Add(block switch
            {
                Heading heading => $"Heading level {(int)heading.Level}: {Text(heading.Inlines)}",
                Paragraph paragraph => $"Paragraph: {Text(paragraph.Inlines)}",
                ListBlock list => $"List with {list.Items.Count} items",
                TableBlock table => $"Table with {table.Rows.Count} rows and {table.Rows[0].Cells.Count} columns",
                _ => block.GetType().Name,
            });
        }

        var report = Path.Combine(output, "basics-read-a-document.txt");
        await File.WriteAllLinesAsync(report, lines);
        return [source, report];

        static string Text(IEnumerable<Inline> inlines) => string.Concat(inlines.OfType<TextRun>().Select(run => run.Text));
    }

    private static async Task<IReadOnlyList<string>> ConvertAsync(string output)
    {
        var docx = Path.Combine(output, "basics-convert-docx-to-pdf.docx");
        await DocumentWriter.WriteAsync(Showcase.ReportDocument(), docx);

        // The conversion: read any supported format, write any supported format.
        var pdf = Path.Combine(output, "basics-convert-docx-to-pdf.pdf");
        var read = DocumentReader.Read(docx);
        await DocumentWriter.WriteAsync(read.Document, pdf);
        return [docx, pdf];
    }

    private static async Task<IReadOnlyList<string>> PdfToDocxAsync(string output)
    {
        var pdf = Path.Combine(output, "basics-pdf-to-docx.pdf");
        await DocumentWriter.WriteAsync(Showcase.ReportDocument(), pdf);

        // Structure recovery is a guess from the positions on the page, so it is switched on explicitly.
        using var stream = File.OpenRead(pdf);
        var recovered = new PdfDocumentReader().Read(stream, new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true });
        var docx = Path.Combine(output, "basics-pdf-to-docx.docx");
        await DocumentWriter.WriteAsync(recovered.Document, docx);
        return [pdf, docx];
    }
}
