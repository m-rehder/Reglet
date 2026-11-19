using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// Bookmarks and the PDF outline (phase Z, Z03): <see cref="Bookmark"/> anchors in DOCX,
/// ODT and PDF as well as the outline tree and named destinations of the PDF.
/// </summary>
internal static class BookmarkChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxBookmarks();
            WritesOdtBookmarks();
            WritesPdfOutline();
            WritesBookmarkRange();
            ReadsBookmarkRange();
            ResolvesRefField();
            FindsBookmarkInsideHyperlink();
            FindsBookmarkOnLaterTablePage();
            KeepsRepeatedHeaderBookmarkOnFirstPage();
            ResolvesPageReferenceInTable();
            ReportsReferencesWithoutATarget();
            FindsBookmarkOnItsActualLine();
            OrdersOutlineByDocumentInSplitRow();
            BookmarkDoesNotChangeJustification();
            RejectsInvalidValues();
            Console.WriteLine("Bookmarks (Z03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Bookmarks (Z03): " + exception);
            return 1;
        }
    }

    private static PortableDocument BookmarkDocument()
        => new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Heading(HeadingLevel.Level1, new TextRun("First")),
                new Paragraph(new Inline[] { new TextRun("See "), new Bookmark("MyTarget"), new TextRun("here.") }),
                new Heading(HeadingLevel.Level2, new TextRun("Second")),
            }));

    private static void WritesDocxBookmarks()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(BookmarkDocument(), buffer).GetAwaiter().GetResult();

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(body.Contains("<w:bookmarkStart", StringComparison.Ordinal), "DOCX: bookmarkStart.");
        Require(body.Contains("w:name=\"MyTarget\"", StringComparison.Ordinal), "DOCX: bookmark name.");
        Require(body.Contains("<w:bookmarkEnd", StringComparison.Ordinal), "DOCX: bookmarkEnd.");
    }

    private static void WritesOdtBookmarks()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(BookmarkDocument(), buffer).GetAwaiter().GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("<text:bookmark", StringComparison.Ordinal), "ODT: text:bookmark.");
        Require(content.Contains("text:name=\"MyTarget\"", StringComparison.Ordinal), "ODT: bookmark name.");
    }

    /// <summary>A bookmark range (start … end) is written as a range, a point bookmark stays a point.</summary>
    private static void WritesBookmarkRange()
    {
        var document = RangeDocument();

        using var docxBuffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");

        // One start and one end, both with the same id, and nothing between them for the end.
        Require(CountOf(body, "<w:bookmarkStart") == 2, "DOCX: two bookmark starts.");
        Require(CountOf(body, "<w:bookmarkEnd") == 2, "DOCX: two bookmark ends.");
        var range = System.Text.RegularExpressions.Regex.Match(
            body, "<w:bookmarkStart w:id=\"([0-9]+)\" w:name=\"Range\" />(?<inside>.*?)<w:bookmarkEnd w:id=\"\\1\" />");
        Require(range.Success, "DOCX: the range start and end share an id: " + body);
        Require(range.Groups["inside"].Value.Contains("range text", StringComparison.Ordinal),
            "DOCX: the text of the range lies between start and end.");

        // The point bookmark keeps the adjacent end, so Word sees a zero-width target.
        Require(System.Text.RegularExpressions.Regex.IsMatch(
                body, "<w:bookmarkStart w:id=\"[0-9]+\" w:name=\"Point\" /><w:bookmarkEnd w:id=\"[0-9]+\" />"),
            "DOCX: a point bookmark closes right after its start.");

        using var odtBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(content.Contains("<text:bookmark-start text:name=\"Range\"", StringComparison.Ordinal),
            "ODT: the range starts with text:bookmark-start.");
        Require(content.Contains("<text:bookmark-end text:name=\"Range\"", StringComparison.Ordinal),
            "ODT: the range ends with text:bookmark-end.");
        Require(content.Contains("<text:bookmark text:name=\"Point\"", StringComparison.Ordinal),
            "ODT: a point bookmark stays text:bookmark.");
        Require(!content.Contains("bookmark-start text:name=\"Point\"", StringComparison.Ordinal),
            "ODT: a point bookmark is no range.");
    }

    /// <summary>The readers keep the end of a range, so a read/write cycle keeps it too.</summary>
    private static void ReadsBookmarkRange()
    {
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(RangeDocument(), buffer, format).GetAwaiter().GetResult();

            var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var rangeStart = paragraph.Inlines.OfType<Bookmark>().Single(bookmark => bookmark.Name == "Range");
            var rangeEnd = paragraph.Inlines.OfType<BookmarkEnd>().Single(end => end.Name == "Range");
            Require(rangeStart is not null && rangeEnd is not null, $"{format}: the range is read.");

            var info = BookmarkScan.Collect(read);
            Require(info.RangeNames.Contains("Range"), $"{format}: the range name is collected.");
            Require(info.Texts["Range"] == "range text", $"{format}: the range text is '{info.Texts.GetValueOrDefault("Range")}'.");
            Require(!info.RangeNames.Contains("Point"), $"{format}: a point bookmark is no range.");
            Require(info.Texts.GetValueOrDefault("Point") is null or "", $"{format}: a point bookmark has no text.");

            // And it survives the next write.
            using var again = new MemoryStream();
            DocumentWriter.WriteAsync(read, again, format).GetAwaiter().GetResult();
            if (format == DocumentFormat.Docx)
            {
                var body = ReadZipEntry(again.ToArray(), "word/document.xml");
                Require(CountOf(body, "<w:bookmarkStart") == 2 && CountOf(body, "w:name=\"Range\"") == 1,
                    "DOCX: the range survives a read/write cycle.");
            }
            else
            {
                var content = ReadZipEntry(again.ToArray(), "content.xml");
                Require(content.Contains("<text:bookmark-end text:name=\"Range\"", StringComparison.Ordinal),
                    "ODT: the range survives a read/write cycle.");
            }
        }
    }

    /// <summary>A REF field shows the text of its bookmark; a missing target is reported (OF-C03).</summary>
    private static void ResolvesRefField()
    {
        var section = new Section(new Block[]
        {
            new Paragraph(new Inline[] { new Bookmark("Range"), new TextRun("range text"), new BookmarkEnd("Range") }),
            new Paragraph(new Inline[] { new Field("REF Range") }),
            new Paragraph(new Inline[] { new Field("REF Missing") }),
            new Paragraph(new Inline[] { new Field("REF \"Two Words\"") }),
            new Paragraph(new Inline[] { new Field("REF InBand") }),
        })
        {
            Footer = new HeaderFooter(new Paragraph(new Inline[] { new Bookmark("InBand"), new TextRun("band") })),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);

        using var docxBuffer = new MemoryStream();
        var docx = new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(body.Contains("REF Range", StringComparison.Ordinal), "DOCX: the REF instruction is written.");
        var instruction = body.IndexOf("REF Range", StringComparison.Ordinal);
        var result = body.IndexOf("<w:t>range text</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"", StringComparison.Ordinal);
        Require(instruction >= 0 && result > instruction,
            "DOCX: the result of the REF field is the text of the bookmark: " + body);

        var docxMessages = Messages(docx);
        Require(docxMessages.Any(message => message.Contains("the REF field to the bookmark 'Missing' has no target", StringComparison.Ordinal)),
            "DOCX: a REF field without a target is reported: " + string.Join("; ", docxMessages));
        Require(docxMessages.Any(message => message.Contains("the REF field to the bookmark 'Two Words' has no target", StringComparison.Ordinal)),
            "DOCX: a quoted REF target is checked.");
        Require(!docxMessages.Any(message => message.Contains("'Range'", StringComparison.Ordinal)),
            "DOCX: a REF field with a target is not reported.");
        Require(!docxMessages.Any(message => message.Contains("'InBand'", StringComparison.Ordinal)),
            "DOCX: a bookmark in a header is a target for a REF field.");

        using var odtBuffer = new MemoryStream();
        var odt = new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var odtMessages = Messages(odt);
        Require(odtMessages.Any(message => message.Contains("the REF field to the bookmark 'Missing' has no target", StringComparison.Ordinal)),
            "ODT: a REF field without a target is reported.");
        // ODT writes a REF as text:bookmark-ref (OF-C02); only a REF that ODF cannot express
        // is written as text and reported.
        Require(!odtMessages.Any(message => message.Contains("the field 'REF Range' is written as text", StringComparison.Ordinal)),
            "ODT: a mapped REF field is not reported (OF-C02).");
        var odtContent = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(odtContent.Contains("<text:bookmark-ref", StringComparison.Ordinal)
                && odtContent.Contains("text:ref-name=\"Range\"", StringComparison.Ordinal),
            "ODT: REF is written as text:bookmark-ref (OF-C02).");

        // A cached result (what a read document carries) is written as the text of the field.
        var cached = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Field("REF Range", "range text"))));
        using var cachedBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(cached, cachedBuffer).GetAwaiter().GetResult();
        Require(ReadZipEntry(cachedBuffer.ToArray(), "content.xml").Contains("range<text:s />text", StringComparison.Ordinal),
            "ODT: the cached result of a REF field is written as text.");

        using var pdfBuffer = new MemoryStream();
        var pdf = new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(pdfBuffer.ToArray()).Contains("(range text) Tj", StringComparison.Ordinal)
                || DocumentReader.ReadPdfPages(new MemoryStream(pdfBuffer.ToArray())).Any(page => page.Contains("range text", StringComparison.Ordinal)),
            "PDF: the REF field shows the text of the bookmark.");
        Require(Messages(pdf).Any(message => message.Contains("the REF field to the bookmark 'Missing' has no target", StringComparison.Ordinal)),
            "PDF: a REF field without a target is reported: " + string.Join("; ", Messages(pdf)));
        Require(Messages(pdf).Count(message => message.Contains("'Missing'", StringComparison.Ordinal)) == 1,
            "PDF: the dangling REF field is reported once: " + string.Join("; ", Messages(pdf)));
        Require(!Messages(pdf).Any(message => message.Contains("'Range'", StringComparison.Ordinal)),
            "PDF: a REF field with a target is not reported.");
    }

    private static PortableDocument RangeDocument()
        => new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Inline[]
            {
                new Bookmark("Range"),
                new TextRun("range text"),
                new BookmarkEnd("Range"),
                new TextRun(" after "),
                new Bookmark("Point"),
                new TextRun(" point"),
            })));

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void WritesPdfOutline()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(BookmarkDocument(), buffer).GetAwaiter().GetResult();

        var text = Encoding.Latin1.GetString(buffer.ToArray());
        Require(text.Contains("/Type /Outlines", StringComparison.Ordinal), "PDF: outline root.");
        Require(text.Contains("/Title (First)", StringComparison.Ordinal), "PDF: first outline entry.");
        Require(text.Contains("/Title (Second)", StringComparison.Ordinal), "PDF: second outline entry.");
        Require(text.Contains("/Dests", StringComparison.Ordinal), "PDF: named destinations.");
        Require(text.Contains("(MyTarget)", StringComparison.Ordinal), "PDF: bookmark destination name.");

        // The second-level heading is a child of the first-level heading.
        var firstIndex = text.IndexOf("/Title (First)", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("/Title (Second)", StringComparison.Ordinal);
        Require(firstIndex >= 0 && secondIndex > firstIndex, "PDF: outline order.");

        // The produced PDF stays readable (xref, page tree).
        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Pdf);
        Require(read.Document.Sections.Count >= 1, "PDF: readable.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentException>(() => new Bookmark(" "));
    }

    private static void FindsBookmarkInsideHyperlink()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[] { new PageRefField("Nested") }),
            new PageBreak(),
            new Paragraph(new Inline[] { new Hyperlink("https://example.com", new Inline[]
            {
                new Bookmark("Nested"), new TextRun("target"),
            }) }),
        }));
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        Require(pdf.Contains("(Nested)", StringComparison.Ordinal), "PDF: nested bookmark destination.");
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains("(2) Tj", StringComparison.Ordinal),
            "PDF: page reference resolves the nested bookmark.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("Nested", StringComparison.Ordinal)),
            "PDF: the nested bookmark is resolved without warning.");
    }

    private static void FindsBookmarkOnLaterTablePage()
    {
        var rows = Enumerable.Range(0, 100).Select(_ => new TableRow(new TableCell("row"))).ToList();
        rows.Add(new TableRow(new TableCell(new Paragraph(new Inline[]
        {
            new Hyperlink("https://example.com", [new Bookmark("CellTarget"), new TextRun("target")]),
        }))));
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new PageRefField("CellTarget")),
            new TableBlock(rows),
        }));
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages.Count > 1 && pages[^1].Contains("target", StringComparison.Ordinal), "PDF: target is on a later table page.");
        Require(pages[0].Contains(pages.Count.ToString(), StringComparison.Ordinal), "PDF: page reference uses the placed table page.");
        Require(Encoding.Latin1.GetString(buffer.ToArray()).Contains("(CellTarget)", StringComparison.Ordinal), "PDF: table bookmark has a destination.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("CellTarget", StringComparison.Ordinal)), "PDF: table bookmark resolves.");
    }

    private static void KeepsRepeatedHeaderBookmarkOnFirstPage()
    {
        var nested = new TableBlock([new TableRow(new TableCell(new Paragraph(new Inline[]
        {
            new Bookmark("HeaderTarget"), new TextRun("header"),
        })))]);
        var rows = new List<TableRow> { new(new TableCell(nested)) { IsHeader = true } };
        rows.AddRange(Enumerable.Range(0, 100).Select(_ => new TableRow(new TableCell("row"))));
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new PageRefField("HeaderTarget")), new TableBlock(rows),
        }));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages.Count > 1 && pages[^1].Contains("header", StringComparison.Ordinal), "PDF: nested header is repeated.");
        Require(pages[0].Contains("1", StringComparison.Ordinal), "PDF: header bookmark retains its first page.");
    }

    private static void ResolvesPageReferenceInTable()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new TableBlock([new TableRow(new TableCell(new Paragraph(new PageRefField("Later"))))]),
            new PageBreak(),
            new Paragraph(new Inline[] { new Bookmark("Later"), new TextRun("target") }),
        }));
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages[0].Contains("2", StringComparison.Ordinal), "PDF: a table cell resolves a forward page reference.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("Later", StringComparison.Ordinal)), "PDF: table page reference resolves without warning.");
    }

    private static void FindsBookmarkOnItsActualLine()
    {
        foreach (var inTable in new[] { false, true })
        {
            var longParagraph = new Paragraph(new Inline[]
            {
                new TextRun(string.Concat(Enumerable.Repeat("filler ", 150))),
                new Hyperlink("https://example.com", [new Bookmark("LaterLine"), new TextRun("TARGETWORD")]),
            });
            Block content = inTable ? new TableBlock([new TableRow(new TableCell(longParagraph))]) : longParagraph;
            var section = new Section(new Block[] { new Paragraph(new Inline[] { new TextRun("REF"), new PageRefField("LaterLine") }), content })
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            var result = new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
            var target = Enumerable.Range(1, pages.Count - 1).FirstOrDefault(index => pages[index].Contains("TARGETWORD", StringComparison.Ordinal));
            Require(target > 0 && pages[0].Contains((target + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                $"PDF: bookmark in {(inTable ? "table" : "body")} resolves to its actual line's page.");
            Require(!result.Warnings.Any(warning => warning.Message.Contains("LaterLine", StringComparison.Ordinal)), "PDF: bookmark resolves without warning.");
        }
    }

    private static void OrdersOutlineByDocumentInSplitRow()
    {
        var first = new TableCell(new Block[]
        {
            new Paragraph(new TextRun(string.Concat(Enumerable.Repeat("filler ", 150)))),
            new Heading(HeadingLevel.Level1, new TextRun("FirstCell")),
        });
        var second = new TableCell(new Heading(HeadingLevel.Level1, new TextRun("SecondCell")));
        var table = new TableBlock([new TableRow(first, second)]);
        foreach (var withContents in new[] { false, true })
        {
            var blocks = withContents ? new Block[] { new TableOfContents(), table } : [table];
            var section = new Section(blocks)
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var pdf = Encoding.Latin1.GetString(buffer.ToArray());
            var firstIndex = pdf.IndexOf("/Title (FirstCell)", StringComparison.Ordinal);
            var secondIndex = pdf.IndexOf("/Title (SecondCell)", StringComparison.Ordinal);
            Require(firstIndex >= 0 && secondIndex > firstIndex,
                $"PDF: split-row outline follows document order with contents={withContents}.");
        }
    }

    private static void BookmarkDoesNotChangeJustification()
    {
        static TextRun Run(string text) => new(text) { FontFamily = "Courier New", FontSize = Length.FromPoints(10) };
        var paragraph = new Paragraph(new Inline[]
        {
            Run("aaaa bbbb "), new Bookmark("Here"), Run("cccc dddd eeee ffff gggg hhhh"),
        }) { Style = ParagraphStyle.Default with { Alignment = TextAlignment.Justify } };
        var section = new Section(paragraph)
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(200)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        var match = System.Text.RegularExpressions.Regex.Match(PdfStreams.Decompress(buffer.ToArray()),
            @"([0-9.]+) [0-9.]+ Td\n\(ffff\) Tj");
        Require(match.Success && Math.Abs(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 166) < 0.01,
            "PDF: a zero-width bookmark does not count as a justification gap.");
    }

    /// <summary>
    /// OF-C03: a link or a page reference whose bookmark does not exist, or that sits in a header
    /// or a footer, is written as it is because Word and LibreOffice keep such a reference too.
    /// Every format reports it. The bookmarks the document really declares, including the one
    /// inside a hyperlink, the forward reference and the reference from a note or from a band, stay
    /// without a warning.
    /// </summary>
    private static void ReportsReferencesWithoutATarget()
    {
        var section = new Section(new Block[]
        {
            new Paragraph(new Inline[] { new Hyperlink("#Reachable", "the target"), new TextRun(" comes later.") }),
            new Paragraph(new Hyperlink("#Absent", "a link without a target")),
            new Paragraph(new Inline[] { new PageRefField("Absent"), new TextRun(" and "), new Hyperlink("#Absent", "the same name again") }),
            new Paragraph(new Inline[] { new Hyperlink("#BandTarget", "a link into the footer"), new TextRun(" and "), new PageRefField("BandTarget") }),
            new Paragraph(new Inline[]
            {
                new Hyperlink("https://example.com", [new Bookmark("Reachable"), new TextRun("the target itself")]),
            }),
            new Paragraph(new Inline[]
            {
                new TextRun("A note "),
                new Footnote(new Paragraph(new Inline[]
                {
                    new Bookmark("NoteTarget"), new Hyperlink("#AbsentNote", "a link inside the note"),
                })),
            }),
            new Paragraph(new Hyperlink("#NoteTarget", "a link into the note")),
        })
        {
            Footer = new HeaderFooter(new Paragraph(new Inline[]
            {
                new Bookmark("BandTarget"), new TextRun("the footer "), new Hyperlink("#Reachable", "jumps up"),
            })),
        };
        var document = new PortableDocument(new DocumentMetadata(), section);

        using var docxBuffer = new MemoryStream();
        var docx = new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var docxMessages = Messages(docx);
        Require(docxMessages.Any(message => message.Contains("DOCX: the link to the bookmark 'Absent' has no target", StringComparison.Ordinal)),
            "DOCX: a link without a target is reported.");
        Require(docxMessages.Any(message => message.Contains("the link to the bookmark 'BandTarget' points into a header or footer", StringComparison.Ordinal)),
            "DOCX: a link into a header or footer is reported.");
        Require(docxMessages.Any(message => message.Contains("the page reference to the bookmark 'Absent' has no target", StringComparison.Ordinal)),
            "DOCX: a page reference without a target is reported.");
        Require(docxMessages.Any(message => message.Contains("the page reference to the bookmark 'BandTarget' points into a header or footer", StringComparison.Ordinal)),
            "DOCX: a page reference into a header or footer is reported.");
        Require(docxMessages.Any(message => message.Contains("the link to the bookmark 'AbsentNote'", StringComparison.Ordinal)),
            "DOCX: a link inside a footnote is reported.");
        Require(docxMessages.Count(message => message.Contains("'Absent'", StringComparison.Ordinal)) == 2,
            "DOCX: a name is reported once per kind of reference.");
        Require(!docxMessages.Any(message => message.Contains("'Reachable'", StringComparison.Ordinal)),
            "DOCX: a bookmark that exists is not reported.");
        Require(!docxMessages.Any(message => message.Contains("'NoteTarget'", StringComparison.Ordinal)),
            "DOCX: a bookmark inside a footnote is a target.");
        var docxNotes = ReadZipEntry(docxBuffer.ToArray(), "word/footnotes.xml");
        Require(docxNotes.Contains("w:name=\"NoteTarget\"", StringComparison.Ordinal),
            "DOCX: the bookmark inside the footnote is written as a real bookmark.");
        var docxBody = ReadZipEntry(docxBuffer.ToArray(), "word/document.xml");
        Require(docxBody.Contains("w:anchor=\"Absent\"", StringComparison.Ordinal)
                && docxBody.Contains("PAGEREF Absent", StringComparison.Ordinal),
            "DOCX: the unresolved reference is written as it is.");

        using var odtBuffer = new MemoryStream();
        var odt = new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var odtMessages = Messages(odt);
        var odtContent = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(odtMessages.Any(message => message.Contains("ODT: the link to the bookmark 'Absent' has no target", StringComparison.Ordinal)),
            "ODT: a link without a target is reported.");
        Require(odtMessages.Any(message => message.Contains("ODT: the page reference to the bookmark 'BandTarget' points into a header or footer", StringComparison.Ordinal)),
            "ODT: a page reference into a header or footer is reported.");
        Require(odtMessages.Any(message => message.Contains("the link to the bookmark 'AbsentNote'", StringComparison.Ordinal)),
            "ODT: a link inside a footnote is reported.");
        Require(!odtMessages.Any(message => message.Contains("'Reachable'", StringComparison.Ordinal)),
            "ODT: a bookmark that exists is not reported.");
        Require(!odtMessages.Any(message => message.Contains("'NoteTarget'", StringComparison.Ordinal)),
            "ODT: a bookmark inside a footnote is a target.");
        Require(odtContent.Contains("text:name=\"NoteTarget\"", StringComparison.Ordinal),
            "ODT: the bookmark inside the footnote is written as a real bookmark.");
        Require(odtContent.Contains("xlink:href=\"#Absent\"", StringComparison.Ordinal)
                && odtContent.Contains("text:ref-name=\"Absent\"", StringComparison.Ordinal),
            "ODT: the unresolved reference is written as it is.");

        using var pdfBuffer = new MemoryStream();
        var pdf = new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        var pdfMessages = Messages(pdf);
        Require(pdfMessages.Any(message => message.Contains("PDF: the link to the bookmark 'Absent' has no target", StringComparison.Ordinal)),
            "PDF: a link without a target is reported.");
        Require(!pdfMessages.Any(message => message.Contains("'BandTarget'", StringComparison.Ordinal)),
            "PDF: a band bookmark gets a destination, so its link and page reference are not reported.");
        Require(pdfMessages.Any(message => message.Contains("the link to the bookmark 'AbsentNote'", StringComparison.Ordinal)),
            "PDF: a link inside a footnote is reported.");
        Require(pdfMessages.Any(message => message.Contains("the page reference to the bookmark 'Absent' is not resolved (bookmark not found)", StringComparison.Ordinal)),
            "PDF: a page reference without a target says that the bookmark is missing.");
        Require(PdfStreams.Decompress(pdfBuffer.ToArray()).Contains("(1) Tj", StringComparison.Ordinal),
            "PDF: the page reference to a band bookmark resolves to the first page.");
        Require(!pdfMessages.Any(message => message.Contains("'NoteTarget'", StringComparison.Ordinal)),
            "PDF: a bookmark inside a note is reachable, so the link to it is not reported.");
        Require(!pdfMessages.Any(message => message.Contains("'Reachable'", StringComparison.Ordinal)),
            "PDF: a bookmark that exists is not reported.");
        Require(Encoding.Latin1.GetString(pdfBuffer.ToArray()).Contains("/Dest (Absent)", StringComparison.Ordinal),
            "PDF: the unresolved link keeps its annotation.");
    }

    private static IReadOnlyList<string> Messages(WriteResult result)
        => result.Warnings.Select(warning => warning.Message).ToList();

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
