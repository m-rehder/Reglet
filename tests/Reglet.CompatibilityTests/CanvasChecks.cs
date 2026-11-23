using System.Globalization;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>PdfCanvas: pages drawn at fixed coordinates, text measurement, anchors, image info, inspection, creator.</summary>
internal static class CanvasChecks
{
    private static readonly PdfCanvasFont Hello = new("Hello Sans");

    internal static int Run()
    {
        try
        {
            DrawsExtractableTextWithEmbeddedFont();
            AnchorsAndMeasurementPlaceTextExactly();
            EmbedsTheSameImageOnce();
            RestoresTheOrigin();
            ExposesFontMetrics();
            ReadsImageInfo();
            WritesCreator();
            Console.WriteLine("Canvas: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Canvas: " + exception);
            return 1;
        }
    }

    private static PdfCanvas Canvas() => new(new PdfWriteOptions
    {
        FontCollection = FontCollection.Empty
            .Add("Hello Sans", FontFace.Load(PdfAChecks.Font()))
            .Add("Subset Sans", FontFace.Load(SubsetChecks.BuildFont())),
    });

    private static byte[] Write(PdfCanvas canvas)
    {
        using var buffer = new MemoryStream();
        canvas.WriteAsync(buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void DrawsExtractableTextWithEmbeddedFont()
    {
        var canvas = Canvas();
        var first = canvas.AddPage(PageSize.A3, PageOrientation.Landscape);
        first.DrawRectangle(0, 0, 120, 20, fill: Color.FromRgb(220, 220, 220), stroke: Color.Black, strokeWidth: 0.5);
        first.DrawLine(0, 20, 120, 20, 0.5);
        first.DrawText("Hello", Hello, 8, 2, 10, TextAnchor.CenterLeft);
        first.DrawText("AB", new PdfCanvasFont("Subset Sans", Bold: true), 8, 2, 40);
        canvas.AddPage(PageSize.A4).DrawText("Hola", Hello, 8, 10, 10);
        var pdf = Write(canvas);

        var texts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(pdf));
        Require(texts.Count == 2 && texts[0].Contains("Hello", StringComparison.Ordinal), "The drawn text is extracted per page.");

        var inspection = PdfInspection.Read(new MemoryStream(pdf));
        Require(inspection.Pages.Count == 2, "Two pages.");
        Require(Math.Abs(inspection.Pages[0].Width - 1190.55) < 0.1 && Math.Abs(inspection.Pages[0].Height - 841.89) < 0.1, "A3 landscape is 420 x 297 mm.");
        var fonts = inspection.Pages[0].Fonts;
        Require(fonts.Count == 2 && fonts.All(font => font.IsEmbedded), "Only the drawn fonts are in the file, all embedded; " + string.Join("; ", fonts));
        Require(fonts.Single(font => font.IsSubset).BaseName == "SubsetTest", "A subset font is reported without its prefix.");
    }

    private static void AnchorsAndMeasurementPlaceTextExactly()
    {
        var canvas = Canvas();
        var page = canvas.AddPage(PageSize.A4);
        // The synthetic font: 1000 units per em, ascender 800, descender 200.
        page.DrawText("Hello", Hello, 10, 50, 100, TextAnchor.TopLeft);
        page.DrawText("Hello", Hello, 10, 50, 100, TextAnchor.CenterLeft);
        page.DrawText("Hello", Hello, 10, 50, 100, TextAnchor.BaselineLeft);
        page.DrawText("Hello", Hello, 10, 300, 100, TextAnchor.BaselineRight);
        var width = canvas.MeasureText("Hello", Hello, 10);
        Require(width > 0 && Math.Abs(width - page.MeasureText("Hello", Hello, 10)) < 1e-9, "Canvas and page measure alike.");

        var moves = Regex.Matches(PdfStreams.DecompressPages(Write(canvas))[0], @"(-?[\d.]+) (-?[\d.]+) Td")
            .Select(match => (X: Parse(match.Groups[1].Value), Y: Parse(match.Groups[2].Value))).ToList();
        var height = page.Height;
        Require(moves.Count == 4, "Four text objects.");
        Require(Math.Abs(moves[0].Y - (height - 108)) < 0.01, "TopLeft: the baseline lies one ascender below the point.");
        Require(Math.Abs(moves[1].Y - (height - 103)) < 0.01, "CenterLeft: the baseline lies (ascender - descender) / 2 below the point.");
        Require(Math.Abs(moves[2].Y - (height - 100)) < 0.01, "BaselineLeft: the baseline lies on the point.");
        Require(Math.Abs(moves[3].X + width - 300) < 0.01, "Right-aligned text ends at the point.");
    }

    private static void EmbedsTheSameImageOnce()
    {
        var canvas = Canvas();
        var png = TestImages.RgbPng(40, 20, 255, 0, 0);
        var page = canvas.AddPage(PageSize.A4);
        page.DrawImage(png, 10, 10, 100, 50);
        page.DrawImage(png.ToArray(), 10, 100, 40, 20);
        canvas.AddPage(PageSize.A4).DrawImage(png, 0, 0, 10, 10);
        var pdf = Write(canvas);
        Require(Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), "/Subtype /Image").Count == 1, "Equal bytes are embedded once.");

