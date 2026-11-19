using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// SVG images (phase AA, AA03): ODT embeds them natively, DOCX embeds them as a media part
/// with the SVG extension of the picture, PDF converts a documented subset into vector
/// graphics.
/// </summary>
internal static class SvgChecks
{
    internal static int Run()
    {
        try
        {
            ConvertsSvgToPdf();
            DrawsSimpleSvgText();
            ReportsUnsupportedSvgParts();
            ReportsBrokenSvg();
            EmbedsSvgIntoDocx();
            EmbedsSvgWithRasterFallback();
            UsesIntrinsicSvgSize();
            UsesCorrectSvgCoordinateAndPhysicalUnits();
            PositionsNonzeroViewBox();
            AppliesViewBoxAspectRatio();
            AppliesSimpleClippingPaths();
            DrawsSimpleGradients();
            DrawsSimplePatterns();
            KeepsPatternMatrixOpacityPairAndMaskTransform();
            RendersSvgOpacity();
            ParsesRealWorldPathData();
            AppliesVisibilityAndColorStyles();
            RejectsOverflowingSvgTransforms();
            EnforcesSvgSizeLimitInPdf();
            AppliesStrokeSettings();
            DrawsUseReferences();
            DrawsNestedViewports();
            DrawsTspanShifts();
            DrawsEmbeddedImages();
            CompositesGroupsAsAUnit();
            DrawsRotatedAndStretchedText();
            AppliesMasks();
            DrawsMarkers();
            DrawsInnerMarkers();
            InheritsPaintServers();
            RepeatsGradients();
            PaintsStrokesWithPaintServers();
            MultipliesElementOpacity();
            AppliesStyleSheetRules();
            Console.WriteLine("SVG (AA03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SVG (AA03): " + exception);
            return 1;
        }
    }

    /// <summary>An SVG with the given content and size attributes.</summary>
    private static byte[] Svg(string body, string size = "width=\"100\" height=\"50\"")
        => Encoding.UTF8.GetBytes(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" {size}>{body}</svg>");

    private static PortableDocument Document(byte[] svg)
    {
        var image = new ImageBlock(svg, ImageMediaType.Svg);
        return new PortableDocument(new DocumentMetadata(), new Section(new Block[] { image }));
    }

