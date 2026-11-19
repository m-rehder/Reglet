using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Reglet;

namespace Gallery;

/// <summary>PDF: page templates and watermarks, protection, signatures, form fields, merging and text extraction.</summary>
public static class Pdf
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("pdf", "watermark", "A \"DRAFT\" watermark and a letterhead on every PDF page, without touching the document.")
        {
            Document = () => Showcase.ReportDocument(),
            Formats = ["pdf"],
            OptionsFor = _ => new PdfWriteOptions
            {
                PageTemplate = new PageTemplate
                {
                    Watermark = new Watermark("DRAFT"),
                    Header = new HeaderFooter(new Paragraph(new TextRun("Reglet examples · internal") { Color = Assets.Petrol, FontSize = Length.FromPoints(9) })),
                },
            },
        };
        yield return new Example("pdf", "password-protection", "A PDF and a DOCX that open only with a password; the PDF also forbids copying.")
        {
            Run = ProtectAsync,
        };
        yield return new Example("pdf", "digital-signature", "Signs a PDF with a certificate; the viewer shows who signed it and whether it changed.")
        {
            Document = () => PortableDocument.Create().Metadata(title: "Signed").Heading("Purchase order 4711").Paragraph("Signed electronically.").Build(),
            Formats = ["pdf"],
            OptionsFor = _ => new PdfWriteOptions
            {
                Signature = new PdfSignatureOptions(DemoCertificate()) { Reason = "Approved", Location = "Bremen", Name = "Reglet example" },
            },
        };
        yield return new Example("pdf", "form-fields", "A fillable form: text fields, check boxes and a drop-down list.")
        {
            Document = Form,
            Formats = ["pdf", "docx"],
        };
        yield return new Example("pdf", "merge-and-split", "Merges two PDFs into one and takes pages out of a PDF.")
        {
            Run = MergeAndSplitAsync,
        };
        yield return new Example("pdf", "extract-text", "Reads the text of a PDF page by page.")
        {
            Run = ExtractTextAsync,
        };
    }

    private static async Task<IReadOnlyList<string>> ProtectAsync(string output)
    {
        var document = PortableDocument.Create().Metadata(title: "Salaries").Heading("Salaries 2026").Paragraph("Confidential.").Build();

        var pdf = Path.Combine(output, "pdf-password-protection.pdf");
        await DocumentWriter.WriteAsync(document, pdf, new PdfWriteOptions
        {
            Encryption = new PdfEncryptionOptions { UserPassword = Assets.Password, Permissions = PdfPermissions.Print },
        });

        var docx = Path.Combine(output, "pdf-password-protection.docx");
        await DocumentWriter.WriteAsync(document, docx, new DocxWriteOptions { Encryption = new DocxEncryptionOptions(Assets.Password) });
        Console.WriteLine($"    password of both files: {Assets.Password}");
        return [pdf, docx];
    }

    /// <summary>A self-signed certificate for the example; a real signature uses a certificate from a trusted authority.</summary>
    private static X509Certificate2 DemoCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static PortableDocument Form()
    {
        var cm = Length.FromCentimeters;
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Course registration")),
            new Paragraph(new TextRun("Name")),
            new FormField(FormFieldKind.Text, "name", cm(8), cm(0.8)) { ToolTip = "Your full name" },
            new Paragraph(new TextRun("E-mail")),
            new FormField(FormFieldKind.Text, "email", cm(8), cm(0.8)),
            new Paragraph(new TextRun("Course")),
            new FormField(FormFieldKind.DropDown, "course", cm(6), cm(0.8), ["Typography", "Layout", "Printing"]) { Value = "Typography" },
            new Paragraph(new TextRun("Send me the newsletter")),
            new FormField(FormFieldKind.CheckBox, "newsletter", cm(0.6), cm(0.6)),
            new Paragraph(new TextRun("Comments")),
            new FormField(FormFieldKind.Text, "comments", cm(14), cm(3)) { Multiline = true },
        };
        return new PortableDocument(new DocumentMetadata { Title = "Form fields" }, new Section(blocks));
    }

    private static async Task<IReadOnlyList<string>> MergeAndSplitAsync(string output)
    {
        var first = Path.Combine(output, "pdf-merge-and-split.part1.pdf");
        var second = Path.Combine(output, "pdf-merge-and-split.part2.pdf");
        await DocumentWriter.WriteAsync(Showcase.InvoiceDocument(), first);
        await DocumentWriter.WriteAsync(Showcase.ReportDocument(), second);

        var merged = Path.Combine(output, "pdf-merge-and-split.merged.pdf");
        using (var a = File.OpenRead(first))
        using (var b = File.OpenRead(second))
        using (var target = File.Create(merged))
        {
            PdfPageOperations.Merge([a, b], target);
        }

        // Pages 2 and 3 of the merged file: the first pages of the report.
        var extracted = Path.Combine(output, "pdf-merge-and-split.pages-2-3.pdf");
        using (var source = File.OpenRead(merged))
        using (var target = File.Create(extracted))
        {
            PdfPageOperations.Extract(source, 2, 3, target);
        }

        return [first, second, merged, extracted];
    }

    private static async Task<IReadOnlyList<string>> ExtractTextAsync(string output)
    {
        var pdf = Path.Combine(output, "pdf-extract-text.pdf");
        await DocumentWriter.WriteAsync(Showcase.InvoiceDocument(), pdf);

        // The plain reader gives one section per page with the text as paragraphs.
        var read = DocumentReader.Read(pdf);
        var lines = new List<string>();
        for (var page = 0; page < read.Document.Sections.Count; page++)
        {
            lines.Add($"--- page {page + 1} ---");
            lines.AddRange(read.Document.Sections[page].Blocks.OfType<Paragraph>().Select(p => string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))));
        }

        var text = Path.Combine(output, "pdf-extract-text.txt");
        await File.WriteAllLinesAsync(text, lines);
        return [pdf, text];
    }
}
