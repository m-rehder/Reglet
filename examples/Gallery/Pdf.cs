using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Reglet;

namespace Gallery;

/// <summary>PDF: page templates and watermarks, protection, signatures, form fields, merging, text extraction, drawn pages and HTML previews.</summary>
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
        yield return new Example("pdf", "encryption", "A PDF and a DOCX that open only with a password; the PDF also forbids copying.")
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
        yield return new Example("pdf", "data-sheet", "A technical data sheet drawn at fixed coordinates (PdfCanvas), with measured line breaks; as PDF and HTML.")
        {
            Run = DataSheetAsync,
        };
        yield return new Example("pdf", "html-preview", "The invoice as an HTML preview with the PDF's pages, alone and as a fragment in a host page.")
        {
            Run = HtmlPreviewAsync,
        };
    }

    private static async Task<IReadOnlyList<string>> DataSheetAsync(string output)
    {
        // A narrow system font if there is one; otherwise the PDF standard font (Helvetica).
        var regularFile = Assets.FindFont("Arial Narrow.ttf", "arialn.ttf", "DejaVuSansCondensed.ttf");
        var boldFile = Assets.FindFont("Arial Narrow Bold.ttf", "arialnb.ttf", "DejaVuSansCondensed-Bold.ttf");
        var fonts = FontCollection.Empty;
        if (regularFile is not null && boldFile is not null)
        {
            fonts = fonts.Add("Sheet", FontFace.LoadFile(regularFile), bold: false, italic: false)
                .Add("Sheet", FontFace.LoadFile(boldFile), bold: true, italic: false);
        }

        var canvas = new PdfCanvas(new PdfWriteOptions { FontCollection = fonts })
        {
            Metadata = new DocumentMetadata { Title = "Production data sheet", Creator = "Reglet example gallery" },
        };
        var regular = new PdfCanvasFont("Sheet");
        var bold = new PdfCanvasFont("Sheet", Bold: true);
        var page = canvas.AddPage(PageSize.A3, PageOrientation.Landscape);
        const double margin = 40;
        var right = page.Width - margin;

        // Title bar: the title on the left, the order number right-aligned against the margin.
        page.DrawRectangle(margin, margin, right - margin, 36, fill: Assets.Petrol);
        page.DrawText("Production data sheet", bold, 18, margin + 12, margin + 18, TextAnchor.CenterLeft, Color.White);
        page.DrawText("Order 2026-1187 · Sheet 1 of 1", regular, 11, right - 12, margin + 18, TextAnchor.CenterRight, Color.White);

        // A parts table at fixed columns; the descriptions are broken into lines by their measured width.
        (string Part, string Description, string Length, string Quantity)[] parts =
        [
            ("CT-100", "Ceiling tube, galvanised steel, with pre-drilled holes for the suspension brackets", "2,450 mm", "12"),
            ("SB-20", "Suspension bracket, adjustable", "180 mm", "24"),
            ("EC-7", "End cap with rubber seal, to be fitted on site after the final alignment of the tube run", "–", "4"),
            ("CN-3", "Connector sleeve", "120 mm", "8"),
        ];
        double[] columns = [margin, margin + 90, margin + 400, margin + 500, margin + 560];
        const double size = 10;
        const double lineHeight = 13;
        var top = margin + 60.0;
        page.DrawRectangle(columns[0], top, columns[^1] - columns[0], 22, fill: Assets.Tint);
        string[] headers = ["Part no.", "Description", "Length", "Qty"];
        for (var index = 0; index < headers.Length; index++)
        {
            page.DrawText(headers[index], bold, size, index < 2 ? columns[index] + 6 : columns[index + 1] - 6, top + 11, index < 2 ? TextAnchor.CenterLeft : TextAnchor.CenterRight);
        }

        top += 22;
        foreach (var (part, description, length, quantity) in parts)
        {
            var lines = Wrap(description, regular, size, columns[2] - columns[1] - 12, canvas);
            var height = Math.Max(22, lines.Count * lineHeight + 9);
            page.DrawText(part, bold, size, columns[0] + 6, top + 6, TextAnchor.TopLeft);
            for (var line = 0; line < lines.Count; line++)
            {
                page.DrawText(lines[line], regular, size, columns[1] + 6, top + 6 + line * lineHeight, TextAnchor.TopLeft);
            }

            page.DrawText(length, regular, size, columns[3] - 6, top + 6, TextAnchor.TopRight);
            page.DrawText(quantity, regular, size, columns[4] - 6, top + 6, TextAnchor.TopRight);
            top += height;
            page.DrawLine(columns[0], top, columns[^1], top, 0.5, Color.FromHex("9E9E9E"));
        }

        // The drawing beside the table, scaled from its pixel size.
        var drawing = Assets.GradientPng(320, 200);
        var info = ImageInfo.Read(drawing);
        page.DrawImage(drawing, columns[^1] + 40, margin + 60, info.PixelWidth * 1.5, info.PixelHeight * 1.5);
        page.DrawRectangle(columns[^1] + 40, margin + 60, info.PixelWidth * 1.5, info.PixelHeight * 1.5, stroke: Color.Black, strokeWidth: 0.5);

        var pdf = Path.Combine(output, "pdf-data-sheet.pdf");
        await using (var stream = File.Create(pdf))
        {
            await canvas.WriteAsync(stream);
        }

        var html = Path.Combine(output, "pdf-data-sheet.html");
        await using (var stream = File.Create(html))
        {
            await canvas.WriteHtmlAsync(stream);
        }

        if (fonts.Families.Count == 0)
        {
            Console.WriteLine("    note: no narrow system font found; the sheet uses Helvetica.");
        }

        return [pdf, html];
    }

    /// <summary>Breaks a text into lines that fit a width, word by word, with the canvas's own measurement.</summary>
    private static List<string> Wrap(string text, PdfCanvasFont font, double size, double width, PdfCanvas canvas)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && canvas.MeasureText(candidate, font, size) > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        lines.Add(current);
        return lines;
    }

    private static async Task<IReadOnlyList<string>> HtmlPreviewAsync(string output)
    {
        var document = Showcase.InvoiceDocument();
        var writer = new PdfDocumentWriter();

        // The saved file and its preview: the HTML has the PDF's pages, line breaks and page numbers.
        var pdf = Path.Combine(output, "pdf-html-preview.pdf");
        await using (var stream = File.Create(pdf))
        {
            await writer.WriteAsync(document, stream);
        }

        var preview = Path.Combine(output, "pdf-html-preview.html");
        await using (var stream = File.Create(preview))
        {
            await writer.WriteHtmlAsync(document, stream);
        }

        // A fragment for an application's own page: the host styles the pages (shadow, gap) and zooms.
        using var fragment = new MemoryStream();
        await writer.WriteHtmlAsync(document, fragment, html: new PdfHtmlOptions { Output = HtmlOutput.Fragment, ClassPrefix = "doc-" });
        var host = Path.Combine(output, "pdf-html-preview-host.html");
        await File.WriteAllTextAsync(host, $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>Host page with a document preview</title>
            <style>
            body { margin: 0; font-family: system-ui, sans-serif; background: #f4f4f4; }
            header { padding: 12px 24px; background: #2ba39a; color: #fff; }
            .preview { padding: 24px; zoom: 0.75; }
            .preview .doc-page { margin: 0 auto 24px; box-shadow: 0 2px 8px rgba(0, 0, 0, .25); }
            </style>
            </head>
            <body>
            <header>Invoice 2026-042 · preview</header>
            <div class="preview">
            {{System.Text.Encoding.UTF8.GetString(fragment.ToArray())}}
            </div>
            </body>
            </html>
            """);
        return [pdf, preview, host];
    }

    private static async Task<IReadOnlyList<string>> ProtectAsync(string output)
    {
        var document = PortableDocument.Create().Metadata(title: "Salaries").Heading("Salaries 2026").Paragraph("Confidential.").Build();

        var pdf = Path.Combine(output, "pdf-encryption.pdf");
        await DocumentWriter.WriteAsync(document, pdf, new PdfWriteOptions
        {
            Encryption = new PdfEncryptionOptions { UserPassword = Assets.Password, Permissions = PdfPermissions.Print },
        });

        var docx = Path.Combine(output, "pdf-encryption.docx");
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
