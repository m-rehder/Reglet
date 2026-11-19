using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Reglet;

internal static class OdtWriterChecks
{
    internal static int Run()
    {
        try
        {
            WritesMimeType();
            PassesSubsetValidator();
            WritesManifest();
            WritesContentSkeleton();
            WritesStylesSkeleton();
            WritesMeta();
            WritesSettings();
            WritesExpectedNamespaces();
            WritesSectionLayouts();
            WritesParagraphs();
            WritesParagraphFormatting();
            WritesAutoHyphenation();
            PreservesCrLfAsOneBreak();
            WritesHeadings();
            WritesTextSpans();
            WritesLists();
            WritesTables();
            DeduplicatesRoundedColumnStyles();
            GivesTablesDistinctNames();
            WritesImages();
            WritesImageReferences();
            WritesImageAltText();
            WritesTableBordersAndPadding();
            WritesPageBreaks();
            Console.WriteLine("ODT writer: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ODT-Writer: " + exception);
            return 1;
        }
    }

    private static void WritesMimeType()
    {
        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());

        Require(bytes.Length >= 30, "Package is too small.");
        Require(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == 0x04034B50,
            "First entry has no local file header.");

        var method = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18, 4));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        var name = Encoding.ASCII.GetString(bytes, 30, nameLength);

        Require(name == "mimetype", $"First entry is '{name}', expected 'mimetype'.");
        Require(method == 0, "mimetype is not uncompressed (method 0).");

        var dataStart = 30 + nameLength + extraLength;
        var content = Encoding.ASCII.GetString(bytes, dataStart, (int)compressedSize);
        Require(
            content == "application/vnd.oasis.opendocument.text",
            $"mimetype content is '{content}'.");
        Require(compressedSize == 39, "mimetype contains additional bytes (no line break).");
    }

    private static void PassesSubsetValidator()
    {
        var document = PortableDocument.Create()
            .Metadata(title: "Title", author: "Autor")
            .Heading("Heading", level: 1)
            .Paragraph(p => p.Text("Text ").Bold("fett"))
            .BulletList("a", "b")
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .PageBreak()
            .Paragraph("Ende")
            .Build();

        var bytes = WritePackage(document);
        var errors = OdtPackageValidator.Validate(bytes);
        Require(errors.Count == 0, "ODT besteht den Subset-Validator: " + string.Join("; ", errors));
    }

    private static void WritesManifest()
    {
        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("META-INF/manifest.xml"), "Manifest present.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(manifest.Contains("manifest:full-path=\"/\"", StringComparison.Ordinal), "Root entry present.");
        Require(
            manifest.Contains("application/vnd.oasis.opendocument.text", StringComparison.Ordinal),
            "ODT media type in the manifest.");
        Require(manifest.Contains("manifest:version=\"1.3\"", StringComparison.Ordinal), "Manifest-Version 1.3.");
    }

    private static void WritesContentSkeleton()
    {
        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("content.xml"), "content.xml present.");

        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("office:document-content", StringComparison.Ordinal), "Root element present.");
        Require(content.Contains("office:version=\"1.3\"", StringComparison.Ordinal), "office:version 1.3 present.");
        Require(content.Contains("office:automatic-styles", StringComparison.Ordinal), "Automatische Stile vorhanden.");
        Require(content.Contains("office:text", StringComparison.Ordinal), "office:text present.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(
            manifest.Contains("manifest:full-path=\"content.xml\"", StringComparison.Ordinal),
            "Manifest listet content.xml.");
    }

    private static void WritesStylesSkeleton()
    {
        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("styles.xml"), "styles.xml present.");

        var styles = ReadEntry(zip, "styles.xml");
        Require(styles.Contains("office:document-styles", StringComparison.Ordinal), "Root element present.");
        Require(styles.Contains("office:styles", StringComparison.Ordinal), "Document styles present.");
        Require(styles.Contains("style:default-style", StringComparison.Ordinal), "Default style present.");
        Require(styles.Contains("style:page-layout", StringComparison.Ordinal), "Page layout present.");
        Require(styles.Contains("style:master-page", StringComparison.Ordinal), "Master page present.");
        Require(styles.Contains("fo:page-width", StringComparison.Ordinal), "Page width set.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(
            manifest.Contains("manifest:full-path=\"styles.xml\"", StringComparison.Ordinal),
            "Manifest listet styles.xml.");
    }

    private static void WritesMeta()
    {
        var document = PortableDocument.Create()
            .Metadata(
                title: "Title",
                author: "Autor",
                subject: "Betreff",
                keywords: new[] { "a", "b" },
                language: "de-DE",
                created: new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero))
            .Paragraph("x")
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var meta = ReadEntry(zip, "meta.xml");
        Require(meta.Contains("office:document-meta", StringComparison.Ordinal), "Root element present.");
        Require(meta.Contains("Title", StringComparison.Ordinal), "Title present.");
        Require(meta.Contains("Autor", StringComparison.Ordinal), "Author present.");
        Require(meta.Contains("Betreff", StringComparison.Ordinal), "Subject present.");
        Require(meta.Contains("de-DE", StringComparison.Ordinal), "Language present.");
        Require(meta.Contains("2026-09-24T00:00:00Z", StringComparison.Ordinal), "Timestamp present.");
        Require(meta.Split("<meta:keyword", StringSplitOptions.None).Length - 1 == 2, "Two keywords present.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(
            manifest.Contains("manifest:full-path=\"meta.xml\"", StringComparison.Ordinal),
            "Manifest listet meta.xml.");
    }

    private static void WritesSettings()
    {
        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var settings = ReadEntry(zip, "settings.xml");
        Require(settings.Contains("office:document-settings", StringComparison.Ordinal), "Root element present.");
        Require(settings.Contains("ViewAreaTop", StringComparison.Ordinal), "ViewAreaTop present.");
        Require(settings.Contains("ZoomValue", StringComparison.Ordinal), "ZoomValue present.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(
            manifest.Contains("manifest:full-path=\"settings.xml\"", StringComparison.Ordinal),
            "Manifest listet settings.xml.");
    }

    private static void WritesExpectedNamespaces()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:office:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:text:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:table:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:style:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0",
            "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0",
            "http://www.w3.org/1999/xlink",
            "urn:oasis:names:tc:opendocument:xmlns:meta:1.0",
            "http://purl.org/dc/elements/1.1/",
            "urn:oasis:names:tc:opendocument:xmlns:config:1.0",
        };

        var bytes = WritePackage(PortableDocument.Create().Paragraph("x").Build());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                RequireNamespace(reader.NamespaceURI, expected, entry.FullName);

                for (var i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    RequireNamespace(reader.NamespaceURI, expected, entry.FullName);
                }

                reader.MoveToElement();
            }
        }
    }

    private static void RequireNamespace(string namespaceUri, HashSet<string> expected, string partName)
    {
        if (string.IsNullOrEmpty(namespaceUri)
            || namespaceUri == "http://www.w3.org/XML/1998/namespace"
            || namespaceUri == "http://www.w3.org/2000/xmlns/")
        {
            return;
        }

        Require(expected.Contains(namespaceUri), $"Unerwarteter Namespace in {partName}: {namespaceUri}");
    }

    private static void WritesSectionLayouts()
    {
        var first = new Section(new Paragraph(new TextRun("One")))
        {
            PageSize = PageSize.Letter,
            Orientation = PageOrientation.Landscape,
        };
        var second = new Section(new Paragraph(new TextRun("Two")))
        {
            PageSize = PageSize.A4,
        };
        var document = new PortableDocument(new DocumentMetadata(), new[] { first, second });

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var styles = ReadEntry(zip, "styles.xml");
        Require(CountOccurrences(styles, "<style:page-layout ") == 2, "Exactly two page layouts.");
        Require(styles.Contains("style:name=\"pm1\"", StringComparison.Ordinal), "First page layout present.");
        Require(styles.Contains("style:name=\"pm2\"", StringComparison.Ordinal), "Second page layout present.");
        Require(styles.Contains("style:name=\"Standard\"", StringComparison.Ordinal), "First master page defined.");
        Require(styles.Contains("style:name=\"Standard2\"", StringComparison.Ordinal), "Second master page defined.");
        Require(styles.Contains("fo:page-width=\"792pt\"", StringComparison.Ordinal), "Landscape width (Letter) preserved.");

        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("text:style-name=\"Sect1\"", StringComparison.Ordinal), "Section 1 references its style.");
        Require(content.Contains("text:style-name=\"Sect2\"", StringComparison.Ordinal), "Section 2 references its style.");
        Require(content.Contains("style:name=\"SectBreak2\"", StringComparison.Ordinal), "Break style for section 2 defined.");
        Require(content.Contains("style:master-page-name=\"Standard2\"", StringComparison.Ordinal), "Second master page assigned.");
        Require(content.Contains("fo:break-before=\"page\"", StringComparison.Ordinal), "Break before section 2 present.");
        // The first paragraph of section 2 carries the break and master page itself; an own
        // empty break paragraph would otherwise stand as a blank line at the top of the new page.
        var firstOfSecond = System.Text.RegularExpressions.Regex.Match(content, "<text:p text:style-name=\"(P[0-9]+)\">Two</text:p>");
        Require(firstOfSecond.Success, "Section 2 begins with its own paragraph style.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content,
                $"style:name=\"{firstOfSecond.Groups[1].Value}\" style:family=\"paragraph\" style:master-page-name=\"Standard2\"[^>]*><style:paragraph-properties fo:break-before=\"page\""),
            "Section 2 starts with a break and a second master page.");
        Require(!content.Contains("text:style-name=\"SectBreak2\"", StringComparison.Ordinal), "No empty break paragraph.");

        var styleNamespace = XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:style:1.0");
        var contentXml = XDocument.Parse(content);
        var breakStyle = contentXml.Descendants(styleNamespace + "style")
            .Single(element => (string?)element.Attribute(styleNamespace + "name") == "SectBreak2");
        Require((string?)breakStyle.Attribute(styleNamespace + "master-page-name") == "Standard2",
            "Master page is assigned directly to the paragraph style.");
        Require(breakStyle.Element(styleNamespace + "paragraph-properties")?.Attribute(styleNamespace + "master-page-name") is null,
            "Paragraph properties contain no invalid master attribute.");

        var stylesXml = XDocument.Parse(styles);
        Require(!stylesXml.Descendants(styleNamespace + "section-properties")
            .Any(element => element.Attribute(styleNamespace + "master-page-name") is not null),
            "Section properties contain no invalid master attribute.");
    }

    private static void WritesParagraphs()
    {
        var document = PortableDocument.Create()
            .Paragraph("a  b")
            .Paragraph("x\ty")
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(content.Contains("<text:p", StringComparison.Ordinal), "Paragraph present.");
        Require(content.Contains("text:c=\"2\"", StringComparison.Ordinal), "Double space as text:s with c=2.");
        Require(content.Contains("<text:tab", StringComparison.Ordinal), "Tab as text:tab.");
        Require(content.Contains("a", StringComparison.Ordinal), "Text content present.");
    }

    private static void WritesParagraphFormatting()
    {
        var style = ParagraphStyle.Default with
        {
            Alignment = TextAlignment.Center,
            SpaceBefore = Length.FromPoints(10),
            SpaceAfter = Length.FromPoints(5),
            LeftIndent = Length.FromPoints(12),
            RightIndent = Length.FromPoints(8),
            FirstLineIndent = Length.FromPoints(-3),
        };
        var document = new PortableDocument(new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("One")) { Style = style },
                new Paragraph(new TextRun("Two")) { Style = style },
            }));

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("style:name=\"P1\"", StringComparison.Ordinal), "Automatic paragraph style present.");
        Require(content.Contains("fo:text-align=\"center\"", StringComparison.Ordinal), "Centering is rendered.");
        Require(content.Contains("fo:margin-top=\"10pt\"", StringComparison.Ordinal), "Space before is rendered.");
        Require(content.Contains("fo:margin-bottom=\"5pt\"", StringComparison.Ordinal), "Space after is rendered.");
        Require(content.Contains("fo:margin-left=\"12pt\"", StringComparison.Ordinal), "Left indent is rendered.");
        Require(content.Contains("fo:margin-right=\"8pt\"", StringComparison.Ordinal), "Right indent is rendered.");
        Require(content.Contains("fo:text-indent=\"-3pt\"", StringComparison.Ordinal), "First-line indent is rendered.");
        Require(CountOccurrences(content, "text:style-name=\"P1\"") == 2, "Equal paragraph styles are deduplicated.");
    }

    private static void WritesAutoHyphenation()
    {
        var document = PortableDocument.Create().Paragraph("Hyphenation").Build();
        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(document, buffer, new OdtWriteOptions { AutoHyphenation = true })
            .GetAwaiter().GetResult();

        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        Require(ReadEntry(zip, "styles.xml").Contains("fo:hyphenate=\"true\"", StringComparison.Ordinal),
            "The generated default style enables hyphenation.");

        // A source package with preserved styles is patched instead of regenerated.
        var source = WritePackage(document);
        using var input = new MemoryStream(source);
        var read = new OdtDocumentReader().Read(input).Document;
        using var rewritten = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(read, rewritten, new OdtWriteOptions { AutoHyphenation = true })
            .GetAwaiter().GetResult();

        using var rewrittenZip = new ZipArchive(new MemoryStream(rewritten.ToArray()), ZipArchiveMode.Read);
        Require(ReadEntry(rewrittenZip, "styles.xml").Contains("fo:hyphenate=\"true\"", StringComparison.Ordinal),
            "The adopted default style enables hyphenation.");
    }

    private static void PreservesCrLfAsOneBreak()
    {
        var document = PortableDocument.Create().Paragraph("before\r\nafter").Build();
        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(CountOccurrences(content, "<text:line-break") == 1, "CRLF produces exactly one line break.");
    }

    private static void WritesHeadings()
    {
        var document = PortableDocument.Create().Heading("Title", level: 2).Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("<text:h", StringComparison.Ordinal), "text:h present.");
        Require(content.Contains("text:outline-level=\"2\"", StringComparison.Ordinal), "outline-level 2 present.");
        Require(content.Contains("text:style-name=\"Heading2\"", StringComparison.Ordinal), "Heading2-Stilreferenz vorhanden.");

        var styles = ReadEntry(zip, "styles.xml");
        Require(styles.Contains("style:name=\"Heading2\"", StringComparison.Ordinal), "Heading2 style defined.");
        Require(styles.Contains("fo:font-weight", StringComparison.Ordinal), "Fett-Definition vorhanden.");
    }

    private static void WritesTextSpans()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p
                .Span("a", bold: true)
                .Span("b", bold: true)
                .Span("c", italic: true))
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(CountOccurrences(content, "style:family=\"text\"") == 2, "Exactly two deduplicated text styles.");
        Require(CountOccurrences(content, "<text:span") == 3, "Three text spans.");
        Require(content.Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal), "Fett-Definition vorhanden.");
        Require(content.Contains("fo:font-style=\"italic\"", StringComparison.Ordinal), "Kursiv-Definition vorhanden.");
    }

    private static void WritesLists()
    {
        var document = PortableDocument.Create()
            .BulletList("Windows", "macOS")
            .NumberedList("One", "Two")
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(content.Contains("<text:list", StringComparison.Ordinal), "text:list present.");
        // OF-B01: one list style per numbering definition, and every list references one of them.
        var styles = System.Text.RegularExpressions.Regex
            .Matches(content, "text:list-style style:name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
        var references = System.Text.RegularExpressions.Regex
            .Matches(content, "<text:list [^>]*text:style-name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Require(styles.Count == 2 && styles.Distinct().Count() == 2, "One list style per numbering definition: " + string.Join(", ", styles));
        Require(references.Count == 2 && references.All(styles.Contains), "Every list references its definition: " + string.Join(", ", references));
        Require(CountOccurrences(content, "<text:list-item") == 4, "Four list items.");
        Require(content.Contains("list-level-style-bullet", StringComparison.Ordinal), "Bullet style defined.");
        Require(content.Contains("list-level-style-number", StringComparison.Ordinal), "Nummerierungsstil definiert.");
    }

    private static void WritesTables()
    {
        var document = PortableDocument.Create()
            .Table(t => t.Header("Kennzahl", "Wert").Row("Documents", "42").ColumnWidths(Length.FromCentimeters(3), Length.FromCentimeters(5)))
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(content.Contains("<table:table", StringComparison.Ordinal), "table:table present.");
        Require(content.Contains("<table:table-header-rows", StringComparison.Ordinal), "Header rows present.");
        Require(content.Contains("<table:table-row", StringComparison.Ordinal), "Rows present.");
        Require(content.Contains("<table:table-cell", StringComparison.Ordinal), "Cells present.");
        Require(content.Contains("<text:p", StringComparison.Ordinal), "Paragraph present in the cell.");
        Require(content.Contains("style:family=\"table-column\"", StringComparison.Ordinal), "Column style defined.");
        Require(content.Contains("Kennzahl", StringComparison.Ordinal), "Cell content present.");
    }

    private static void DeduplicatesRoundedColumnStyles()
    {
        var table = new TableBlock(new[] { new TableRow(new TableCell("A"), new TableCell("B")) })
        {
            ColumnWidths = new[] { Length.FromPoints(100), Length.FromPoints(100.01) },
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));
        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(CountOccurrences(content, "style:family=\"table-column\"") == 1,
            "Columns rounded to the same ODT width use only one style.");
    }

    private static void GivesTablesDistinctNames()
    {
        var document = PortableDocument.Create()
            .Table(t => t.Row("A"))
            .Table(t => t.Row("B"))
            .Build();
        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");
        Require(content.Contains("table:name=\"Table1\"", StringComparison.Ordinal), "First table name present.");
        Require(content.Contains("table:name=\"Table2\"", StringComparison.Ordinal), "Second table name is unique.");
    }

    private static void WritesImages()
    {
        var png = CreatePng(96, 48);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("Pictures/image1.png"), "Image lies under Pictures/.");
        Require(ReadBinaryEntry(zip, "Pictures/image1.png").SequenceEqual(png), "Image bytes unchanged.");

        var manifest = ReadEntry(zip, "META-INF/manifest.xml");
        Require(
            manifest.Contains("manifest:full-path=\"Pictures/image1.png\"", StringComparison.Ordinal),
            "Manifest lists the image.");
        Require(manifest.Contains("image/png", StringComparison.Ordinal), "Media type image/png in the manifest.");
    }

    private static void WritesImageReferences()
    {
        var png = CreatePng(96, 48);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png, altText: "Image").Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(content.Contains("<draw:frame", StringComparison.Ordinal), "draw:frame present.");
        Require(content.Contains("<draw:image", StringComparison.Ordinal), "draw:image present.");
        Require(
            content.Contains("xlink:href=\"Pictures/image1.png\"", StringComparison.Ordinal),
            "Relative Paketpfad-Referenz vorhanden.");
        Require(content.Contains("svg:width=", StringComparison.Ordinal), "Frame width set.");
        Require(content.Contains("svg:height=", StringComparison.Ordinal), "Frame height set.");
    }

    /// <summary>N04: alternative text lands as svg:title and svg:desc on the draw:frame.</summary>
    private static void WritesImageAltText()
    {
        var png = CreatePng(96, 48);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png, altText: "Diagramm").Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        Require(content.Contains("<svg:title>Diagramm</svg:title>", StringComparison.Ordinal), "svg:title present.");
        Require(content.Contains("<svg:desc>Diagramm</svg:desc>", StringComparison.Ordinal), "svg:desc present.");
        Require(
            content.IndexOf("</draw:image>", StringComparison.Ordinal)
                < content.IndexOf("<svg:title>", StringComparison.Ordinal),
            "svg:title stands after draw:image (ODF schema).");

        // Without alternative text no empty title element is produced.
        var withoutAlt = WritePackage(PortableDocument.Create().Image(png, ImageMediaType.Png).Build());
        using var withoutZip = new ZipArchive(new MemoryStream(withoutAlt), ZipArchiveMode.Read);
        Require(
            !ReadEntry(withoutZip, "content.xml").Contains("svg:title", StringComparison.Ordinal),
            "Without alternative text no svg:title.");
    }

    /// <summary>N01/N02: ODT cells get borders and cell padding like DOCX (styles by position).</summary>
    private static void WritesTableBordersAndPadding()
    {
        var document = PortableDocument.Create()
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        foreach (var name in new[] { "Cell", "CellR", "CellU", "CellRU" })
        {
            Require(
                content.Contains($"style:name=\"{name}\" style:family=\"table-cell\"", StringComparison.Ordinal),
                $"Cell style {name} is defined.");
        }

        Require(
            content.Contains("fo:border-left=\"0.5pt solid #000000\"", StringComparison.Ordinal)
                && content.Contains("fo:border-top=\"0.5pt solid #000000\"", StringComparison.Ordinal),
            "Cells get a border on the left/top (single 0.5 pt).");
        Require(
            content.Contains("fo:border-right=\"none\"", StringComparison.Ordinal),
            "Inner right borders remain open (no double lines).");
        Require(
            content.Contains("fo:padding-left=\"5.4pt\"", StringComparison.Ordinal)
                && content.Contains("fo:padding-right=\"5.4pt\"", StringComparison.Ordinal),
            "Cells get the horizontal cell padding from Typography.");
        Require(
            CountOccurrences(content, "table:style-name=\"Cell\"") >= 1,
            "Cells reference their positional cell styles.");
        Require(
            content.Contains("table:style-name=\"CellR\"", StringComparison.Ordinal),
            "Last column uses the right border style.");

        // Without a table no cell styles are produced.
        var noTable = WritePackage(PortableDocument.Create().Paragraph("nothing").Build());
        using var noZip = new ZipArchive(new MemoryStream(noTable), ZipArchiveMode.Read);
        Require(
            !ReadEntry(noZip, "content.xml").Contains("style:family=\"table-cell\"", StringComparison.Ordinal),
            "Without a table no cell style.");
    }

    private static void WritesPageBreaks()
    {
        var document = PortableDocument.Create()
            .Paragraph("before")
            .PageBreak()
            .Paragraph("after")
            .Build();

        var bytes = WritePackage(document);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var content = ReadEntry(zip, "content.xml");

        // The following paragraph carries the break; an empty break paragraph would otherwise stand as
        // blank line at the top of the new page.
        var after = System.Text.RegularExpressions.Regex.Match(content, "<text:p text:style-name=\"(P[0-9]+)\">after</text:p>");
        Require(after.Success, "Following paragraph references its own style.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(content,
                $"style:name=\"{after.Groups[1].Value}\" style:family=\"paragraph\"[^>]*><style:paragraph-properties fo:break-before=\"page\""),
            "The following paragraph's style breaks before it.");
        Require(!content.Contains("text:style-name=\"PageBreak\"", StringComparison.Ordinal), "No empty break paragraph.");

        // Without a following block the empty break paragraph remains (as Word produces an empty page).
        var trailing = PortableDocument.Create().Paragraph("before").PageBreak().Build();
        using var trailingZip = new ZipArchive(new MemoryStream(WritePackage(trailing)), ZipArchiveMode.Read);
        var trailingContent = ReadEntry(trailingZip, "content.xml");
        Require(trailingContent.Contains("style:name=\"PageBreak\"", StringComparison.Ordinal), "Page break style defined.");
        Require(trailingContent.Contains("<text:p text:style-name=\"PageBreak\" />", StringComparison.Ordinal),
            "Break at the end as an empty paragraph.");
        Require(CountOccurrences(content, "style:name=\"PageBreak\"") == 1, "Style defined only once.");
    }

    private static byte[] CreatePng(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(bytes, "IHDR", ihdr);
        AppendChunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(Encoding.ASCII.GetBytes(type));
        target.AddRange(data);
        target.AddRange(new byte[4]);
    }

    private static byte[] ReadBinaryEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static int CountOccurrences(string text, string needle)
        => text.Split(needle, StringSplitOptions.None).Length - 1;

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static byte[] WritePackage(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
