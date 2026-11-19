using Reglet;
using System;
using System.IO.Compression;
using System.Text;

internal static class ReaderChecks
{
    internal static int Run()
    {
        try
        {
            RejectsDeepXmlQuickly();
            ToleratesSingleOddValues();
            ReadsBackTinyLineSpacing();
            ReadsOdtLineSpacingFromEveryAttribute();
            ReportsUnusablePageMargins();
            ReturnsReadResultAndKeepsStreamOpen();
            ReadsDocxMetadata();
            ReadsDocxBlocks();
            ReadsDocxListsAndTables();
            ReadsDocxPageBreakAndField();
            ReadsDocxParagraphSpacingAndIndents();
            ReadsOdtMetadata();
            ReadsOdtBlocksAndListsAndTables();
            ReadsOdtParagraphFormatting();
            ReadsOdtImage();
            ReadsOdtHeaderFooter();
            ReadsDocxHeaderVariants();
            ReadsDocxImage();
            ReadsOdtFormattedRunWithSpaces();
            ReadsOdtSectionPageLayouts();
            ReadsOdtSectionMasterPageViaParentStyle();
            ReadsPdfTextAndMetadata();
            ReadsPdfEmbeddedFontViaToUnicode();
            ReadsPdfRightToLeftInLogicalOrder();
            ReadsPdfActualText();
            JoinsHyphenatedPdfLines();
            Console.WriteLine("Reader: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Reader: " + exception);
            return 1;
        }
    }

    /// <summary>O01/O02: the DOCX reader returns a model + warnings and keeps the stream open.</summary>
    private static void RejectsDeepXmlQuickly()
    {
        var docx = DeepPackage(DocumentFormat.Docx, "word/document.xml", "</w:body>", "<w:tbl><w:tr><w:tc>", "<w:p/></w:tc></w:tr></w:tbl>");
        var odt = DeepPackage(DocumentFormat.Odt, "content.xml", "</office:text>", "<table:table><table:table-row><table:table-cell>", "<text:p/></table:table-cell></table:table-row></table:table>");
        foreach (var (format, package) in new[] { (DocumentFormat.Docx, docx), (DocumentFormat.Odt, odt) })
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                DocumentReader.Read(new MemoryStream(package), format);
                throw new InvalidOperationException($"{format}: deep XML was accepted.");
            }
            catch (DocumentValidationException)
            {
            }

