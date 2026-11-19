using System.IO.Compression;
using Reglet;

/// <summary>
/// images and layout (phase AA): cropping (AA02) and floating images with text wrap (AA01) in
/// DOCX and ODT; PDF crops via a clipping path and reports floating images.
/// </summary>
internal static class ImageLayoutChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxImage();
            RoundTripsFloatingImagesAcrossFormats();
            KeepsTheWrapKind();
            WrapsThroughTheVisibleShape();
            WritesOdtImage();
            ReadsInheritedOdtImageStyle();
            WritesTemplateImageStyles();
            WritesHeaderImageStyles();
            RendersPdfImage();
            PositionsFloatingPdfImages();
            WrapsPdfTextLineByLine();
            WrapsComplexPdfParagraphs();
            HandlesSvg();
            RejectsInvalidValues();
            Console.WriteLine("Images (AA): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Images (AA): " + exception);
            return 1;
        }
    }

    private static PortableDocument ImageDocument()
    {
        var image = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(80),
            Height = Length.FromPoints(40),
            Crop = new ImageCrop(0.1, 0.1, 0.1, 0.1),
            Wrap = ImageWrap.Square,
            OffsetX = Length.FromPoints(6),
            OffsetY = Length.FromPoints(4),
        };

        return new PortableDocument(new DocumentMetadata(), new Section(new Block[] { image }));
    }

    private static void ReadsInheritedOdtImageStyle()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(ImageDocument(), buffer).GetAwaiter().GetResult();
        System.Xml.Linq.XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var content = System.Xml.Linq.XDocument.Parse(ReadZipEntry(buffer.ToArray(), "content.xml"));
        var child = content.Descendants(style + "style").First(element => (string?)element.Attribute(style + "family") == "graphic");
        var properties = child.Element(style + "graphic-properties")!;
        var parent = new System.Xml.Linq.XElement(style + "style",
            new System.Xml.Linq.XAttribute(style + "name", "ImportedParent"),
            new System.Xml.Linq.XAttribute(style + "family", "graphic"),
            new System.Xml.Linq.XElement(properties));
        child.AddBeforeSelf(parent);
        child.SetAttributeValue(style + "parent-style-name", "ImportedParent");
        properties.Remove();
        var package = TestPackages.ReplacePart(buffer.ToArray(), "content.xml", System.Text.Encoding.UTF8.GetBytes(content.ToString()));
        var result = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt);
        var image = result.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
        Require(image.Wrap == ImageWrap.Square && Math.Abs(image.Crop!.Value.Left - 0.1) < 0.01,
            "ODT: image wrap and crop are inherited from the parent graphic style.");
    }

    private static void WritesDocxImage()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(ImageDocument(), buffer).GetAwaiter().GetResult();

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(body.Contains("<a:srcRect", StringComparison.Ordinal), "DOCX: crop srcRect.");
        Require(body.Contains("l=\"10000\"", StringComparison.Ordinal), "DOCX: crop left 10%.");
        Require(body.Contains("<wp:anchor", StringComparison.Ordinal), "DOCX: anchor.");
        Require(body.Contains("<wp:wrapSquare", StringComparison.Ordinal), "DOCX: square wrap.");
        Require(body.Contains("<wp:positionH", StringComparison.Ordinal), "DOCX: horizontal position.");
        var image = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Docx).Document.Sections
            .SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
        Require(image.Wrap == ImageWrap.Square && Math.Abs(image.OffsetX!.Value.Points - 6) < 0.01
            && Math.Abs(image.OffsetY!.Value.Points - 4) < 0.01 && Math.Abs(image.Crop!.Value.Left - 0.1) < 0.01,
            "DOCX: image wrap, offsets and crop survive a read/write cycle.");
        System.Xml.Linq.XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        System.Xml.Linq.XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        var xml = System.Xml.Linq.XDocument.Parse(body);
        xml.Descendants(a + "srcRect").Single().SetAttributeValue("l", "10%");
        xml.Descendants(wp + "positionH").Single().SetAttributeValue("relativeFrom", "page");
        var changed = TestPackages.ReplacePart(buffer.ToArray(), "word/document.xml", System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
        var read = DocumentReader.Read(new MemoryStream(changed), DocumentFormat.Docx);
        var changedImage = read.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
        Require(Math.Abs(changedImage.Crop!.Value.Left - 0.1) < 0.01 && changedImage.OffsetX is null
            && read.Warnings.Any(warning => warning.Message.Contains("image position", StringComparison.Ordinal)),
            "DOCX: percent crops are read and unsupported anchor positions are reported.");
    }

    private static void RoundTripsFloatingImagesAcrossFormats()
    {
        foreach (var wrap in new[] { ImageWrap.Square, ImageWrap.TopAndBottom })
        {
            var image = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
            {
                Width = Length.FromPoints(80), Height = Length.FromPoints(40), Wrap = wrap,
                OffsetX = Length.FromPoints(-6), OffsetY = Length.FromPoints(-4), Crop = new ImageCrop(0.1, 0.2, 0.3, 0.1),
            };
            var document = new PortableDocument(new DocumentMetadata(), new Section(image));
            foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Docx })
            {
                using var buffer = new MemoryStream();
                DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
                document = DocumentReader.Read(new MemoryStream(buffer.ToArray()), format).Document;
                var read = document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
                Require(read.Wrap == wrap && Math.Abs(read.OffsetX!.Value.Points + 6) < 0.01
                    && Math.Abs(read.OffsetY!.Value.Points + 4) < 0.01 && Math.Abs(read.Crop!.Value.Right - 0.3) < 0.01,
                    $"{format}: {wrap} and negative offsets survive format conversion.");
            }
        }
    }

    /// <summary>
    /// OF-B06: through, behind and in front keep their meaning — in the XML of DOCX and ODT and in the
    /// PDF, where they no longer reserve space like top-and-bottom.
    /// </summary>
    private static void KeepsTheWrapKind()
    {
        var picture = TestImages.RgbPng(8, 4, 255, 0, 0);
        foreach (var wrap in new[] { ImageWrap.Through, ImageWrap.Behind, ImageWrap.InFront })
        {
            var image = new ImageBlock(picture, ImageMediaType.Png)
            {
                Width = Length.FromPoints(40), Height = Length.FromPoints(20), Wrap = wrap,
            };
            var document = new PortableDocument(new DocumentMetadata(), new Section(image));

            using var docxBuffer = new MemoryStream();
            new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
            var body = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
            Require(body.Contains(wrap == ImageWrap.Through ? "<wp:wrapThrough" : "<wp:wrapNone", StringComparison.Ordinal),
                $"DOCX: {wrap} uses {(wrap == ImageWrap.Through ? "wrapThrough" : "wrapNone")}: " + body);
            Require(body.Contains(wrap == ImageWrap.Behind ? "behindDoc=\"1\"" : "behindDoc=\"0\"", StringComparison.Ordinal),
                $"DOCX: {wrap} writes its layer.");
            Require(!body.Contains("<wp:wrapTopAndBottom", StringComparison.Ordinal), $"DOCX: {wrap} is no top-and-bottom wrap.");

            using var odtBuffer = new MemoryStream();
            new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
            var styles = ReadZipEntry(odtBuffer.ToArray(), "styles.xml") + ReadZipEntry(odtBuffer.ToArray(), "content.xml");
            Require(styles.Contains("style:wrap=\"run-through\"", StringComparison.Ordinal),
                $"ODT: {wrap} runs through the text: " + styles);
            Require(styles.Contains(
                    wrap == ImageWrap.InFront ? "style:run-through=\"foreground\"" : "style:run-through=\"background\"", StringComparison.Ordinal),
                $"ODT: {wrap} writes its layer.");

            // The PDF draws the image without moving the text.
            var section = new Section(new Block[] { image, new Paragraph(new TextRun("AFTERIMAGE")) })
            {
                PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var pdfBuffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), pdfBuffer).GetAwaiter().GetResult();
            var content = PdfStreams.Decompress(pdfBuffer.ToArray());
            var imageAt = content.IndexOf("40 0 0 20", StringComparison.Ordinal);
            var textAt = content.IndexOf("(AFTERIMAGE) Tj", StringComparison.Ordinal);
            Require(imageAt >= 0 && textAt >= 0, $"PDF: the {wrap} image and the text are drawn.");
            if (wrap == ImageWrap.InFront)
            {
                Require(imageAt > textAt, "PDF: an image in front of the text is drawn after it.");
            }
            else
            {
                Require(imageAt < textAt, $"PDF: an image {wrap} is drawn before the text.");
            }

            // The text is not pushed down: its baseline is the same as without the image.
            var withImage = BaselineOf(content, "AFTERIMAGE");
            using var plainBuffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(
                new PortableDocument(new DocumentMetadata(), section with { Blocks = [new Paragraph(new TextRun("AFTERIMAGE"))] }),
                plainBuffer).GetAwaiter().GetResult();
            Require(Math.Abs(withImage - BaselineOf(PdfStreams.Decompress(plainBuffer.ToArray()), "AFTERIMAGE")) < 0.01,
                $"PDF: {wrap} does not move the following text.");

            // The readers keep the kind. ODF has one representation for "text in front of the image",
            // so a through wrap reads back as behind there.
            foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
            {
                using var buffer = new MemoryStream();
                DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
                var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), format).Document;
                var readImage = read.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
                var expected = format == DocumentFormat.Odt && wrap == ImageWrap.Through ? ImageWrap.Behind : wrap;
                Require(readImage.Wrap == expected, $"{format}: {wrap} is read back as {readImage.Wrap}.");
            }
        }
    }

    /// <summary>The y coordinate of a drawn text line.</summary>
    private static double BaselineOf(string content, string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            content, @"[0-9.]+ ([0-9.]+) Td\n\(" + System.Text.RegularExpressions.Regex.Escape(text) + @"\) Tj");
        Require(match.Success, $"Text '{text}' is drawn.");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WritesOdtImage()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(ImageDocument(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("text:anchor-type=\"paragraph\"", StringComparison.Ordinal), "ODT: anchor type.");
        Require(content.Contains("style:wrap=\"parallel\"", StringComparison.Ordinal), "ODT: wrap.");
        Require(content.Contains("fo:clip=\"rect(", StringComparison.Ordinal), "ODT: crop clip.");
        Require(content.Contains("svg:x=\"", StringComparison.Ordinal), "ODT: offset x.");
        var xml = System.Xml.Linq.XDocument.Parse(content);
        System.Xml.Linq.XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        System.Xml.Linq.XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var frame = xml.Descendants(draw + "frame").Single();
        var name = (string?)frame.Attribute(draw + "style-name");
        var graphic = xml.Descendants(style + "style").Single(element => (string?)element.Attribute(style + "name") == name)
            .Element(style + "graphic-properties");
        Require(graphic?.Attribute(style + "wrap")?.Value == "parallel" && frame.Attribute(style + "wrap") is null,
            "ODT: wrap is written in the frame's graphic style.");
        var image = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Odt).Document.Sections
            .SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
        Require(image.Wrap == ImageWrap.Square && Math.Abs(image.OffsetX!.Value.Points - 6) < 0.01
            && Math.Abs(image.OffsetY!.Value.Points - 4) < 0.01 && Math.Abs(image.Crop!.Value.Left - 0.1) < 0.01,
            "ODT: image wrap, offsets and crop survive a read/write cycle.");
        System.Xml.Linq.XNamespace svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
        frame.SetAttributeValue(svg + "width", "-1pt");
        frame.SetAttributeValue(svg + "x", "NaNpt");
        var malformed = TestPackages.ReplacePart(buffer.ToArray(), "content.xml", System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
        var recovered = DocumentReader.Read(new MemoryStream(malformed), DocumentFormat.Odt);
        var recoveredImage = recovered.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Single();
        Require(recovered.Warnings.Any(warning => warning.Message.Contains("image length", StringComparison.Ordinal))
            && recoveredImage.Width is null && recoveredImage.OffsetX is null,
            "ODT: invalid image dimensions and offsets are reported without rejecting the image.");
    }

    private static void WritesTemplateImageStyles()
    {
        var original = (ImageBlock)ImageDocument().Sections[0].Blocks[0];
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            original, new Paragraph(new TextRun("{{Images}}")),
        }));
        using var input = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(source, input).GetAwaiter().GetResult();
        System.Xml.Linq.XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        System.Xml.Linq.XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var content = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(input.ToArray(), "content.xml")));
        var oldStyle = content.Descendants(style + "style").Single(node => (string?)node.Attribute(style + "family") == "graphic");
        oldStyle.SetAttributeValue(style + "name", "PDImage_image2_png");
        content.Descendants(draw + "frame").Single().SetAttributeValue(draw + "style-name", "PDImage_image2_png");
        var package = TestPackages.ReplacePart(input.ToArray(), "content.xml", System.Text.Encoding.UTF8.GetBytes(content.ToString()));
        var png = new ImageBlock(TestImages.RgbPng(2, 2, 0, 0, 255), ImageMediaType.Png) { Wrap = ImageWrap.Square };
        var svg = new ImageBlock(System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>"), ImageMediaType.Svg)
        {
            Wrap = ImageWrap.TopAndBottom,
        };
        var template = DocumentTemplate.FromBytes(package).Bind("Images", new Block[] { png, svg });
        using var output = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(PortableDocument.Create().Build(), output, new OdtWriteOptions { Template = template }).GetAwaiter().GetResult();
        var result = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(output.ToArray(), "content.xml")));
        var names = result.Descendants(style + "style").Where(node => (string?)node.Attribute(style + "family") == "graphic")
            .Select(node => (string?)node.Attribute(style + "name")).ToArray();
        var references = result.Descendants(draw + "frame").Select(node => (string?)node.Attribute(draw + "style-name")).ToArray();
        Require(names.Length == 3 && names.Distinct().Count() == 3 && references.All(name => names.Contains(name)),
            "ODT: template image styles stay unique and references follow renamed styles.");
        var images = DocumentReader.Read(new MemoryStream(output.ToArray()), DocumentFormat.Odt).Document.Sections
            .SelectMany(section => section.Blocks).OfType<ImageBlock>().ToArray();
        Require(images.Length == 3 && images[1].Wrap == ImageWrap.Square && images[2].Wrap == ImageWrap.TopAndBottom,
            "ODT: PNG and SVG image wrapping survives template insertion.");
    }

    private static void WritesHeaderImageStyles()
    {
        var image = (ImageBlock)ImageDocument().Sections[0].Blocks[0];
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(image),
        });
        using var output = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, output).GetAwaiter().GetResult();
        var styles = ReadZipEntry(output.ToArray(), "styles.xml");
        Require(styles.Contains("style:wrap=\"parallel\"", StringComparison.Ordinal), "ODT: header images have graphic styles in styles.xml.");
        var read = DocumentReader.Read(new MemoryStream(output.ToArray()), DocumentFormat.Odt).Document;
        var readImage = read.Sections[0].Header?.Blocks.OfType<ImageBlock>().Single();
        Require(readImage?.Wrap == ImageWrap.Square && Math.Abs(readImage.OffsetX!.Value.Points - 6) < 0.01,
            "ODT: header image styling survives reading.");
    }

    private static void RendersPdfImage()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(ImageDocument(), buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("W n", StringComparison.Ordinal), "PDF: clipping path.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("floating images", StringComparison.Ordinal)),
            "PDF: floating image is placed, not reported.");
    }

    private static void PositionsFloatingPdfImages()
    {
        foreach (var vector in new[] { false, true })
        foreach (var wrap in new[] { ImageWrap.Square, ImageWrap.TopAndBottom })
        {
            var bytes = vector
                ? System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>")
                : TestImages.RgbPng(8, 4, 255, 0, 0);
            var image = new ImageBlock(bytes, vector ? ImageMediaType.Svg : ImageMediaType.Png)
            {
                Width = Length.FromPoints(80), Height = Length.FromPoints(40), Wrap = wrap,
                OffsetX = Length.FromPoints(6), OffsetY = Length.FromPoints(4),
            };
            var section = new Section(new Block[] { image, new Paragraph(new TextRun("AFTERIMAGE")) })
            {
                PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var content = PdfStreams.Decompress(buffer.ToArray());
            Require(content.Contains(vector ? "8 0 0 -4 16 106 cm" : "80 0 0 40 16 66 cm", StringComparison.Ordinal),
                $"PDF: {(vector ? "SVG" : "raster")} float uses both offsets and the requested size.");
            Require(content.Contains("(AFTERIMAGE)", StringComparison.Ordinal), "PDF: text following a floating image remains visible.");
            if (wrap == ImageWrap.TopAndBottom)
            {
                var match = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(AFTERIMAGE\) Tj");
                Require(match.Success && double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 66,
                    "PDF: top-and-bottom wrap places the next line below the image.");
            }
        }

        var wide = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(200), Height = Length.FromPoints(40), Wrap = ImageWrap.Square,
        };
        using var wideBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            wide, new Paragraph(new TextRun("WIDEFOLLOWING")),
        })
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        }), wideBuffer).GetAwaiter().GetResult();
        var widePages = DocumentReader.ReadPdfPages(new MemoryStream(wideBuffer.ToArray()));
        Require(widePages.Count <= 2 && widePages.Any(page => page.Contains("WIDEFOLLOWING", StringComparison.Ordinal)),
            "PDF: a full-width floating image moves following text below it.");

        var picture = TestImages.RgbPng(8, 4, 255, 0, 0);
        ImageBlock Float(double offset) => new(picture, ImageMediaType.Png)
        {
            Width = Length.FromPoints(40), Height = Length.FromPoints(40), Wrap = ImageWrap.Square,
            OffsetX = Length.FromPoints(offset),
        };
        using var multipleBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            Float(0), Float(160), new Paragraph(new TextRun("BETWEEN")),
        })
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        }), multipleBuffer).GetAwaiter().GetResult();
        var multipleContent = PdfStreams.Decompress(multipleBuffer.ToArray());
        Require(System.Text.RegularExpressions.Regex.IsMatch(multipleContent, @"56 [0-9.]+ Td\n\(BETWEEN\) Tj"),
            "PDF: text avoids floating pictures on both sides.");
    }

    /// <summary>
    /// OF-B06: a through wrap reserves only the visible shape, not the transparent part of the box, so
    /// the text comes closer to the image than with a square wrap.
    /// </summary>
    private static void WrapsThroughTheVisibleShape()
    {
        // A half-transparent PNG: only the left half is opaque, so a tight wrap uses half of the width.
        var png = HalfTransparentPng(80, 40);
        double FirstLineX(ImageWrap wrap)
        {
            var image = new ImageBlock(png, ImageMediaType.Png)
            {
                Width = Length.FromPoints(80), Height = Length.FromPoints(40), Wrap = wrap,
            };
            var section = new Section(new Block[] { image, new Paragraph(new TextRun("WRAPPEDTEXT")) })
            {
                PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(160)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var content = PdfStreams.Decompress(buffer.ToArray());
            var match = System.Text.RegularExpressions.Regex.Match(content, @"([0-9.]+) [0-9.]+ Td\n\(WRAPPEDTEXT\) Tj");
            Require(match.Success, "PDF: the wrapped text is drawn.");
            return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        Require(ImageContour.From(png, ImageMediaType.Png) is not null, "the test image has a contour");
        var square = FirstLineX(ImageWrap.Square);
        var through = FirstLineX(ImageWrap.Through);
        Require(square > 80, "PDF: a square wrap uses the whole width of the image: " + square);
        Require(through < square && through > 40,
            $"PDF: a through wrap uses only the opaque half ({through} vs {square}).");

        // An opaque image has no contour: through and square wrap alike.
        var opaque = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(80), Height = Length.FromPoints(40), Wrap = ImageWrap.Through,
        };
        var opaqueSection = new Section(new Block[] { opaque, new Paragraph(new TextRun("OPAQUETEXT")) })
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(160)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var opaqueBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), opaqueSection), opaqueBuffer).GetAwaiter().GetResult();
        var opaqueMatch = System.Text.RegularExpressions.Regex.Match(
            PdfStreams.Decompress(opaqueBuffer.ToArray()), @"([0-9.]+) [0-9.]+ Td\n\(OPAQUETEXT\) Tj");
        Require(opaqueMatch.Success && Math.Abs(double.Parse(opaqueMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - square) < 0.01,
            "PDF: an opaque image wraps like a square one.");
    }

    /// <summary>A PNG whose right half is transparent.</summary>
    private static byte[] HalfTransparentPng(int width, int height)
    {
        using var raw = new MemoryStream();
        var row = new byte[1 + (width * 4)];
        for (var y = 0; y < height; y++)
        {
            row[0] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var offset = 1 + (x * 4);
                row[offset] = 255;
                row[offset + 1] = 0;
                row[offset + 2] = 0;
                row[offset + 3] = x < width / 2 ? (byte)255 : (byte)0;
            }

            raw.Write(row);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            png.Write(length);
            var name = System.Text.Encoding.ASCII.GetBytes(type);
            png.Write(name);
            png.Write(data);
            var crc = Crc32(name, data);
            var checksum = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
            png.Write(checksum);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type RGBA
        Chunk("IHDR", header);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();
    }

    private static uint Crc32(byte[] name, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void WrapsPdfTextLineByLine()
    {
        foreach (var offsetY in new[] { 0.0, 50.0 })
        foreach (var background in new[] { false, true })
        {
            var image = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
            {
                Width = Length.FromPoints(80), Height = Length.FromPoints(30), Wrap = ImageWrap.Square,
                OffsetY = Length.FromPoints(offsetY),
            };
            var words = Enumerable.Range(0, 45).Select(index => "word" + index.ToString("D2")).ToArray();
            var paragraph = new Paragraph(new TextRun(string.Join(" ", words)))
            {
                Style = ParagraphStyle.Default with { Background = background ? Color.FromHex("FFFF00") : null },
            };
            var section = new Section(new Block[] { image, paragraph })
            {
                PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(180)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var content = PdfStreams.Decompress(buffer.ToArray());
            var positions = System.Text.RegularExpressions.Regex.Matches(content, @"([0-9.]+) ([0-9.]+) Td\n\(word[0-9]{2}\) Tj")
                .Select(match => (X: double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Y: double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                .GroupBy(position => position.Y).Select(group => group.Min(position => position.X)).ToArray();
            Require(positions.Contains(96) && positions.Contains(10)
                && (offsetY == 0 ? positions[0] == 96 : positions[0] == 10),
                "PDF: each line wraps only while it intersects the floating image: "
                + string.Join(", ", positions.Select(position => $"{position:0.##}")));
            var text = string.Join(" ", DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray())));
            Require(words.All(word => System.Text.RegularExpressions.Regex.Matches(text, $@"\b{word}\b").Count == 1),
                "PDF: line-by-line float wrapping preserves all words.");
            if (background) Require(content.Contains("1 1 0 rg", StringComparison.Ordinal), "PDF: wrapping keeps paragraph shading.");
        }

        var floatImage = new ImageBlock(TestImages.RgbPng(8, 4, 255, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(80), Height = Length.FromPoints(20), Wrap = ImageWrap.Square,
        };
        var next = new Paragraph(new TextRun("AFTERPAGE")) { Style = ParagraphStyle.Default with { SpaceBefore = Length.FromPoints(30) } };
        var blocks = Enumerable.Range(0, 4).Select(_ => (Block)new Paragraph(new TextRun("Filler"))).Append(floatImage).Append(next).ToArray();
        using var pageBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(blocks)
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        }), pageBuffer).GetAwaiter().GetResult();
        var pageContent = PdfStreams.Decompress(pageBuffer.ToArray());
        var pageTexts = DocumentReader.ReadPdfPages(new MemoryStream(pageBuffer.ToArray()));
        Require(pageTexts.Count > 1 && System.Text.RegularExpressions.Regex.IsMatch(pageContent, @"10 [0-9.]+ Td\n\(AFTERPAGE\) Tj"),
            "PDF: a paragraph moved to the next page no longer reserves space for the previous page's float.");
    }

    /// <summary>
    /// AA01: paragraphs that are not plain text — a list entry with its marker, a bordered
    /// paragraph, a keep-together paragraph and a paragraph with a note reference — take the
    /// width their own line leaves beside a floating image, and a paragraph that breaks in the
    /// middle flows over the full width again on the following page.
    /// </summary>
    private static void WrapsComplexPdfParagraphs()
    {
        var picture = TestImages.RgbPng(8, 4, 255, 0, 0);
        var words = Enumerable.Range(0, 40).Select(index => "w" + index.ToString("D2")).ToArray();
        var text = string.Join(" ", words);

        ImageBlock Float() => new(picture, ImageMediaType.Png)
        {
            Width = Length.FromPoints(80),
            Height = Length.FromPoints(30),
            Wrap = ImageWrap.Square,
        };

        Section Page(params Block[] blocks) => new(blocks)
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(180)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };

        var entry = TestLists.List(ListKind.Bullet, [new ListItem(new Paragraph(new TextRun(text)))]);
        var cases = new (string Name, PortableDocument Document, double PlainStart, double NarrowStart)[]
        {
            ("list entry", TestLists.Document(new DocumentMetadata(), Page(Float(), entry)), 46, 132),
            (
                "bordered paragraph",
                new PortableDocument(new DocumentMetadata(), Page(Float(), new Paragraph(new TextRun(text))
                {
                    Style = ParagraphStyle.Default with { Borders = ParagraphBorders.All(new ParagraphBorder()) },
                })),
                10,
                96),
            (
                "keep-together paragraph",
                new PortableDocument(new DocumentMetadata(), Page(Float(), new Paragraph(new TextRun(text))
                {
                    Style = ParagraphStyle.Default with { KeepTogether = true },
                })),
                10,
                96),
            (
                "paragraph with a note",
                new PortableDocument(new DocumentMetadata(), Page(Float(), new Paragraph(
                [
                    new TextRun("REF "),
                    new Footnote(new Paragraph(new TextRun("FNTEXT"))),
                    new TextRun(text),
                ]))),
                10,
                96),
        };

        foreach (var (name, document, plainStart, narrowStart) in cases)
        {
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            var pages = PdfStreams.DecompressPages(buffer.ToArray());
            Require(pages.Count == 1, $"PDF: the {name} beside a floating image stays on one page.");

            var positions = System.Text.RegularExpressions.Regex.Matches(pages[0], @"([0-9.]+) ([0-9.]+) Td")
                .Select(match => (
                    X: double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Y: double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();

            // The image covers x 10..90 at the top of the page (y 140..170): no text piece and
            // no list marker may start inside it.
            Require(positions.All(position => position.Y is < 140 or > 170 || position.X >= 96),
                $"PDF: the {name} wraps line by line around the floating image.");
            var starts = positions.Select(position => position.X).Distinct().ToArray();
            Require(starts.Contains(narrowStart) && starts.Contains(plainStart),
                $"PDF: the {name} narrows single lines and uses the full width below the image.");
            Require(System.Text.RegularExpressions.Regex.Matches(
                    string.Join(" ", DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()))), "w[0-9]{2}").Count == words.Length,
                $"PDF: the {name} keeps every word while wrapping.");
        }

        // A paragraph that breaks while the image still reaches into it: the image stays on its
        // page, so the lines on the following page no longer keep its margins.
        var floatImage = new ImageBlock(picture, ImageMediaType.Png)
        {
            Width = Length.FromPoints(80),
            Height = Length.FromPoints(20),
            Wrap = ImageWrap.Square,
        };
        var longText = string.Join(" ", Enumerable.Range(0, 60).Select(index => "x" + index.ToString("D2")));
        using var brokeBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("FILLER")),
            floatImage,
            new Paragraph(new TextRun(longText)),
        })
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        }), brokeBuffer).GetAwaiter().GetResult();
        var brokePages = PdfStreams.DecompressPages(brokeBuffer.ToArray());
        Require(brokePages.Count > 1, "PDF: the paragraph breaks across the floating image.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(brokePages[1], @"\n10 [0-9.]+ Td\n")
            && !System.Text.RegularExpressions.Regex.IsMatch(brokePages[1], @"\n96 [0-9.]+ Td\n"),
            "PDF: the lines after a mid-paragraph page break flow over the full width again.");
        Require(System.Text.RegularExpressions.Regex.Matches(string.Join(" ", DocumentReader.ReadPdfPages(new MemoryStream(brokeBuffer.ToArray()))), "x[0-9]{2}").Count == 60,
            "PDF: a paragraph breaking around a floating image keeps every word.");
    }

    private static PortableDocument SvgDocument()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>");
        var image = new ImageBlock(svg, ImageMediaType.Svg)
        {
            Width = Length.FromPoints(20),
            Height = Length.FromPoints(20),
        };

        return new PortableDocument(new DocumentMetadata(), new Section(new Block[] { image }));
    }

    private static void HandlesSvg()
    {
        // ODT supports SVG natively.
        using (var buffer = new MemoryStream())
        {
            new OdtDocumentWriter().WriteAsync(SvgDocument(), buffer).GetAwaiter().GetResult();
            var package = buffer.ToArray();
            Require(
                ReadZipEntry(package, "META-INF/manifest.xml").Contains("image/svg+xml", StringComparison.Ordinal),
                "ODT: SVG media type in the manifest.");
            Require(ReadZipEntry(package, "content.xml").Contains("draw:image", StringComparison.Ordinal), "ODT: SVG image.");
        }

        // DOCX and PDF embed or draw the SVG; the details are checked in SvgChecks.
        using (var buffer = new MemoryStream())
        {
            var result = new DocxDocumentWriter().WriteAsync(SvgDocument(), buffer).GetAwaiter().GetResult();
            Require(result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)), "DOCX: the missing raster fallback is reported.");
        }

        using (var buffer = new MemoryStream())
        {
            var result = new PdfDocumentWriter().WriteAsync(SvgDocument(), buffer).GetAwaiter().GetResult();
            Require(!result.Warnings.Any(warning => warning.Message.Contains("SVG", StringComparison.Ordinal)), "PDF: the SVG is drawn.");
        }
    }

    private static void RejectsInvalidValues()
    {
        var png = TestImages.RgbPng(4, 2, 0, 0, 255);
        Throws<ArgumentOutOfRangeException>(() => new ImageBlock(png, ImageMediaType.Png) { Crop = new ImageCrop(0.6, 0, 0, 0) });
        Throws<ArgumentOutOfRangeException>(() => new ImageBlock(png, ImageMediaType.Png) { Crop = new ImageCrop(0.5, 0, 0.5, 0) });
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