        var inspection = PdfInspection.Read(new MemoryStream(pdf));
        Require(inspection.Pages[0].Images.Count == 1 && inspection.Pages[1].Images.Count == 1, "The inspection lists the distinct images drawn per page.");
        Require(inspection.Pages[0].Images.All(image => image is { PixelWidth: 40, PixelHeight: 20 }), "The inspection reports the pixel size.");
    }

    private static void RestoresTheOrigin()
    {
        var canvas = Canvas();
        var page = canvas.AddPage(PageSize.A4);
        page.Translate(20, 30);
        page.SaveState();
        page.Translate(100, 100);
        page.RestoreState();
        page.DrawText("Hello", Hello, 10, 0, 0);
        var move = Regex.Match(PdfStreams.DecompressPages(Write(canvas))[0], @"(-?[\d.]+) (-?[\d.]+) Td");
        Require(Math.Abs(Parse(move.Groups[1].Value) - 20) < 0.01 && Math.Abs(Parse(move.Groups[2].Value) - (page.Height - 30)) < 0.01, "RestoreState returns to the saved origin.");
        Expect<InvalidOperationException>(page.RestoreState, "RestoreState without SaveState is rejected.");
        Expect<InvalidOperationException>(() => Write(new PdfCanvas()), "A canvas without pages is rejected.");
    }

    private static void ExposesFontMetrics()
    {
        var face = FontFace.Load(PdfAChecks.Font());
        Require(face is { UnitsPerEm: 1000, Ascent: 800, Descent: 200, LineGap: 0 }, "FontFace exposes the hhea metrics, the descent positive.");
    }

    private static void ReadsImageInfo()
    {
        var png = ImageInfo.Read(TestImages.RgbPng(7, 3, 0, 0, 0));
        Require(png is { MediaType: ImageMediaType.Png, PixelWidth: 7, PixelHeight: 3, DpiX: null }, "PNG size without pHYs.");
        Expect<ImageException>(() => ImageInfo.Read([1, 2, 3]), "Other bytes are rejected.");
    }

    private static void WritesCreator()
    {
        var canvas = Canvas();
        canvas.Metadata = new DocumentMetadata { Title = "Sheet", Creator = "Planning tool 26.11" };
        canvas.AddPage(PageSize.A4);
        var pdf = Write(canvas);
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Require(text.Contains("/Creator (Planning tool 26.11)", StringComparison.Ordinal), "Info /Creator.");
        Require(text.Contains("<xmp:CreatorTool>Planning tool 26.11</xmp:CreatorTool>", StringComparison.Ordinal), "XMP xmp:CreatorTool.");
        var read = new PdfDocumentReader().Read(new MemoryStream(pdf));
        Require(read.Document.Metadata.Creator == "Planning tool 26.11", "The reader returns the creator.");
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

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
