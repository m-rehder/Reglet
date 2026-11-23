using System.IO.Compression;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>R5: floating images and shapes anchored to the page or margin, with an alignment instead of an offset.</summary>
internal static class FloatAnchorChecks
{
    internal static int Run()
    {
        try
        {
            PageAnchoredImageIgnoresTheParagraph();
            AlignsWithinTheMargin();
            RoundTripsThroughDocx();
            RoundTripsThroughOdt();
            Console.WriteLine("Float anchors: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Float anchors: " + exception);
            return 1;
        }
    }

    private static ImageBlock Logo() => new(TestImages.RgbPng(40, 20, 0, 0, 255), ImageMediaType.Png)
    {
        Width = Length.FromPoints(80),
        Height = Length.FromPoints(40),
        Wrap = ImageWrap.InFront,
        HorizontalRelativeTo = HorizontalAnchor.Page,
        HorizontalAlignment = HorizontalFloatAlignment.Right,
        VerticalRelativeTo = VerticalAnchor.Page,
        OffsetY = Length.FromMillimeters(15),
    };

    private static string ImageMatrix(params Block[] blocks)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(blocks) { PageSize = PageSize.A4 }), buffer).GetAwaiter().GetResult();
        return Regex.Match(PdfStreams.DecompressPages(buffer.ToArray())[0], @"([-\d. ]+) cm\s*/Im\d+ Do").Groups[1].Value.Trim();
    }

    private static void PageAnchoredImageIgnoresTheParagraph()
    {
        var first = ImageMatrix(Logo(), new Paragraph(new TextRun("Text")));
        var later = ImageMatrix(new Paragraph(new TextRun("One")), new Paragraph(new TextRun("Two")), new Paragraph(new TextRun("Three")), Logo());
        Require(first.Length > 0 && first == later, $"The page-anchored image lands at the same place ({first} / {later}).");

        // A4 is 595.28 x 841.89 pt: right-aligned to the page edge, 15 mm (42.52 pt) below its top.
        var numbers = first.Split(' ').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Require(Math.Abs(numbers[4] - (595.28 - 80)) < 0.05 && Math.Abs(numbers[5] - (841.89 - 42.52 - 40)) < 0.05, "Right edge of the page, 15 mm from its top: " + first);
    }

    private static void AlignsWithinTheMargin()
    {
        var centered = new ImageBlock(TestImages.RgbPng(4, 4, 0, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(100),
            Height = Length.FromPoints(50),
            Wrap = ImageWrap.Behind,
            HorizontalRelativeTo = HorizontalAnchor.Margin,
            HorizontalAlignment = HorizontalFloatAlignment.Center,
            VerticalRelativeTo = VerticalAnchor.Margin,
            VerticalAlignment = VerticalFloatAlignment.Bottom,
        };
        var numbers = ImageMatrix(new Paragraph(new TextRun("Text")), centered).Split(' ')
            .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // Normal margins are 1 inch: centred between them, its bottom on the bottom margin.
        Require(Math.Abs(numbers[4] - (595.28 - 100) / 2) < 0.05 && Math.Abs(numbers[5] - 72) < 0.05, "Centred in the margins, at the bottom margin.");
    }

    private static PortableDocument Sample() => new(new DocumentMetadata(), new Section(new Block[]
    {
        new Paragraph(new TextRun("Text")),
        Logo(),
        new DrawingShape(ShapeKind.Rectangle, Length.FromPoints(50), Length.FromPoints(20))
        {
            Wrap = ImageWrap.Square,
            HorizontalRelativeTo = HorizontalAnchor.Margin,
            OffsetX = Length.FromPoints(30),
            VerticalRelativeTo = VerticalAnchor.Margin,
            VerticalAlignment = VerticalFloatAlignment.Center,
        },
    }));

    private static void CheckRead(PortableDocument document, string format)
    {
        var image = document.Sections[0].Blocks.OfType<ImageBlock>().Single();
        Require(image is { HorizontalRelativeTo: HorizontalAnchor.Page, HorizontalAlignment: HorizontalFloatAlignment.Right, VerticalRelativeTo: VerticalAnchor.Page, VerticalAlignment: null },
            format + ": the image anchors survive.");
        Require(Math.Abs(image.OffsetY!.Value.To(LengthUnit.Millimeters) - 15) < 0.05, format + ": the vertical offset survives.");
        var shape = document.Sections[0].Blocks.OfType<DrawingShape>().Single();
        Require(shape is { HorizontalRelativeTo: HorizontalAnchor.Margin, HorizontalAlignment: null, VerticalRelativeTo: VerticalAnchor.Margin, VerticalAlignment: VerticalFloatAlignment.Center },
            format + ": the shape anchors survive.");
        Require(Math.Abs(shape.OffsetX!.Value.Points - 30) < 0.05, format + ": the horizontal offset survives.");
    }

    private static void RoundTripsThroughDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Sample(), buffer).GetAwaiter().GetResult();
        using (var archive = new ZipArchive(new MemoryStream(buffer.ToArray())))
        {
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Require(xml.Contains("<wp:positionH relativeFrom=\"page\"><wp:align>right</wp:align></wp:positionH>", StringComparison.Ordinal), "DOCX: wp:align right relative to the page.");
            Require(xml.Contains("<wp:positionV relativeFrom=\"margin\"><wp:align>center</wp:align></wp:positionV>", StringComparison.Ordinal), "DOCX: wp:align center relative to the margin.");
        }

        buffer.Position = 0;
        var result = new DocxDocumentReader().Read(buffer);
        Require(!result.Warnings.Any(warning => warning.Message.Contains("position", StringComparison.Ordinal)), "DOCX: no position warning.");
        CheckRead(result.Document, "DOCX");
    }

    private static void RoundTripsThroughOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Sample(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var result = new OdtDocumentReader().Read(buffer);
        Require(!result.Warnings.Any(warning => warning.Message.Contains("position", StringComparison.Ordinal)), "ODT: no position warning.");
        CheckRead(result.Document, "ODT");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
