using System.IO.Compression;
using Reglet;

/// <summary>
/// document structure (phase Z, Z-a: Z01/Z05/Z07): fields (page number, total page count,
/// date), custom properties and paper sizes in DOCX, ODT and PDF.
/// </summary>
internal static class DocumentStructureChecks
{
    internal static int Run()
    {
        try
        {
            SupportsPaperSizes();
            WritesFieldsDocx();
            WritesFieldsOdt();
            WritesCustomPropertiesDocx();
            WritesCustomPropertiesOdt();
            RoundTripsDocumentVariables();
            WritesDateFieldPdf();
            WritesReferenceFields();
            ReadsDocxFields();
            ReadsOdtFields();
            WritesNamedStyleDefinition();
            RendersPdfTabStops();
            RendersPdfColumns();
            WrapsPdfFootnotes();
            SeparatesPdfFootnoteParagraphs();
            ExtractsAndMergesPdfPages();
            ExtractsPdfOutlineHierarchy();
            MergesPdfOutlineHierarchy();
            ExtractsPdfAttachments();
            Console.WriteLine("Document structure (Z-a): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Document structure (Z-a): " + exception);
            return 1;
        }
    }

    private static PortableDocument FieldDocument()
        => new(new DocumentMetadata
        {
            Title = "Fields",
            CustomProperties = new Dictionary<string, string> { ["Project"] = "Reglet", ["Revision"] = "42" },
        }, new Section(new Paragraph(new Inline[]
        {
            new TextRun("Page "),
            new PageNumberField(),
            new TextRun(" of "),
            new PageCountField(),
            new TextRun(", Date: "),
            new DateField(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero)),
        })));

    private static void SupportsPaperSizes()
    {
        Require(PageSize.A5.Width.Points < PageSize.A4.Width.Points, "A5 is narrower than A4.");
        Require(PageSize.A3.Height.Points > PageSize.A4.Height.Points, "A3 is taller than A4.");
        Require(PageSize.Legal.Height.Points > PageSize.Letter.Height.Points, "Legal is taller than Letter.");
        var custom = new PageSize(Length.FromMillimeters(200), Length.FromMillimeters(300));
        Require(custom.Width.Points > 0, "Arbitrary paper size is possible.");
    }

    private static void WritesFieldsDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(FieldDocument(), buffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");

        Require(body.Contains("PAGE", StringComparison.Ordinal), "DOCX: page number field.");
        Require(body.Contains("NUMPAGES", StringComparison.Ordinal), "DOCX: page count field.");
        Require(body.Contains("DATE", StringComparison.Ordinal) && body.Contains("2026-09-25", StringComparison.Ordinal), "DOCX: date field with a result.");
    }

    private static void WritesFieldsOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(FieldDocument(), buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");

        Require(content.Contains("<text:page-number", StringComparison.Ordinal), "ODT: page number field.");
        Require(content.Contains("<text:page-count", StringComparison.Ordinal), "ODT: page count field.");
        Require(content.Contains("<text:date", StringComparison.Ordinal) && content.Contains("2026-09-25", StringComparison.Ordinal), "ODT: date field.");
    }

    private static void WritesCustomPropertiesDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(FieldDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var custom = ReadZipEntry(package, "docProps/custom.xml");
        Require(custom.Contains("name=\"Project\"", StringComparison.Ordinal) && custom.Contains("Reglet", StringComparison.Ordinal), "DOCX: user-defined property.");
        Require(custom.Contains("name=\"Revision\"", StringComparison.Ordinal), "DOCX: second property.");

        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        Require(zip.Entries.Any(entry => entry.FullName == "docProps/custom.xml"), "DOCX: custom.xml present.");
        var contentTypes = ReadZipEntry(package, "[Content_Types].xml");
        Require(contentTypes.Contains("custom-properties", StringComparison.Ordinal), "DOCX: content type of the custom properties.");
        Require(ReadZipEntry(package, "_rels/.rels").Contains("custom-properties", StringComparison.Ordinal), "DOCX: relationship to custom.xml.");
    }

    private static void WritesCustomPropertiesOdt()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(FieldDocument(), buffer).GetAwaiter().GetResult();
        var meta = ReadZipEntry(buffer.ToArray(), "meta.xml");

        Require(meta.Contains("meta:user-defined", StringComparison.Ordinal), "ODT: user-defined property.");
        Require(meta.Contains("meta:name=\"Project\"", StringComparison.Ordinal) && meta.Contains("Reglet", StringComparison.Ordinal), "ODT: name and value.");
    }

    private static PortableDocument ReferenceFieldsDocument()
        => new(new DocumentMetadata { Title = "Fields" }, new Section(new Paragraph(new Inline[]
        {
            new DocPropertyField("Title"),
            new TextRun(" "),
            new DocVariableField("Project"),
            new TextRun(" "),
            new PageRefField("Target"),
        })));

    private static void WritesReferenceFields()
    {
        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(ReferenceFieldsDocument(), docx).GetAwaiter().GetResult();
        var body = ReadZipEntry(docx.ToArray(), "word/document.xml");
        Require(body.Contains("DOCPROPERTY Title", StringComparison.Ordinal), "DOCX: DOCPROPERTY.");
        Require(body.Contains("DOCVARIABLE Project", StringComparison.Ordinal), "DOCX: DOCVARIABLE.");
        Require(body.Contains("PAGEREF Target", StringComparison.Ordinal), "DOCX: PAGEREF.");
        // Word does not calculate DOCPROPERTY on open; without a result the field stays empty.
        Require(body.Contains("<w:t>Fields</w:t>", StringComparison.Ordinal), "DOCX: DOCPROPERTY carries its result.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(ReferenceFieldsDocument(), odt).GetAwaiter().GetResult();
        var content = ReadZipEntry(odt.ToArray(), "content.xml");
        // The title is dc:title, so it is the text:title field; LibreOffice leaves user-defined empty.
        Require(content.Contains("<text:title>Fields</text:title>", StringComparison.Ordinal), "ODT: DOCPROPERTY Title is text:title.");
        Require(content.Contains("<text:user-field-get text:name=\"Project\"", StringComparison.Ordinal), "ODT: user-field-get.");
        Require(content.Contains("<text:bookmark-ref text:ref-name=\"Target\"", StringComparison.Ordinal), "ODT: bookmark-ref.");

        // AUTHOR is text:initial-creator, which shows meta:initial-creator.
        using var authored = new MemoryStream();
        var withAuthor = ReferenceFieldsDocument() with { Metadata = new DocumentMetadata { Author = "Reviewer" } };
        new OdtDocumentWriter().WriteAsync(withAuthor, authored).GetAwaiter().GetResult();
        Require(ReadZipEntry(authored.ToArray(), "meta.xml").Contains(">Reviewer</meta:initial-creator>", StringComparison.Ordinal), "ODT: meta:initial-creator.");

        // The PDF resolves the fields; the title is set, the variable and the bookmark are not.
        using var pdf = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(ReferenceFieldsDocument(), pdf).GetAwaiter().GetResult();
        Require(PdfStreams.Decompress(pdf.ToArray()).Contains("(Fields) Tj", StringComparison.Ordinal), "PDF: DOCPROPERTY Title is resolved.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("document variable 'Project'", StringComparison.Ordinal)),
            "PDF: an unset document variable is reported.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("bookmark 'Target'", StringComparison.Ordinal)),
            "PDF: a page reference to a missing bookmark is reported.");
    }

    private static void RoundTripsDocumentVariables()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Variables = new Dictionary<string, string> { ["Project"] = "Reglet", ["Empty"] = string.Empty } },
            new Section(new Paragraph(new DocVariableField("Project"))));

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        var settings = ReadZipEntry(docx.ToArray(), "word/settings.xml");
        Require(settings.Contains("<w:docVar w:name=\"Project\" w:val=\"Reglet\"", StringComparison.Ordinal), "DOCX: w:docVar.");
        Require(ReadZipEntry(docx.ToArray(), "word/document.xml").Contains("<w:t>Reglet</w:t>", StringComparison.Ordinal), "DOCX: DOCVARIABLE carries its result.");
        Require(settings.IndexOf("<w:compat>", StringComparison.Ordinal) < settings.IndexOf("<w:docVars>", StringComparison.Ordinal), "DOCX: w:docVars follows w:compat.");
        docx.Position = 0;
        var fromDocx = DocumentReader.Read(docx, DocumentFormat.Docx).Document;
        Require(fromDocx.Metadata.Variables is { } docxVariables && docxVariables["Project"] == "Reglet" && docxVariables["Empty"] == string.Empty, "DOCX: variables are read back.");

        // Rewriting a read document keeps a changed variable.
        using var rewritten = new MemoryStream();
        var changed = fromDocx with { Metadata = fromDocx.Metadata with { Variables = new Dictionary<string, string> { ["Project"] = "New" } } };
        new DocxDocumentWriter().WriteAsync(changed, rewritten).GetAwaiter().GetResult();
        var rewrittenSettings = ReadZipEntry(rewritten.ToArray(), "word/settings.xml");
        Require(rewrittenSettings.Contains("w:val=\"New\"", StringComparison.Ordinal) && !rewrittenSettings.Contains("Reglet", StringComparison.Ordinal), "DOCX: the model's variables replace the source's.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        var content = ReadZipEntry(odt.ToArray(), "content.xml");
        Require(content.Contains("<text:user-field-decl office:value-type=\"string\" office:string-value=\"Reglet\" text:name=\"Project\"", StringComparison.Ordinal), "ODT: user-field-decl.");
        Require(content.Contains("<text:user-field-get text:name=\"Project\">Reglet</text:user-field-get>", StringComparison.Ordinal), "ODT: user-field-get shows the value.");
        odt.Position = 0;
        var fromOdt = DocumentReader.Read(odt, DocumentFormat.Odt).Document;
        Require(fromOdt.Metadata.Variables is { } odtVariables && odtVariables["Project"] == "Reglet", "ODT: variables are read back.");
    }

    private static void ReadsDocxFields()
    {
        var inlines = ReadDocxInlines(FieldDocument());
        Require(inlines.OfType<PageNumberField>().Any(), "DOCX: PAGE read as PageNumberField.");
        Require(inlines.OfType<PageCountField>().Any(), "DOCX: NUMPAGES read as PageCountField.");
        Require(inlines.OfType<DateField>().Any(), "DOCX: DATE read as DateField.");

        var referenceInlines = ReadDocxInlines(ReferenceFieldsDocument());
        Require(referenceInlines.OfType<DocPropertyField>().Any(field => field.Name == "Title"), "DOCX: DOCPROPERTY read.");
        Require(referenceInlines.OfType<DocVariableField>().Any(field => field.Name == "Project"), "DOCX: DOCVARIABLE read.");
        Require(referenceInlines.OfType<PageRefField>().Any(field => field.BookmarkName == "Target"), "DOCX: PAGEREF read.");
    }

    private static List<Inline> ReadDocxInlines(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        return read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .ToList();
    }

    private static void ReadsOdtFields()
    {
        var inlines = ReadOdtInlines(FieldDocument());
        Require(inlines.OfType<PageNumberField>().Any(), "ODT: page number field read.");
        Require(inlines.OfType<PageCountField>().Any(), "ODT: page count field read.");
        Require(inlines.OfType<DateField>().Any(), "ODT: date field read.");

        var referenceInlines = ReadOdtInlines(ReferenceFieldsDocument());
        Require(referenceInlines.OfType<Field>().Any(field => field.Instruction == "TITLE" && field.Result == "Fields"), "ODT: text:title read.");
        Require(referenceInlines.OfType<DocVariableField>().Any(field => field.Name == "Project"), "ODT: user-field-get read.");
        Require(referenceInlines.OfType<PageRefField>().Any(field => field.BookmarkName == "Target"), "ODT: bookmark-ref read.");
    }

    private static List<Inline> ReadOdtInlines(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        return read.Sections
            .SelectMany(section => section.Blocks)
            .OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines)
            .ToList();
    }

    private static void WritesNamedStyleDefinition()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new Paragraph(new TextRun("Quote")) { Style = new ParagraphStyle { StyleName = "Quote" } }))
        {
            Styles = new[] { new ParagraphStyle { StyleName = "Quote", LeftIndent = Length.FromPoints(18) } },
        };

        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        var package = docx.ToArray();
        var styles = ReadZipEntry(package, "word/styles.xml");
        Require(styles.Contains("w:styleId=\"Quote\"", StringComparison.Ordinal), "DOCX: named style definition.");
        Require(styles.Contains("w:ind w:left=\"360\"", StringComparison.Ordinal), "DOCX: named style properties.");
        Require(
            ReadZipEntry(package, "word/document.xml").Contains("w:pStyle w:val=\"Quote\"", StringComparison.Ordinal),
            "DOCX: named style reference.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        var stylesXml = ReadZipEntry(odt.ToArray(), "styles.xml");
        Require(stylesXml.Contains("style:name=\"Quote\"", StringComparison.Ordinal), "ODT: named style definition.");
        Require(stylesXml.Contains("fo:margin-left=\"18pt\"", StringComparison.Ordinal), "ODT: named style properties.");
    }

    private static void RendersPdfTabStops()
    {
        var paragraph = new Paragraph(new TextRun("Name\t1"))
        {
            Style = ParagraphStyle.Default with
            {
                TabStops = new[] { new TabStop(Length.FromPoints(100), TabStopAlignment.Right, TabStopLeader.Dot) },
            },
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(paragraph));

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains("(Name)", StringComparison.Ordinal), "PDF: text before the tab.");
        Require(content.Contains("(1)", StringComparison.Ordinal), "PDF: text after the tab.");
        Require(!content.Contains("(Name    1)", StringComparison.Ordinal), "PDF: the tab is not rendered as spaces.");
    }

    private static void RendersPdfColumns()
    {
        var blocks = new List<Block>();
        for (var index = 0; index < 60; index++)
        {
            blocks.Add(new Paragraph(new TextRun("Column line " + index + " with some extra text to wrap.")));
        }

        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(blocks) { TextColumns = 2 });

        using var pdf = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("multi-column", StringComparison.Ordinal)),
            "PDF: multi-column is rendered.");
        Require(PdfStreams.Decompress(pdf.ToArray()).Contains("Column", StringComparison.Ordinal), "PDF: column text.");
    }

    private static void WrapsPdfFootnotes()
    {
        var words = string.Join(' ', Enumerable.Range(0, 80).Select(index => "word" + index));
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new Paragraph(new Inline[]
            {
                new TextRun("Text"),
                new Footnote(new Paragraph(new TextRun(words))),
            })));

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains("word0", StringComparison.Ordinal), "PDF: footnote starts.");
        Require(content.Contains("word79", StringComparison.Ordinal), "PDF: footnote is wrapped to the end.");
    }


    private static void ExtractsPdfOutlineHierarchy()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Heading(HeadingLevel.Level1, new TextRun("Parent")),
                new Heading(HeadingLevel.Level2, new TextRun("Child")),
                new PageBreak(),
                new Paragraph(new TextRun("Tail")),
            }));

        using var source = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, source).GetAwaiter().GetResult();
        using var extracted = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(source.ToArray()), 1, 1, extracted);

        var file = PdfFile.Load(extracted.ToArray());
        var catalog = file.Dictionary(file.Trailer.Get("Root"));
        var outline = catalog is null ? null : file.Dictionary(catalog.Get("Outlines"));
        var parent = outline is null ? null : file.Dictionary(outline.Get("First"));
        Require(parent is not null && parent.Get("First") is not null, "Extract keeps the outline hierarchy.");
    }


    private static void MergesPdfOutlineHierarchy()
    {
        static byte[] Write(string title)
        {
            var document = new PortableDocument(
                new DocumentMetadata(),
                new Section(new Block[]
                {
                    new Heading(HeadingLevel.Level1, new TextRun(title)),
                    new Heading(HeadingLevel.Level2, new TextRun(title + " child")),
                    new PageBreak(),
                    new Paragraph(new TextRun("tail")),
                }));
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            return buffer.ToArray();
        }

        using var merged = new MemoryStream();
        PdfPageOperations.Merge(
            new Stream[] { new MemoryStream(Write("A")), new MemoryStream(Write("B")) },
            merged);

        var file = PdfFile.Load(merged.ToArray());
        var catalog = file.Dictionary(file.Trailer.Get("Root"));
        var outline = catalog is null ? null : file.Dictionary(catalog.Get("Outlines"));
        var first = outline is null ? null : file.Dictionary(outline.Get("First"));
        var second = first is null ? null : file.Dictionary(first.Get("Next"));
        Require(first is not null && first.Get("First") is not null, "Merge keeps the first source hierarchy.");
        Require(second is not null && second.Get("First") is not null, "Merge keeps the second source hierarchy.");
    }

    private static void ExtractsAndMergesPdfPages()
    {
        var threePages = new PortableDocument(new DocumentMetadata { Title = "ThreePages" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Pageone")),
            new PageBreak(),
            new Heading(HeadingLevel.Level1, new Inline[] { new Bookmark("pagetwo-mark"), new TextRun("Pagetwo") }),
            new PageBreak(),
            new Heading(HeadingLevel.Level1, new TextRun("Pagethree")),
        }));

        using var source = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(threePages, source).GetAwaiter().GetResult();
        Require(
            System.Text.Encoding.Latin1.GetString(source.ToArray()).Contains("/Outlines", StringComparison.Ordinal),
            "Extract: the source has an outline.");

        using var extracted = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(source.ToArray()), 2, 3, extracted);
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(extracted.ToArray()));
        Require(pages.Count == 2, "Extract: two pages.");
        Require(
            pages[0].Contains("Pagetwo", StringComparison.Ordinal) && pages[1].Contains("Pagethree", StringComparison.Ordinal),
            "Extract: page content and order.");
        Require(
            new PdfDocumentReader().Read(new MemoryStream(extracted.ToArray())).Document.Metadata.Title == "ThreePages",
            "Extract: the source metadata is carried over.");

        var extractedText = System.Text.Encoding.Latin1.GetString(extracted.ToArray());
        Require(extractedText.Contains("/Outlines", StringComparison.Ordinal), "Extract: the outline is carried over.");
        Require(
            CountOf(extractedText, "/Dest [") == 2,
            "Extract: only outline entries for the extracted pages are kept.");
        Require(
            extractedText.Contains("/Names", StringComparison.Ordinal) && extractedText.Contains("pagetwo-mark", StringComparison.Ordinal),
            "Extract: named destinations for the extracted pages are carried over.");

        var single = new PortableDocument(new DocumentMetadata { Title = "SingleTitle" }, new Section(new Paragraph(new TextRun("Single"))));
        using var singleStream = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(single, singleStream).GetAwaiter().GetResult();

        using var merged = new MemoryStream();
        PdfPageOperations.Merge(
            new Stream[] { new MemoryStream(singleStream.ToArray()), new MemoryStream(source.ToArray()) },
            merged);
        var mergedPages = DocumentReader.ReadPdfPages(new MemoryStream(merged.ToArray()));
        Require(mergedPages.Count == 4, "Merge: 1 + 3 pages.");
        Require(mergedPages[0].Contains("Single", StringComparison.Ordinal), "Merge: first source first.");
        Require(
            new PdfDocumentReader().Read(new MemoryStream(merged.ToArray())).Document.Metadata.Title == "SingleTitle",
            "Merge: the first source's metadata is carried over.");

        var mergedText = System.Text.Encoding.Latin1.GetString(merged.ToArray());
        Require(mergedText.Contains("/Outlines", StringComparison.Ordinal), "Merge: the source outlines are carried over.");
        Require(CountOf(mergedText, "/Dest [") == 3, "Merge: all source outline entries are kept.");
    }

    private static void ExtractsPdfAttachments()
    {
        var document = PortableDocument.Create().Metadata(title: "Attached").Paragraph("Body").Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions
        {
            Attachments = new[]
            {
                new PdfAttachment { Name = "note.txt", Data = new byte[] { 1, 2, 3 }, MimeType = "text/plain" },
            },
        }).GetAwaiter().GetResult();

        using var extracted = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(buffer.ToArray()), 1, 1, extracted);
        var latin = System.Text.Encoding.Latin1.GetString(extracted.ToArray());
        Require(latin.Contains("/EmbeddedFiles", StringComparison.Ordinal), "Extract: the embedded-file name tree is carried over.");
        Require(latin.Contains("note.txt", StringComparison.Ordinal), "Extract: the attachment name survives.");
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void SeparatesPdfFootnoteParagraphs()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(
            new Paragraph(new Inline[]
            {
                new TextRun("Text"),
                new Footnote(new Block[]
                {
                    new Paragraph(new TextRun("Firstpara")),
                    new Paragraph(new TextRun("Secondpara")),
                }),
            })));

        using var pdf = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(pdf.ToArray());
        Require(content.Contains("(1. Firstpara)", StringComparison.Ordinal), "PDF: the first footnote paragraph.");
        Require(content.Contains("(Secondpara)", StringComparison.Ordinal), "PDF: the second footnote paragraph is its own line.");
    }

    private static void WritesDateFieldPdf()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(FieldDocument(), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());

        Require(content.Contains("2026-09-25", StringComparison.Ordinal), "PDF: date field is rendered.");
        Require(content.Contains("(1)", StringComparison.Ordinal), "PDF: the total page count is rendered.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("NUMPAGES", StringComparison.Ordinal)), "PDF: the total page count is resolved by a layout pass.");
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
