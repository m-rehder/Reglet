using System.IO.Compression;
using System.Xml.Linq;
using Reglet;

/// <summary>Shapes and text boxes (ADR-0022): the model rules and the DOCX writer, also in a header.</summary>
internal static class DrawingShapeChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidShapes();
            WritesDocx();
            ReadsDocx();
            WritesOdt();
            ReadsOdt();
            RendersPdf();
            Console.WriteLine("Drawing shapes: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Drawing shapes: " + exception);
            return 1;
        }
    }

    private static Length Pt(double value) => Length.FromPoints(value);

    private static DrawingShape Box(string text) => new(
        ShapeKind.TextBox, Pt(200), Pt(60), new Block[] { new Paragraph(new Inline[] { new TextRun(text) }) })
    {
        Fill = Color.FromHex("#EEEEEE"),
        Outline = Color.Black,
        Wrap = ImageWrap.InFront,
        OffsetX = Pt(10),
        OffsetY = Pt(20),
        AltText = "Letterhead",
    };

    private static PortableDocument Document()
        => new(new DocumentMetadata(), new Section(new Block[]
        {
            new DrawingShape(ShapeKind.Rectangle, Pt(100), Pt(50)) { Fill = Color.FromHex("#FF0000"), Rotation = 15 },
            new DrawingShape(ShapeKind.Line, Pt(100), Pt(0)) { Outline = Color.Black, Dash = ShapeDash.Dash, OutlineWidth = Pt(2) },
            new DrawingShape(ShapeKind.Ellipse, Pt(40), Pt(40)) { Outline = Color.Black },
            new DrawingShape(ShapeKind.Freeform, Pt(50), Pt(50), path: new[]
            {
                new ShapePathStep(ShapePathCommand.MoveTo, Pt(0), Pt(0)),
                new ShapePathStep(ShapePathCommand.LineTo, Pt(50), Pt(0)),
                new ShapePathStep(ShapePathCommand.CubicTo, Pt(50), Pt(50), Pt(60), Pt(10), Pt(60), Pt(40)),
                new ShapePathStep(ShapePathCommand.Close, Pt(0), Pt(0)),
            }) { Fill = Color.FromHex("#00FF00") },
            new Paragraph(new Inline[] { new TextRun("Body text") }),
        })
        {
            Header = new HeaderFooter(Box("In the header")),
        });

    private static void RejectsInvalidShapes()
    {
        Throws(() => new DrawingShape(ShapeKind.Rectangle, Pt(0), Pt(10)));
        Throws(() => new DrawingShape(ShapeKind.Line, Pt(0), Pt(0)));
        Throws(() => new DrawingShape(ShapeKind.Rectangle, Pt(-1), Pt(10)));
        Throws(() => new DrawingShape(ShapeKind.TextBox, Pt(10), Pt(10)));
        Throws(() => new DrawingShape(ShapeKind.Rectangle, Pt(10), Pt(10), new Block[] { new PageBreak() }));
        Throws(() => new DrawingShape(ShapeKind.TextBox, Pt(10), Pt(10), new Block[] { new PageBreak() }));
        Throws(() => new DrawingShape(ShapeKind.Freeform, Pt(10), Pt(10), path: new[] { new ShapePathStep(ShapePathCommand.LineTo, Pt(1), Pt(1)) }));
        Throws(() => new DrawingShape(ShapeKind.Rectangle, Pt(10), Pt(10)) { OutlineWidth = Pt(0) });
    }

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader((zip.GetEntry(name) ?? throw new InvalidOperationException("Part missing: " + name)).Open());
        return reader.ReadToEnd();
    }

    private static void WritesDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();
        var body = Part(package, "word/document.xml");
        XDocument.Parse(body);

        Require(body.Contains("prst=\"rect\"") && body.Contains("prst=\"line\"") && body.Contains("prst=\"ellipse\""), "DOCX: preset geometries.");
        Require(body.Contains("rot=\"900000\""), "DOCX: the rotation is in 1/60000 degree.");
        Require(body.Contains("<a:srgbClr val=\"FF0000\""), "DOCX: the fill color.");
        Require(body.Contains("prstDash val=\"dash\"") && body.Contains("w=\"25400\""), "DOCX: dash and outline width (2 pt = 25400 EMU).");
        Require(body.Contains("<a:custGeom") && body.Contains("<a:cubicBezTo") && body.Contains("<a:close"), "DOCX: the freeform is a custom geometry.");
        Require(body.Contains("Body text"), "DOCX: text after the shapes is kept.");

        var header = Part(package, "word/header1.xml");
        XDocument.Parse(header);
        Require(header.Contains("txBox=\"1\"") && header.Contains("<w:txbxContent>") && header.Contains("In the header"), "DOCX: a text box in the header.");
        Require(header.Contains("<wp:anchor") && header.Contains("behindDoc=\"0\"") && header.Contains("descr=\"Letterhead\""), "DOCX: an anchored, described text box.");
        Require(header.Contains("<wp:posOffset>127000</wp:posOffset>"), "DOCX: the offset of 10 pt is 127000 EMU.");
    }

    private static void ReadsDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = DocumentReader.Read(buffer, DocumentFormat.Docx);
        Require(!read.Warnings.Any(warning => warning.Message.Contains("shape", StringComparison.OrdinalIgnoreCase)), "DOCX read: no shape warning: " + string.Join("|", read.Warnings.Select(warning => warning.Message)));
        var shapes = read.Document.Sections[0].Blocks.OfType<DrawingShape>().ToList();
        Require(shapes.Select(shape => shape.Kind).SequenceEqual(new[] { ShapeKind.Rectangle, ShapeKind.Line, ShapeKind.Ellipse, ShapeKind.Freeform }), "DOCX read: the kinds in order.");
        Require(shapes[0].Fill == Color.FromHex("#FF0000") && shapes[0].Outline is null && Math.Abs(shapes[0].Rotation - 15) < 1e-9, "DOCX read: fill and rotation.");
        Require(shapes[1].Dash == ShapeDash.Dash && shapes[1].OutlineWidth.Points == 2 && shapes[1].Height.Points == 0, "DOCX read: dash, width and a flat line.");
        Require(shapes[3].Path is { Count: 4 } path && path[2].Command == ShapePathCommand.CubicTo && Math.Abs(path[2].X1.Points - 60) < 1e-6 && path[3].Command == ShapePathCommand.Close, "DOCX read: the freeform path.");
        Require(read.Document.Sections[0].Blocks.OfType<Paragraph>().Any(), "DOCX read: the body text follows.");

        var box = (DrawingShape)read.Document.Sections[0].Header!.Blocks.Single();
        Require(box.Kind == ShapeKind.TextBox && box.Wrap == ImageWrap.InFront && box.OffsetX?.Points == 10 && box.OffsetY?.Points == 20 && box.AltText == "Letterhead", "DOCX read: the anchored text box.");
        Require(box.Fill == Color.FromHex("#EEEEEE") && box.Outline == Color.Black, "DOCX read: text box colors.");
        Require(box.Content!.OfType<Paragraph>().Single().Inlines.OfType<TextRun>().Single().Text == "In the header", "DOCX read: the text of the box.");

        // Word's own wrapping: the shape sits in the choice of an alternate content; an unknown geometry is reported.
        var wrapped = RewriteDocumentXml(buffer.ToArray(), xml => xml
            .Replace("<w:drawing>", "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"wps\"><w:drawing>", StringComparison.Ordinal)
            .Replace("</w:drawing>", "</w:drawing></mc:Choice><mc:Fallback /></mc:AlternateContent>", StringComparison.Ordinal)
            .Replace("prst=\"ellipse\"", "prst=\"star5\"", StringComparison.Ordinal));
        var foreign = DocumentReader.Read(new MemoryStream(wrapped), DocumentFormat.Docx);
        Require(foreign.Document.Sections[0].Blocks.OfType<DrawingShape>().Count() == 3, "DOCX read: shapes in an alternate content are read, the unknown geometry is left out.");
        Require(foreign.Warnings.Any(warning => warning.Message.Contains("star5", StringComparison.Ordinal)), "DOCX read: the unknown geometry is reported.");

        // Theme colors (with a luminance change) and the colors of a shape style.
        var themed = RewriteDocumentXml(buffer.ToArray(), xml => xml
            .Replace("<a:solidFill><a:srgbClr val=\"FF0000\" /></a:solidFill>", "<a:solidFill><a:schemeClr val=\"accent2\"><a:lumMod val=\"50000\" /></a:schemeClr></a:solidFill>", StringComparison.Ordinal)
            .Replace("<a:solidFill><a:srgbClr val=\"00FF00\" /></a:solidFill>", string.Empty, StringComparison.Ordinal)
            .Replace("<wps:bodyPr />", "<wps:style><a:lnRef idx=\"0\"><a:schemeClr val=\"accent1\" /></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"><a:shade val=\"50000\" /></a:schemeClr></a:fillRef></wps:style><wps:bodyPr />", StringComparison.Ordinal));
        var themedRead = DocumentReader.Read(new MemoryStream(themed), DocumentFormat.Docx);
        var themedShapes = themedRead.Document.Sections[0].Blocks.OfType<DrawingShape>().ToList();
        Require(themedShapes[0].Fill is { } darker && Math.Abs(darker.Red - 0x84) <= 1 && Math.Abs(darker.Green - 0x3C) <= 1 && Math.Abs(darker.Blue - 0x0C) <= 1, "DOCX read: a theme color with lumMod (accent2, darker 50%): " + themedShapes[0].Fill);
        Require(themedShapes[3].Fill == Color.FromHex("#223962"), "DOCX read: the fill of the shape style (accent1, shade 50%): " + themedShapes[3].Fill);
        Require(!themedRead.Warnings.Any(warning => warning.Message.Contains("color", StringComparison.OrdinalIgnoreCase)), "DOCX read: theme colors raise no warning.");

        // A second round trip is stable.
        using var again = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(read.Document, again).GetAwaiter().GetResult();
        again.Position = 0;
        Require(DocumentReader.Read(again, DocumentFormat.Docx).Document.Sections[0].Blocks.OfType<DrawingShape>().Count() == 4, "DOCX read: the second round trip keeps the shapes.");
    }

    private static byte[] RewriteDocumentXml(byte[] package, Func<string, string> change)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var copy = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName == "word/document.xml")
                {
                    using var reader = new StreamReader(input);
                    using var writer = new StreamWriter(copy);
                    writer.Write(change(reader.ReadToEnd()));
                }
                else
                {
                    input.CopyTo(copy);
                }
            }
        }

        return output.ToArray();
    }

    private static void WritesOdt()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "ODT: no warning: " + string.Join("|", result.Warnings.Select(warning => warning.Message)));
        var package = buffer.ToArray();
        if (Environment.GetEnvironmentVariable("PD_SHAPES_ODT") is { Length: > 0 } output)
        {
            File.WriteAllBytes(output, package);
        }

        Require(DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt).Document.Sections.Count > 0, "ODT: a document with shapes can be read back.");
        var content = Part(package, "content.xml");
        XDocument.Parse(content);
        Require(content.Contains("<draw:rect ") && content.Contains("<draw:line ") && content.Contains("<draw:ellipse ") && content.Contains("<draw:path "), "ODT: the basic draw elements.");
        Require(content.Contains("draw:transform=\"rotate (-0.2618)"), "ODT: the rotation of 15 degrees clockwise is -0.2618 rad.");
        Require(content.Contains("svg:d=\"M 0 0 L 500 0 C 600 100 600 400 500 500 Z\""), "ODT: the freeform path in tenths of a point.");
        Require(content.Contains("draw:fill-color=\"#FF0000\"") && content.Contains("draw:stroke=\"dash\"") && content.Contains("svg:stroke-width=\"0.071cm\""), "ODT: fill, dash and outline width (2 pt).");
        Require(content.Contains(">Body<"), "ODT: text after the shapes is kept.");

        var styles = Part(package, "styles.xml");
        XDocument.Parse(styles);
        Require(styles.Contains("<draw:stroke-dash ") && styles.Contains("<draw:frame ") && styles.Contains("<draw:text-box>") && styles.Contains("header</text:p>"), "ODT: a text box in the header and the dash styles.");
        Require(styles.Contains("svg:x=\"0.353cm\"") && styles.Contains("text:anchor-type=\"paragraph\""), "ODT: an anchored text box with its offset (10 pt = 0.353 cm).");
    }

    private static void ReadsOdt()
    {
        var anchored = new DrawingShape(ShapeKind.Rectangle, Pt(150), Pt(40)) { Fill = Color.FromHex("#FF0000"), Rotation = 30, Wrap = ImageWrap.InFront, OffsetX = Pt(100), OffsetY = Pt(60) };
        var document = new PortableDocument(new DocumentMetadata(), new Section(Document().Sections[0].Blocks.Append(anchored).ToList())
        {
            Header = Document().Sections[0].Header,
        });
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = DocumentReader.Read(buffer, DocumentFormat.Odt);
        Require(!read.Warnings.Any(warning => warning.Message.Contains("drawing object", StringComparison.OrdinalIgnoreCase) || warning.Message.Contains("shape", StringComparison.OrdinalIgnoreCase)),
            "ODT read: no shape warning: " + string.Join("|", read.Warnings.Select(warning => warning.Message)));
        var shapes = read.Document.Sections[0].Blocks.OfType<DrawingShape>().ToList();
        Require(shapes.Select(shape => shape.Kind).SequenceEqual(new[] { ShapeKind.Rectangle, ShapeKind.Line, ShapeKind.Ellipse, ShapeKind.Freeform, ShapeKind.Rectangle }), "ODT read: the kinds in order.");
        Require(shapes[0].Fill == Color.FromHex("#FF0000") && shapes[0].Outline is null && Math.Abs(shapes[0].Rotation - 15) < 0.01, "ODT read: fill and rotation.");
        Require(shapes[1].Dash == ShapeDash.Dash && Math.Abs(shapes[1].OutlineWidth.Points - 2) < 0.05 && shapes[1].Height.Points == 0, "ODT read: dash, width and a flat line.");
        Require(shapes[3].Path is { Count: 4 } path && path[2].Command == ShapePathCommand.CubicTo && Math.Abs(path[2].X1.Points - 60) < 0.05 && path[3].Command == ShapePathCommand.Close, "ODT read: the freeform path.");
        Require(Math.Abs(shapes[4].Rotation - 30) < 0.01 && shapes[4].Wrap == ImageWrap.InFront
            && Math.Abs(shapes[4].OffsetX!.Value.Points - 100) < 0.1 && Math.Abs(shapes[4].OffsetY!.Value.Points - 60) < 0.1, "ODT read: an anchored, rotated shape keeps its place.");

        var box = (DrawingShape)read.Document.Sections[0].Header!.Blocks.Single();
        Require(box.Kind == ShapeKind.TextBox && box.Wrap == ImageWrap.InFront && Math.Abs(box.OffsetX!.Value.Points - 10) < 0.05 && box.AltText == "Letterhead", "ODT read: the anchored text box.");
        Require(box.Fill == Color.FromHex("#EEEEEE") && box.Outline == Color.Black, "ODT read: text box colors.");
        Require(box.Content!.OfType<Paragraph>().Single().Inlines.OfType<TextRun>().Select(run => run.Text).Aggregate(string.Concat).Replace('\u00A0', ' ') == "In the header", "ODT read: the text of the box.");
    }

    private static void RendersPdf()
    {
        var body = new List<Block>
        {
            new DrawingShape(ShapeKind.Rectangle, Pt(100), Pt(50)) { Fill = Color.FromHex("#FF0000"), Outline = Color.Black, Rotation = 15 },
            new DrawingShape(ShapeKind.Line, Pt(100), Pt(0)) { Outline = Color.Black, Dash = ShapeDash.Dash },
            new DrawingShape(ShapeKind.Ellipse, Pt(40), Pt(40)) { Outline = Color.Black },
            new DrawingShape(ShapeKind.Freeform, Pt(50), Pt(50), path: new[]
            {
                new ShapePathStep(ShapePathCommand.MoveTo, Pt(0), Pt(0)),
                new ShapePathStep(ShapePathCommand.CubicTo, Pt(50), Pt(50), Pt(60), Pt(10), Pt(60), Pt(40)),
                new ShapePathStep(ShapePathCommand.Close, Pt(0), Pt(0)),
            }) { Fill = Color.FromHex("#00FF00") },
            new DrawingShape(ShapeKind.TextBox, Pt(220), Pt(60), new Block[]
            {
                new Paragraph(new Inline[] { new TextRun("Inline box text") }),
                new Heading(HeadingLevel.Level1, new TextRun("Not an outline entry")),
            }) { Outline = Color.Black },
            new Paragraph(new Inline[] { new TextRun("Body text") }),
            new Heading(HeadingLevel.Level1, new TextRun("Real heading")),
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(body)
        {
            Header = new HeaderFooter(Box("In the header")),
        });
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "PDF: no warning: " + string.Join("|", result.Warnings.Select(warning => warning.Message)));
        if (Environment.GetEnvironmentVariable("PD_SHAPES_OUT") is { Length: > 0 } output)
        {
            File.WriteAllBytes(output, buffer.ToArray());
        }
        var text = string.Join("\n", DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray())));
        Require(text.Contains("In the header", StringComparison.Ordinal), "PDF: a text box in the header.");
        Require(text.Contains("Inline box text", StringComparison.Ordinal) && text.Contains("Body text", StringComparison.Ordinal), "PDF: the text of an inline text box and the body: " + text);
    }

    private static void Throws(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Expected an ArgumentException.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