            Require(timer.Elapsed < TimeSpan.FromSeconds(3), $"{format}: deep XML is rejected quickly ({timer.ElapsedMilliseconds} ms).");
        }

        Require(ReaderGuard.ParseXml("<a><b/></a>").Root?.Name.LocalName == "a", "ParseXml: normal XML is read.");
        var deep = string.Concat(Enumerable.Repeat("<a>", ReaderGuard.MaxXmlDepth + 2)) + string.Concat(Enumerable.Repeat("</a>", ReaderGuard.MaxXmlDepth + 2));
        var limit = string.Concat(Enumerable.Repeat("<a>", ReaderGuard.MaxXmlDepth + 1)) + string.Concat(Enumerable.Repeat("</a>", ReaderGuard.MaxXmlDepth + 1));
        Require(ReaderGuard.ParseXml(limit).Root is not null, "ParseXml: the depth limit itself is allowed.");
        try
        {
            ReaderGuard.ParseXml(deep);
            throw new InvalidOperationException("ParseXml: too deep XML was accepted.");
        }
        catch (System.Xml.XmlException)
        {
        }
    }

    private static byte[] Patch(byte[] package, string part, Func<string, string> edit)
    {
        using var output = new MemoryStream();
        output.Write(package);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(part)!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(part).Open());
            writer.Write(edit(xml));
        }

        return output.ToArray();
    }

    private static byte[] OddValuesDocument(DocumentFormat format)
    {
        var style = new ParagraphStyle
        {
            LineSpacing = LineSpacing.Exactly(Length.FromPoints(14)),
            TabStops = [new TabStop(Length.FromPoints(50))],
            Borders = new ParagraphBorders { Top = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1)) },
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("KeepThisText")) { Style = style },
            new TableBlock([new TableRow(new TableCell("x") { Padding = Length.FromPoints(3) })]) { CellPadding = Length.FromPoints(2) },
        }));
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static void ToleratesSingleOddValues()
    {
        var docx = OddValuesDocument(DocumentFormat.Docx);
        foreach (var (attribute, value) in new[] { ("w:line", "0"), ("w:line", "-1"), ("w:sz", "0"), ("w:sz", "-1"), ("w:pos", "-1") })
        {
            var edited = Patch(docx, "word/document.xml", xml => System.Text.RegularExpressions.Regex.Replace(xml, attribute + "=\"-?\\d+\"", $"{attribute}=\"{value}\""));
            var paragraph = DocumentReader.Read(new MemoryStream(edited), DocumentFormat.Docx).Document.Sections[0].Blocks.OfType<Paragraph>().First();
            Require(paragraph.Inlines.OfType<TextRun>().Any(run => run.Text == "KeepThisText"), $"DOCX: {attribute}={value} keeps the document.");
        }

        var odt = OddValuesDocument(DocumentFormat.Odt);
        foreach (var (attribute, value) in new[] { ("fo:line-height", "0pt"), ("fo:line-height", "0%"), ("fo:line-height", "-1pt"), ("style:position", "-1pt"), ("fo:padding", "-1pt") })
        {
            var edited = Patch(odt, "content.xml", xml => System.Text.RegularExpressions.Regex.Replace(xml, attribute + "=\"[^\"]*\"", $"{attribute}=\"{value}\""));
            var paragraph = DocumentReader.Read(new MemoryStream(edited), DocumentFormat.Odt).Document.Sections[0].Blocks.OfType<Paragraph>().First();
            Require(paragraph.Inlines.OfType<TextRun>().Any(run => run.Text == "KeepThisText"), $"ODT: {attribute}={value} keeps the document.");
        }
    }

    private static void ReadsBackTinyLineSpacing()
    {
        foreach (var spacing in new[] { LineSpacing.Multiple(0.00001), LineSpacing.Exactly(Length.FromPoints(0.001)), LineSpacing.AtLeast(Length.FromPoints(0.001)) })
        {
            foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
            {
                var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x")) { Style = new ParagraphStyle { LineSpacing = spacing } }));
                using var output = new MemoryStream();
                DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
                output.Position = 0;
                var read = DocumentReader.Read(output, format).Document.Sections[0].Blocks.OfType<Paragraph>().Single();
                Require(read.Style.LineSpacing?.Rule == spacing.Rule, $"{format}: a tiny line spacing keeps its rule.");
            }
        }
    }

    private static void ReadsOdtLineSpacingFromEveryAttribute()
    {
        var odt = OddValuesDocument(DocumentFormat.Odt);
        foreach (var (attribute, value, rule) in new[]
        {
            ("fo:line-height", "150%", LineSpacingRule.Multiple),
            ("fo:line-height", "14pt", LineSpacingRule.Exactly),
            ("style:line-height-at-least", "14pt", LineSpacingRule.AtLeast),
            ("style:line-height", "150%", LineSpacingRule.Multiple),
        })
        {
            var edited = Patch(odt, "content.xml", xml => System.Text.RegularExpressions.Regex.Replace(xml, "fo:line-height=\"[^\"]*\"", $"{attribute}=\"{value}\""));
            var paragraph = DocumentReader.Read(new MemoryStream(edited), DocumentFormat.Odt).Document.Sections[0].Blocks.OfType<Paragraph>().First();
            Require(paragraph.Style.LineSpacing?.Rule == rule, $"ODT: {attribute}={value} is {rule}.");
        }
    }

    private static void ReportsUnusablePageMargins()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x"))) { Margins = new PageMargins(Length.FromPoints(100000)) });
        try
        {
            DocumentWriter.WriteAsync(document, new MemoryStream(), DocumentFormat.Pdf).GetAwaiter().GetResult();
            throw new InvalidOperationException("Margins without a content area were accepted.");
        }
        catch (DocumentValidationException)
        {
        }
    }

    private static byte[] DeepPackage(DocumentFormat format, string part, string before, string open, string close)
    {
        const int Depth = 20_000;
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("x")))), output, format).GetAwaiter().GetResult();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(part)!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            entry.Delete();
            var at = xml.IndexOf(before, StringComparison.Ordinal);
            xml = xml[..at] + string.Concat(Enumerable.Repeat(open, Depth)) + string.Concat(Enumerable.Repeat(close, Depth)) + xml[at..];
            using var writer = new StreamWriter(zip.CreateEntry(part).Open());
            writer.Write(xml);
        }

        return output.ToArray();
    }

    private static void ReturnsReadResultAndKeepsStreamOpen()
    {
        var document = PortableDocument.Create().Paragraph("Text").Build();
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;

        using var tracking = new TrackingStream(buffer.ToArray());
        var reader = new DocxDocumentReader();
        var result = reader.Read(tracking);
        Require(result.Document.Sections.Count >= 1, "At least one section.");
        Require(!tracking.Disposed, "Reader does not close the foreign stream.");
    }

    /// <summary>O02: metadata (title, author, language, keywords, timestamp) is read.</summary>
    private static void ReadsDocxMetadata()
    {
        var document = PortableDocument.Create()
            .Metadata(
                title: "Title",
                author: "Autor",
                subject: "Betreff",
                keywords: new[] { "one", "two" },
                language: "de-DE",
                created: new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)))
            .Paragraph("x")
            .Build();

        var metadata = ReadDocx(document).Document.Metadata;
        Require(metadata.Title == "Title", "Title read.");
        Require(metadata.Author == "Autor", "Author read.");
        Require(metadata.Subject == "Betreff", "Subject read.");
        Require(metadata.Language == "de-DE", "Sprache gelesen (dc:language).");
        Require(metadata.Keywords is { } keys && keys.SequenceEqual(new[] { "one", "two" }),
            "Keywords read.");
        Require(metadata.Created is { } created
            && created == new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)),
            "Timestamp read (same instant).");
    }

    /// <summary>O02: heading, formatted paragraph, page break and section are read.</summary>
    private static void ReadsDocxBlocks()
    {
        var document = PortableDocument.Create()
            .Metadata(title: "B")
            .Paragraph(p => p.Text("Normal ").Bold("fett").Text(" ").Span("kursiv", italic: true, fontSize: Length.FromPoints(14)))
            .PageBreak()
            .Paragraph("Ende")
            .Build();

        var read = ReadDocx(document).Document;
        var blocks = read.Sections[0].Blocks;
        Require(blocks.Count == 3, "Three blocks (paragraph, page break, paragraph).");
        Require(blocks[1] is PageBreak, "A standalone page break becomes PageBreak.");

        var inlines = ((Paragraph)blocks[0]).Inlines;
        Require(inlines.Count == 4, "Four runs.");
        Require(inlines[0] is TextRun r0 && r0.Text == "Normal ", "First run with a space.");
        Require(inlines[1] is TextRun r1 && r1.Text == "fett" && r1.Bold == true, "Fetter Lauf.");
        Require(inlines[3] is TextRun r3 && r3.Text == "kursiv" && r3.Italic == true, "Kursiver Lauf.");
        Require(inlines[3] is TextRun r3b && r3b.FontSize?.Points == 14, "Font size read.");
    }

    /// <summary>O02: bullet lists, numbered lists and tables are read into the model.</summary>
    private static void ReadsDocxListsAndTables()
    {
        var document = PortableDocument.Create()
            .BulletList("one", "two")
            .NumberedList("first", "second")
            .Table(t => t.Header("A", "B").Row("1", "2").ColumnWidths(Length.FromCentimeters(3), Length.FromCentimeters(5)))
            .Build();

        var readDocument = ReadDocx(document).Document;
        var blocks = readDocument.Sections[0].Blocks;
        Require(blocks.Count == 3, "List, numbered list, table.");
        Require(blocks[0] is ListBlock bullet
            && TestLists.Level(readDocument, bullet).Kind == ListKind.Bullet && bullet.Items.Count == 2,
            "Bullet list read.");
        Require(blocks[1] is ListBlock numbered
            && TestLists.Level(readDocument, numbered).Kind == ListKind.Number && numbered.Items.Count == 2,
            "Numbering read.");
        Require(blocks[2] is TableBlock, "Table read.");
        var table = (TableBlock)blocks[2];
        Require(table.Rows.Count == 2, "Table read (rows).");
        Require(table.Rows[0].IsHeader, "Header row marked.");
        Require(table.Rows[0].Cells[0].Blocks[0] is Paragraph p && p.Inlines.Count == 1
            && p.Inlines[0] is TextRun t && t.Text == "A", "Table cell A read.");
    }

    /// <summary>O02: page number field in the body is read as PageNumberField.</summary>
    private static void ReadsDocxPageBreakAndField()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section[]
            {
                new(new Paragraph(new TextRun("Page "))),
                new(new Paragraph(new Inline[] { new TextRun("Blatt "), new PageNumberField() })),
            });

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;

        Require(read.Sections.Count == 2, "Two sections after a section break.");
        var second = read.Sections[1].Blocks[0] as Paragraph;
        Require(second is not null && second.Inlines.Count == 2, "Second section begins with a paragraph.");
        Require(second!.Inlines[1] is PageNumberField, "Page number field read.");
    }

    /// <summary>O02: paragraph spacing, alignment and indents are read from twips.</summary>
    private static void ReadsDocxParagraphSpacingAndIndents()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Text("Paragraph")
                .Alignment(TextAlignment.Justify)
                .SpaceBefore(Length.FromPoints(12))
                .SpaceAfter(Length.FromPoints(8))
                .LeftIndent(Length.FromPoints(24))
                .RightIndent(Length.FromPoints(6)))
            .Build();

        var style = ((Paragraph)ReadDocx(document).Document.Sections[0].Blocks[0]).Style;
        Require(style.SpaceBefore?.Points == 12, "Space before read (twips → points).");
        Require(style.SpaceAfter?.Points == 8, "Space after read (twips → points).");
        Require((style.LeftIndent?.Points ?? 0) == 24, "Left indent read.");
        Require((style.RightIndent?.Points ?? 0) == 6, "Right indent read.");
        Require(style.Alignment == TextAlignment.Justify, "Justification read.");
    }

    /// <summary>O03: metadata (title, author, language, timestamp) is read from ODT.</summary>
    private static void ReadsOdtMetadata()
    {
        var document = PortableDocument.Create()
            .Metadata(
                title: "Title",
                author: "Autor",
                subject: "Betreff",
                keywords: new[] { "one", "two" },
                language: "de-DE",
                created: new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)))
            .Paragraph("x")
            .Build();

        var metadata = ReadOdt(document).Document.Metadata;
        Require(metadata.Title == "Title", "ODT: title read.");
        Require(metadata.Author == "Autor", "ODT: author read.");
        Require(metadata.Subject == "Betreff", "ODT: subject read.");
        Require(metadata.Language == "de-DE", "ODT: language read.");
        Require(metadata.Keywords is { } keys && keys.SequenceEqual(new[] { "one", "two" }), "ODT: keywords read.");
        Require(metadata.Created is { } created && created == new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)),
            "ODT: timestamp read (same instant).");
    }

    /// <summary>O03: heading, formatted paragraph, lists and table are read from ODT.</summary>
    private static void ReadsOdtBlocksAndListsAndTables()
    {
        var document = PortableDocument.Create()
            .Heading("Heading", level: 1)
            .Paragraph(p => p.Text("Normal ").Bold("fett").Text(" ").Span("kursiv", italic: true))
            .BulletList("one", "two")
            .NumberedList("first", "second")
            .Table(t => t.Header("A", "B").Row("1", "2"))
            .Build();

        var readDocument = ReadOdt(document).Document;
        var blocks = readDocument.Sections[0].Blocks;
        Require(blocks[0] is Heading h && h.Level == HeadingLevel.Level1, "ODT: heading read.");
        Require(blocks[1] is Paragraph par && par.Inlines.Count >= 3, "ODT: paragraph with runs.");
        Require(blocks[2] is ListBlock bullet
            && TestLists.Level(readDocument, bullet).Kind == ListKind.Bullet && bullet.Items.Count == 2, "ODT: bullet list.");
        Require(blocks[3] is ListBlock numbered
            && TestLists.Level(readDocument, numbered).Kind == ListKind.Number && numbered.Items.Count == 2, "ODT: Nummerierung.");
        Require(blocks[4] is TableBlock table && table.Rows.Count == 2 && table.Rows[0].IsHeader, "ODT: table with a header row.");

        var inlines = ((Paragraph)blocks[1]).Inlines;
        Require(inlines.OfType<TextRun>().Any(r => r.Text == "fett" && r.Bold == true), "ODT: fetter Lauf.");
        Require(inlines.OfType<TextRun>().Any(r => r.Text == "kursiv" && r.Italic == true), "ODT: kursiver Lauf.");
        Require(inlines.Count >= 3, "ODT: at least text and formatting runs.");
    }

    /// <summary>O03: spaces within formatted runs are preserved when reading.</summary>
    private static void ReadsOdtParagraphFormatting()
    {
        var style = ParagraphStyle.Default with
        {
            Alignment = TextAlignment.Center,
            SpaceBefore = Length.FromPoints(6),
            SpaceAfter = Length.FromPoints(3),
            LeftIndent = Length.FromPoints(12),
        };

        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("x")) { Style = style }));
        var paragraph = (Paragraph)ReadOdt(document).Document.Sections[0].Blocks[0];

        Require(paragraph.Style.Alignment == TextAlignment.Center, "AC01: alignment.");
        Require(Math.Abs((paragraph.Style.LeftIndent?.Points ?? 0) - 12) < 0.01, "AC01: left indent.");
        Require(Math.Abs((paragraph.Style.SpaceBefore?.Points ?? 0) - 6) < 0.01, "AC01: space before.");
        Require(Math.Abs((paragraph.Style.SpaceAfter?.Points ?? 0) - 3) < 0.01, "AC01: space after.");
    }

    private static void ReadsOdtImage()
    {
        var bytes = TestImages.RgbPng(2, 2, 255, 0, 0);
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new ImageBlock(bytes, ImageMediaType.Png)
            {
                Width = Length.FromPoints(50),
                Height = Length.FromPoints(40),
            }));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Odt);
        var image = read.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().FirstOrDefault();
        Require(image is not null, "ODT: the image is read back.");
        Require(image!.MediaType == ImageMediaType.Png, "ODT: image media type.");
        Require(image.Data.Span.SequenceEqual(bytes), "ODT: image bytes.");
    }

    private static void ReadsOdtHeaderFooter()
    {
        var section = new Section(new Paragraph([new TextRun("body")]))
        {
            Header = new HeaderFooter(new Paragraph([new TextRun("head")])),
            Footer = new HeaderFooter(new Paragraph([new TextRun("foot")])),
        };
        var document = new PortableDocument(new DocumentMetadata(), [section]);

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Odt);
        var readSection = read.Document.Sections[0];
        Require(
            readSection.Header is { } header
                && header.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<TextRun>().Any(r => r.Text == "head")),
            "ODT: the header is read back.");
        Require(
            readSection.Footer is { } footer
                && footer.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<TextRun>().Any(r => r.Text == "foot")),
            "ODT: the footer is read back.");
    }

    private static void ReadsDocxHeaderVariants()
    {
        var section = new Section(new Paragraph([new TextRun("body")]))
        {
            FirstHeader = new HeaderFooter(new Paragraph([new TextRun("first")])),
            EvenFooter = new HeaderFooter(new Paragraph([new TextRun("even")])),
        };
        var document = new PortableDocument(new DocumentMetadata(), [section]);

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Docx);
        var readSection = read.Document.Sections[0];
        Require(
            readSection.FirstHeader is { } firstHeader
                && firstHeader.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<TextRun>().Any(r => r.Text == "first")),
            "DOCX: the first-page header is read back.");
        Require(
            readSection.EvenFooter is { } evenFooter
                && evenFooter.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<TextRun>().Any(r => r.Text == "even")),
            "DOCX: the even-page footer is read back.");
    }

    private static void ReadsDocxImage()
    {
        var bytes = TestImages.RgbPng(2, 2, 0, 0, 255);
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new ImageBlock(bytes, ImageMediaType.Png)
            {
                Width = Length.FromPoints(50),
                Height = Length.FromPoints(40),
            }));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Docx);
        var image = read.Document.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().FirstOrDefault();
        Require(image is not null, "DOCX: the image is read back.");
        Require(image!.MediaType == ImageMediaType.Png, "DOCX: image media type.");
        Require(image.Data.Span.SequenceEqual(bytes), "DOCX: image bytes.");
    }

    private static void ReadsOdtFormattedRunWithSpaces()
    {
        var document = PortableDocument.Create()
            .Paragraph(p => p.Bold("hello world").Span("bold italic", bold: true, italic: true))
            .Build();

        var inlines = ((Paragraph)ReadOdt(document).Document.Sections[0].Blocks[0]).Inlines;
        Require(inlines.OfType<TextRun>().Any(r => r.Text == "hello world" && r.Bold == true),
            "ODT: spaces in the bold run are preserved.");
        Require(inlines.OfType<TextRun>().Any(r => r.Text == "bold italic" && r.Bold == true && r.Italic == true),
            "ODT: spaces in combined formatting are preserved.");
    }

    /// <summary>O03: several sections keep their respective page size.</summary>
    private static void ReadsOdtSectionPageLayouts()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new[]
            {
                new Section(new Paragraph(new TextRun("A"))) { PageSize = PageSize.A4 },
                new Section(new Paragraph(new TextRun("B"))) { PageSize = PageSize.Letter },
            });

        var sections = ReadOdt(document).Document.Sections;
        Require(sections.Count == 2, "ODT: two sections read.");
        Require(Math.Abs(sections[0].PageSize.Width.Points - PageSize.A4.Width.Points) < 0.01, "ODT: first section A4.");
        Require(Math.Abs(sections[1].PageSize.Width.Points - PageSize.Letter.Width.Points) < 0.01, "ODT: second section Letter.");
    }

    /// <summary>
    /// O03: foreign ODTs can inherit the master page of a section via
    /// <c>style:parent-style-name</c>. The resolution follows the chain via content.xml
    /// and styles.xml.
    /// </summary>
    private static void ReadsOdtSectionMasterPageViaParentStyle()
    {
        var contentXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content
                xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                office:version="1.3">
              <office:automatic-styles>
                <style:style style:name="Break1" style:family="paragraph" style:master-page-name="Standard"/>
                <style:style style:name="Break2" style:family="paragraph" style:parent-style-name="BaseBreak"/>
              </office:automatic-styles>
              <office:body>
                <office:text>
                  <text:section text:name="S1" text:style-name="Sect1"><text:p text:style-name="Break1">A</text:p></text:section>
                  <text:section text:name="S2" text:style-name="Sect2"><text:p text:style-name="Break2">B</text:p></text:section>
                </office:text>
              </office:body>
            </office:document-content>
            """;

        var stylesXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-styles
                xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0"
                xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0"
                office:version="1.3">
              <office:styles>
                <style:style style:name="BaseBreak" style:family="paragraph" style:master-page-name="Standard2"/>
              </office:styles>
              <office:automatic-styles>
                <style:page-layout style:name="pm1">
                  <style:page-layout-properties fo:page-width="595.28pt" fo:page-height="841.89pt" fo:margin-top="72pt" fo:margin-right="72pt" fo:margin-bottom="72pt" fo:margin-left="72pt"/>
                </style:page-layout>
                <style:page-layout style:name="pm2">
                  <style:page-layout-properties fo:page-width="612pt" fo:page-height="792pt" fo:margin-top="72pt" fo:margin-right="72pt" fo:margin-bottom="72pt" fo:margin-left="72pt"/>
                </style:page-layout>
              </office:automatic-styles>
              <office:master-styles>
                <style:master-page style:name="Standard" style:page-layout-name="pm1"/>
                <style:master-page style:name="Standard2" style:page-layout-name="pm2"/>
              </office:master-styles>
            </office:document-styles>
            """;

        using var buffer = new MemoryStream(BuildOdt(contentXml, stylesXml));
        var sections = new OdtDocumentReader().Read(buffer).Document.Sections;
        Require(sections.Count == 2, "ODT: two sections from a synthetic package.");
        Require(Math.Abs(sections[0].PageSize.Width.Points - 595.28) < 0.1, "ODT: first section via a direct master reference.");
        Require(Math.Abs(sections[1].PageSize.Width.Points - 612) < 0.1, "ODT: second section via a parent style chain.");
    }

    /// <summary>Builds a minimal ODT package for synthetic checks.</summary>
    private static byte[] BuildOdt(string contentXml, string stylesXml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mime.Open(), new UTF8Encoding(false)))
            {
                writer.Write("application/vnd.oasis.opendocument.text");
            }

            WriteZipText(zip, "content.xml", contentXml);
            WriteZipText(zip, "styles.xml", stylesXml);
        }

        return buffer.ToArray();
    }

    private static void WriteZipText(ZipArchive zip, string name, string value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    /// <summary>O04: PDF text (WinAnsi) and metadata are extracted.</summary>
    private static void ReadsPdfTextAndMetadata()
    {
        var source = PortableDocument.Create()
            .Metadata(
                title: "Title",
                author: "Autor",
                subject: "Betreff",
                keywords: new[] { "one", "two" },
                created: new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.Zero))
            .Heading("Heading", level: 1)
            .Paragraph(p => p.Text("Hallo ").Bold("Welt"))
            .Build();
        var document = source with
        {
            Metadata = source.Metadata with
            {
                CustomProperties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Department"] = "Engineering",
                    ["Review"] = "OK",
                },
            },
        };

        var result = ReadPdf(document);
        Require(result.Document.Metadata.Title == "Title", "PDF: title read.");
        Require(result.Document.Metadata.Author == "Autor", "PDF: author read.");
        Require(result.Document.Metadata.Subject == "Betreff", "PDF: subject read.");
        Require(result.Document.Metadata.Keywords is { } keys && keys.SequenceEqual(new[] { "one", "two" }),
            "PDF: keywords read.");
        Require(result.Document.Metadata.Created == new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.Zero),
            "PDF: timestamp read.");
        Require(result.Document.Metadata.CustomProperties is { } custom
            && custom.TryGetValue("Department", out var department) && department == "Engineering"
            && custom.TryGetValue("Review", out var review) && review == "OK",
            "PDF: custom properties read.");

        var text = AllText(result.Document);
        Require(text.Contains("Heading", StringComparison.Ordinal), "PDF: Unicode text read.");
        Require(text.Contains("Hallo Welt", StringComparison.Ordinal), "PDF: formatted text read.");
    }

    /// <summary>O04: Identity-H text is decoded via the ToUnicode CMap.</summary>
    private static void ReadsPdfEmbeddedFontViaToUnicode()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 1)),
            ("maxp", SyntheticTrueTypeFont.Maxp(5)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx((500, 0))),
            ("name", SyntheticTrueTypeFont.Name("ReaderFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap4()));

        var document = PortableDocument.Create()
            .Paragraph(p => p.Span("AA", fontFamily: "Test"))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Test"] = fontData } })
            .GetAwaiter().GetResult();
        buffer.Position = 0;

        var read = new PdfDocumentReader().Read(buffer).Document;
        Require(AllText(read).Contains("AA", StringComparison.Ordinal), "PDF: Identity-H/ToUnicode text read.");
    }

    private static void ReadsPdfRightToLeftInLogicalOrder()
    {
        var fontData = SyntheticTrueTypeFont.Build(
            ("head", SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800)),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, 21)),
            ("maxp", SyntheticTrueTypeFont.Maxp(21)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx(
                (500, 0), (500, 0), (500, 0), (500, 0), (500, 0), (500, 0),
                (500, 0), (500, 0), (500, 0), (500, 0), (500, 0), (500, 0),
                (500, 0), (500, 0), (500, 0), (500, 0), (500, 0), (500, 0),
                (500, 0), (500, 0), (500, 0))),
            ("name", SyntheticTrueTypeFont.Name("HebrewFont")),
            ("cmap", SyntheticTrueTypeFont.Cmap12(
                (0x0020, 16), (0x002E, 17), (0x0030, 6), (0x0031, 7), (0x0032, 8), (0x0033, 9),
                (0x0034, 10), (0x0035, 11), (0x0036, 12), (0x0037, 13), (0x0038, 14), (0x0039, 15),
                (0x0041, 18), (0x0042, 19), (0x0043, 20),
                (0x05D0, 3), (0x05D1, 4), (0x05D2, 5))));

        var document = PortableDocument.Create()
            .Paragraph(p => p.Run(new TextRun("\u05D0\u05D1\u05D2") { FontFamily = "Heb" }))
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = fontData } })
            .GetAwaiter().GetResult();

        var texts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(buffer.ToArray(), writable: false));
        Require(
            texts.Count == 1 && texts[0].Contains("\u05D0\u05D1\u05D2", StringComparison.Ordinal),
            "PDF: a right-to-left line is returned in logical order: " + string.Join(" / ", texts));

        var read = new PdfDocumentReader().Read(new MemoryStream(buffer.ToArray(), writable: false)).Document;
        Require(
            AllText(read).Contains("\u05D0\u05D1\u05D2", StringComparison.Ordinal),
            "PDF: ReadPages returns a right-to-left line in logical order.");

        var withNumbers = PortableDocument.Create()
            .Paragraph(p => p.Run(new TextRun("\u05D0\u05D1\u05D2 12.3") { FontFamily = "Heb" }))
            .Build();
        using var numberBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            withNumbers,
            numberBuffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = fontData } })
            .GetAwaiter().GetResult();
        var numberTexts = new PdfDocumentReader()
            .ReadPageTexts(new MemoryStream(numberBuffer.ToArray(), writable: false));
        Require(
            numberTexts.Count == 1 && numberTexts[0].Contains("\u05D0\u05D1\u05D2 12.3", StringComparison.Ordinal),
            "PDF: numbers in a right-to-left line stay logical: " + string.Join(" / ", numberTexts));

        var mixed = PortableDocument.Create()
            .Paragraph(p => p.Run(new TextRun("\u05D0\u05D1\u05D2 ABC") { FontFamily = "Heb" }))
            .Build();
        using var mixedBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            mixed,
            mixedBuffer,
            new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { ["Heb"] = fontData } })
            .GetAwaiter().GetResult();
        var mixedTexts = new PdfDocumentReader()
            .ReadPageTexts(new MemoryStream(mixedBuffer.ToArray(), writable: false));
        Require(
            mixedTexts.Count == 1 && mixedTexts[0].Contains("\u05D0\u05D1\u05D2 ABC", StringComparison.Ordinal),
            "PDF: mixed right-to-left and Latin text is written with ActualText: " + string.Join(" / ", mixedTexts));
    }

    /// <summary>O04: marked content /ActualText replaces the glyphs in the extracted text.</summary>
    private static void ReadsPdfActualText()
    {
        var result = ReadRawPdf(
            "/Span << /ActualText (logical) >> BDC "
            + "BT /F1 12 Tf 72 700 Td (visual) Tj ET EMC");
        var text = AllText(result.Document);
        Require(text.Contains("logical", StringComparison.Ordinal) && !text.Contains("visual", StringComparison.Ordinal),
            "PDF: /ActualText replaces the glyph text: " + text);
    }

    /// <summary>O04: a hyphen at a line end joins the word instead of leaving "word- next".</summary>
    private static void JoinsHyphenatedPdfLines()
    {
        var hyphenated = ReadRawPdf(
            "BT /F1 12 Tf 72 700 Td (hyphen-) Tj ET "
            + "BT /F1 12 Tf 72 686 Td (ation) Tj ET");
        var text = AllText(hyphenated.Document);
        Require(text.Contains("hyphenation", StringComparison.Ordinal),
            "PDF: a hyphenated line break is joined: " + text);

        var normal = ReadRawPdf(
            "BT /F1 12 Tf 72 700 Td (hello) Tj ET "
            + "BT /F1 12 Tf 72 686 Td (world) Tj ET");
        Require(AllText(normal.Document).Contains("hello world", StringComparison.Ordinal),
            "PDF: a normal line break keeps its space.");

        var shuffled = ReadRawPdf(
            "BT /F1 12 Tf 72 686 Td (second) Tj ET "
            + "BT /F1 12 Tf 72 700 Td (first) Tj ET");
        Require(AllText(shuffled.Document).Contains("first second", StringComparison.Ordinal),
            "PDF: lines drawn out of order are reconstructed top to bottom.");
    }

    private static ReadResult ReadRawPdf(string content)
        => DocumentReader.Read(new MemoryStream(SinglePagePdf(content)), DocumentFormat.Pdf);

    private static byte[] SinglePagePdf(string content) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

    /// <summary>Builds a PDF with a classic xref table from the given objects (number = index + 1).</summary>
    private static byte[] Pdf(params string[] objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static ReadResult ReadPdf(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        return new PdfDocumentReader().Read(buffer);
    }

    private static string AllText(PortableDocument document)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var section in document.Sections)
        {
            AppendBlocks(builder, section.Blocks);
        }

        return builder.ToString();
    }

    private static void AppendBlocks(System.Text.StringBuilder builder, IReadOnlyList<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    AppendInlines(builder, paragraph.Inlines);
                    break;
                case Heading heading:
                    AppendInlines(builder, heading.Inlines);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        AppendBlocks(builder, item.Blocks);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            AppendBlocks(builder, cell.Blocks);
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendInlines(System.Text.StringBuilder builder, IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            builder.Append(inline switch
            {
                TextRun run => run.Text,
                PageNumberField => " ",
                _ => string.Empty,
            });
        }
    }

    private static ReadResult ReadOdt(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        return new OdtDocumentReader().Read(buffer);
    }

    private static ReadResult ReadDocx(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        return new DocxDocumentReader().Read(buffer);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] bytes) : base(bytes)
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
