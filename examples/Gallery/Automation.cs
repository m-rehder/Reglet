using Reglet;

namespace Gallery;

/// <summary>Automation: find and replace, templates with placeholders and mail merge.</summary>
public static class Automation
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("automation", "find-and-replace", "Replaces placeholders like %NAME% in a finished document.")
        {
            Document = FindAndReplace,
        };
        yield return new Example("automation", "template", "Fills a Word template: text fields and a table that grows with its data.")
        {
            Run = TemplateAsync,
        };
        yield return new Example("automation", "mail-merge", "One letter per row of a CSV list, each written as its own file.")
        {
            Run = MailMergeAsync,
        };
    }

    private static PortableDocument FindAndReplace()
    {
        var letter = PortableDocument.Create()
            .Metadata(title: "Find and replace")
            .Paragraph("Dear %NAME%,")
            .Paragraph("your parcel %PARCEL% left our warehouse in %CITY% today.")
            .Build();

        // The model is immutable: every replacement returns a new document.
        foreach (var (key, value) in new[] { ("%NAME%", "Ms Jansen"), ("%PARCEL%", "RG-48213"), ("%CITY%", "Bremen") })
        {
            letter = DocumentSearch.Replace(letter, new TextQuery(key), value);
        }

        return letter;
    }

    private static async Task<IReadOnlyList<string>> TemplateAsync(string output)
    {
        // A template is an ordinary document with {{placeholders}}; here it is written by Reglet itself.
        var templatePath = Path.Combine(output, "automation-template.template.docx");
        await DocumentWriter.WriteAsync(
            PortableDocument.Create()
                .Heading("Delivery note {{Number}}")
                .Paragraph("Customer: {{Customer}}")
                .Table(t => t.Header("Article", "Quantity").Row("{{Items}}{{Article}}", "{{Quantity}}"))
                .Paragraph("Delivered on {{Date}}.")
                .Build(),
            templatePath);

        var items = new[]
        {
            new Dictionary<string, string?> { ["Article"] = "Desk lamp", ["Quantity"] = "2" },
            new Dictionary<string, string?> { ["Article"] = "Office chair", ["Quantity"] = "1" },
            new Dictionary<string, string?> { ["Article"] = "Cable set", ["Quantity"] = "5" },
        };
        var template = DocumentTemplate.FromBytes(await File.ReadAllBytesAsync(templatePath))
            .BindText("Number", "LS-2026-117")
            .BindText("Customer", "Harbour Books Ltd")
            .BindText("Date", "20 March 2026")
            .RepeatRows("Items", items);

        var filled = Path.Combine(output, "automation-template.docx");
        var empty = new PortableDocument(new DocumentMetadata { Title = "Delivery note" }, new Section(Array.Empty<Block>()));
        await DocumentWriter.WriteAsync(empty, filled, new DocxWriteOptions { Template = template });
        return [templatePath, filled];
    }

    private static async Task<IReadOnlyList<string>> MailMergeAsync(string output)
    {
        var templatePath = Path.Combine(output, "automation-mail-merge.template.docx");
        await DocumentWriter.WriteAsync(
            PortableDocument.Create()
                .Paragraph("{{Name}}")
                .Paragraph("{{Street}}")
                .Paragraph("{{City}}")
                .Paragraph("Dear {{Name}},")
                .Paragraph("thank you for your order {{Order}}. It will arrive on {{Date}}.")
                .Paragraph("Kind regards")
                .Build(),
            templatePath);

        // Data from CSV (also available: JSON, XML and Excel workbooks).
        const string Csv = "Name,Street,City,Order,Date\n"
            + "Ms Jansen,Am Markt 3,28195 Bremen,A-1001,24 March\n"
            + "Mr Okafor,12 Quay Street,Dublin 2,A-1002,25 March\n"
            + "Ms Rossi,Via Roma 8,20121 Milano,A-1003,26 March\n";
        var rows = MergeDataSource.FromCsv(new StringReader(Csv));

        var directory = Path.Combine(output, "automation-mail-merge");
        Directory.CreateDirectory(directory);
        var letters = await MailMerge.MergeAsync(DocumentTemplate.FromBytes(await File.ReadAllBytesAsync(templatePath)), rows, directory, DocumentFormat.Docx);
        return [templatePath, .. letters];
    }
}
