using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using Reglet;

internal static class DocxWriterChecks
{
    internal static int Run()
    {
        try
        {
            WritesRequiredParts();
            PassesSubsetValidator();
            WritesCoreProperties();
            WritesSectionProperties();
            WritesAllSectionProperties();
            WritesStandardStyles();
            WritesParagraphs();
            WritesTextRuns();
            PreservesWhitespace();
            WritesNumbering();
            WritesLists();
            KeepsSeparateLists();
            ContinuesSharedNumbering();
            WritesTables();
            WritesImages();
            WritesImageExtents();
            WritesPageBreaks();
            WritesHeaderFooter();
            Console.WriteLine("DOCX writer: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DOCX-Writer: " + exception);
            return 1;
        }
    }

    private static void WritesRequiredParts()
    {
        var document = PortableDocument.Create().Paragraph("Hallo").Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(buffer.Length > 0, "The writer wrote no bytes.");

        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var part in new[]
                 {
                     "[Content_Types].xml",
                     "_rels/.rels",
                     "word/document.xml",
                     "word/_rels/document.xml.rels",
                 })
        {
            Require(names.Contains(part), $"Required part missing: {part}");
        }

        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
            }
        }
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

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var errors = DocxPackageValidator.Validate(buffer.ToArray());
        Require(errors.Count == 0, "DOCX besteht den Subset-Validator: " + string.Join("; ", errors));
    }

    private static void WritesCoreProperties()
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

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var core = ReadEntry(zip, "docProps/core.xml");
        Require(core.Contains("Title", StringComparison.Ordinal), "core.xml contains the title.");
        Require(core.Contains("Autor", StringComparison.Ordinal), "core.xml contains the author.");
        Require(core.Contains("de-DE", StringComparison.Ordinal), "core.xml contains the language.");
        Require(core.Contains("2026-09-24T00:00:00Z", StringComparison.Ordinal), "core.xml contains the timestamp.");

        var app = ReadEntry(zip, "docProps/app.xml");
        Require(app.Contains("Reglet", StringComparison.Ordinal), "app.xml contains the application.");

        var rootRels = ReadEntry(zip, "_rels/.rels");
        Require(rootRels.Contains("core-properties", StringComparison.Ordinal), "root .rels references Core Properties.");
        Require(rootRels.Contains("extended-properties", StringComparison.Ordinal), "root .rels references App Properties.");
    }

    private static void WritesSectionProperties()
    {
        var document = PortableDocument.Create()
            .Page(PageSize.Letter, margins: PageMargins.Normal, orientation: PageOrientation.Landscape)
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("w:w=\"15840\"", StringComparison.Ordinal), "Landscape-Breite in Twips (11 Zoll).");
        Require(body.Contains("w:h=\"12240\"", StringComparison.Ordinal), "Landscape-Hoehe in Twips (8,5 Zoll).");
        Require(body.Contains("w:orient=\"landscape\"", StringComparison.Ordinal), "Orientierung landscape.");
        Require(body.Contains("w:top=\"1440\"", StringComparison.Ordinal), "Top margin in twips (1 inch).");
        Require(body.Contains("w:left=\"1440\"", StringComparison.Ordinal), "Left margin in twips (1 inch).");
    }

    private static void WritesAllSectionProperties()
    {
        var first = new Section(new Paragraph(new TextRun("First section")))
        {
            PageSize = PageSize.Letter,
            Orientation = PageOrientation.Landscape,
            Header = new HeaderFooter(new Paragraph(new TextRun("Header One"))),
        };
        var second = new Section(new Paragraph(new TextRun("Second section")))
        {
            PageSize = PageSize.A4,
        };
        var document = new PortableDocument(new DocumentMetadata(), new[] { first, second });

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Split("<w:sectPr", StringSplitOptions.None).Length - 1 == 2, "Each section has its own page properties.");
        Require(body.Contains("<w:pPr><w:sectPr", StringComparison.Ordinal), "First section ends with a section break.");
        Require(body.Contains("w:orient=\"landscape\"", StringComparison.Ordinal), "First alignment is preserved.");
        Require(body.Contains("w:w=\"11906\"", StringComparison.Ordinal), "A4 width of the second section is preserved.");
        Require(body.Contains("headerReference", StringComparison.Ordinal), "Header of the first section is referenced.");
    }

    private static void WritesStandardStyles()
    {
        var document = PortableDocument.Create().Heading("Heading", level: 1).Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var styles = ReadEntry(zip, "word/styles.xml");

        Require(styles.Contains("w:styleId=\"Normal\"", StringComparison.Ordinal), "Normal style present.");
        for (var level = 1; level <= 6; level++)
        {
            Require(
                styles.Contains($"w:styleId=\"Heading{level}\"", StringComparison.Ordinal),
                $"Heading{level} present.");
        }

        Require(styles.Contains("w:outlineLvl", StringComparison.Ordinal), "outlineLvl set.");

        var rels = ReadEntry(zip, "word/_rels/document.xml.rels");
        Require(rels.Contains("styles.xml", StringComparison.Ordinal), "document.xml.rels references styles.xml.");
    }

    private static void WritesParagraphs()
    {
        var document = PortableDocument.Create()
            .Heading("Title", level: 2)
            .Paragraph(p => p
                .Text("Zentriert")
                .Alignment(TextAlignment.Center)
                .SpaceBefore(Length.FromPoints(6))
                .SpaceAfter(Length.FromPoints(12))
                .LeftIndent(Length.FromPoints(18)))
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("w:pStyle w:val=\"Heading2\"", StringComparison.Ordinal), "Heading uses the Heading2 style.");
        Require(body.Contains("Title", StringComparison.Ordinal), "Heading text present.");
        Require(body.Contains("w:jc w:val=\"center\"", StringComparison.Ordinal), "Alignment center.");
        Require(body.Contains("w:before=\"120\"", StringComparison.Ordinal), "Space before (6 pt = 120 twips).");
        Require(body.Contains("w:after=\"240\"", StringComparison.Ordinal), "Space after (12 pt = 240 twips).");
        Require(body.Contains("w:left=\"360\"", StringComparison.Ordinal), "Left indent (18 pt = 360 twips).");
        Require(body.Contains("Zentriert", StringComparison.Ordinal), "Paragraph text present.");
    }

    private static void WritesTextRuns()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p
                .Text("normal ")
                .Span("fett", bold: true)
                .Span("kursiv", italic: true)
                .Span("unterstrichen", underline: true)
                .Span("gross", fontSize: Length.FromPoints(20))
                .Span("rot", color: Color.FromHex("#FF0000"))
                .Span("serif", fontFamily: "Times New Roman"))
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("<w:b", StringComparison.Ordinal), "Bold formatting present.");
        Require(body.Contains("<w:i", StringComparison.Ordinal), "Italic formatting present.");
        Require(body.Contains("w:u w:val=\"single\"", StringComparison.Ordinal), "Unterstreichung vorhanden.");
        Require(body.Contains("w:sz w:val=\"40\"", StringComparison.Ordinal), "Font size 20 pt = 40 half-points.");
        Require(body.Contains("w:color w:val=\"FF0000\"", StringComparison.Ordinal), "Color present.");
        Require(body.Contains("w:rFonts w:ascii=\"Times New Roman\"", StringComparison.Ordinal), "Font family present.");
    }

    private static void PreservesWhitespace()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p
                .Text(" leading")
                .Text("folgend ")
                .Text("doppel  leer")
                .Text("normal"))
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        var preserved = body.Split("xml:space=\"preserve\"", StringSplitOptions.None).Length - 1;
        Require(preserved == 3, $"Exactly three text runs with xml:space=preserve expected, found: {preserved}.");
    }

    private static void WritesNumbering()
    {
        var document = PortableDocument.Create().Paragraph("x").Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var numbering = ReadEntry(zip, "word/numbering.xml");

        // A document without lists has no numbering definitions; the part stays valid.
        Require(numbering.Contains("<w:numbering", StringComparison.Ordinal), "numbering.xml is present.");
        Require(!numbering.Contains("w:abstractNum", StringComparison.Ordinal), "No abstractNum without a list.");
        Require(!numbering.Contains("<w:num ", StringComparison.Ordinal), "No num without a list.");

        var rels = ReadEntry(zip, "word/_rels/document.xml.rels");
        Require(rels.Contains("numbering.xml", StringComparison.Ordinal), "document.xml.rels references numbering.xml.");
    }

    private static void WritesLists()
    {
        var nested = TestLists.List(
            ListKind.Bullet,
            new ListItem[] { new(new Paragraph(new TextRun("two-a"))) },
            nestingLevel: 2);

        var list = TestLists.List(
            ListKind.Number,
            new ListItem[]
            {
                new(new Paragraph(new TextRun("one"))),
                new(new Block[] { new Paragraph(new TextRun("two")), nested }),
            });

        var document = TestLists.Document(list);

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("w:ilvl w:val=\"0\"", StringComparison.Ordinal), "Level 0 present.");
        Require(body.Contains("w:ilvl w:val=\"1\"", StringComparison.Ordinal), "Level 1 present.");
        Require(body.Contains("w:numId w:val=\"2\"", StringComparison.Ordinal), "Aufzaehlung (numId 2) vorhanden.");
        Require(body.Contains("w:numId w:val=\"1\"", StringComparison.Ordinal), "Nummerierung (numId 1) vorhanden.");
        Require(body.Contains("one", StringComparison.Ordinal), "List text one present.");
        Require(body.Contains("two-a", StringComparison.Ordinal), "Verschachtelter Listentext vorhanden.");
    }

    /// <summary>
    /// Two lists of the same format stay two lists when they are read back: every list has its
    /// own numbering id, and the reader keeps it.
    /// </summary>
    private static void KeepsSeparateLists()
    {
        var document = TestLists.Document(
            new Block[]
            {
                TestLists.List(ListKind.Number, new ListItem[] { new(new Paragraph(new TextRun("a"))) }),
                TestLists.List(ListKind.Number, new ListItem[] { new(new Paragraph(new TextRun("b"))) }),
            });

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        var read = new DocxDocumentReader().Read(buffer).Document;
        Require(
            read.Sections[0].Blocks is [ListBlock, ListBlock],
            "Two lists of the same format stay two lists when read back.");
    }

    /// <summary>
    /// Two lists with the same numbering definition continue each other in Word, also with an
    /// intervening paragraph, and the read result keeps one shared definition.
    /// </summary>
    private static void ContinuesSharedNumbering()
    {
        var document = PortableDocument.Create()
            .Numbering(new NumberingDefinition("steps", [new NumberingLevel { Kind = ListKind.Number }]))
            .List("steps", "one", "two")
            .Paragraph("Zwischentext")
            .List("steps", "three", "four")
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        var read = new DocxDocumentReader().Read(buffer).Document;
        var lists = ListScan.Collect(read.Sections[0].Blocks).ToList();
        Require(lists.Count == 2, "Two lists are read back.");
        Require(
            lists[0].NumberingName == lists[1].NumberingName,
            "Both lists use the same numbering definition after the round trip.");
        Require(read.Numbering.Count == 1, "One shared numbering definition is read back.");
        Require(read.Numbering[0].Levels.Count == NumberingDefinition.MaxLevels, "The definition carries all levels.");
    }

    private static void WritesTables()
    {
        var document = PortableDocument.Create()
            .Table(t => t.Header("Kennzahl", "Wert").Row("Documents", "42"))
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("<w:tbl>", StringComparison.Ordinal), "Table present.");
        Require(body.Contains("<w:tblGrid>", StringComparison.Ordinal), "tblGrid present.");
        Require(body.Contains("<w:gridCol", StringComparison.Ordinal), "gridCol present.");
        Require(body.Contains("<w:tblHeader", StringComparison.Ordinal), "Header row marked.");
        Require(body.Contains("<w:tblBorders>", StringComparison.Ordinal), "Table borders present.");
        Require(body.Contains("<w:tblCellMar>", StringComparison.Ordinal), "Cell paddings present.");
        Require(body.Contains("Kennzahl", StringComparison.Ordinal), "Cell content Kennzahl present.");
        Require(body.Contains("42", StringComparison.Ordinal), "Cell content 42 present.");
    }

    private static void WritesImages()
    {
        var png = CreatePng(96, 48);
        var jpeg = CreateJpeg(96, 48);

        var document = PortableDocument.Create()
            .Image(png, ImageMediaType.Png, altText: "Png image")
            .Image(jpeg, ImageMediaType.Jpeg)
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

        Require(names.Contains("word/media/image1.png"), "PNG part present.");
        Require(names.Contains("word/media/image2.jpeg"), "JPEG part present.");
        Require(ReadBinaryEntry(zip, "word/media/image1.png").SequenceEqual(png), "PNG-Bytes unveraendert.");

        var rels = ReadEntry(zip, "word/_rels/document.xml.rels");
        Require(rels.Contains("media/image1.png", StringComparison.Ordinal), "Relationship for PNG present.");
        Require(rels.Contains("media/image2.jpeg", StringComparison.Ordinal), "Relationship for JPEG present.");

        var body = ReadEntry(zip, "word/document.xml");
        Require(body.Contains("<w:drawing>", StringComparison.Ordinal), "Drawing present.");
        Require(body.Contains("r:embed=\"rId4\"", StringComparison.Ordinal), "First image relationship is rId4 (rId3 = settings.xml).");
        Require(body.Contains("r:embed=\"rId4\"", StringComparison.Ordinal), "Second image relationship is rId4.");
        Require(body.Contains("Png image", StringComparison.Ordinal), "Alternative text present.");
    }

    private static void WritesImageExtents()
    {
        var png = CreatePng(96, 48);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("cx=\"914400\"", StringComparison.Ordinal), "Breite 1 Zoll = 914400 EMU.");
        Require(body.Contains("cy=\"457200\"", StringComparison.Ordinal), "Hoehe 0,5 Zoll = 457200 EMU.");
    }

    private static void WritesPageBreaks()
    {
        var document = PortableDocument.Create()
            .Paragraph("before")
            .PageBreak()
            .Paragraph("after")
            .Build();

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var body = ReadEntry(zip, "word/document.xml");

        Require(body.Contains("w:br w:type=\"page\"", StringComparison.Ordinal), "Manual page break present.");

        var before = body.IndexOf("before", StringComparison.Ordinal);
        var pageBreak = body.IndexOf("w:type=\"page\"", StringComparison.Ordinal);
        var after = body.IndexOf("after", StringComparison.Ordinal);
        Require(before >= 0 && pageBreak > before && after > pageBreak, "The break lies between the paragraphs.");
    }

    private static void WritesHeaderFooter()
    {
        var section = new Section(new Paragraph(new TextRun("Text")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
            Footer = new HeaderFooter(new Paragraph(new PageNumberField())),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

        Require(names.Contains("word/header1.xml"), "header1.xml present.");
        Require(names.Contains("word/footer1.xml"), "footer1.xml present.");

        var header = ReadEntry(zip, "word/header1.xml");
        Require(header.Contains("<w:hdr", StringComparison.Ordinal), "Header-Wurzelelement vorhanden.");
        Require(header.Contains("Header", StringComparison.Ordinal), "Header text present.");

        var footer = ReadEntry(zip, "word/footer1.xml");
        Require(footer.Contains("<w:ftr", StringComparison.Ordinal), "Footer-Wurzelelement vorhanden.");
        Require(footer.Contains("PAGE", StringComparison.Ordinal), "Page number field present.");

        var body = ReadEntry(zip, "word/document.xml");
        Require(body.Contains("headerReference", StringComparison.Ordinal), "sectPr references the header.");
        Require(body.Contains("footerReference", StringComparison.Ordinal), "sectPr references the footer.");

        var rels = ReadEntry(zip, "word/_rels/document.xml.rels");
        Require(rels.Contains("header1.xml", StringComparison.Ordinal), "Relationship to the header present.");
        Require(rels.Contains("footer1.xml", StringComparison.Ordinal), "Relationship to the footer present.");
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

    private static byte[] CreateJpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00 });

        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00 });

        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
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

    private static string ReadEntry(ZipArchive zip, string name)
    {
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
