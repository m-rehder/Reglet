using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>ADR-0039: paged HTML with the layout of the PDF writer, for documents and for PdfCanvas.</summary>
internal static class HtmlPagedChecks
{
    internal static int Run()
    {
        try
        {
            PagesMatchThePdf();
            CanvasPagesMatchThePdf();
            FragmentsAreScoped();
            ClipsCroppedImages();
            FailsOnApproximationWhenAsked();
            Console.WriteLine("Paged HTML: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Paged HTML: " + exception);
            return 1;
        }
    }

    private static readonly PdfWriteOptions Options = new()
    {
        FontCollection = FontCollection.Empty.Add("Hello Sans", FontFace.Load(PdfAChecks.Font())),
    };

    internal static PortableDocument Sample()
    {
        var rows = new List<TableRow> { new(new TableCell("Item"), new TableCell("Price")) { IsHeader = true } };
        rows.AddRange(Enumerable.Range(1, 110).Select(index => new TableRow(new TableCell($"Position {index}"), new TableCell($"{index * 3},50 EUR"))));
        var footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField(), new TextRun(" of "), new PageCountField() }));
        var header = new HeaderFooter(new Paragraph(new TextRun("Hello") { FontFamily = "Hello Sans" }));
        var logo = new ImageBlock(TestImages.RgbPng(40, 20, 0, 0, 255), ImageMediaType.Png)
        {
            Width = Length.FromPoints(80),
            Height = Length.FromPoints(40),
            Wrap = ImageWrap.InFront,
            HorizontalRelativeTo = HorizontalAnchor.Page,
            HorizontalAlignment = HorizontalFloatAlignment.Right,
            VerticalRelativeTo = VerticalAnchor.Page,
            OffsetY = Length.FromMillimeters(15),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Offer" }, new[]
        {
            new Section(new Block[] { logo, new Paragraph(new TextRun("Dear customer, this offer lists the positions below.")), new TableBlock(rows) })
            {
                Header = header,
                Footer = footer,
            },
            new Section(new Paragraph(new TextRun("Landscape appendix.")))
            {
                Orientation = PageOrientation.Landscape,
                Footer = footer,
            },
        });
    }

    private static List<string> PageTexts(string html)
        => Regex.Split(html, "<div class=\"[a-z-]*page\"").Skip(1)
            .Select(page => string.Concat(Regex.Matches(page, "<text[^>]*>([^<]*)</text>").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))))
            .ToList();

    private static string Squeeze(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static void PagesMatchThePdf()
    {
        var document = Sample();
        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf, Options).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteHtmlAsync(document, buffer, Options).GetAwaiter().GetResult();
        var html = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        var pdfTexts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(pdf.ToArray()));
        var htmlTexts = PageTexts(html);
        if (Environment.GetEnvironmentVariable("HTML_FILES") is { Length: > 0 } directory)
        {
            // Files to compare by eye in a browser (HTML_FILES=dir).
            File.WriteAllBytes(Path.Combine(directory, "paged.pdf"), pdf.ToArray());
            File.WriteAllText(Path.Combine(directory, "paged.html"), html);
        }

        Require(pdfTexts.Count >= 4 && htmlTexts.Count == pdfTexts.Count, $"The same page count ({htmlTexts.Count} / {pdfTexts.Count}).");
        for (var index = 0; index < pdfTexts.Count; index++)
        {
            Require(Squeeze(htmlTexts[index]) == Squeeze(pdfTexts[index]), $"Page {index + 1}: the text and its reading order are the PDF's.");
        }

        Require(htmlTexts[1].Contains($"Page 2 of {pdfTexts.Count}", StringComparison.Ordinal), "The footer fields are resolved per page.");
        Require(htmlTexts.Count(text => text.Contains("Item", StringComparison.Ordinal) && text.Contains("Price", StringComparison.Ordinal)) >= 3, "The header row repeats.");
        Require(Regex.IsMatch(html, "style=\"width:841.89\\d*pt;height:595.27\\d*pt\""), "The landscape page has its size.");
        Require(Regex.Matches(html, "@font-face").Count == 1 && html.Contains("data:font/ttf;base64,", StringComparison.Ordinal), "The used font of the collection is embedded once.");
        Require(Regex.Matches(html, "<image id=").Count == 1, "The logo is embedded once.");
        Require(html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal) && html.Contains("<title>Offer</title>", StringComparison.Ordinal), "A complete document.");
        Require(!result.HasWarnings, "No warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
    }

    private static void CanvasPagesMatchThePdf()
    {
        var canvas = new PdfCanvas(Options);
        var font = new PdfCanvasFont("Hello Sans");
        var png = TestImages.RgbPng(10, 10, 255, 0, 0);
        var page = canvas.AddPage(PageSize.A3, PageOrientation.Landscape);
        page.DrawRectangle(20, 20, 200, 30, fill: Color.FromRgb(230, 230, 230));
        page.DrawLine(20, 50, 220, 50, 0.5);
        page.DrawText("Hello", font, 12, 220, 40, TextAnchor.CenterRight);
        page.DrawImage(png, 300, 100, 50, 50);
        page.DrawImage(png, 400, 100, 20, 20);
        using var buffer = new MemoryStream();
        canvas.WriteHtmlAsync(buffer).GetAwaiter().GetResult();
        var html = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

        Require(Regex.IsMatch(html, "style=\"width:1190.55\\d*pt;height:841.8\\d*pt\""), "A3 landscape.");
        Require(Regex.Matches(html, "<image id=").Count == 1 && Regex.Matches(html, "<use href=").Count == 2, "The image drawn twice is embedded once.");

        // The text matrix is (1 0 0 1 x baseline) in page space; right-aligned text ends at 220.
        var text = Regex.Match(html, "<text transform=\"matrix\\(([^)]*)\\)\"[^>]*textLength=\"([^\"]+)\"[^>]*>Hello</text>");
        Require(text.Success, "The text is drawn with its length.");
        var matrix = text.Groups[1].Value.Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var length = double.Parse(text.Groups[2].Value, CultureInfo.InvariantCulture);
        Require(Math.Abs(matrix[4] + length - 220) < 0.01, "The right-aligned text ends at its point.");
        // Centre anchor with ascender 800 and descender 200: the baseline is 12 * 0.3 below the point.
        Require(Math.Abs(matrix[5] - 43.6) < 0.01, "The text sits on the PDF's baseline.");
        Require(Regex.IsMatch(html, "<path d=\"M20 791.89L220 791.89\" transform=\"matrix\\(1 0 0 -1 0 841.89\\)\""), "The line is at the PDF's place.");
    }

    private static void FragmentsAreScoped()
    {
        using var first = new MemoryStream();
        new PdfDocumentWriter().WriteHtmlAsync(Sample(), first, Options, new PdfHtmlOptions { Output = HtmlOutput.Fragment, ClassPrefix = "doc-" }).GetAwaiter().GetResult();
        var html = System.Text.Encoding.UTF8.GetString(first.ToArray());
        Require(html.StartsWith("<div class=\"doc-document\">", StringComparison.Ordinal) && !html.Contains("<html", StringComparison.Ordinal) && !html.Contains("<body", StringComparison.Ordinal), "A fragment is one root element.");
        var css = Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        var rules = Regex.Replace(css, "@font-face\\{[^}]*\\}", string.Empty);
        Require(Regex.Matches(rules, "(^|\\})([^{]+)\\{").All(match => match.Groups[2].Value.StartsWith(".doc-", StringComparison.Ordinal)), "Every rule selects a prefixed class: " + rules);

        using var second = new MemoryStream();
        var other = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Other") { FontFamily = "Hello Sans" })));
        new PdfDocumentWriter().WriteHtmlAsync(other, second, Options, new PdfHtmlOptions { Output = HtmlOutput.Fragment, ClassPrefix = "doc-" }).GetAwaiter().GetResult();
        var ids = Regex.Matches(html, "id=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();
        var families = Regex.Matches(html, "font-family:\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToHashSet();
        var secondHtml = System.Text.Encoding.UTF8.GetString(second.ToArray());
        Require(!Regex.Matches(secondHtml, "id=\"([^\"]+)\"").Any(match => ids.Contains(match.Groups[1].Value)), "Two fragments share no id.");
        Require(!Regex.Matches(secondHtml, "font-family:\"([^\"]+)\"").Any(match => families.Contains(match.Groups[1].Value)), "Two fragments share no font family name.");
        Expect<ArgumentException>(() => _ = new PdfHtmlOptions { ClassPrefix = "a}b" }, "A prefix with CSS syntax is rejected.");
    }

    private static string Html(PortableDocument document, PdfWriteOptions? options = null)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteHtmlAsync(document, buffer, options ?? Options).GetAwaiter().GetResult();
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void ClipsCroppedImages()
    {
        var cropped = new ImageBlock(TestImages.RgbPng(20, 20, 0, 128, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(100),
            Height = Length.FromPoints(100),
            Crop = new ImageCrop(0.25, 0.25, 0.25, 0.25),
        };
        var html = Html(new PortableDocument(new DocumentMetadata(), new Section(cropped)));
        var clip = Regex.Match(html, "<clipPath id=\"([^\"]+)\"[^>]*><path d=\"([^\"]+)\"");
        Require(clip.Success, "The crop becomes a clip path.");
        Require(Regex.IsMatch(html, $"<g clip-path=\"url\\(#{Regex.Escape(clip.Groups[1].Value)}\\)\"><use href="), "The image is drawn inside the clip.");
    }

    private static void FailsOnApproximationWhenAsked()
    {
        // A gradient in an SVG picture is drawn with a shading, which the HTML only approximates.
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient></defs><rect width=\"100\" height=\"50\" fill=\"url(#g)\"/></svg>");
        var document = new PortableDocument(new DocumentMetadata(), new Section(new ImageBlock(svg, ImageMediaType.Svg)));
        using (var buffer = new MemoryStream())
        {
            var result = new PdfDocumentWriter().WriteHtmlAsync(document, buffer, Options).GetAwaiter().GetResult();
            Require(result.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.Ordinal)), "The approximated gradient is reported.");
        }

        Expect<UnsupportedFeatureException>(
            () => Html(document, Options with { UnsupportedContent = UnsupportedContentBehavior.Fail }),
            "With Fail the approximation throws.");
    }

    private static void Expect<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