    private static void ConvertsSvgToPdf()
    {
        var svg = Svg("<rect x=\"10\" y=\"10\" width=\"80\" height=\"30\" fill=\"#ff0000\" stroke=\"#0000ff\" stroke-width=\"2\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();

        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)),
            "PDF: a supported SVG is drawn without a warning.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains(" re", StringComparison.Ordinal), "PDF: the rectangle of the SVG is drawn.");
        Require(content.Contains("1 0 0 rg", StringComparison.Ordinal), "PDF: the fill color of the SVG is used.");
        Require(content.Contains("0 0 1 RG", StringComparison.Ordinal), "PDF: the stroke color of the SVG is used.");
        Require(content.Contains(" cm", StringComparison.Ordinal), "PDF: the SVG is placed with a transformation.");
    }

    private static void ReportsUnsupportedSvgParts()
    {
        var svg = Svg("<text x=\"10\" y=\"20\"><tspan rotate=\"10\">Hello</tspan></text><circle cx=\"20\" cy=\"20\" r=\"10\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("texts", StringComparison.Ordinal)),
            "PDF: a text in an SVG is reported.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("circle", StringComparison.Ordinal)),
            "PDF: a supported element is not reported.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains(" c", StringComparison.Ordinal), "PDF: the circle of the SVG is drawn.");
    }

    private static void DrawsSimpleSvgText()
    {
        var svg = Svg("<g fill=\"#ff0000\" font-size=\"12\" font-weight=\"bold\"><text x=\"40\" y=\"20\" text-anchor=\"middle\">SVGText</text></g>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(!result.Warnings.Any(warning => warning.Message.Contains("texts", StringComparison.Ordinal))
            && content.Contains("(SVGText) Tj", StringComparison.Ordinal) && pages.Any(page => page.Contains("SVGText", StringComparison.Ordinal)),
            "PDF: simple SVG text is drawn with a registered font and remains extractable.");
        Require(content.Contains("1 0 0 rg", StringComparison.Ordinal) && content.Contains("0 0 -1", StringComparison.Ordinal),
            "PDF: SVG text uses its fill and remains upright in the SVG coordinate system.");

        var spans = Svg("<text x=\"10\" y=\"20\">Hello <tspan font-weight=\"bold\" fill=\"red\">world</tspan> again</text>");
        using var spanBuffer = new MemoryStream();
        var spanResult = new PdfDocumentWriter().WriteAsync(Document(spans), spanBuffer).GetAwaiter().GetResult();
        var spanContent = PdfStreams.Decompress(spanBuffer.ToArray());
        Require(!spanResult.Warnings.Any(warning => warning.Message.Contains("texts", StringComparison.Ordinal))
            && spanContent.Contains("(Hello) Tj", StringComparison.Ordinal) && spanContent.Contains("( world) Tj", StringComparison.Ordinal)
            && spanContent.Contains("( again) Tj", StringComparison.Ordinal) && spanContent.Contains("1 0 0 rg", StringComparison.Ordinal),
            "PDF: SVG text spans keep their formatting and whitespace between runs.");

        using var nbspBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(Svg("<text x=\"10\" y=\"20\">A&#160;B</text>")), nbspBuffer).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(nbspBuffer.ToArray()).Contains(@"(A\240B) Tj", StringComparison.Ordinal),
            "PDF: SVG whitespace normalization preserves non-breaking spaces.");

        using var sizeBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(Svg("<g font-size=\"12pt\"><text y=\"20\">A<tspan font-size=\"50%\">B</tspan></text></g>")), sizeBuffer)
            .GetAwaiter().GetResult();
        var sizes = PdfStreams.Decompress(sizeBuffer.ToArray());
        Require(sizes.Contains(" 16 Tf", StringComparison.Ordinal) && sizes.Contains(" 8 Tf", StringComparison.Ordinal),
            "PDF: SVG text resolves point and inherited percentage font sizes in user units.");

        using var strokeBuffer = new MemoryStream();
        var strokeResult = new PdfDocumentWriter().WriteAsync(Document(Svg("<text y=\"20\" fill=\"none\" stroke=\"red\">Hidden</text>")), strokeBuffer)
            .GetAwaiter().GetResult();
        Require(strokeResult.Warnings.Any(warning => warning.Message.Contains("text strokes", StringComparison.Ordinal)),
            "PDF: unsupported stroke-only SVG text is reported.");

        using var embeddedBuffer = new MemoryStream();
        var embeddedSvg = Svg("<text x=\"2\" y=\"20\" font-weight=\"bold\" fill=\"red\">Hallo</text>");
        var embeddedResult = new PdfDocumentWriter().WriteAsync(Document(embeddedSvg), embeddedBuffer, new PdfWriteOptions
        {
            PdfA = PdfAConformance.PdfA2b,
            Fonts = new Dictionary<string, byte[]> { [""] = PdfAChecks.Font() },
        }).GetAwaiter().GetResult();
        var embeddedPages = DocumentReader.ReadPdfPages(new MemoryStream(embeddedBuffer.ToArray()));
        Require(embeddedPages.Any(page => page.Contains("Hallo", StringComparison.Ordinal)),
            "PDF/A: SVG text uses the embedded font and remains extractable after subsetting: "
            + string.Join(" | ", embeddedPages) + "; " + string.Join("; ", embeddedResult.Warnings.Select(warning => warning.Message)));
        Require(PdfStreams.Decompress(embeddedBuffer.ToArray()).Contains("1 0 0 RG", StringComparison.Ordinal),
            "PDF: synthesized bold SVG text uses the fill color for its stroke.");
    }

    private static void ReportsBrokenSvg()
    {
        var broken = Encoding.UTF8.GetBytes("<svg><rect></svg>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(broken), buffer).GetAwaiter().GetResult();

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)),
            "PDF: a broken SVG is reported.");
    }

    private static void EmbedsSvgIntoDocx()
    {
        var svg = Svg("<circle cx=\"20\" cy=\"20\" r=\"10\" fill=\"green\"/>");
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var contentTypes = ReadZipEntry(package, "[Content_Types].xml");
        Require(contentTypes.Contains("image/svg+xml", StringComparison.Ordinal), "DOCX: SVG media type.");

        var document = ReadZipEntry(package, "word/document.xml");
        Require(document.Contains("svgBlip", StringComparison.Ordinal), "DOCX: the SVG extension of the picture.");
        Require(document.Contains("drawing/2016/SVG/main", StringComparison.Ordinal), "DOCX: the SVG namespace.");

        var media = ZipEntries(package).Any(name =>
            name.StartsWith("word/media/image", StringComparison.Ordinal) && name.EndsWith(".svg", StringComparison.Ordinal));
        Require(media, "DOCX: the SVG is stored as a media part.");

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)),
            "DOCX: the missing raster fallback is reported.");
    }

    private static void EmbedsSvgWithRasterFallback()
    {
        var svgBytes = Svg("<circle cx=\"20\" cy=\"20\" r=\"10\"/>");
        var rasterBytes = TestImages.RgbPng(4, 2, 0, 0, 255);
        var image = new ImageBlock(svgBytes, ImageMediaType.Svg)
        {
            RasterFallback = new ImageBlock(rasterBytes, ImageMediaType.Png),
        };
        foreach (var mode in new[] { "body", "header", "template" })
        {
            var source = mode == "header"
                ? new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Body"))) { Header = new HeaderFooter(image) })
                : new PortableDocument(new DocumentMetadata(), new Section(mode == "body" ? image : new Paragraph(new TextRun("{{Image}}"))));
            using var output = new MemoryStream();
            WriteResult result;
            if (mode == "template")
            {
                using var templateBuffer = new MemoryStream();
                new DocxDocumentWriter().WriteAsync(source, templateBuffer).GetAwaiter().GetResult();
                var template = DocumentTemplate.FromBytes(templateBuffer.ToArray()).Bind("Image", image);
                result = new DocxDocumentWriter().WriteAsync(PortableDocument.Create().Build(), output, new DocxWriteOptions { Template = template }).GetAwaiter().GetResult();
            }
            else result = new DocxDocumentWriter().WriteAsync(source, output).GetAwaiter().GetResult();
            var package = output.ToArray();
            var part = mode == "header" ? "word/header1.xml" : "word/document.xml";
            var xml = System.Xml.Linq.XDocument.Parse(ReadZipEntry(package, part));
            System.Xml.Linq.XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            System.Xml.Linq.XNamespace asvg = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";
            System.Xml.Linq.XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var rasterId = (string?)xml.Descendants(a + "blip").Single().Attribute(r + "embed");
            var svgId = (string?)xml.Descendants(asvg + "svgBlip").Single().Attribute(r + "embed");
            var relsPath = mode == "header" ? "word/_rels/header1.xml.rels" : "word/_rels/document.xml.rels";
            var rels = System.Xml.Linq.XDocument.Parse(ReadZipEntry(package, relsPath));
            string? Target(string? id) => rels.Descendants().FirstOrDefault(node => (string?)node.Attribute("Id") == id)?.Attribute("Target")?.Value;
            Require(rasterId != svgId && Target(rasterId)?.EndsWith(".png", StringComparison.Ordinal) == true
                && Target(svgId)?.EndsWith(".svg", StringComparison.Ordinal) == true
                && !result.Warnings.Any(warning => warning.Message.Contains("raster fallback", StringComparison.Ordinal)),
                $"DOCX: {mode} has distinct SVG and PNG relationships and no missing-fallback warning.");
            var read = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Docx).Document;
            var blocks = mode == "header" ? read.Sections[0].Header!.Blocks : read.Sections.SelectMany(section => section.Blocks);
            var readImage = blocks.OfType<ImageBlock>().Single();
            Require(readImage.MediaType == ImageMediaType.Svg && readImage.Data.Span.SequenceEqual(svgBytes)
                && readImage.RasterFallback is { } actualFallback && actualFallback.Data.Span.SequenceEqual(rasterBytes),
                $"DOCX: {mode} reads both the SVG and its raster fallback.");
            if (mode == "body")
            {
                xml.Descendants(asvg + "svgBlip").Single().SetAttributeValue(r + "embed", "missing-svg");
                var damaged = TestPackages.ReplacePart(package, part, Encoding.UTF8.GetBytes(xml.ToString()));
                var recovered = DocumentReader.Read(new MemoryStream(damaged), DocumentFormat.Docx);
                var recoveredImage = recovered.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
                Require(recoveredImage.MediaType == ImageMediaType.Png && recoveredImage.Data.Span.SequenceEqual(rasterBytes)
                    && recovered.Warnings.Any(warning => warning.Message.Contains("SVG image is missing", StringComparison.Ordinal)),
                    "DOCX: a missing SVG is reported and its raster fallback is recovered.");
            }
        }
        try
        {
            _ = new ImageBlock(rasterBytes, ImageMediaType.Png) { RasterFallback = new ImageBlock(rasterBytes, ImageMediaType.Png) };
            throw new InvalidOperationException("A raster image must not accept an SVG fallback.");
        }
        catch (ArgumentException) { }

        var limited = new ImageBlock(Encoding.UTF8.GetBytes("<svg width=\"1\" height=\"1\"/>"), ImageMediaType.Svg)
        {
            RasterFallback = new ImageBlock(rasterBytes, ImageMediaType.Png),
        };
        try
        {
            using var buffer = new MemoryStream();
            new DocxDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(limited)), buffer,
                new DocxWriteOptions { Limits = new ResourceLimits { MaxImageBytes = 40 } }).GetAwaiter().GetResult();
            throw new InvalidOperationException("An oversized raster fallback must be rejected.");
        }
        catch (ImageException) { }
    }

    private static void UsesIntrinsicSvgSize()
    {
        // Without width and height the size comes from the view box.
        var svg = Svg("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>", "viewBox=\"0 0 200 100\"");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();

        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)),
            "PDF: the size of an SVG without width and height is taken from the view box.");
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains(" cm", StringComparison.Ordinal), "PDF: the SVG is placed.");
    }

    private static void RendersSvgOpacity()
    {
        var svg = Svg("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#000000\" fill-opacity=\"0.5\"/>");
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();

        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        Require(pdf.Contains("/ExtGState", StringComparison.Ordinal), "PDF: the transparency of the SVG is a graphics state.");
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains(" gs", StringComparison.Ordinal), "PDF: the graphics state is applied.");
    }

    private static void PositionsNonzeroViewBox()
    {
        var svg = Svg("<rect x=\"10\" y=\"-20\" width=\"100\" height=\"50\"/>",
            "width=\"100\" height=\"50\" viewBox=\"10 -20 100 50\"");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("1 0 0 1 -10 20 cm", StringComparison.Ordinal), "PDF: the viewBox origin is translated to the image origin.");
        Require(content.Contains("10 -20 100 50 re", StringComparison.Ordinal), "PDF: the original SVG coordinates are retained.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)), "PDF: a valid viewBox produces no warning.");

        var zeroOrigin = SvgConverter.TryConvert(Svg("<rect width=\"10\" height=\"10\"/>", "viewBox=\"0 0 100 50\""), _ => "GS1", out _);
        Require(zeroOrigin is not null && !zeroOrigin.Content.Contains("1 0 0 1", StringComparison.Ordinal),
            "PDF: a zero viewBox origin needs no translation.");
    }

    private static void UsesCorrectSvgCoordinateAndPhysicalUnits()
    {
        var viewBoxOnly = Svg("<rect width=\"200\" height=\"100\"/>", "viewBox=\"0 0 200 100\"");
        var size = ImageLayout.Resolve(new ImageBlock(viewBoxOnly, ImageMediaType.Svg));
        Require(Math.Abs(size.Width.Points - 150) < 0.001 && Math.Abs(size.Height.Points - 75) < 0.001,
            "SVG: viewBox units use 96 pixels per inch when deriving the intrinsic size.");

        var oneDimension = Svg("<rect width=\"200\" height=\"100\"/>", "width=\"400px\" viewBox=\"0 0 200 100\"");
        var oneSize = ImageLayout.Resolve(new ImageBlock(oneDimension, ImageMediaType.Svg));
        Require(Math.Abs(oneSize.Width.Points - 300) < 0.001 && Math.Abs(oneSize.Height.Points - 150) < 0.001,
            "SVG: one specified dimension is retained and the other follows the viewBox ratio.");

        var noViewBox = Svg("<rect width=\"100\" height=\"50\"/>", "width=\"100px\" height=\"50px\"");
        var drawing = SvgConverter.TryConvert(noViewBox, _ => "GS1", out _);
        Require(drawing is not null && Math.Abs(drawing.UserWidth - 100) < 0.001 && Math.Abs(drawing.UserHeight - 50) < 0.001,
            "PDF: SVG user units remain pixels when no viewBox is present.");
    }

    private static void AppliesViewBoxAspectRatio()
    {
        static SvgDrawing Convert(string extra, out IReadOnlyList<string> warnings)
            => SvgConverter.TryConvert(Svg("<rect width=\"100\" height=\"100\"/>",
                "width=\"200\" height=\"100\" viewBox=\"0 0 100 100\" " + extra), _ => "GS1", out warnings)
                ?? throw new InvalidOperationException("SVG not converted.");

        var centered = Convert("", out var defaultWarnings);
        Require(centered.Content.Contains("0.5 0 0 1 25 0 cm", StringComparison.Ordinal), "SVG: default meet centers without stretching.");
        Require(defaultWarnings.Count == 0, "SVG: default aspect ratio has no warning.");

        var stretched = Convert("preserveAspectRatio=\"none\"", out _);
        Require(!stretched.Content.Contains(" cm", StringComparison.Ordinal), "SVG: none permits non-uniform stretching.");

        var cropped = Convert("preserveAspectRatio=\"xMaxYMax slice\"", out _);
        Require(cropped.Content.Contains("0 0 100 100 re W n", StringComparison.Ordinal), "SVG: slice clips to the viewport.");
        Require(cropped.Content.Contains("1 0 0 2 0 -100 cm", StringComparison.Ordinal), "SVG: slice aligns the bottom edge.");

        var shifted = SvgConverter.TryConvert(Svg("<rect x=\"10\" y=\"-20\" width=\"100\" height=\"100\"/>",
            "width=\"200\" height=\"100\" viewBox=\"10 -20 100 100\""), _ => "GS1", out _);
        Require(shifted is not null && shifted.Content.Contains("0.5 0 0 1 20 20 cm", StringComparison.Ordinal),
            "SVG: centering accounts for a shifted viewBox origin.");

        _ = Convert("preserveAspectRatio=\"invalid\"", out var invalidWarnings);
        Require(invalidWarnings.Any(warning => warning.Contains("preserveAspectRatio", StringComparison.Ordinal)), "SVG: invalid alignment is reported.");
    }

    private static void AppliesSimpleClippingPaths()
    {
        var svg = Svg("<defs><clipPath id=\"cut\"><rect x=\"2\" y=\"3\" width=\"20\" height=\"10\"/></clipPath></defs>" +
            "<g transform=\"translate(5 7)\" clip-path=\"url(#cut)\"><rect width=\"50\" height=\"30\"/></g>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("2 3 20 10 re\nQ\nW n", StringComparison.Ordinal), "PDF: clipping rectangle is applied.");
        Require(content.Contains("1 0 0 1 5 7 cm", StringComparison.Ordinal), "PDF: clipping uses the element transform.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("clipping", StringComparison.OrdinalIgnoreCase)), "PDF: supported clipping has no warning.");

        var multiple = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\"><rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"
            + "<rect x=\"20\" y=\"0\" width=\"10\" height=\"10\"/></clipPath></defs>"
            + "<rect width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var multipleWarnings);
        Require(multiple is not null
            && multiple.Content.Contains("0 0 10 10 re", StringComparison.Ordinal)
            && multiple.Content.Contains("20 0 10 10 re", StringComparison.Ordinal)
            && !multipleWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: multiple clip shapes are united.");

        var transformed = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\">"
            + "<rect width=\"10\" height=\"10\" transform=\"translate(2 3)\"/>"
            + "<rect x=\"20\" width=\"10\" height=\"10\" transform=\"scale(2 1)\"/></clipPath></defs>"
            + "<rect width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var transformedWarnings);
        Require(transformed is not null
            && transformed.Content.Contains("q\n1 0 0 1 2 3 cm\n0 0 10 10 re\nQ\n", StringComparison.Ordinal)
            && transformed.Content.Contains("q\n2 0 0 1 0 0 cm\n20 0 10 10 re\nQ\n", StringComparison.Ordinal)
            && !transformedWarnings.Any(warning => warning.Contains("per-shape", StringComparison.OrdinalIgnoreCase)),
            "SVG: per-shape transforms in a multi-shape clip are applied.");

        var singleTransformed = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\">"
            + "<rect width=\"10\" height=\"10\" transform=\"translate(2 3)\"/></clipPath></defs>"
            + "<rect width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out _);
        Require(singleTransformed is not null
            && singleTransformed.Content.Contains("q\n1 0 0 1 2 3 cm\n0 0 10 10 re\nQ\n", StringComparison.Ordinal),
            "SVG: a single clip shape keeps its transform.");

        var rounded = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\"><rect x=\"2\" y=\"3\" width=\"20\" height=\"10\" rx=\"4\" ry=\"6\"/></clipPath></defs>"
            + "<rect width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var roundedWarnings);
        Require(rounded is not null
            && rounded.Content.Contains("6 3 m", StringComparison.Ordinal)
            && rounded.Content.Contains(" c\n", StringComparison.Ordinal)
            && !roundedWarnings.Any(warning => warning.Contains("clipping shape", StringComparison.OrdinalIgnoreCase)),
            "SVG: a rounded-rectangle clip is applied.");

        var evenOdd = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\"><path style=\"clip-rule:evenodd\" d=\"M0 0 H20 V20 H0 Z\"/></clipPath></defs>" +
            "<rect width=\"50\" height=\"30\" style=\"clip-path:url(#c)\"/>"), _ => "GS1", out var warnings);
        Require(evenOdd is not null && evenOdd.Content.Contains("W* n", StringComparison.Ordinal) && warnings.Count == 0,
            "SVG: style clipping and the even-odd rule are applied.");

        var bounded = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"0.5\" height=\"1\"/></clipPath></defs>" +
            "<rect x=\"10\" y=\"20\" width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var boundedWarnings);
        Require(bounded is not null && bounded.Content.Contains("50 0 0 30 10 20 cm", StringComparison.Ordinal)
            && !boundedWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: object-bounding-box clipping maps to the target rectangle.");
        var ellipse = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><circle cx=\".5\" cy=\".5\" r=\".5\"/></clipPath></defs>" +
            "<ellipse cx=\"40\" cy=\"30\" rx=\"20\" ry=\"10\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var ellipseWarnings);
        Require(ellipse is not null && ellipse.Content.Contains("40 0 0 20 20 20 cm", StringComparison.Ordinal)
            && !ellipseWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: object-bounding-box clipping maps to the target ellipse.");
        var pathClip = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"1\" height=\"1\"/></clipPath></defs>" +
            "<path d=\"M0 0 H50 V30 Z\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var pathClipWarnings);
        Require(pathClip is not null && pathClip.Content.Contains("50 0 0 30 0 0 cm", StringComparison.Ordinal)
            && !pathClipWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: object-bounding-box clipping maps to the target path.");
        _ = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\"><rect width=\"1\" height=\"1\"/></clipPath></defs>" +
            "<text x=\"0\" y=\"10\" clip-path=\"url(#c)\">X</text>"), _ => "GS1", out var unsupported);
        Require(unsupported.Any(warning => warning.Contains("clipping path", StringComparison.OrdinalIgnoreCase)), "SVG: unsupported clip bounds are reported.");
        _ = SvgConverter.TryConvert(Svg("<defs><clipPath id=\"c\"><rect width=\"50%\" height=\"10\"/></clipPath></defs>" +
            "<rect width=\"50\" height=\"30\" clip-path=\"url(#c)\"/>"), _ => "GS1", out var relative);
        Require(relative.Any(warning => warning.Contains("clipping length", StringComparison.OrdinalIgnoreCase)), "SVG: unsupported relative clip lengths are reported.");

        var nested = SvgConverter.TryConvert(Svg("<defs>"
            + "<clipPath id=\"inner\"><rect x=\"2\" y=\"2\" width=\"10\" height=\"10\"/></clipPath>"
            + "<clipPath id=\"outer\"><rect width=\"20\" height=\"20\" clip-path=\"url(#inner)\"/></clipPath>"
            + "</defs><rect width=\"50\" height=\"30\" clip-path=\"url(#outer)\"/>"), _ => "GS1", out var nestedWarnings);
        Require(nested is not null
            && nested.Content.Contains("2 2 10 10 re", StringComparison.Ordinal)
            && nested.Content.Contains("0 0 20 20 re", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(nested.Content, "W n").Count == 2
            && !nestedWarnings.Any(warning => warning.Contains("nested", StringComparison.OrdinalIgnoreCase)),
            "SVG: a nested clip on a single shape intersects with the outer clip.");

        var nestedMultiple = SvgConverter.TryConvert(Svg("<defs>"
            + "<clipPath id=\"inner\"><rect x=\"2\" y=\"2\" width=\"10\" height=\"10\"/></clipPath>"
            + "<clipPath id=\"outer\"><rect width=\"10\" height=\"10\" clip-path=\"url(#inner)\"/>"
            + "<rect x=\"20\" width=\"10\" height=\"10\"/></clipPath>"
            + "</defs><rect width=\"50\" height=\"30\" clip-path=\"url(#outer)\"/>"), _ => "GS1", out var nestedMultipleWarnings);
        Require(nestedMultiple is not null
            && nestedMultiple.Content.Contains("2 2 10 10 re", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(nestedMultiple.Content, "W n").Count == 2
            && !nestedMultipleWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: a nested clip in a multi-shape union is applied: " + string.Join("; ", nestedMultipleWarnings));

        var sharedNested = SvgConverter.TryConvert(Svg("<defs>"
            + "<clipPath id=\"inner\"><rect x=\"2\" y=\"2\" width=\"10\" height=\"10\"/></clipPath>"
            + "<clipPath id=\"outer\"><rect width=\"10\" height=\"10\" clip-path=\"url(#inner)\"/>"
            + "<rect x=\"20\" width=\"10\" height=\"10\" clip-path=\"url(#inner)\"/></clipPath>"
            + "</defs><rect width=\"50\" height=\"30\" clip-path=\"url(#outer)\"/>"), _ => "GS1", out var sharedWarnings);
        Require(sharedNested is not null
            && !sharedWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: a shared nested clip in a multi-shape union is applied: " + string.Join("; ", sharedWarnings));

        var distinctNested = SvgConverter.TryConvert(Svg("<defs>"
            + "<clipPath id=\"inner\"><rect x=\"2\" y=\"2\" width=\"10\" height=\"10\"/></clipPath>"
            + "<clipPath id=\"other\"><rect x=\"4\" y=\"4\" width=\"6\" height=\"6\"/></clipPath>"
            + "<clipPath id=\"outer\"><rect width=\"10\" height=\"10\" clip-path=\"url(#inner)\"/>"
            + "<rect x=\"20\" width=\"10\" height=\"10\" clip-path=\"url(#other)\"/></clipPath>"
            + "</defs><rect width=\"50\" height=\"30\" clip-path=\"url(#outer)\"/>"), _ => "GS1", out var distinctWarnings);
        Require(distinctNested is not null
            && System.Text.RegularExpressions.Regex.Matches(distinctNested.Content, "W n").Count == 4
            && !distinctWarnings.Any(warning => warning.Contains("clipping", StringComparison.OrdinalIgnoreCase)),
            "SVG: different nested clips in a multi-shape clipping path are applied: "
                + string.Join("; ", distinctWarnings));
    }

    /// <summary>Path data as editors and minifiers write it; none of it may be dropped or break the PDF.</summary>
    private static void DrawsSimpleGradients()
    {
        var linear = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0%\" stop-color=\"red\"/><stop offset=\"100%\" stop-color=\"blue\"/></linearGradient></defs>"
            + "<rect x=\"10\" y=\"5\" width=\"60\" height=\"20\" fill=\"url(#g)\" stroke=\"black\"/>"
            + "<rect x=\"20\" y=\"30\" width=\"20\" height=\"10\" fill=\"url(#g)\"/>");
        using var linearBuffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(linear), linearBuffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(linearBuffer.ToArray());
        var content = PdfStreams.Decompress(linearBuffer.ToArray());
        Require(!result.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase))
            && pdf.Contains("/ShadingType 2", StringComparison.Ordinal)
            && content.Contains("60 0 0 20 10 5 cm", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(content, @"/Sh1 sh").Count == 2,
            "PDF: linear SVG gradient maps to each rectangle and reuses its shading.");
        Require(content.Contains(" S\n", StringComparison.Ordinal) || content.Contains("\nS\n", StringComparison.Ordinal),
            "PDF: a gradient-filled SVG shape retains its stroke.");

        var radial = Svg("<defs><radialGradient id=\"g\"><stop offset=\"0\" stop-color=\"white\"/><stop offset=\"1\" stop-color=\"black\"/></radialGradient></defs>"
            + "<ellipse cx=\"30\" cy=\"20\" rx=\"20\" ry=\"10\" fill=\"url(#g)\"/>");
        using var radialBuffer = new MemoryStream();
        var radialResult = new PdfDocumentWriter().WriteAsync(Document(radial), radialBuffer).GetAwaiter().GetResult();
        Require(!radialResult.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase))
            && Encoding.Latin1.GetString(radialBuffer.ToArray()).Contains("/ShadingType 3", StringComparison.Ordinal)
            && PdfStreams.Decompress(radialBuffer.ToArray()).Contains("40 0 0 20 10 10 cm", StringComparison.Ordinal),
            "PDF: radial SVG gradients follow an ellipse's bounding box.");

        var multistop = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\".5\" stop-color=\"green\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient></defs>"
            + "<rect width=\"20\" height=\"20\" fill=\"url(#g)\"/>");
        using var multiBuffer = new MemoryStream();
        var multiResult = new PdfDocumentWriter().WriteAsync(Document(multistop), multiBuffer).GetAwaiter().GetResult();
        var multiPdf = Encoding.Latin1.GetString(multiBuffer.ToArray());
        Require(!multiResult.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase))
            && multiPdf.Contains("/FunctionType 3", StringComparison.Ordinal) && multiPdf.Contains("/Bounds [0.5 ", StringComparison.Ordinal),
            "PDF: SVG gradients preserve an intermediate color stop.");
        var unsupported = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\"/><stop offset=\"0\"/><stop offset=\"1\"/></linearGradient></defs>"
            + "<rect width=\"20\" height=\"20\" fill=\"url(#g)\"/>");
        using var unsupportedBuffer = new MemoryStream();
        var unsupportedResult = new PdfDocumentWriter().WriteAsync(Document(unsupported), unsupportedBuffer).GetAwaiter().GetResult();
        Require(unsupportedResult.Warnings.Any(warning => warning.Message.Contains("repeated or unordered", StringComparison.Ordinal)),
            "PDF: unsupported coincident gradient stops are reported.");

        var translucent = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\" stop-opacity=\".5\"/>"
            + "<stop offset=\"1\" stop-color=\"blue\" stop-opacity=\".5\"/></linearGradient></defs><rect width=\"20\" height=\"20\" fill=\"url(#g)\"/>");
        using var translucentBuffer = new MemoryStream();
        var translucentResult = new PdfDocumentWriter().WriteAsync(Document(translucent), translucentBuffer).GetAwaiter().GetResult();
        Require(!translucentResult.Warnings.Any(warning => warning.Message.Contains("opacity", StringComparison.OrdinalIgnoreCase))
            && PdfStreams.Decompress(translucentBuffer.ToArray()).Contains("/GS1 gs", StringComparison.Ordinal)
            && Encoding.Latin1.GetString(translucentBuffer.ToArray()).Contains("/ca 0.5", StringComparison.Ordinal),
            "PDF: equal gradient stop opacity is applied to the whole shading.");
        var varying = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-opacity=\"0\"/>"
            + "<stop offset=\"1\" stop-opacity=\"1\"/></linearGradient></defs><rect width=\"20\" height=\"20\" fill=\"url(#g)\"/>");
        using var varyingBuffer = new MemoryStream();
        var varyingResult = new PdfDocumentWriter().WriteAsync(Document(varying), varyingBuffer).GetAwaiter().GetResult();
        Require(varyingResult.Warnings.Any(warning => warning.Message.Contains("varying gradient stop opacity", StringComparison.Ordinal)),
            "PDF: unsupported varying gradient opacity is reported.");
    }

    /// <summary>Regression checks from the .NET/Swift comparison run (transform-aware patterns, one alpha state, one mask transform).</summary>
    private static void KeepsPatternMatrixOpacityPairAndMaskTransform()
    {
        // 1. The pattern matrix includes the view box scale, the group and the element transform.
        var scaled = Svg("<defs><pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"2\" height=\"2\"><rect width=\"1\" height=\"1\"/></pattern></defs>"
            + "<g transform=\"translate(1 0)\"><rect transform=\"scale(2)\" width=\"4\" height=\"4\" fill=\"url(#p)\"/></g>");
        using var scaledBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(scaled), scaledBuffer).GetAwaiter().GetResult();
        Require(Encoding.Latin1.GetString(scaledBuffer.ToArray()).Contains("/Matrix [2 0 0 2 1 0]", StringComparison.Ordinal),
            "PDF: an SVG pattern matrix includes the enclosing group and element transforms.");

        // 2. Fill and stroke opacity share one graphics state, so the second does not reset the first.
        var both = Svg("<circle cx=\"10\" cy=\"10\" r=\"5\" fill=\"blue\" fill-opacity=\"0.5\" stroke=\"black\" stroke-opacity=\"0.2\" stroke-width=\"2\"/>");
        using var bothBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(both), bothBuffer).GetAwaiter().GetResult();
        var bothPdf = Encoding.Latin1.GetString(bothBuffer.ToArray());
        Require(bothPdf.Contains("/ca 0.5 /CA 0.2", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(PdfStreams.Decompress(bothBuffer.ToArray()), " gs").Count == 1,
            "PDF: SVG fill and stroke opacity are one graphics state.");

        // 3. A stroke painted with a gradient applies the element transform once in its mask.
        var stroked = Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/><stop offset=\"1\" stop-color=\"blue\"/></linearGradient></defs>"
            + "<rect transform=\"translate(5 5)\" x=\"10\" y=\"10\" width=\"70\" height=\"60\" fill=\"none\" stroke=\"url(#g)\" stroke-width=\"8\"/>");
        using var strokedBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(stroked), strokedBuffer).GetAwaiter().GetResult();
        var maskForm = System.Text.RegularExpressions.Regex.Match(
            Encoding.Latin1.GetString(strokedBuffer.ToArray()), @"/S /Transparency /CS /DeviceGray >>[^>]*>>\s*stream\r?\n(.*?)endstream", System.Text.RegularExpressions.RegexOptions.Singleline);
        Require(maskForm.Success && System.Text.RegularExpressions.Regex.Matches(maskForm.Groups[1].Value, "1 0 0 1 5 5 cm").Count == 1,
            "PDF: the stroke-paint mask applies the element transform once.");
    }

    private static void DrawsSimplePatterns()
    {
        var svg = Svg("<defs><pattern id=\"p\" patternUnits=\"userSpaceOnUse\" width=\"10\" height=\"10\">"
            + "<rect width=\"5\" height=\"10\" fill=\"red\"/></pattern></defs>"
            + "<rect width=\"40\" height=\"20\" fill=\"url(#p)\"/><circle cx=\"60\" cy=\"20\" r=\"10\" fill=\"url(#p)\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(!result.Warnings.Any(warning => warning.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase))
            && pdf.Contains("/PatternType 1", StringComparison.Ordinal) && pdf.Contains("/XStep 10 /YStep 10", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(content + pdf, @"/P1 scn").Count == 2, // the drawing is a form XObject
            "PDF: a user-space SVG pattern becomes a reusable tiling pattern.");
        Require(pdf.Contains("1 0 0 rg", StringComparison.Ordinal) && pdf.Contains("0 0 5 10 re", StringComparison.Ordinal),
            "PDF: the SVG pattern contains the tile's original drawing.");

        var relative = Svg("<defs><pattern id=\"p\" width=\"25%\" height=\"50%\" patternContentUnits=\"objectBoundingBox\">"
            + "<rect width=\".1\" height=\".2\" fill=\"red\"/></pattern></defs>"
            + "<rect x=\"10\" y=\"5\" width=\"40\" height=\"20\" fill=\"url(#p)\"/>");
        using var relativeBuffer = new MemoryStream();
        var relativeResult = new PdfDocumentWriter().WriteAsync(Document(relative), relativeBuffer).GetAwaiter().GetResult();
        var relativePdf = Encoding.Latin1.GetString(relativeBuffer.ToArray());
        Require(!relativeResult.Warnings.Any(warning => warning.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase))
            && relativePdf.Contains("/BBox [10 5 20 15]", StringComparison.Ordinal)
            && relativePdf.Contains("40 0 0 20 10 5 cm", StringComparison.Ordinal),
            "PDF: object-bounding-box pattern tiles and their contents use the target bounds.");

        // A path has a bounding box too, so its pattern tiles scale with it.
        var onPath = Svg("<defs><pattern id=\"p\" width=\".1\" height=\".1\"><rect width=\"1\" height=\"1\"/></pattern></defs>"
            + "<path d=\"M0 0 H20 V20 Z\" fill=\"url(#p)\"/>");
        using var onPathBuffer = new MemoryStream();
        var onPathResult = new PdfDocumentWriter().WriteAsync(Document(onPath), onPathBuffer).GetAwaiter().GetResult();
        Require(!onPathResult.Warnings.Any(warning => warning.Message.Contains("pattern object bounding-box", StringComparison.Ordinal)),
            "PDF: a pattern on a path uses its bounding box: " + string.Join("; ", onPathResult.Warnings));

        // A text has no bounding box here, so the pattern stays reported.
        var unsupported = Svg("<defs><pattern id=\"p\" width=\".1\" height=\".1\"><rect width=\"1\" height=\"1\"/></pattern></defs>"
            + "<text x=\"0\" y=\"10\" fill=\"url(#p)\">X</text>");
        using var unsupportedBuffer = new MemoryStream();
        var unsupportedResult = new PdfDocumentWriter().WriteAsync(Document(unsupported), unsupportedBuffer).GetAwaiter().GetResult();
        Require(unsupportedResult.Warnings.Any(warning => warning.Message.Contains("pattern object bounding-box", StringComparison.Ordinal)),
            "PDF: unsupported object-bounding-box pattern geometry is reported.");
    }

    private static void ParsesRealWorldPathData()
    {
        static string Path(string data)
            => SvgConverter.TryConvert(Svg($"<path d=\"{data}\"/>"), _ => "GS1", out _)?.Content
                ?? throw new InvalidOperationException("SVG not converted.");

        var curves = Path("M0 0 C1 1 2 2 3 3 4 4 5 5 6 6 L9 9");
        Require(curves.Contains("4 4 5 5 6 6 c", StringComparison.Ordinal), "SVG: a repeated curve coordinate set is drawn.");
        Require(curves.Contains("9 9 l", StringComparison.Ordinal), "SVG: the command after a repeated curve is drawn.");

        var lines = Path("M0 0 H10 20 V5 6");
        Require(lines.Contains("20 0 l", StringComparison.Ordinal) && lines.Contains("20 6 l", StringComparison.Ordinal),
            "SVG: repeated H and V coordinates are drawn.");

        var minified = Path("M.5.5L10.5.5");
        Require(minified.Contains("0.5 0.5 m", StringComparison.Ordinal) && minified.Contains("10.5 0.5 l", StringComparison.Ordinal),
            "SVG: a second dot starts the next number.");

        var smooth = Path("M0 0 L1 0 S2 2 3 0 4 2 5 0");
        Require(smooth.Contains("4 -2 4 2 5 0 c", StringComparison.Ordinal), "SVG: a repeated S reflects the preceding S.");

        var overflow = Path("M1e400 0 L1 1");
        Require(!overflow.Contains("Infinity", StringComparison.Ordinal) && !overflow.Contains("NaN", StringComparison.Ordinal),
            "SVG: a non-finite number never reaches the PDF.");

        // Linear scan: the scanner used to copy the rest of the path for every number.
        var large = new StringBuilder("M0 0");
        for (var i = 0; i < 200_000; i++)
        {
            large.Append(" L").Append(i % 100).Append(' ').Append(i % 97);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = Path(large.ToString());
        Require(watch.Elapsed < TimeSpan.FromSeconds(5), $"SVG: a 1.3 MB path converts quickly, took {watch.ElapsedMilliseconds} ms.");
    }

    /// <summary>Styles as editors write them: hidden layers, zero strokes and colors with alpha.</summary>
    private static void AppliesVisibilityAndColorStyles()
    {
        static string Convert(string body, Func<string, string>? alpha = null)
            => SvgConverter.TryConvert(Svg(body), alpha ?? (_ => "GS1"), out _)?.Content
                ?? throw new InvalidOperationException("SVG not converted.");

        var hidden = Convert("<g style=\"display:none\"><rect width=\"10\" height=\"10\" fill=\"red\"/></g><rect display=\"none\" width=\"5\" height=\"5\"/>");
        Require(!hidden.Contains(" re", StringComparison.Ordinal), "SVG: display:none skips the element and its children.");

        var invisible = Convert("<g visibility=\"hidden\"><rect width=\"10\" height=\"10\"/><rect visibility=\"visible\" width=\"5\" height=\"5\"/></g>");
        Require(invisible.Contains("10 10 re\nn\n", StringComparison.Ordinal), "SVG: a hidden shape ends its path without painting.");
        Require(invisible.Contains("5 5 re\n0 0 0 rg\nf\n", StringComparison.Ordinal), "SVG: a child may be visible again.");

        var zeroStroke = Convert("<rect width=\"10\" height=\"10\" fill=\"none\" stroke=\"black\" stroke-width=\"0\"/>");
        Require(!zeroStroke.Contains(" RG", StringComparison.Ordinal) && zeroStroke.Contains("re\nn\n", StringComparison.Ordinal),
            "SVG: stroke-width 0 draws no stroke.");

        var keys = new List<string>();
        var rgba = Convert(
            "<rect width=\"10\" height=\"10\" fill=\"rgba(255,0,0,0.5)\" fill-opacity=\"0.5\"/>"
            + "<rect width=\"5\" height=\"5\" fill=\"rgb(0%,100%,0%)\"/>"
            + "<rect width=\"2\" height=\"2\" fill=\"#0000FF80\"/>",
            key => { keys.Add(key); return "GS" + keys.Count; });
        Require(rgba.Contains("1 0 0 rg", StringComparison.Ordinal) && keys.Contains("pair:0.25:1"),
            "SVG: rgba sets the color and multiplies its alpha with fill-opacity.");
        Require(rgba.Contains("0 1 0 rg", StringComparison.Ordinal), "SVG: rgb() accepts percentages.");
        Require(rgba.Contains("0 0 1 rg", StringComparison.Ordinal) && keys.Contains("pair:0.502:1"), "SVG: #RRGGBBAA carries an alpha.");
    }

    private static IReadOnlyList<string> ZipEntries(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    private static void EnforcesSvgSizeLimitInPdf()
    {
        var image = new ImageBlock(Svg("<rect width=\"10\" height=\"10\"/>"), ImageMediaType.Svg);
        foreach (var block in new Block[] { image, new TableBlock([new TableRow(new TableCell(image))]) })
        {
            try
            {
                using var buffer = new MemoryStream();
                new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(block)), buffer,
                    new PdfWriteOptions { Limits = new ResourceLimits { MaxImageBytes = 10 } }).GetAwaiter().GetResult();
                throw new InvalidOperationException("PDF: oversized SVG was not rejected.");
            }
            catch (ImageException) { }
        }
    }

    private static void RejectsOverflowingSvgTransforms()
    {
        var svg = Svg("<rect width=\"10\" height=\"10\" transform=\"scale(1e308) scale(1e308)\"/>");
        var drawing = SvgConverter.TryConvert(svg, _ => "GS1", out var warnings);
        Require(drawing is not null && warnings.Any(warning => warning.Contains("numeric range", StringComparison.Ordinal))
            && !drawing.Content.Contains("Infinity", StringComparison.Ordinal) && !drawing.Content.Contains("NaN", StringComparison.Ordinal),
            "SVG: overflowing transformations are reported instead of emitting invalid PDF numbers.");
    }

    /// <summary>OF-B07: the dash pattern, the line caps and the miter limit of a stroke.</summary>
    private static void AppliesStrokeSettings()
    {
        var styled = SvgConverter.TryConvert(Svg("<line x1=\"0\" y1=\"5\" x2=\"100\" y2=\"5\" stroke=\"black\""
            + " stroke-dasharray=\"5 3\" stroke-dashoffset=\"2\" stroke-linecap=\"round\""
            + " stroke-linejoin=\"bevel\" stroke-miterlimit=\"6\"/>"), _ => "GS1", out var warnings);
        Require(styled is not null && warnings.Count == 0
            && styled.Content.Contains("[5 3] 2 d\n", StringComparison.Ordinal)
            && styled.Content.Contains("1 J\n", StringComparison.Ordinal)
            && styled.Content.Contains("2 j\n", StringComparison.Ordinal)
            && styled.Content.Contains("6 M\n", StringComparison.Ordinal),
            "SVG: the dash pattern, the line cap, the line join and the miter limit are applied.");

        var repeated = SvgConverter.TryConvert(Svg("<rect width=\"10\" height=\"10\" stroke=\"black\" stroke-dasharray=\"4\"/>"
            + "<rect x=\"20\" width=\"10\" height=\"10\" stroke=\"black\" stroke-dasharray=\"2,2\"/>"
            + "<rect x=\"40\" width=\"10\" height=\"10\" stroke=\"black\" stroke-dasharray=\"none\"/>"), _ => "GS1", out var dashWarnings);
        Require(repeated is not null && dashWarnings.Count == 0
            && repeated.Content.Contains("[4 4] 0 d\n", StringComparison.Ordinal)
            && repeated.Content.Contains("[2 2] 0 d\n", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(repeated.Content, " d\n").Count == 2,
            "SVG: one dash length repeats and none draws a solid line without a dash operator.");

        var invalid = SvgConverter.TryConvert(Svg("<rect width=\"10\" height=\"10\" stroke=\"black\" stroke-dasharray=\"-4\""
            + " stroke-dashoffset=\"x\" stroke-linecap=\"triangle\" stroke-linejoin=\"spike\" stroke-miterlimit=\"0\"/>"),
            _ => "GS1", out var reports);
        Require(invalid is not null && invalid.Content.Contains("0 0 10 10 re", StringComparison.Ordinal)
            && reports.Any(report => report.Contains("dash pattern", StringComparison.Ordinal))
            && reports.Any(report => report.Contains("dash offset", StringComparison.Ordinal))
            && reports.Any(report => report.Contains("line cap", StringComparison.Ordinal))
            && reports.Any(report => report.Contains("line join", StringComparison.Ordinal))
            && reports.Any(report => report.Contains("miter limit", StringComparison.Ordinal)),
            "SVG: invalid stroke settings are reported and the shape is still drawn.");
    }

    /// <summary>OF-B07: a <c>use</c> element draws an element defined elsewhere, also in a group.</summary>
    private static void DrawsUseReferences()
    {
        const string WithXLink = "width=\"100\" height=\"50\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";
        var reused = SvgConverter.TryConvert(Svg(
            "<defs><circle id=\"dot\" cx=\"5\" cy=\"5\" r=\"4\"/></defs>"
            + "<use href=\"#dot\" x=\"20\" y=\"10\" fill=\"#ff0000\"/>"
            + "<use xlink:href=\"#dot\" transform=\"translate(40 0)\"/>", WithXLink), _ => "GS1", out var warnings);
        Require(reused is not null && warnings.Count == 0
            && reused.Content.Contains("1 0 0 1 20 10 cm", StringComparison.Ordinal)
            && reused.Content.Contains("1 0 0 1 40 0 cm", StringComparison.Ordinal)
            && reused.Content.Contains("1 0 0 rg", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(reused.Content, " m\n").Count == 2,
            "SVG: a use element draws the referenced shape again, shifted and with its own style.");

        var inherited = SvgConverter.TryConvert(Svg(
            "<defs><path id=\"p\" d=\"M0 0 H10 V10 Z\"/></defs><g stroke=\"blue\" stroke-width=\"2\" fill=\"none\">"
            + "<use href=\"#p\" x=\"5\"/></g>"), _ => "GS1", out var inheritedWarnings);
        Require(inherited is not null && inheritedWarnings.Count == 0
            && inherited.Content.Contains("0 0 1 RG\n", StringComparison.Ordinal)
            && inherited.Content.Contains("2 w\n", StringComparison.Ordinal)
            && inherited.Content.Contains("1 0 0 1 5 0 cm", StringComparison.Ordinal),
            "SVG: the style of the ancestors of a use element reaches the referenced shape.");

        var tiled = Svg("<defs><path id=\"p\" d=\"M0 0 H5 V5 Z\"/><pattern id=\"t\" patternUnits=\"userSpaceOnUse\" width=\"8\" height=\"8\">"
            + "<use href=\"#p\" fill=\"red\"/></pattern></defs><rect width=\"20\" height=\"20\" fill=\"url(#t)\"/>");
        using var tileBuffer = new MemoryStream();
        var tileResult = new PdfDocumentWriter().WriteAsync(Document(tiled), tileBuffer).GetAwaiter().GetResult();
        var tilePdf = Encoding.Latin1.GetString(tileBuffer.ToArray());
        Require(!tileResult.Warnings.Any(warning => warning.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase))
            && tilePdf.Contains("/PatternType 1", StringComparison.Ordinal) && tilePdf.Contains("1 0 0 rg", StringComparison.Ordinal),
            "PDF: a use element inside a pattern tile is drawn with the tile and keeps its own style.");

        _ = SvgConverter.TryConvert(Svg("<use href=\"#missing\"/><use/>"), _ => "GS1", out var missing);
        Require(missing.Any(report => report.Contains("reference '#missing'", StringComparison.Ordinal))
            && missing.Any(report => report.Contains("without a reference", StringComparison.Ordinal)),
            "SVG: a use element without a target is reported.");

        _ = SvgConverter.TryConvert(Svg("<use id=\"self\" href=\"#self\"/>"), _ => "GS1", out var self);
        Require(self.Any(report => report.Contains("reference '#self'", StringComparison.Ordinal)),
            "SVG: a use element that refers to itself is reported.");

        _ = SvgConverter.TryConvert(Svg("<g id=\"loop\"><use href=\"#loop\"/></g>"), _ => "GS1", out var loop);
        Require(loop.Any(report => report.Contains("nesting", StringComparison.Ordinal)),
            "SVG: a cyclic use reference ends in a report instead of a stack overflow.");
    }

    /// <summary>OF-B07: <c>opacity</c> applies to the fill and the stroke, also of the children.</summary>
    /// <summary>
    /// OF-B07: a nested <c>svg</c> is a viewport of its own (x/y, size, viewBox) and a <c>symbol</c>
    /// is placed and sized by its <c>use</c> reference.
    /// </summary>
    private static void DrawsNestedViewports()
    {
        var svg = Svg(
            "<symbol id=\"dot\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#ff0000\"/></symbol>"
            + "<svg x=\"10\" y=\"5\" width=\"20\" height=\"20\" viewBox=\"0 0 10 10\">"
            + "<circle cx=\"5\" cy=\"5\" r=\"5\" fill=\"#00ff00\"/></svg>"
            + "<use href=\"#dot\" x=\"50\" y=\"10\" width=\"20\" height=\"20\"/>",
            "width=\"100\" height=\"50\"");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("symbol", StringComparison.Ordinal)),
            "PDF: a symbol is drawn through its use reference: " + string.Join("; ", result.Warnings));
        Require(content.Contains("1 0 0 1 10 5 cm", StringComparison.Ordinal),
            "PDF: the nested svg is placed at its x and y.");
        Require(content.Contains("0 0 20 20 re W n", StringComparison.Ordinal),
            "PDF: the nested viewport is clipped to its size.");
        Require(content.Contains("2 0 0 2 0 0 cm", StringComparison.Ordinal),
            "PDF: the view box of the nested svg is scaled into the viewport.");
        Require(content.Contains("1 0 0 1 50 10 cm", StringComparison.Ordinal),
            "PDF: the use element places its symbol.");
        Require(content.Contains("0 0 10 10 re", StringComparison.Ordinal),
            "PDF: the symbol content is drawn.");

        // A nested svg without a size keeps the parent's units and only shifts its content.
        var shifted = Svg("<svg x=\"5\" y=\"5\"><rect width=\"10\" height=\"10\"/></svg>");
        using var shiftedBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(shifted), shiftedBuffer).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(shiftedBuffer.ToArray()).Contains("1 0 0 1 5 5 cm", StringComparison.Ordinal),
            "PDF: a nested svg without a size is only shifted.");

        // The ODT and DOCX writers embed the SVG unchanged.
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var package = new MemoryStream();
            DocumentWriter.WriteAsync(Document(svg), package, format).GetAwaiter().GetResult();
            Require(package.Length > 0, $"{format}: the SVG document is written.");
        }
    }

    /// <summary>
    /// OF-B07: a tspan may shift (dx/dy) or place (x/y) the cursor — the multi-line text pattern.
    /// </summary>
    private static void DrawsTspanShifts()
    {
        var svg = Svg(
            "<text x=\"10\" y=\"20\">A<tspan dx=\"5\">B</tspan><tspan x=\"10\" dy=\"12\">C</tspan></text>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("texts", StringComparison.Ordinal)),
            "PDF: a tspan with dx/dy is supported: " + string.Join("; ", result.Warnings));
        Require(content.Contains("(A) Tj", StringComparison.Ordinal) && content.Contains("(B) Tj", StringComparison.Ordinal)
                && content.Contains("(C) Tj", StringComparison.Ordinal),
            "PDF: all segments of the shifted text are drawn.");
        // B follows A shifted by 5 on the same baseline, C starts a new line at x 10 and y 32.
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"1 0 0 -1 10 20 cm\nBT[\s\S]*?\(A\) Tj"),
            "PDF: the first segment keeps the text position.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"1 0 0 -1 (1[0-9.]+) 20 cm\nBT[\s\S]*?\(B\) Tj"),
            "PDF: dx moves the next segment on the same baseline.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"1 0 0 -1 10 32 cm\nBT[\s\S]*?\(C\) Tj"),
            "PDF: x and dy place the segment absolutely: " + content);
    }

    /// <summary>
    /// OF-B07: a raster image embedded in an SVG as a base64 data URI is drawn; an external
    /// reference stays reported.
    /// </summary>
    private static void DrawsEmbeddedImages()
    {
        var png = Convert.ToBase64String(TestImages.RgbPng(4, 2, 255, 0, 0));
        var svg = Svg(
            $"<image x=\"10\" y=\"5\" width=\"40\" height=\"20\" xlink:href=\"data:image/png;base64,{png}\"/>"
                .Replace("xlink:", string.Empty, StringComparison.Ordinal),
            "width=\"100\" height=\"50\"");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(!result.Warnings.Any(warning => warning.Message.Contains("image", StringComparison.OrdinalIgnoreCase)),
            "PDF: an embedded raster image is drawn: " + string.Join("; ", result.Warnings));
        Require(content.Contains("40 0 0 -20 10 25 cm", StringComparison.Ordinal),
            "PDF: the embedded image is placed in its box: " + content);
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"/Im[0-9]+ Do"),
            "PDF: the embedded image is painted from an XObject.");
        Require(Encoding.Latin1.GetString(buffer.ToArray()).Contains("/Subtype /Image", StringComparison.Ordinal),
            "PDF: the embedded image is embedded as an image XObject.");

        // An external reference needs a file that is not part of the SVG.
        using var external = new MemoryStream();
        var externalResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<image width=\"10\" height=\"10\" href=\"logo.png\"/>")), external)
            .GetAwaiter().GetResult();
        RequreExternal(externalResult);
        Require(!PdfStreams.Decompress(external.ToArray()).Contains(" Do", StringComparison.Ordinal),
            "PDF: an external image reference is not drawn.");
    }

    private static void RequreExternal(WriteResult result)
        => Require(result.Warnings.Any(warning => warning.Message.Contains("external reference", StringComparison.Ordinal)),
            "PDF: an embedded image with an external reference is reported.");

    /// <summary>
    /// OF-B07: a group with its own opacity is drawn as a transparency group, so overlapping
    /// children composite as a unit instead of showing through each other.
    /// </summary>
    private static void CompositesGroupsAsAUnit()
    {
        var svg = Svg("<g opacity=\"0.5\"><rect width=\"20\" height=\"20\" fill=\"#ff0000\"/>"
            + "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#ff0000\"/></g>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("opacity", StringComparison.OrdinalIgnoreCase)),
            "PDF: a group opacity is supported: " + string.Join("; ", result.Warnings));
        Require(pdf.Contains("/S /Transparency", StringComparison.Ordinal) && pdf.Contains("/Subtype /Form", StringComparison.Ordinal),
            "PDF: the group becomes a form XObject with a transparency group.");
        Require(pdf.Contains("/ca 0.5", StringComparison.Ordinal) && pdf.Contains("/CA 0.5", StringComparison.Ordinal),
            "PDF: the transparency of the group applies to fill and stroke.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"/Fm[0-9]+ Do"),
            "PDF: the group is painted as one object.");
        // Both rectangles are inside the form, so they overlap within the same alpha layer.
        Require(pdf.Contains("/BBox [0 0 100 50]", StringComparison.Ordinal),
            "PDF: the group uses the user space of the image as its box.");

        // An element's own opacity keeps multiplying (there is no group around a single shape).
        var element = Svg("<rect width=\"10\" height=\"10\" opacity=\"0.4\" fill=\"#00ff00\"/>");
        using var elementBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(element), elementBuffer).GetAwaiter().GetResult();
        Require(!PdfStreams.Decompress(elementBuffer.ToArray()).Contains(" Do", StringComparison.Ordinal),
            "PDF: an element opacity is no transparency group.");
    }

    /// <summary>OF-B07: a text may rotate as a whole and be stretched to <c>textLength</c>.</summary>
    private static void DrawsRotatedAndStretchedText()
    {
        var svg = Svg("<text x=\"20\" y=\"30\" rotate=\"90\">Rot</text>"
            + "<text x=\"10\" y=\"45\" textLength=\"60\">Breit</text>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("texts", StringComparison.Ordinal)),
            "PDF: a rotated and stretched text is supported: " + string.Join("; ", result.Warnings));
        Require(content.Contains("0 1 -1 0 20 30 cm", StringComparison.Ordinal),
            "PDF: rotate=\"90\" turns the text clockwise around its anchor: " + content);
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"1 0 0 -1 10 45 cm\n(?<scale>[0-9.]+) 0 0 1 0 0 cm"),
            "PDF: textLength stretches the text horizontally.");
        var scale = double.Parse(
            System.Text.RegularExpressions.Regex.Match(content, @"1 0 0 -1 10 45 cm\n(?<scale>[0-9.]+) 0 0 1 0 0 cm")
                .Groups["scale"].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Require(scale > 1, "PDF: the text is stretched to reach its target length: " + scale);

        // A rotation list is applied per glyph; the last value repeats.
        using var listBuffer = new MemoryStream();
        var listResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<text x=\"5\" y=\"10\" rotate=\"0 45 0\">Rot</text>")), listBuffer)
            .GetAwaiter().GetResult();
        var listContent = PdfStreams.Decompress(listBuffer.ToArray());
        Require(!listResult.Warnings.Any(warning => warning.Message.Contains("rotation per glyph", StringComparison.Ordinal)),
            "PDF: a rotation per glyph is drawn: " + string.Join("; ", listResult.Warnings));
        Require(listContent.Contains("0.7071", StringComparison.Ordinal),
            "PDF: the middle glyph is rotated by 45 degrees: " + listContent);
    }

    /// <summary>OF-B07: a mask becomes a luminance soft mask over the element's content.</summary>
    private static void AppliesMasks()
    {
        var svg = Svg("<defs><mask id=\"m\"><rect width=\"50\" height=\"50\" fill=\"#ffffff\"/></mask></defs>"
            + "<rect width=\"40\" height=\"40\" fill=\"#ff0000\" mask=\"url(#m)\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("mask", StringComparison.OrdinalIgnoreCase)),
            "PDF: a mask is applied: " + string.Join("; ", result.Warnings));
        Require(pdf.Contains("/Subtype /Form", StringComparison.Ordinal) && pdf.Contains("/Group << /S /Luminosity /CS /DeviceGray >>", StringComparison.Ordinal),
            "PDF: the mask is a luminance group in the grey colour space.");
        Require(pdf.Contains("/Type /Mask /S /Luminosity", StringComparison.Ordinal) && pdf.Contains("/SMask", StringComparison.Ordinal),
            "PDF: the mask is attached as a soft mask to a graphics state.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"/GS[0-9]+ gs"),
            "PDF: the element is painted with the mask state: " + content);

        // A mask that does not exist in the document is reported.
        using var missing = new MemoryStream();
        var missingResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<rect width=\"10\" height=\"10\" mask=\"url(#absent)\"/>")), missing)
            .GetAwaiter().GetResult();
        Require(missingResult.Warnings.Any(warning => warning.Message.Contains("mask 'url(#absent)' is not found", StringComparison.Ordinal)),
            "PDF: a mask without a target is reported.");
    }

    /// <summary>OF-B07: arrowheads — the marker of a path is placed and oriented at its ends.</summary>
    private static void DrawsMarkers()
    {
        var svg = Svg(
            "<defs><marker id=\"arrow\" markerWidth=\"4\" markerHeight=\"4\" refX=\"4\" refY=\"2\" orient=\"auto\">"
            + "<path d=\"M0,0 L4,2 L0,4 z\" fill=\"#ff0000\"/></marker></defs>"
            + "<path d=\"M10,25 L60,25\" stroke=\"#000000\" stroke-width=\"2\" marker-end=\"url(#arrow)\"/>"
            + "<line x1=\"10\" y1=\"10\" x2=\"40\" y2=\"10\" stroke=\"#000000\" marker-start=\"url(#arrow)\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("marker", StringComparison.OrdinalIgnoreCase)),
            "PDF: the markers are drawn: " + string.Join("; ", result.Warnings));
        // The end marker of the path sits at (60,25) and points along +x, so it is not rotated;
        // markerUnits="strokeWidth" (the default) scales it with the stroke width of 2.
        Require(content.Contains("2 0 0 2 60 25 cm", StringComparison.Ordinal),
            "PDF: the end marker is placed at the end of the path and scaled: " + content);
        Require(content.Contains("1 0 0 1 -4 -2 cm", StringComparison.Ordinal),
            "PDF: refX and refY move the marker onto the vertex.");
        Require(content.Contains("0 0 4 4 re W n", StringComparison.Ordinal),
            "PDF: the marker is clipped to its size.");
        // The start marker of the line runs along +x as well; auto-start-reverse is no default.
        Require(content.Contains("1 0 0 1 10 10 cm", StringComparison.Ordinal),
            "PDF: the start marker is placed at the start of the line.");

        // A missing marker and an inner-vertex marker are reported.
        using var missing = new MemoryStream();
        var missingResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<path d=\"M0,0 L10,10\" marker-end=\"url(#absent)\"/>")), missing)
            .GetAwaiter().GetResult();
        Require(missingResult.Warnings.Any(warning => warning.Message.Contains("marker 'url(#absent)' is not found", StringComparison.Ordinal)),
            "PDF: a marker without a target is reported.");

        using var mid = new MemoryStream();
        var midResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<path d=\"M0,0 L10,10 L20,0\" marker-mid=\"url(#absent)\"/>")), mid)
            .GetAwaiter().GetResult();
        Require(midResult.Warnings.Any(warning => warning.Message.Contains("marker 'url(#absent)' is not found", StringComparison.Ordinal)),
            "PDF: a missing inner-vertex marker is reported.");

        // A marker on a shape without vertices is reported.
        using var rect = new MemoryStream();
        var rectResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<rect width=\"10\" height=\"10\" marker-start=\"url(#absent)\"/>")), rect)
            .GetAwaiter().GetResult();
        Require(rectResult.Warnings.Any(warning => warning.Message.Contains("on a <rect> is not drawn", StringComparison.Ordinal)),
            "PDF: a marker on a rectangle is reported.");
    }

    /// <summary>OF-B07: inner-vertex markers and the marker shorthand are drawn.</summary>
    private static void DrawsInnerMarkers()
    {
        var svg = Svg(
            "<defs><marker id=\"arrow\" markerWidth=\"4\" markerHeight=\"4\" refX=\"4\" refY=\"2\" orient=\"auto\">"
            + "<path d=\"M0,0 L4,2 L0,4 z\" fill=\"#ff0000\"/></marker></defs>"
            + "<polyline points=\"0,0 10,0 10,10\" fill=\"none\" stroke=\"#000000\" marker-mid=\"url(#arrow)\"/>"
            + "<path d=\"M20,0 L30,0 L30,10 L40,10\" fill=\"none\" stroke=\"#000000\" marker-mid=\"url(#arrow)\"/>"
            + "<polyline points=\"50,0 60,0 60,10\" fill=\"none\" stroke=\"#000000\" marker=\"url(#arrow)\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("marker", StringComparison.OrdinalIgnoreCase)),
            "PDF: start, mid and end markers are drawn: " + string.Join("; ", result.Warnings));
        var count = System.Text.RegularExpressions.Regex.Matches(content, "0 0 4 4 re W n").Count;
        Require(count == 6, "PDF: one inner marker on the polyline, two on the path and three from the shorthand, got " + count + ".");
    }

    /// <summary>OF-B07: a gradient or pattern that references another one inherits its attributes and stops.</summary>
    private static void InheritsPaintServers()
    {
        var svg = Svg(
            "<defs><linearGradient id=\"base\"><stop offset=\"0\" stop-color=\"#ff0000\"/>"
            + "<stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient>"
            + "<linearGradient id=\"derived\" href=\"#base\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\"/>"
            + "<pattern id=\"baseTile\" width=\"10\" height=\"10\" patternUnits=\"userSpaceOnUse\">"
            + "<rect width=\"5\" height=\"5\" fill=\"#00ff00\"/></pattern>"
            + "<pattern id=\"tile\" href=\"#baseTile\"/></defs>"
            + "<rect width=\"40\" height=\"20\" fill=\"url(#derived)\"/>"
            + "<rect y=\"20\" width=\"40\" height=\"20\" fill=\"url(#tile)\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase)
                || warning.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase)),
            "PDF: inherited gradients and patterns are converted: " + string.Join("; ", result.Warnings));
        Require(pdf.Contains("/ShadingType", StringComparison.Ordinal), "PDF: the inherited gradient becomes a shading.");
        Require(pdf.Contains("/PatternType 1", StringComparison.Ordinal), "PDF: the inherited pattern becomes a tiling pattern.");
        Require(!pdf.Contains("/Resources <<>>", StringComparison.Ordinal),
            "PDF: a pattern tile uses the page resources, so it may hold an image.");
    }

    /// <summary>OF-B07: spreadMethod reflect/repeat tiles the gradient in a pattern cell.</summary>
    private static void RepeatsGradients()
    {
        byte[] Gradient(string spread) => Svg(
            "<defs><linearGradient id=\"g\" spreadMethod=\"" + spread + "\">"
            + "<stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>"
            + "<rect width=\"40\" height=\"20\" fill=\"url(#g)\"/>");

        using var reflect = new MemoryStream();
        var reflectResult = new PdfDocumentWriter().WriteAsync(Document(Gradient("reflect")), reflect).GetAwaiter().GetResult();
        var reflectPdf = Encoding.Latin1.GetString(reflect.ToArray());
        Require(!reflectResult.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase)),
            "PDF: a reflecting gradient is converted: " + string.Join("; ", reflectResult.Warnings));
        Require(reflectPdf.Contains("/PatternType 1", StringComparison.Ordinal), "PDF: the repeating gradient is a tiling pattern.");
        Require(reflectPdf.Contains("/BBox [0 0 2 1]", StringComparison.Ordinal),
            "PDF: the reflecting cell holds two gradient lengths.");
        Require(reflectPdf.Contains("/Extend [false false", StringComparison.Ordinal),
            "PDF: the shading of a repeating cell does not extend itself.");
        Require(reflectPdf.Split("/Sh").Length - 1 >= 2, "PDF: the reflecting cell paints the gradient forwards and backwards.");

        using var repeat = new MemoryStream();
        var repeatResult = new PdfDocumentWriter().WriteAsync(Document(Gradient("repeat")), repeat).GetAwaiter().GetResult();
        var repeatPdf = Encoding.Latin1.GetString(repeat.ToArray());
        Require(!repeatResult.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase)),
            "PDF: a repeating gradient is converted: " + string.Join("; ", repeatResult.Warnings));
        Require(repeatPdf.Contains("/BBox [0 0 1 1]", StringComparison.Ordinal), "PDF: the repeating cell holds one gradient length.");

        // A repeating radial gradient becomes one shading with a sampled colour function.
        using var radial = new MemoryStream();
        var radialResult = new PdfDocumentWriter().WriteAsync(
            Document(Svg("<defs><radialGradient id=\"r\" spreadMethod=\"reflect\">"
                + "<stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/>"
                + "</radialGradient></defs><rect width=\"20\" height=\"20\" fill=\"url(#r)\"/>")),
            radial).GetAwaiter().GetResult();
        Require(!radialResult.Warnings.Any(warning => warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase)),
            "PDF: a repeating radial gradient is converted: " + string.Join("; ", radialResult.Warnings));
        var radialPdf = Encoding.Latin1.GetString(radial.ToArray());
        Require(radialPdf.Contains("/ShadingType 3", StringComparison.Ordinal)
            && radialPdf.Contains("/FunctionType 0", StringComparison.Ordinal)
            && radialPdf.Contains("/Size [512]", StringComparison.Ordinal),
            "PDF: a repeating radial gradient uses a sampled colour function.");
    }

    /// <summary>
    /// OF-B07: a stroke painted with a gradient — PDF cannot stroke with a shading, so the stroked shape
    /// becomes an alpha soft mask and the gradient is laid over the shape through it.
    /// </summary>
    private static void PaintsStrokesWithPaintServers()
    {
        var svg = Svg(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/>"
            + "<stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>"
            + "<path d=\"M10,10 L60,20 L30,40 Z\" fill=\"none\" stroke=\"url(#g)\" stroke-width=\"6\"/>");
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(svg), buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(!result.Warnings.Any(warning => warning.Message.Contains("stroke", StringComparison.OrdinalIgnoreCase)
                || warning.Message.Contains("gradient", StringComparison.OrdinalIgnoreCase)),
            "PDF: a stroke with a gradient is converted: " + string.Join("; ", result.Warnings));
        Require(pdf.Contains("/S /Alpha", StringComparison.Ordinal),
            "PDF: the stroked shape becomes an alpha soft mask.");
        Require(pdf.Contains("/SMask", StringComparison.Ordinal), "PDF: the mask is attached to a graphics state.");
        Require(pdf.Contains("/ShadingType", StringComparison.Ordinal), "PDF: the gradient of the stroke is a shading.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content, @"/GS[0-9]+ gs[\s\S]*?/Sh[0-9]+ sh"),
            "PDF: the shading is painted through the mask: " + content);
        Require(content.Contains("W n", StringComparison.Ordinal),
            "PDF: the shading is limited to the shape of the stroke.");

        // A gradient on a path with a zero height has no bounding box; SVG does not render it.
        using var flat = new MemoryStream();
        var flatResult = new PdfDocumentWriter()
            .WriteAsync(Document(Svg("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/>"
                + "<stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>"
                + "<path d=\"M10,25 L60,25\" fill=\"url(#g)\"/>")), flat)
            .GetAwaiter().GetResult();
        Require(flatResult.Warnings.Any(warning => warning.Message.Contains("bounding-box", StringComparison.Ordinal)),
            "PDF: a gradient on a path without an area is reported.");
    }

    private static void MultipliesElementOpacity()
    {
        var keys = new List<string>();
        var drawing = SvgConverter.TryConvert(Svg("<g opacity=\".6\"><rect width=\"10\" height=\"10\" fill=\"#000000\""
            + " fill-opacity=\".5\" stroke=\"#ff0000\" stroke-opacity=\".5\"/></g>"),
            key =>
            {
                keys.Add(key);
                return "GS1";
            },
            out var warnings);
        Require(drawing is not null && warnings.Count == 0
            && keys.Contains("pair:0.3:0.3"),
            "SVG: opacity multiplies with the fill and stroke opacity of the children.");

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(Svg("<g opacity=\".25\"><rect width=\"10\" height=\"10\"/></g>")), buffer)
            .GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        Require(!result.Warnings.Any(warning => warning.Message.Contains("opacity", StringComparison.OrdinalIgnoreCase))
            && pdf.Contains("/ca 0.25", StringComparison.Ordinal)
            && PdfStreams.Decompress(buffer.ToArray()).Contains("/GS1 gs", StringComparison.Ordinal),
            "PDF: the opacity of an SVG group writes one transparency value and is not reported.");

        _ = SvgConverter.TryConvert(Svg("<rect width=\"10\" height=\"10\" opacity=\"huge\"/>"), _ => "GS1", out var invalid);
        Require(invalid.Any(report => report.Contains("opacity 'huge'", StringComparison.Ordinal)),
            "SVG: an invalid opacity value is reported.");
    }

    /// <summary>OF-B07: the rules of an SVG <c>style</c> element style the shapes they select.</summary>
    private static void AppliesStyleSheetRules()
    {
        var classed = SvgConverter.TryConvert(
            Svg("<style>.red{fill:#ff0000}</style><rect class=\"red\" width=\"10\" height=\"10\"/>"), _ => "GS1", out var classWarnings);
        Require(classed is not null && classWarnings.Count == 0
            && classed.Content.Contains("1 0 0 rg", StringComparison.Ordinal),
            "SVG: a class rule of a style element fills the shape it selects.");

        var specific = SvgConverter.TryConvert(Svg(
            "<defs><style>rect{fill:#00ff00}.red{fill:#ff0000}#c{fill:#0000ff}</style></defs>"
            + "<rect id=\"c\" class=\"red\" width=\"10\" height=\"10\"/><rect width=\"5\" height=\"5\"/>"), _ => "GS1", out var ruleWarnings);
        Require(specific is not null && ruleWarnings.Count == 0
            && specific.Content.Contains("0 0 1 rg", StringComparison.Ordinal)
            && specific.Content.Contains("0 1 0 rg", StringComparison.Ordinal),
            "SVG: an id rule wins over a class rule and a class rule over an element rule.");

        var attribute = SvgConverter.TryConvert(Svg(
            "<style>.red{fill:#ff0000}</style><rect class=\"red\" fill=\"#00ff00\" width=\"10\" height=\"10\"/>"
            + "<rect class=\"red\" style=\"fill:#00ff00\" width=\"5\" height=\"5\"/>"), _ => "GS1", out var attributeWarnings);
        Require(attribute is not null && attributeWarnings.Count == 0
            && attribute.Content.Contains("0 1 0 rg", StringComparison.Ordinal)
            && !attribute.Content.Contains("1 0 0 rg", StringComparison.Ordinal),
            "SVG: a presentation attribute and an inline style win over a rule of the style sheet.");

        var strokes = SvgConverter.TryConvert(Svg(
            "<style>/* a comment */ .l{stroke:black;stroke-width:2;stroke-dasharray:4 2;stroke-linecap:round}</style>"
            + "<line class=\"l\" x1=\"0\" y1=\"5\" x2=\"100\" y2=\"5\"/>"), _ => "GS1", out var strokeWarnings);
        Require(strokes is not null && strokeWarnings.Count == 0
            && strokes.Content.Contains("2 w\n", StringComparison.Ordinal)
            && strokes.Content.Contains("[4 2] 0 d", StringComparison.Ordinal)
            && strokes.Content.Contains("1 J\n", StringComparison.Ordinal),
            "SVG: a style sheet rule sets the stroke and its settings, and comments are ignored.");

        _ = SvgConverter.TryConvert(Svg(
            "<style>@media screen{rect{fill:red}}.a .b{fill:red}circle,a{fill:red}</style>"
            + "<rect width=\"10\" height=\"10\"/><circle r=\"4\"/>"), _ => "GS1", out var sheetReports);
        Require(sheetReports.Any(report => report.Contains("@media", StringComparison.Ordinal))
            && sheetReports.Any(report => report.Contains("selector '.a .b'", StringComparison.Ordinal))
            && !sheetReports.Any(report => report.Contains("circle", StringComparison.Ordinal)),
            "SVG: a conditional group and a combined selector of a style sheet are reported, a list of simple selectors is not.");

        _ = SvgConverter.TryConvert(
            Svg("<style type=\"text/scss\">.a{fill:red}</style><rect class=\"a\" width=\"10\" height=\"10\"/>"), _ => "GS1", out var typeReports);
        Require(typeReports.Any(report => report.Contains("style sheet type 'text/scss'", StringComparison.Ordinal)),
            "SVG: the style sheet of another type is reported.");

        var untouched = SvgConverter.TryConvert(Svg(
            "<rect width=\"10\" height=\"10\" fill=\"none\" stroke=\"url(#g)\" filter=\"url(#f)\" mask=\"url(#m)\" marker-end=\"url(#a)\"/>"), _ => "GS1", out var effectReports);
        Require(untouched is not null && untouched.Content.Contains("0 0 10 10 re", StringComparison.Ordinal)
            && effectReports.Any(report => report.Contains("filter 'url(#f)'", StringComparison.Ordinal))
            && effectReports.Any(report => report.Contains("mask 'url(#m)'", StringComparison.Ordinal))
            && effectReports.Any(report => report.Contains("marker 'url(#a)'", StringComparison.Ordinal))
            && effectReports.Any(report => report.Contains("stroke paint 'url(#g)'", StringComparison.Ordinal)),
            "SVG: a filter, a mask, a marker and an unavailable stroke paint server are reported and the shape is still drawn.");

        _ = SvgConverter.TryConvert(
            Svg("<rect width=\"10\" height=\"10\" fill=\"none\" stroke=\"black\" filter=\"none\" marker-end=\"none\"/>"), _ => "GS1", out var noneReports);
        Require(noneReports.Count == 0, "SVG: a filter or a marker set to none is not reported.");

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(
            Document(Svg("<style>.red{fill:#ff0000}</style><rect class=\"red\" width=\"10\" height=\"10\"/>")), buffer)
            .GetAwaiter().GetResult();
        Require(result.Warnings.Count == 0
            && PdfStreams.Decompress(buffer.ToArray()).Contains("1 0 0 rg", StringComparison.Ordinal),
            "PDF: an SVG styled by a class rule writes the fill of that rule and reports nothing.");
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
