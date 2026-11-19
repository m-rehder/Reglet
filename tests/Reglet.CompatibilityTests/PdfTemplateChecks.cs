using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// checks PDF page templates (phase S, S04): headers/footers, watermark and
/// page frame from <see cref="PageTemplate"/> via <see cref="PdfWriteOptions"/>.
/// </summary>
internal static class PdfTemplateChecks
{
    internal static int Run()
    {
        try
        {
            WritesWatermark();
            WritesPictureWatermark();
            ReportsSvgPictureWatermark();
            WritesPageFrame();
            UsesTemplateHeaderFooter();
            SectionHeaderOverridesTemplate();
            DrawsOnEveryPage();
            ReportsUnsupportedPageTemplateContent();
            Console.WriteLine("PDF templates (S04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF templates (S04): " + exception);
            return 1;
        }
    }

    private static void WritesWatermark()
    {
        var document = PortableDocument.Create().Paragraph("Content").Build();
        var pdf = Write(document, new PdfWriteOptions
        {
            PageTemplate = new PageTemplate { Watermark = new Watermark("ENTWURF") },
        });

        var content = PdfStreams.Decompress(pdf);
        Require(content.Contains("(ENTWURF) Tj", StringComparison.Ordinal), "The watermark text is written.");
        Require(content.Contains(" gs\n", StringComparison.Ordinal), "The graphics state is set.");

        var latin = Encoding.Latin1.GetString(pdf);
        Require(latin.Contains("/ExtGState", StringComparison.Ordinal), "The graphics state is kept as a resource.");
        Require(latin.Contains("/ca 0.1", StringComparison.Ordinal), "The opacity (0.1) is set.");
    }

    /// <summary>
    /// OF-B09: a picture watermark is embedded and drawn centered behind the content.
    /// </summary>
    private static void WritesPictureWatermark()
    {
        var png = TestImages.RgbPng(40, 20, 200, 200, 200);
        var document = PortableDocument.Create().Paragraph("Content").Build();
        var pdf = Write(document, new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                PictureWatermark = new PictureWatermark(png, ImageMediaType.Png)
                {
                    Width = Length.FromPoints(100),
                    Opacity = 0.25,
                },
            },
        });

        var content = PdfStreams.Decompress(pdf);
        // The width is 100 pt, the height follows from the aspect ratio of the image (40 × 20).
        // A4 is 595.276 × 841.89 pt, so the picture sits centered on the page.
        Require(
            content.Contains("100 0 0 50 247.638 395.945 cm", StringComparison.Ordinal),
            "The picture watermark is drawn centered: " + content);

        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"q\s+/\S+ gs\s+q\s+[^\n]+ cm\s+/\S+ Do\s+Q\s+Q\s+EMC"),
            "Picture watermark opacity must be restored before subsequent page content.");
        var latin = Encoding.Latin1.GetString(pdf);
        Require(latin.Contains("/Subtype /Image", StringComparison.Ordinal), "The image is embedded.");
        Require(latin.Contains("/ca 0.25", StringComparison.Ordinal), "The opacity (0.25) is set.");
    }

    /// <summary>
    /// OF-B09: an SVG picture watermark is not supported and is reported.
    /// </summary>
    private static void ReportsSvgPictureWatermark()
    {
        var document = PortableDocument.Create().Paragraph("Content").Build();
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions
            {
                PageTemplate = new PageTemplate
                {
                    PictureWatermark = new PictureWatermark(
                        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"u8.ToArray(),
                        ImageMediaType.Svg),
                },
            }).GetAwaiter().GetResult();

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("SVG picture watermark", StringComparison.Ordinal)),
            "The unsupported picture watermark is reported.");
    }

    private static void WritesPageFrame()
    {
        var document = PortableDocument.Create().Paragraph("Content").Build();
        var pdf = Write(document, new PdfWriteOptions
        {
            PageTemplate = new PageTemplate { PageFrame = new PageFrame { Inset = Length.FromPoints(24) } },
        });

        var content = PdfStreams.Decompress(pdf);
        // A4 is 595.276 × 841.89 pt; at 24 pt distance that gives 547.276 × 793.89 pt.
        Require(content.Contains("24 24 547.276 793.89 re\nS\n", StringComparison.Ordinal), "The frame is drawn and stroked at the expected position: " + content);
    }

    private static void UsesTemplateHeaderFooter()
    {
        var document = PortableDocument.Create().Paragraph("Content").Build();
        var pdf = Write(document, new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                Header = new HeaderFooter(new Paragraph(new TextRun("Template header"))),
                Footer = new HeaderFooter(new Paragraph(new TextRun("Template footer"))),
            },
        });

        var content = PdfStreams.Decompress(pdf);
        Require(content.Contains("(header) Tj", StringComparison.Ordinal), "The template's header is used.");
        Require(content.Contains("(footer) Tj", StringComparison.Ordinal), "The template's footer is used.");
    }

    private static void SectionHeaderOverridesTemplate()
    {
        var section = new Section(new Paragraph(new TextRun("Content")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Ownheader"))),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);
        var pdf = Write(document, new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                Header = new HeaderFooter(new Paragraph(new TextRun("Template header"))),
            },
        });

        var content = PdfStreams.Decompress(pdf);
        Require(content.Contains("Ownheader", StringComparison.Ordinal), "The section's own header wins.");
        Require(!content.Contains("(Template) Tj", StringComparison.Ordinal), "The template header is not drawn additionally.");
    }

    private static void DrawsOnEveryPage()
    {
        var builder = PortableDocument.Create();
        for (var index = 0; index < 120; index++)
        {
            builder.Paragraph("Row " + index);
        }

        var pdf = Write(builder.Build(), new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                Watermark = new Watermark("KOPIE"),
                PageFrame = new PageFrame(),
            },
        });

        var content = PdfStreams.Decompress(pdf);
        var watermarks = CountOccurrences(content, "(KOPIE) Tj");
        var frames = CountOccurrences(content, " re\n");
        Require(watermarks >= 2, "The watermark appears on every page.");
        Require(frames == watermarks, "Frame and watermark appear on the same number of pages.");
    }

    private static void ReportsUnsupportedPageTemplateContent()
    {
        var document = PortableDocument.Create().Paragraph("Content").Build();
        var options = new PdfWriteOptions
        {
            PageTemplate = new PageTemplate
            {
                Header = new HeaderFooter(new Block[] { new TableBlock(new[] { new TableRow(new TableCell("Cell")) }), new PageBreak() }),
            },
        };

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains("(Cell) Tj", StringComparison.Ordinal), "A table in the template header is drawn.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("PageBreak in a header or footer", StringComparison.Ordinal)), "A page break in the template header is reported.");
    }

    private static byte[] Write(PortableDocument document, PdfWriteOptions options)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static int CountOccurrences(string text, string needle)
        => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
