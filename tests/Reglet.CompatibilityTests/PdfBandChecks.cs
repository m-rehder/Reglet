using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// Headers/footers with the full block layout in the PDF (images, tables, headings, character
/// formatting) and the resolution of page reference, document property and variable fields.
/// </summary>
internal static class PdfBandChecks
{
    internal static int Run()
    {
        try
        {
            DrawsImagesTablesAndFormattingInBands();
            PlacesBandsLikeWord();
            PushesBodyBelowTallHeader();
            KeepsBandsOutOfStructureAndOutline();
            ReportsUnrenderableBandContent();
            ResolvesPageReferences();
            ResolvesPageReferencesInBands();
            ResolvesDocumentProperties();
            ResolvesDocumentVariables();
            ReportsUnresolvableFields();
            Console.WriteLine("PDF bands and fields: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF bands and fields: " + exception);
            return 1;
        }
    }

    private static void DrawsImagesTablesAndFormattingInBands()
    {
        var logo = TestImages.Png(8, 4, colorType: 6, bitDepth: 8, (x, _) => [200, 0, 0, x < 4 ? 0 : 255]);
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Block[]
            {
                new ImageBlock(logo, ImageMediaType.Png) { Width = Length.FromPoints(40), Height = Length.FromPoints(20) },
                new Heading(HeadingLevel.Level2, new TextRun("Letterhead")),
                new TableBlock(new[] { new TableRow(new[] { new TableCell("Left"), new TableCell("Right") }) }),
            }),
            Footer = new HeaderFooter(new Paragraph(new Inline[]
            {
                new TextRun("Bold") { Bold = true, Color = new Color(255, 0, 0) },
                new TextRun(" "),
                new TextRun("Small") { FontSize = Length.FromPoints(7) },
            })),
        };

        var (pdf, result) = Write(new PortableDocument(new DocumentMetadata(), section));
        var content = PdfStreams.Decompress(pdf);
        Require(!result.HasWarnings, "Images, headings, tables and formatting in bands produce no warning.");
        Require(content.Contains("/Im1 Do", StringComparison.Ordinal), "The header image is drawn.");
        Require(Encoding.Latin1.GetString(pdf).Contains("/SMask", StringComparison.Ordinal), "The header image keeps its transparency.");
        Require(content.Contains("(Letterhead) Tj", StringComparison.Ordinal), "The header heading is drawn.");
        Require(content.Contains("(Left) Tj", StringComparison.Ordinal) && content.Contains("(Right) Tj", StringComparison.Ordinal), "The header table is drawn.");
        Require(content.Contains("/F2 ", StringComparison.Ordinal), "The bold run in the footer uses the bold font.");
        Require(content.Contains("1 0 0 rg", StringComparison.Ordinal), "The footer run keeps its color.");
        Require(content.Contains("/F1 7 Tf", StringComparison.Ordinal), "The footer run keeps its font size.");
    }

    private static void PlacesBandsLikeWord()
    {
        // A4, margins 72 pt: header at 36 pt below the top edge, footer ending 36 pt above the
        // bottom edge, the body starts at the page margin.
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Head"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("Foot"))),
        };

        var (pdf, _) = Write(new PortableDocument(new DocumentMetadata(), section));
        var content = PdfStreams.Decompress(pdf);
        var pageHeight = PageSize.A4.Height.Points;
        var head = Baseline(content, "Head");
        var foot = Baseline(content, "Foot");
        var body = Baseline(content, "Body");
        Require(head < pageHeight - 36 && head > pageHeight - 36 - 14, "The header starts 36 pt below the top edge.");
        Require(foot > 36 && foot < 36 + 14, "The footer ends 36 pt above the bottom edge.");
        Require(body < pageHeight - 72 && body > pageHeight - 72 - 14, "The body starts at the top margin.");
    }

    private static void PushesBodyBelowTallHeader()
    {
        var tall = TestImages.RgbPng(4, 4, 0, 0, 255);
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new ImageBlock(tall, ImageMediaType.Png) { Width = Length.FromPoints(100), Height = Length.FromPoints(100) }),
            Footer = new HeaderFooter(new Block[]
            {
                new Paragraph(new TextRun("Line1")),
                new Paragraph(new TextRun("Line2")),
                new Paragraph(new TextRun("Line3")),
                new Paragraph(new TextRun("Line4")),
            }),
        };

        var (pdf, _) = Write(new PortableDocument(new DocumentMetadata(), section));
        var content = PdfStreams.Decompress(pdf);
        var pageHeight = PageSize.A4.Height.Points;
        var body = Baseline(content, "Body");
        Require(body < pageHeight - 36 - 100, "A header taller than the margin pushes the body down.");
        Require(Baseline(content, "Line4") > 36 - 1e-6, "The last footer line stays above the footer distance.");
        Require(Baseline(content, "Line1") > Baseline(content, "Line4"), "Footer lines run top to bottom.");

        // A long body stops above a footer that grows beyond the bottom margin.
        var builder = new List<Block>();
        for (var index = 0; index < 80; index++)
        {
            builder.Add(new Paragraph(new TextRun("Row" + index.ToString(CultureInfo.InvariantCulture))));
        }

        var longSection = new Section(builder) { Footer = section.Footer };
        var (longPdf, _) = Write(new PortableDocument(new DocumentMetadata(), longSection));
        var longContent = PdfStreams.Decompress(longPdf);
        var lowestBody = Regex.Matches(longContent, @"[0-9.]+ ([0-9.]+) Td\n\(Row\d+\) Tj")
            .Min(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Require(lowestBody > Baseline(longContent, "Line1"), "The body ends above a tall footer.");
    }

    private static void KeepsBandsOutOfStructureAndOutline()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Block[]
            {
                new Heading(HeadingLevel.Level1, new Inline[] { new Bookmark("InHeader"), new TextRun("HeaderHeading") }),
                new TableBlock(new[] { new TableRow(new TableCell("Cell")) }),
            }),
        };

        var (pdf, _) = Write(new PortableDocument(new DocumentMetadata(), section));
        var raw = Encoding.Latin1.GetString(pdf);
        var content = PdfStreams.Decompress(pdf);
        Require(!raw.Contains("/Outlines", StringComparison.Ordinal), "A heading in the header adds no outline entry.");
        Require(raw.Contains("(InHeader)", StringComparison.Ordinal), "A bookmark in the header gets a named destination.");
        Require(!raw.Contains("/S /H1", StringComparison.Ordinal) && !raw.Contains("/S /Table", StringComparison.Ordinal), "Band content is not tagged.");
        var artifact = content.IndexOf("/Artifact BMC", StringComparison.Ordinal);
        var heading = content.IndexOf("(HeaderHeading) Tj", StringComparison.Ordinal);
        Require(artifact >= 0 && artifact < heading, "The band is drawn as an artifact.");
        Require(content.IndexOf("BDC", artifact, heading - artifact, StringComparison.Ordinal) < 0, "No tagged content inside the band.");
    }

    private static void ReportsUnrenderableBandContent()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Footer = new HeaderFooter(new Block[]
            {
                new Paragraph(new Inline[] { new TextRun("Note"), new Footnote(new Paragraph(new TextRun("footnote text"))) }),
                new PageBreak(),
            }),
        };

        section = section with { Blocks = [new Paragraph(new TextRun("Body")), new PageBreak(), new Paragraph(new TextRun("More"))] };
        var (pdf, result) = Write(new PortableDocument(new DocumentMetadata(), section));
        Require(
            result.Warnings.Count(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)) == 1,
            "A note in a band is reported once (not per page and pass).");
        Require(result.Warnings.Any(warning => warning.Message.Contains("PageBreak in a header or footer", StringComparison.Ordinal)), "A page break in a band is reported.");
        var bandText = PdfStreams.Decompress(pdf);
        Require(bandText.Contains("(Note) Tj", StringComparison.Ordinal), "The band text around the note is drawn.");
        Require(
            bandText.Contains("(footnote) Tj", StringComparison.Ordinal) && bandText.Contains("(text) Tj", StringComparison.Ordinal),
            "The text of a band note is drawn in the band.");
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(pdf));
        Require(
            pages.All(page => !page.Contains("1. footnote", StringComparison.Ordinal)),
            "A band note is no footnote at the page end.");

        var tall = TestImages.RgbPng(2, 2, 0, 0, 0);
        var huge = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new ImageBlock(tall, ImageMediaType.Png) { Width = Length.FromPoints(100), Height = Length.FromPoints(700) }),
        };
        var (hugePdf, _) = Write(new PortableDocument(new DocumentMetadata(), huge));
        Require(Baseline(PdfStreams.Decompress(hugePdf), "Body") > PageSize.A4.Height.Points / 3, "A header is limited to a third of the page.");
    }

    private static void ResolvesPageReferences()
    {
        var blocks = new List<Block>
        {
            new Paragraph(new Inline[] { new TextRun("See "), new PageRefField("Target"), new TextRun(" and "), new PageRefField("Early") }),
            new Paragraph(new Inline[] { new Bookmark("Early"), new TextRun("Early") }),
            new PageBreak(),
            new PageBreak(),
            new Paragraph(new Inline[] { new Bookmark("Target"), new TextRun("Target") }),
        };
        var section = new Section(blocks) { PageNumberStart = 10 };

        var (pdf, result) = Write(new PortableDocument(new DocumentMetadata(), section));
        var content = PdfStreams.Decompress(pdf);
        Require(!result.HasWarnings, "Resolved page references produce no warning.");
        Require(content.Contains("(12) Tj", StringComparison.Ordinal), "PAGEREF shows the displayed page number of a later bookmark.");
        Require(content.Contains("(10) Tj", StringComparison.Ordinal), "PAGEREF shows the page of a bookmark on the same page.");
        Require(!content.Contains("(0) Tj", StringComparison.Ordinal), "No placeholder remains.");

        // The body page number field follows PageNumberStart as well.
        var numbered = new Section(new Paragraph(new PageNumberField())) { PageNumberStart = 7 };
        var (numberedPdf, _) = Write(new PortableDocument(new DocumentMetadata(), numbered));
        Require(PdfStreams.Decompress(numberedPdf).Contains("(7) Tj", StringComparison.Ordinal), "PAGE in the body follows PageNumberStart.");
    }

    private static void ResolvesPageReferencesInBands()
    {
        var section = new Section(new Block[]
        {
            new Paragraph(new TextRun("One")),
            new PageBreak(),
            new Paragraph(new Inline[] { new Bookmark("Appendix"), new TextRun("Appendix") }),
        })
        {
            Footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Appendix on "), new PageRefField("Appendix") })),
        };

        var (pdf, _) = Write(new PortableDocument(new DocumentMetadata(), section));
        var content = PdfStreams.Decompress(pdf);
        Require(Regex.Matches(content, @"\(2\) Tj").Count == 2, "PAGEREF in the footer is resolved on every page.");
    }

    private static void ResolvesDocumentProperties()
    {
        var metadata = new DocumentMetadata
        {
            Title = "Offer",
            Author = "Jane",
            Subject = "Pricing",
            Keywords = ["alpha", "beta"],
            Created = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            CustomProperties = new Dictionary<string, string> { ["Client"] = "ACME" },
        };
        var document = new PortableDocument(metadata, new Section(new Paragraph(new Inline[]
        {
            new DocPropertyField("Title"), new TextRun(" "),
            new DocPropertyField("author"), new TextRun(" "),
            new DocPropertyField("Subject"), new TextRun(" "),
            new DocPropertyField("Keywords"), new TextRun(" "),
            new DocPropertyField("CreateTime"), new TextRun(" "),
            new DocPropertyField("client"),
        })));

        var (pdf, result) = Write(document);
        var text = PdfStreams.ExtractText(pdf);
        Require(!result.HasWarnings, "Set document properties produce no warning.");
        foreach (var expected in new[] { "Offer", "Jane", "Pricing", "alpha,", "beta", "2026-09-25", "ACME" })
        {
            Require(text.Contains(expected, StringComparison.Ordinal), $"DOCPROPERTY resolves to '{expected}'.");
        }
    }

    private static void ResolvesDocumentVariables()
    {
        var metadata = new DocumentMetadata { Variables = new Dictionary<string, string> { ["CaseNumber"] = "AZ-4711" } };
        var section = new Section(new Paragraph(new DocVariableField("casenumber")))
        {
            Header = new HeaderFooter(new Paragraph(new DocVariableField("CaseNumber"))),
        };

        var (pdf, result) = Write(new PortableDocument(metadata, section));
        Require(!result.HasWarnings, "A set document variable produces no warning.");
        Require(Regex.Matches(PdfStreams.Decompress(pdf), @"\(AZ-4711\) Tj").Count == 2, "DOCVARIABLE resolves in the body and the header.");
    }

    private static void ReportsUnresolvableFields()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new PageRefField("Missing"), new DocPropertyField("Nope"), new DocVariableField("Unset"),
        })));

        var (_, result) = Write(document);
        Require(result.Warnings.Any(warning => warning.Message.Contains("bookmark 'Missing'", StringComparison.Ordinal)), "A missing bookmark is reported.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("document property 'Nope'", StringComparison.Ordinal)), "An unset property is reported.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("document variable 'Unset'", StringComparison.Ordinal)), "An unset variable is reported.");

        try
        {
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { UnsupportedContent = UnsupportedContentBehavior.Fail })
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Unresolvable fields must fail in strict mode.");
        }
        catch (UnsupportedFeatureException)
        {
        }
    }

    /// <summary>The baseline (y) of the first text piece that equals <paramref name="text"/>.</summary>
    private static double Baseline(string content, string text)
    {
        var match = Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(" + Regex.Escape(text) + @"\) Tj");
        Require(match.Success, $"Text '{text}' is drawn.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static (byte[] Pdf, WriteResult Result) Write(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return (buffer.ToArray(), result);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
