using Reglet;

/// <summary>Revisions (ADR-0027): model rules, accept and reject.</summary>
internal static class RevisionChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidChanges();
            AcceptsAndRejects();
            WorksAcrossParagraphs();
            ReturnsTheSameDocumentWithoutChanges();
            WritesDocx();
            ReadsDocx();
            DocxAcrossHyperlinkAndOverlap();
            ReportsWhatItCannotWrite();
            WritesOdt();
            ReadsOdt();
            ReadsLibreOfficeRegions();
            ReportsOdtLimits();
            ShowsRevisionsInPdf();
            Console.WriteLine("Revisions: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Revisions: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RejectsInvalidChanges()
    {
        var invalid = new Action[]
        {
            () => new TrackedChange(-1, TrackedChangeKind.Insertion, "Ann"),
            () => new TrackedChange(0, TrackedChangeKind.Insertion, ""),
            () => new TrackedChange(0, (TrackedChangeKind)7, "Ann"),
            () => new TrackedChangeEnd(-1),
        };
        foreach (var create in invalid)
        {
            var threw = false;
            try
            {
                create();
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Require(threw, "An invalid tracked change must be rejected.");
        }

        var date = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
        var change = new TrackedChange(3, TrackedChangeKind.Deletion, "Ann") { Date = date };
        Require(change.Id == 3 && change.Kind == TrackedChangeKind.Deletion && change.Author == "Ann" && change.Date == date, "Values are kept.");
    }

    internal static readonly DateTimeOffset Stamp = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    internal static PortableDocument Sample()
        => new(new DocumentMetadata(), new Section(new Block[]
        {
            // an insertion and a deletion in one paragraph
            new Paragraph(new Inline[]
            {
                new TextRun("a "),
                new TrackedChange(0, TrackedChangeKind.Insertion, "Ann") { Date = Stamp }, new TextRun("new"), new TrackedChangeEnd(0),
                new TextRun(" b "),
                new TrackedChange(1, TrackedChangeKind.Deletion, "Bob") { Date = Stamp }, new TextRun("old"), new TrackedChangeEnd(1),
                new TextRun(" c"),
            }),
            // a deletion across two paragraphs
            new Paragraph(new Inline[] { new TextRun("x "), new TrackedChange(2, TrackedChangeKind.Deletion, "Ann") { Date = Stamp }, new TextRun("gone") }),
            new Paragraph(new Inline[] { new TextRun("too"), new TrackedChangeEnd(2), new TextRun(" y") }),
        }));

    private static string Text(PortableDocument document)
        => string.Join("|", document.Sections[0].Blocks.OfType<Paragraph>()
            .Select(paragraph => string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text))));

    private static bool HasMarkers(PortableDocument document)
        => document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines)
            .Any(inline => inline is TrackedChange or TrackedChangeEnd);

    private static void AcceptsAndRejects()
    {
        var accepted = DocumentRevisions.AcceptAll(Sample());
        Require(Text(accepted) == "a new b  c|x |" + " y", "AcceptAll: " + Text(accepted));
        Require(!HasMarkers(accepted), "AcceptAll leaves no markers.");

        var rejected = DocumentRevisions.RejectAll(Sample());
        Require(Text(rejected) == "a  b old c|x gone|too y", "RejectAll: " + Text(rejected));
        Require(!HasMarkers(rejected), "RejectAll leaves no markers.");

        var one = DocumentRevisions.Accept(Sample(), 1);
        Require(Text(one) == "a new b  c|x gone|too y", "Accept(1): " + Text(one));
        var left = one.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<TrackedChange>().Select(change => change.Id).ToList();
        Require(left.SequenceEqual(new[] { 0, 2 }), "Accept(1) leaves the other changes: " + string.Join(",", left));

        var back = DocumentRevisions.Reject(Sample(), 0);
        Require(Text(back).StartsWith("a  b old c", StringComparison.Ordinal), "Reject(0) removes the insertion: " + Text(back));
    }

    private static void WorksAcrossParagraphs()
    {
        var rejected = DocumentRevisions.Reject(Sample(), 2);
        Require(Text(rejected).EndsWith("|x gone|too y", StringComparison.Ordinal), "Reject of a deletion across paragraphs keeps the text: " + Text(rejected));

        // A comment and a nested change inside accepted-deleted text vanish with it, with their ends.
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[]
            {
                new TrackedChange(0, TrackedChangeKind.Deletion, "Ann"), new TextRun("a"),
                new Comment(5, "Cy", "note"), new TextRun("b"), new TrackedChange(1, TrackedChangeKind.Insertion, "Bob"), new TextRun("c"),
            }),
            new Paragraph(new Inline[] { new TextRun("d"), new CommentEnd(5), new TrackedChangeEnd(1), new TrackedChangeEnd(0), new TextRun("e") }),
        }));
        var accepted = DocumentRevisions.AcceptAll(document);
        var inlines = accepted.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).ToList();
        Require(inlines.Count == 1 && inlines[0] is TextRun { Text: "e" }, "Everything inside the accepted deletion goes, including nested markers.");
    }

    private static void ReturnsTheSameDocumentWithoutChanges()
    {
        var plain = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("t")])));
        Require(ReferenceEquals(DocumentRevisions.AcceptAll(plain), plain), "No change, same instance.");
        var sample = Sample();
        Require(ReferenceEquals(DocumentRevisions.Accept(sample, 99), sample), "An unknown id changes nothing.");
    }

    private static byte[] Write(PortableDocument document, DocumentFormat format, out WriteResult result)
    {
        using var buffer = new MemoryStream();
        result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static ReadResult Read(byte[] bytes, DocumentFormat format) => DocumentReader.Read(new MemoryStream(bytes), format);

    private static string Part(byte[] package, string name)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(package));
        using var reader = new StreamReader((zip.GetEntry(name) ?? throw new InvalidOperationException("Part missing: " + name)).Open());
        return reader.ReadToEnd();
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The text of every paragraph with the changes as <c>&lt;ins:Ann&gt;…&lt;/&gt;</c> or <c>&lt;del:Ann&gt;…&lt;/&gt;</c>.</summary>
    internal static string Shape(PortableDocument document)
        => string.Join("|", document.Sections[0].Blocks.OfType<Paragraph>().Select(paragraph => string.Concat(paragraph.Inlines.Select(inline => inline switch
        {
            TextRun run => run.Text,
            TrackedChange change => $"<{(change.Kind == TrackedChangeKind.Insertion ? "ins" : "del")}:{change.Author}>",
            TrackedChangeEnd => "</>",
            Hyperlink link => "{" + string.Concat(link.Inlines.Select(inner => inner switch
            {
                TextRun run => run.Text,
                TrackedChange change => $"<{(change.Kind == TrackedChangeKind.Insertion ? "ins" : "del")}:{change.Author}>",
                TrackedChangeEnd => "</>",
                _ => "?",
            })) + "}",
            _ => "?",
        }))));

    private static void WritesDocx()
    {
        var bytes = Write(Sample(), DocumentFormat.Docx, out var result);
        Require(!result.HasWarnings, "DOCX: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var body = Part(bytes, "word/document.xml");
        System.Xml.Linq.XDocument.Parse(body);
        Require(Count(body, "<w:ins ") == 1 && Count(body, "<w:del ") == 3, "DOCX: one insertion and three deletion pieces (the third change is cut at the paragraph border).");
        Require(body.Contains("w:author=\"Ann\"") && body.Contains("w:date=\"2026-11-18T09:30:00Z\""), "DOCX: author and date.");
        Require(body.Contains("<w:delText") && Count(body, "<w:delText") == 3, "DOCX: deleted text is w:delText.");
        Require(!body.Contains("<w:t>old") && !body.Contains(">old</w:t>"), "DOCX: no deleted text in w:t.");
        var ids = System.Text.RegularExpressions.Regex.Matches(body, "<w:(?:ins|del) w:id=\"(\\d+)\"").Select(m => m.Groups[1].Value).ToList();
        Require(ids.Count == 4 && ids.Distinct().Count() == 4, "DOCX: unique w:id per element.");
    }

    private static void ReadsDocx()
    {
        var read = Read(Write(Sample(), DocumentFormat.Docx, out _), DocumentFormat.Docx);
        Require(read.Warnings.Count == 0, "DOCX: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        Require(Shape(read.Document) == "a <ins:Ann>new</> b <del:Bob>old</> c|x <del:Ann>gone</>|<del:Ann>too</> y", "DOCX read: " + Shape(read.Document));
        var starts = read.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TrackedChange>().ToList();
        Require(starts.Select(change => change.Id).Distinct().Count() == 4 && starts.All(change => change.Date == Stamp), "DOCX read: own ids and the date.");

        // A second round trip is stable, accepting and rejecting still work on the read model.
        var again = Read(Write(read.Document, DocumentFormat.Docx, out var result), DocumentFormat.Docx);
        Require(!result.HasWarnings && Shape(again.Document) == Shape(read.Document), "DOCX: second round trip is stable.");
        Require(Text(DocumentRevisions.AcceptAll(read.Document)) == "a new b  c|x |" + " y", "Accept on the read model: " + Text(DocumentRevisions.AcceptAll(read.Document)));

        // Accepting every change leaves plain DOCX without tracked changes.
        var accepted = Part(Write(DocumentRevisions.AcceptAll(read.Document), DocumentFormat.Docx, out _), "word/document.xml");
        Require(!accepted.Contains("<w:ins") && !accepted.Contains("<w:del"), "DOCX: no tracked change after accepting.");
    }

    private static void DocxAcrossHyperlinkAndOverlap()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TrackedChange(0, TrackedChangeKind.Insertion, "Ann"), new TextRun("a"),
            new Hyperlink("https://example.com", new Inline[] { new TextRun("link"), new TrackedChange(1, TrackedChangeKind.Deletion, "Bob"), new TextRun("gone") }),
            new TrackedChangeEnd(0), new TextRun("b"), new TrackedChangeEnd(1),
        })));
        var bytes = Write(document, DocumentFormat.Docx, out var result);
        var body = Part(bytes, "word/document.xml");
        System.Xml.Linq.XDocument.Parse(body);
        Require(!result.HasWarnings, "DOCX: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var read = Read(bytes, DocumentFormat.Docx);
        // The deletion has no end inside the link: it is cut there and goes on after it.
        var shape = Shape(read.Document);
                Require(Count(shape, "<ins:Ann>") >= 2 && Count(shape, "<del:Bob>") >= 2, "DOCX: both changes are cut at the hyperlink border: " + shape);
        Require(shape.Replace("<ins:Ann>", string.Empty).Replace("<del:Bob>", string.Empty).Replace("</>", string.Empty) == "a{linkgone}b", "DOCX: the text is unchanged: " + shape);
    }

    private static void ReportsWhatItCannotWrite()
    {
        var footer = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { new TrackedChange(0, TrackedChangeKind.Insertion, "Ann"), new TextRun("f"), new TrackedChangeEnd(0) }) });
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new TextRun("t"), new TrackedChangeEnd(7), new TrackedChange(1, TrackedChangeKind.Deletion, "Bob"), new TextRun("x"),
        })) { Footer = footer });
        Write(document, DocumentFormat.Docx, out var result);
        var messages = string.Join("|", result.Warnings.Select(w => w.Message));
        Require(messages.Contains("tracked change in a header, footer or note") && messages.Contains("end of the tracked change 7 has no start") && messages.Contains("tracked change 1 has no end"), "DOCX: reported: " + messages);
    }

    private static void WritesOdt()
    {
        var bytes = Write(Sample(), DocumentFormat.Odt, out var result);
        Require(!result.HasWarnings, "ODT: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var content = Part(bytes, "content.xml");
        System.Xml.Linq.XDocument.Parse(content);
        Require(Count(content, "<text:changed-region ") == 4, "ODT: four regions (the third change is cut at the paragraph border): " + Count(content, "<text:changed-region "));
        Require(Count(content, "<text:insertion>") == 1 && Count(content, "<text:deletion>") == 3, "ODT: one insertion and three deletions.");
        Require(Count(content, "<text:change-start ") == 1 && Count(content, "<text:change-end ") == 1 && Count(content, "<text:change ") == 3, "ODT: markers in the text.");
        Require(content.Contains(">Ann</dc:creator>") && content.Contains(">2026-11-18T09:30:00</dc:date>"), "ODT: author and date.");
        var regionsEnd = content.IndexOf("</text:tracked-changes>", StringComparison.Ordinal);
        Require(regionsEnd > 0 && content.IndexOf("<text:p", StringComparison.Ordinal) > content.IndexOf("<office:text", StringComparison.Ordinal)
            && content.IndexOf(">new<", StringComparison.Ordinal) > regionsEnd, "ODT: the regions come before the body text.");
        // The deleted text lives in the region, not in the body.
        var body = content[regionsEnd..];
        Require(!body.Contains("old") && !body.Contains("gone") && !body.Contains("too"), "ODT: deleted text is not in the body.");
        var regions = content[..regionsEnd];
        Require(regions.Contains("old") && regions.Contains("gone") && regions.Contains("too"), "ODT: deleted text is in the regions.");
    }

    private static void ReadsOdt()
    {
        var read = Read(Write(Sample(), DocumentFormat.Odt, out _), DocumentFormat.Odt);
        Require(read.Warnings.Count == 0, "ODT: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        Require(Shape(read.Document) == "a <ins:Ann>new</> b <del:Bob>old</> c|x <del:Ann>gone</>|<del:Ann>too</> y", "ODT read: " + Shape(read.Document));
        var starts = read.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TrackedChange>().ToList();
        Require(starts.Count == 4 && starts.Select(change => change.Id).Distinct().Count() == 4 && starts.All(change => change.Date == Stamp), "ODT read: own ids and the date.");

        var again = Read(Write(read.Document, DocumentFormat.Odt, out var result), DocumentFormat.Odt);
        Require(!result.HasWarnings && Shape(again.Document) == Shape(read.Document), "ODT: second round trip is stable.");
        var accepted = Part(Write(DocumentRevisions.AcceptAll(read.Document), DocumentFormat.Odt, out _), "content.xml");
        Require(!accepted.Contains("changed-region") && !accepted.Contains("text:change"), "ODT: no tracked change after accepting.");

        // An insertion across two paragraphs keeps its range.
        var across = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[] { new TextRun("a"), new TrackedChange(0, TrackedChangeKind.Insertion, "Ann"), new TextRun("b") }),
            new Paragraph(new Inline[] { new TextRun("c"), new TrackedChangeEnd(0), new TextRun("d") }),
        }));
        var bytes = Write(across, DocumentFormat.Odt, out _);
        Require(Count(Part(bytes, "content.xml"), "<text:changed-region ") == 1, "ODT: one region for an insertion across paragraphs.");
        Require(Shape(Read(bytes, DocumentFormat.Odt).Document) == "a<ins:Ann>b|c</>d", "ODT: insertion across paragraphs: " + Shape(Read(bytes, DocumentFormat.Odt).Document));
    }

    /// <summary>LibreOffice writes a deletion as a point with the text in the region, an unknown date as zeros, and format changes.</summary>
    private static void ReadsLibreOfficeRegions()
    {
        var odt = Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("x")]))), DocumentFormat.Odt, out _);
        var content = Part(odt, "content.xml");
        const string Regions =
            "<text:tracked-changes text:track-changes=\"true\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<text:changed-region text:id=\"ct1\"><text:deletion><office:change-info><dc:creator>Bob</dc:creator><dc:date>0000-00-00T00:00:00</dc:date></office:change-info><text:p>one</text:p><text:p>two</text:p></text:deletion></text:changed-region>"
            + "<text:changed-region text:id=\"ct2\"><text:format-change><office:change-info><dc:creator>Cy</dc:creator><dc:date>2026-11-18T09:30:00</dc:date></office:change-info></text:format-change></text:changed-region>"
            + "</text:tracked-changes>";
        var at = content.IndexOf("<text:p", StringComparison.Ordinal);
        at = content.IndexOf('>', at) + 1;
        var patched = content.Insert(at, "<text:change xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" text:change-id=\"ct1\"/><text:change-start text:change-id=\"ct2\"/>y<text:change-end text:change-id=\"ct2\"/>");
        var bodyStart = patched.IndexOf("<office:text", StringComparison.Ordinal);
        bodyStart = patched.IndexOf('>', bodyStart) + 1;
        patched = patched.Insert(bodyStart, Regions);
        var read = Read(TestPackages.ReplacePart(odt, "content.xml", System.Text.Encoding.UTF8.GetBytes(patched)), DocumentFormat.Odt);
        var shape = Shape(read.Document);
        Require(shape == "<del:Bob>one\ntwo</>yx", "ODT: LibreOffice deletion with two paragraphs: " + shape);
        var change = read.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TrackedChange>().Single();
        Require(change.Date is null, "ODT: unknown date stays null.");
        var messages = string.Join("|", read.Warnings.Select(w => w.Message));
        Require(messages.Contains("across paragraphs") && messages.Contains("formatting"), "ODT: reported: " + messages);
    }

    private static void ReportsOdtLimits()
    {
        var footer = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { new TrackedChange(0, TrackedChangeKind.Insertion, "Ann"), new TextRun("f"), new TrackedChangeEnd(0) }) });
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[] { new TextRun("t"), new TrackedChangeEnd(7), new TrackedChange(1, TrackedChangeKind.Deletion, "Bob"), new TextRun("x") }),
            new Paragraph(new Inline[]
            {
                new TrackedChange(2, TrackedChangeKind.Deletion, "Ann"), new TextRun("a"),
                new TrackedChange(3, TrackedChangeKind.Insertion, "Bob"), new Hyperlink("https://example.com", new Inline[] { new TextRun("l") }),
                new TrackedChangeEnd(3), new TrackedChangeEnd(2),
            }),
            new Paragraph(new Inline[]
            {
                new Hyperlink("https://example.com", new Inline[] { new TextRun("l"), new TrackedChange(4, TrackedChangeKind.Insertion, "Cy"), new TextRun("m") }),
                new TrackedChangeEnd(4),
            }),
        }) { Footer = footer });
        Write(document, DocumentFormat.Odt, out var result);
        var messages = string.Join("|", result.Warnings.Select(w => w.Message));
        Require(messages.Contains("tracked change in a header, footer or note") && messages.Contains("end of the tracked change 7 has no start")
            && messages.Contains("tracked change 1 has no end") && messages.Contains("inside a deleted text") && messages.Contains("hyperlink inside or across"), "ODT: reported: " + messages);
    }

    private static string PdfText(PortableDocument document, PdfRevisionMode mode, out byte[] pdf)
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { Revisions = mode }).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "PDF: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        pdf = buffer.ToArray();
        return string.Join("\n", new PdfDocumentReader().ReadPageTexts(new MemoryStream(pdf)));
    }

    private static void ShowsRevisionsInPdf()
    {
        var final = PdfText(Sample(), PdfRevisionMode.Final, out var finalPdf);
        Require(final.Contains("new") && !final.Contains("old") && !final.Contains("gone") && !final.Contains("too"), "PDF Final: insertions shown, deletions left out: " + final);
        Require(PdfText(Sample(), PdfRevisionMode.Final, out var defaultPdf) == final && defaultPdf.SequenceEqual(finalPdf), "PDF: Final is the default and deterministic.");
        var original = PdfText(Sample(), PdfRevisionMode.Original, out _);
        Require(!original.Contains("new") && original.Contains("old") && original.Contains("gone") && original.Contains("too"), "PDF Original: deletions shown, insertions left out: " + original);
        var markup = PdfText(Sample(), PdfRevisionMode.Markup, out var markupPdf);
        Require(markup.Contains("new") && markup.Contains("old") && markup.Contains("gone") && markup.Contains("too"), "PDF Markup: everything shown: " + markup);
        Require(!markupPdf.SequenceEqual(finalPdf), "PDF Markup differs from Final (colors, underline, strikethrough).");

        // Default options do not use the markup: a document without changes is untouched by the mode.
        var plain = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("t")])));
        Require(PdfText(plain, PdfRevisionMode.Markup, out var plainMarkup) == PdfText(plain, PdfRevisionMode.Final, out var plainFinal) && plainMarkup.SequenceEqual(plainFinal), "PDF: no changes, no difference.");
    }
}
