using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// Footnotes and endnotes (phase Z, Z08): DOCX writes the footnotes/endnotes parts, ODT and
/// PDF report the notes they do not render.
/// </summary>
internal static class NoteChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxNotes();
            ReadsDocxNotes();
            ReportsOdtNotes();
            ReadsOdtNotes();
            ReportsPdfNotes();
            WritesBandNotesAsText();
            KeepsSourceBandNotesWhenTheBandIsPreserved();
            WrapsLongFootnoteWords();
            PlacesFootnoteWithReference();
            PlacesTableFootnoteWithReference();
            SplitsFootnotesAcrossPages();
            KeepsFootnotesAcrossContinuousSections();
            PlacesNestedTableFootnoteWithReference();
            KeepsReferencesWhenNestedTableIsFlattened();
            RejectsInvalidValues();
            Console.WriteLine("Notes (Z08): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Notes (Z08): " + exception);
            return 1;
        }
    }

    private static PortableDocument NoteDocument()
        => new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new Inline[]
                {
                    new TextRun("Text"),
                    new Footnote(new Paragraph(new TextRun("Footnote text"))),
                    new TextRun(" more"),
                    new Footnote(new Paragraph(new TextRun("Endnote text"))) { Kind = NoteKind.Endnote },
                }),
            }));

    private static void WritesDocxNotes()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(NoteDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var body = ReadZipEntry(package, "word/document.xml");
        Require(body.Contains("<w:footnoteReference w:id=\"1\"", StringComparison.Ordinal), "DOCX: footnote reference.");
        Require(body.Contains("<w:endnoteReference w:id=\"1\"", StringComparison.Ordinal), "DOCX: endnote reference.");

        var footnotes = ReadZipEntry(package, "word/footnotes.xml");
        Require(footnotes.Contains("<w:footnote w:id=\"1\"", StringComparison.Ordinal), "DOCX: footnote part.");
        Require(footnotes.Contains("<w:footnoteRef", StringComparison.Ordinal), "DOCX: footnoteRef marker.");

        var endnotes = ReadZipEntry(package, "word/endnotes.xml");
        Require(endnotes.Contains("<w:endnote w:id=\"1\"", StringComparison.Ordinal), "DOCX: endnote part.");

        var contentTypes = ReadZipEntry(package, "[Content_Types].xml");
        Require(contentTypes.Contains("footnotes+xml", StringComparison.Ordinal), "DOCX: footnotes content type.");
        Require(contentTypes.Contains("endnotes+xml", StringComparison.Ordinal), "DOCX: endnotes content type.");

        var rels = ReadZipEntry(package, "word/_rels/document.xml.rels");
        Require(rels.Contains("relationships/footnotes", StringComparison.Ordinal), "DOCX: footnotes relationship.");
        Require(rels.Contains("relationships/endnotes", StringComparison.Ordinal), "DOCX: endnotes relationship.");

        var styles = ReadZipEntry(package, "word/styles.xml");
        Require(styles.Contains("w:styleId=\"FootnoteReference\"", StringComparison.Ordinal), "DOCX: footnote style.");
    }

    private static void ReadsDocxNotes()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(NoteDocument(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;

        var notes = read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<Footnote>()
            .ToList();

        Require(notes.Count == 2, "DOCX: two notes read.");
        Require(notes[0].Kind == NoteKind.Footnote, "DOCX: the footnote kind is read.");
        Require(notes[1].Kind == NoteKind.Endnote, "DOCX: the endnote kind is read.");
        Require(
            notes[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines)
                .OfType<TextRun>().Any(run => run.Text == "Footnote text"),
            "DOCX: the footnote content is read.");
    }

    private static void ReportsOdtNotes()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(NoteDocument(), buffer).GetAwaiter().GetResult();
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("footnote", StringComparison.Ordinal)),
            "ODT: no note warning.");

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("text:note-class=\"footnote\"", StringComparison.Ordinal), "ODT: footnote.");
        Require(content.Contains("text:note-class=\"endnote\"", StringComparison.Ordinal), "ODT: endnote.");
        Require(content.Contains("<text:note-body>", StringComparison.Ordinal), "ODT: note body.");
        Require(content.Contains("<text:note-citation>", StringComparison.Ordinal), "ODT: note citation.");
    }

    private static void ReadsOdtNotes()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(NoteDocument(), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;

        var notes = read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .OfType<Footnote>()
            .ToList();

        Require(notes.Count == 2, "ODT: two notes read.");
        Require(notes[0].Kind == NoteKind.Footnote && notes[1].Kind == NoteKind.Endnote, "ODT: the note kinds are read.");
        Require(
            notes[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines)
                .OfType<TextRun>().Any(run => run.Text == "Footnote text"),
            "ODT: the footnote content is read.");
    }

    private static void ReportsPdfNotes()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(NoteDocument(), buffer).GetAwaiter().GetResult();
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("endnotes are not rendered", StringComparison.Ordinal)),
            "PDF: endnotes rendered.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(1. Footnote text)", StringComparison.Ordinal), "PDF: footnote text at the page end.");
        Require(content.Contains("(Endnotes)", StringComparison.Ordinal), "PDF: endnote heading at the document end.");
        Require(content.Contains("(Endnote)", StringComparison.Ordinal), "PDF: endnote text at the document end.");

        var read = DocumentReader.Read(new MemoryStream(buffer.ToArray()), DocumentFormat.Pdf);
        Require(read.Document.Sections.Count >= 1, "PDF: readable.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentNullException>(() => new Footnote((Block)null!));
    }

    private static void WrapsLongFootnoteWords()
    {
        var word = new string('W', 250);
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("Reference"), new Footnote(new Paragraph(new TextRun(word))),
        })));
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var pieces = System.Text.RegularExpressions.Regex.Matches(content, @"\((?:1\. )?(W+)\) Tj")
            .Select(match => match.Groups[1].Value).ToArray();
        Require(pieces.Length > 1 && string.Concat(pieces) == word, "PDF: a long footnote word wraps without losing text.");
        Require(pieces.All(piece => piece.Length < 80), "PDF: no fragment exceeds the available line width.");
    }

    private static void PlacesFootnoteWithReference()
    {
        for (var count = 2; count <= 8; count++)
        {
            var blocks = Enumerable.Range(0, count).Select(_ => (Block)new Paragraph(new TextRun("Filler"))).ToList();
            blocks.Add(new Paragraph(new Inline[] { new TextRun("MARKERWORD"), new Footnote(new Paragraph(new TextRun("NOTEWORD"))) }));
            var section = new Section(blocks)
            {
                PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
            Require(pages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
                $"PDF: footnote and reference share a page after {count} paragraphs.");
        }

        var longParagraph = new Paragraph(new Inline[]
        {
            new TextRun(string.Concat(Enumerable.Repeat("filler ", 120)) + "MARKERWORD"),
            new Footnote(new Paragraph(new TextRun("NOTEWORD"))),
        });
        using var longBuffer = new MemoryStream();
        var longSection = new Section(longParagraph)
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), longSection), longBuffer).GetAwaiter().GetResult();
        var longPages = DocumentReader.ReadPdfPages(new MemoryStream(longBuffer.ToArray()));
        Require(longPages.Count > 1 && longPages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
            "PDF: footnote and reference share a page in a split paragraph.");
    }

    private static void PlacesTableFootnoteWithReference()
    {
        var rows = Enumerable.Range(0, 20).Select(_ => new TableRow(new TableCell("Filler"))).ToList();
        rows.Add(new TableRow(new TableCell(new Paragraph(new Inline[]
        {
            new TextRun("MARKERWORD"), new Footnote(new Paragraph(new TextRun("NOTEWORD"))),
        }))));
        var section = new Section(new TableBlock(rows))
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages.Count > 1 && pages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
            "PDF: table footnote and reference share their placed page.");

        var splitCell = new TableCell(new Paragraph(new Inline[]
        {
            new TextRun(string.Concat(Enumerable.Repeat("filler ", 100)) + "MARKERWORD"),
            new Footnote(new Paragraph(new TextRun("NOTEWORD"))),
        }));
        using var splitBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section with
        {
            Blocks = [new TableBlock([new TableRow(splitCell)])],
        }), splitBuffer).GetAwaiter().GetResult();
        var splitPages = DocumentReader.ReadPdfPages(new MemoryStream(splitBuffer.ToArray()));
        Require(splitPages.Count > 1 && splitPages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
            "PDF: footnote stays with its reference in a split table row.");

        var header = new TableRow(new TableCell(new Paragraph(new Inline[]
        {
            new TextRun("HEADERWORD"), new Footnote(new Paragraph(new TextRun("HEADERNOTE"))),
        }))) { IsHeader = true };
        var headerRows = new List<TableRow> { header };
        headerRows.AddRange(Enumerable.Range(0, 20).Select(_ => new TableRow(new TableCell("Filler"))));
        using var headerBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section with
        {
            Blocks = [new TableBlock(headerRows)],
        }), headerBuffer).GetAwaiter().GetResult();
        var headerPages = DocumentReader.ReadPdfPages(new MemoryStream(headerBuffer.ToArray()));
        Require(headerPages.Count > 1 && headerPages[^1].Contains("HEADERWORD", StringComparison.Ordinal), "PDF: table header repeats.");
        Require(headerPages.Count(page => page.Contains("HEADERNOTE", StringComparison.Ordinal)) == 1 && headerPages[0].Contains("HEADERNOTE", StringComparison.Ordinal),
            "PDF: repeated headers do not repeat footnotes.");
    }

    private static void SplitsFootnotesAcrossPages()
    {
        var noteText = "STARTNOTE " + string.Join(" ", Enumerable.Range(0, 180).Select(index => "word" + index)) + " ENDNOTE";
        var note = new Footnote(new Paragraph(new TextRun(noteText)));
        foreach (var inTable in new[] { false, true })
        {
            var paragraph = new Paragraph(new Inline[] { new TextRun("REFERENCE"), note });
            Block content = inTable ? new TableBlock([new TableRow(new TableCell(paragraph))]) : paragraph;
            var section = new Section(content)
            {
                PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
            var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
            Require(pages.Count > 2 && pages[0].Contains("REFERENCE", StringComparison.Ordinal)
                && pages[0].Contains("STARTNOTE", StringComparison.Ordinal) && pages[^1].Contains("ENDNOTE", StringComparison.Ordinal),
                $"PDF: long {(inTable ? "table" : "body")} footnotes continue after their reference page.");
            var allText = string.Join(" ", pages);
            for (var index = 0; index < 180; index++)
            {
                var word = "word" + index;
                Require(System.Text.RegularExpressions.Regex.Matches(allText, $@"\b{word}\b").Count == 1,
                    $"PDF: split footnote retains {word} exactly once.");
            }

            var following = new Paragraph(new Inline[] { new TextRun("NEXTREFERENCE"),
                new Footnote(new Paragraph(new TextRun("NEXTNOTE"))) });
            using var followingBuffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section with
            {
                Blocks = [content, following],
            }), followingBuffer).GetAwaiter().GetResult();
            var followingPages = DocumentReader.ReadPdfPages(new MemoryStream(followingBuffer.ToArray()));
            Require(followingPages.Any(page => page.Contains("NEXTREFERENCE", StringComparison.Ordinal)
                && page.Contains("NEXTNOTE", StringComparison.Ordinal)), "PDF: a later footnote remains on its reference page after a continuation.");
            Require(Array.FindIndex(followingPages.ToArray(), page => page.Contains("ENDNOTE", StringComparison.Ordinal))
                <= Array.FindIndex(followingPages.ToArray(), page => page.Contains("NEXTNOTE", StringComparison.Ordinal)),
                "PDF: long footnotes finish before later notes.");

            using var sectionsBuffer = new MemoryStream();
            var nextSection = new Section(new Paragraph(new TextRun("NEXTSECTION")))
            {
                PageSize = new PageSize(Length.FromPoints(250), Length.FromPoints(150)),
                Margins = new PageMargins(Length.FromPoints(10)),
            };
            new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), [section, nextSection]), sectionsBuffer)
                .GetAwaiter().GetResult();
            var sectionPages = DocumentReader.ReadPdfPages(new MemoryStream(sectionsBuffer.ToArray()));
            Require(Array.FindIndex(sectionPages.ToArray(), page => page.Contains("ENDNOTE", StringComparison.Ordinal))
                < Array.FindIndex(sectionPages.ToArray(), page => page.Contains("NEXTSECTION", StringComparison.Ordinal)),
                "PDF: the previous section's footnotes finish before the next section starts.");
        }

        var twoNotes = new Paragraph(new Inline[]
        {
            new TextRun("BOTHREFERENCES"),
            new Footnote(new Paragraph(new TextRun("FIRSTSTART " + noteText + " FIRSTEND"))),
            new Footnote(new Paragraph(new TextRun("SECONDSTART " + noteText + " SECONDEND"))),
        });
        using var twoNotesBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(twoNotes)
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        }), twoNotesBuffer).GetAwaiter().GetResult();
        var twoNotePages = DocumentReader.ReadPdfPages(new MemoryStream(twoNotesBuffer.ToArray()));
        Require(twoNotePages[0].Contains("BOTHREFERENCES", StringComparison.Ordinal)
            && twoNotePages[0].Contains("FIRSTSTART", StringComparison.Ordinal)
            && twoNotePages[0].Contains("SECONDSTART", StringComparison.Ordinal)
            && twoNotePages[^1].Contains("SECONDEND", StringComparison.Ordinal),
            "PDF: two long notes on one line both start with their references and continue without loss.");
    }

    private static void KeepsFootnotesAcrossContinuousSections()
    {
        var section = new Section(new Paragraph(new Inline[]
        {
            new TextRun("REFERENCE"), new Footnote(new Paragraph(new TextRun("FOOTERNOTE"))),
        }))
        {
            PageSize = new PageSize(Length.FromPoints(220), Length.FromPoints(120)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        var next = section with
        {
            StartType = SectionStartType.Continuous,
            Blocks = [new Paragraph(new TextRun("BODYAFTER"))],
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), [section, next]), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        var match = System.Text.RegularExpressions.Regex.Match(content, @"[0-9.]+ ([0-9.]+) Td\n\(1\. FOOTERNOTE\) Tj");
        Require(match.Success && double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 10,
            "PDF: a continuous section preserves the reserved footnote area above the bottom margin.");
    }

    private static void PlacesNestedTableFootnoteWithReference()
    {
        var nested = new TableBlock([new TableRow(new TableCell(new Paragraph(new Inline[]
        {
            new TextRun("MARKERWORD"), new Footnote(new Paragraph(new TextRun("NOTEWORD"))),
        })))]);
        var rows = Enumerable.Range(0, 20).Select(_ => new TableRow(new TableCell("Filler"))).ToList();
        rows.Add(new TableRow(new TableCell(nested)));
        var section = new Section(new TableBlock(rows))
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(pages.Count > 1 && pages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
            "PDF: nested table footnote and reference share their placed page.");

        var nestedHeaderRows = new List<TableRow> { new(new TableCell(nested)) { IsHeader = true } };
        nestedHeaderRows.AddRange(Enumerable.Range(0, 20).Select(_ => new TableRow(new TableCell("Filler"))));
        using var headerBuffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section with
        {
            Blocks = [new TableBlock(nestedHeaderRows)],
        }), headerBuffer).GetAwaiter().GetResult();
        var headerPages = DocumentReader.ReadPdfPages(new MemoryStream(headerBuffer.ToArray()));
        Require(headerPages.Count > 1 && headerPages[^1].Contains("MARKERWORD", StringComparison.Ordinal), "PDF: nested table header repeats.");
        Require(headerPages.Count(page => page.Contains("NOTEWORD", StringComparison.Ordinal)) == 1 && headerPages[0].Contains("NOTEWORD", StringComparison.Ordinal),
            "PDF: a repeated nested table header does not repeat its footnote.");
    }

    private static void KeepsReferencesWhenNestedTableIsFlattened()
    {
        var rows = Enumerable.Range(0, 20).Select(_ => new TableRow(new TableCell("Filler"))).ToList();
        rows.Add(new TableRow(new TableCell(new Block[]
        {
            new Heading(HeadingLevel.Level2, new TextRun("NestedHeading")),
            new Paragraph(new Inline[]
            {
                new TextRun("MARKERWORD"), new Footnote(new Paragraph(new TextRun("NOTEWORD"))),
                new Footnote(new Paragraph(new TextRun("ENDNOTEWORD"))) { Kind = NoteKind.Endnote },
            }),
        })));
        var section = new Section(new Block[]
        {
            new TableOfContents(),
            new TableBlock([new TableRow(new TableCell(new TableBlock(rows)))]),
            new Heading(HeadingLevel.Level1, new TextRun("Following")),
        })
        {
            PageSize = new PageSize(Length.FromPoints(200), Length.FromPoints(100)),
            Margins = new PageMargins(Length.FromPoints(10)),
        };
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), section), buffer).GetAwaiter().GetResult();
        var pdf = System.Text.Encoding.Latin1.GetString(buffer.ToArray());
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray()));
        Require(result.Warnings.Any(warning => warning.Message.Contains("nested table", StringComparison.Ordinal)), "PDF: flattened nested table is reported.");
        Require(pages[0].Contains("NestedHeading", StringComparison.Ordinal) && pdf.Contains("/Title (NestedHeading)", StringComparison.Ordinal),
            "PDF: flattened table heading has a contents entry and outline.");
        Require(pdf.Contains("(_Toc0)", StringComparison.Ordinal) && pdf.Contains("(_Toc1)", StringComparison.Ordinal),
            "PDF: flattened and following headings have destinations.");
        Require(pages.Any(page => page.Contains("MARKERWORD", StringComparison.Ordinal) && page.Contains("NOTEWORD", StringComparison.Ordinal)),
            "PDF: flattened table footnote stays with its reference.");
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains("(1. NOTEWORD)", StringComparison.Ordinal),
            "PDF: the discarded layout does not consume a footnote number.");
        Require(pages.Sum(page => page.Split("ENDNOTEWORD", StringSplitOptions.None).Length - 1) == 1,
            "PDF: the discarded layout does not duplicate an endnote.");
    }

    private static void WritesBandNotesAsText()
    {
        // A band carries no note in any format: Word shows none there, LibreOffice drops a
        // text:note and cannot open a DOCX whose band part has a note reference. Every writer
        // keeps the text visible at the reference and reports it once.
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new Inline[]
            {
                new TextRun("Head "), new Footnote(new Paragraph(new TextRun("Band note"))),
            })),
        });

        using var docxBuffer = new MemoryStream();
        var docxResult = new DocxDocumentWriter().WriteAsync(document, docxBuffer).GetAwaiter().GetResult();
        var docx = docxBuffer.ToArray();
        Require(
            docxResult.Warnings.Count(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)) == 1,
            "DOCX: a band note is reported once.");
        var header = ReadZipEntry(docx, "word/header1.xml");
        Require(header.Contains("Band note", StringComparison.Ordinal), "DOCX: the text of a band note is written in the band.");
        Require(!header.Contains("footnoteReference", StringComparison.Ordinal), "DOCX: a band note is no note reference.");
        using (var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read))
        {
            Require(zip.GetEntry("word/footnotes.xml") is null, "DOCX: a band note needs no footnotes part.");
        }

        using var odtBuffer = new MemoryStream();
        var odtResult = new OdtDocumentWriter().WriteAsync(document, odtBuffer).GetAwaiter().GetResult();
        var odt = ReadZipEntry(odtBuffer.ToArray(), "styles.xml");
        Require(
            odtResult.Warnings.Count(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)) == 1,
            "ODT: a band note is reported once.");
        Require(odt.Contains("Band<text:s />note", StringComparison.Ordinal), "ODT: the text of a band note is written in the band.");
        Require(!odt.Contains("text:note", StringComparison.Ordinal), "ODT: a band note is no text:note.");

        using var pdfBuffer = new MemoryStream();
        var pdfResult = new PdfDocumentWriter().WriteAsync(document, pdfBuffer).GetAwaiter().GetResult();
        Require(
            pdfResult.Warnings.Count(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)) == 1,
            "PDF: a band note is reported once.");
        var content = PdfStreams.Decompress(pdfBuffer.ToArray());
        Require(
            content.Contains("(Head) Tj", StringComparison.Ordinal) && content.Contains("(Band) Tj", StringComparison.Ordinal)
                && content.Contains("(note) Tj", StringComparison.Ordinal),
            "PDF: the text of a band note is drawn in the band.");
        Require(!content.Contains("(1. Band note)", StringComparison.Ordinal), "PDF: a band note is no footnote at the page end.");

        // A note in the body is unaffected: it stays a note in every format, and a band note does
        // not take its id.
        using var bodyBuffer = new MemoryStream();
        var mixed = document with
        {
            Sections =
            [
                document.Sections[0] with
                {
                    Blocks = [new Paragraph(new Inline[] { new TextRun("Body"), new Footnote(new Paragraph(new TextRun("Body note"))) })],
                },
            ],
        };
        var bodyResult = new DocxDocumentWriter().WriteAsync(mixed, bodyBuffer).GetAwaiter().GetResult();
        Require(
            bodyResult.Warnings.Count(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)) == 1,
            "DOCX: only the band note is reported next to a body note.");
        Require(
            ReadZipEntry(bodyBuffer.ToArray(), "word/document.xml").Contains("<w:footnoteReference w:id=\"1\"", StringComparison.Ordinal),
            "DOCX: a body note keeps the first note id next to a band note.");
        var footnotes = ReadZipEntry(bodyBuffer.ToArray(), "word/footnotes.xml");
        Require(
            footnotes.Split("<w:footnote w:id=").Length - 1 == 1,
            "DOCX: only the body note is written to the notes part.");
    }

    private static void KeepsSourceBandNotesWhenTheBandIsPreserved()
    {
        // An ODT of another tool can hold a text:note in a master-page band. When the band is
        // unchanged, styles.xml is adopted verbatim: the note stays there and no warning claims
        // that it was turned into text.
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Head"))),
        });
        var package = TestPackages.Write(source, DocumentFormat.Odt);
        var styles = Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml"));
        const string note = "<text:note text:id=\"ftn1\" text:note-class=\"footnote\">"
            + "<text:note-citation>1</text:note-citation><text:note-body>"
            + "<text:p>band note</text:p></text:note-body></text:note>";
        Require(styles.Contains(">Head<", StringComparison.Ordinal), "The generated band holds the header text.");
        styles = styles.Replace(">Head<", ">Head" + note + "<", StringComparison.Ordinal);
        var withNote = TestPackages.ReplacePart(package, "styles.xml", Encoding.UTF8.GetBytes(styles));

        var read = DocumentReader.Read(new MemoryStream(withNote), DocumentFormat.Odt).Document;
        Require(
            read.Sections[0].Header?.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines).OfType<Footnote>().Count() == 1,
            "ODT: a note in a band is read as a note.");

        var changed = read with
        {
            Sections = [read.Sections[0] with { Blocks = [new Paragraph(new TextRun("Changed"))] }],
        };
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(changed, buffer).GetAwaiter().GetResult();
        var written = Encoding.UTF8.GetString(TestPackages.ReadPart(buffer.ToArray(), "styles.xml"));
        Require(written.Contains("text:note", StringComparison.Ordinal), "ODT: an adopted band keeps its note.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("note in a header or footer", StringComparison.Ordinal)),
            "ODT: a kept band note is not reported as text.");
        Require(
            Encoding.UTF8.GetString(TestPackages.ReadPart(buffer.ToArray(), "content.xml")).Contains("Changed", StringComparison.Ordinal),
            "ODT: the changed body is written.");
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
