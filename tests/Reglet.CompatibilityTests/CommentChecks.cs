using Reglet;

/// <summary>Comments (ADR-0026): model rules.</summary>
internal static class CommentChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidComments();
            KeepsValues();
            RejectsInvalidReplies();
            WritesDocx();
            ReadsDocx();
            KeepsCommentsWhenChanged();
            ReportsCommentsItCannotWrite();
            ReadsAReferenceWithoutRange();
            WritesOdt();
            ReadsOdt();
            KeepsOdtCommentsWhenChanged();
            ReadsLibreOfficeOrder();
            WritesPdfNotes();
            PdfNotesFollowPagesAndPdfA();
            Console.WriteLine("Comments: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Comments: " + exception);
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

    private static void RejectsInvalidComments()
    {
        var invalid = new Action[]
        {
            () => new Comment(-1, "Ann", "text"),
            () => new Comment(0, "", "text"),
            () => new Comment(0, "Ann", (string)null!),
            () => new Comment(0, "Ann", Array.Empty<string>()),
            () => new Comment(0, "Ann", new string?[] { "a", null }!),
            () => new CommentEnd(-1),
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

            Require(threw, "An invalid comment must be rejected.");
        }
    }

    private static void KeepsValues()
    {
        var date = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
        var reply = new Comment(2, "Bob", "Agreed");
        var comment = new Comment(1, "Ann", new[] { "First", "Second" })
        {
            Initials = "A", Date = date, Resolved = true, Replies = new[] { reply },
        };
        Require(comment.Id == 1 && comment.Author == "Ann" && comment.Initials == "A" && comment.Date == date, "Values are kept.");
        Require(comment.Paragraphs.SequenceEqual(new[] { "First", "Second" }) && comment.Resolved, "Paragraphs and flag.");
        Require(comment.Replies.Single() == reply && reply.Replies.Count == 0 && !reply.Resolved, "Replies.");
        Require(new CommentEnd(1).Id == 1, "The end carries the id.");

        var paragraph = new Paragraph(new Inline[] { comment, new TextRun("x"), new CommentEnd(1) });
        Require(paragraph.Inlines.Count == 3, "A comment is an inline of a paragraph.");
    }

    private static void RejectsInvalidReplies()
    {
        var nested = new Comment(2, "Bob", "r") { Replies = new[] { new Comment(3, "Cy", "rr") } };
        var invalid = new Action[]
        {
            () => _ = new Comment(1, "Ann", "t") { Replies = new[] { nested } },
            () => _ = new Comment(1, "Ann", "t") { Replies = new[] { new Comment(1, "Bob", "r") } },
            () => _ = new Comment(1, "Ann", "t") { Replies = new[] { new Comment(2, "Bob", "r"), new Comment(2, "Cy", "r") } },
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

            Require(threw, "Invalid replies must be rejected.");
        }
    }

    private static readonly DateTimeOffset Stamp = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    internal static PortableDocument Sample()
    {
        var thread = new Comment(0, "Ann", new[] { "First & <more>", "Second\tline" })
        {
            Initials = "A", Date = Stamp, Resolved = true,
            Replies = new[] { new Comment(1, "Bob", "Agreed") { Date = Stamp.AddHours(1) } },
        };
        return new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            // range inside one paragraph
            new Paragraph(new Inline[] { new TextRun("a "), thread, new TextRun("marked"), new CommentEnd(0), new TextRun(" b") }),
            // range across two paragraphs
            new Paragraph(new Inline[] { new Comment(2, "Cy", "Spans") { Date = Stamp }, new TextRun("one") }),
            new Paragraph(new Inline[] { new TextRun("two"), new CommentEnd(2) }),
            // point comment
            new Paragraph(new Inline[] { new TextRun("c"), new Comment(3, "Di", "Point"), new TextRun("d") }),
        }));
    }

    private static byte[] Write(PortableDocument document, DocumentFormat format, out WriteResult result)
    {
        using var buffer = new MemoryStream();
        result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static ReadResult Read(byte[] bytes, DocumentFormat format) => DocumentReader.Read(new MemoryStream(bytes), format);

    private static string Part(byte[] docx, string name)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
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

    private static void WritesDocx()
    {
        var bytes = Write(Sample(), DocumentFormat.Docx, out var result);
        Require(!result.HasWarnings, "DOCX: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var comments = Part(bytes, "word/comments.xml");
        System.Xml.Linq.XDocument.Parse(comments);
        Require(Count(comments, "<w:comment ") == 4, "DOCX: four comments (three and one reply).");
        Require(comments.Contains("w:author=\"Ann\"") && comments.Contains("w:initials=\"A\"") && comments.Contains("w:date=\"2026-11-18T09:30:00Z\""), "DOCX: author, initials, date.");
        Require(comments.Contains("First &amp; &lt;more&gt;") && comments.Contains("<w:tab") && Count(comments, "<w:annotationRef") == 4, "DOCX: escaped text, tab, annotation reference.");

        var extended = Part(bytes, "word/commentsExtended.xml");
        System.Xml.Linq.XDocument.Parse(extended);
        Require(Count(extended, "<w15:commentEx ") == 4 && extended.Contains("paraIdParent") && extended.Contains("w15:done=\"1\""), "DOCX: replies and done flag.");

        var body = Part(bytes, "word/document.xml");
        Require(Count(body, "<w:commentRangeStart") == 4 && Count(body, "<w:commentRangeEnd") == 4 && Count(body, "<w:commentReference") == 4, "DOCX: anchors for every comment and reply.");
        Require(Part(bytes, "[Content_Types].xml").Contains("comments+xml") && Part(bytes, "word/_rels/document.xml.rels").Contains("relationships/comments\""), "DOCX: content type and relationship.");

        // Without replies and done flags there is no extended part.
        var plain = Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new Comment(0, "Ann", "x"), new TextRun("y") }))), DocumentFormat.Docx, out _);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(plain));
        Require(zip.GetEntry("word/comments.xml") is not null && zip.GetEntry("word/commentsExtended.xml") is null, "DOCX: no extended part for plain comments.");
        using var none = new System.IO.Compression.ZipArchive(new MemoryStream(Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("t")]))), DocumentFormat.Docx, out _)));
        Require(none.GetEntry("word/comments.xml") is null, "DOCX: no comments part without comments.");
    }

    private static void RequireSame(Comment found, Comment expected, string label)
    {
        Require(found.Id == expected.Id && found.Author == expected.Author && found.Initials == expected.Initials
            && found.Date == expected.Date && found.Resolved == expected.Resolved
            && found.Paragraphs.SequenceEqual(expected.Paragraphs) && found.Replies.Count == expected.Replies.Count, label + ": comment values.");
        for (var i = 0; i < expected.Replies.Count; i++)
        {
            RequireSame(found.Replies[i], expected.Replies[i], label + " reply");
        }
    }

    private static List<Inline> Inlines(PortableDocument document)
        => document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).ToList();

    private static void ReadsDocx()
    {
        var expected = Inlines(Sample());
        var read = Read(Write(Sample(), DocumentFormat.Docx, out _), DocumentFormat.Docx);
        Require(read.Warnings.Count == 0, "DOCX: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        var found = Inlines(read.Document);
        var starts = found.OfType<Comment>().ToList();
        Require(starts.Count == 3, "DOCX: three anchored comments (the reply has no anchor).");
        var originals = expected.OfType<Comment>().ToList();
        for (var i = 0; i < originals.Count; i++)
        {
            RequireSame(starts[i], originals[i], "DOCX");
        }

        Require(found.OfType<CommentEnd>().Select(end => end.Id).SequenceEqual(new[] { 0, 2 }), "DOCX: the two ranges have an end, the point has none.");
        var kinds = found.Where(inline => inline is Comment or CommentEnd).Select(inline => inline is Comment c ? "S" + c.Id : "E" + ((CommentEnd)inline).Id);
        Require(string.Join(",", kinds) == "S0,E0,S2,E2,S3", "DOCX: anchors keep their order: " + string.Join(",", kinds));
        Require(found.OfType<TextRun>().Select(run => run.Text).SequenceEqual(expected.OfType<TextRun>().Select(run => run.Text)), "DOCX: the text around the anchors is unchanged.");
    }

    /// <summary>A comment is part of the model: a changed document keeps it, a removed one disappears.</summary>
    private static void KeepsCommentsWhenChanged()
    {
        var first = Read(Write(Sample(), DocumentFormat.Docx, out _), DocumentFormat.Docx).Document;
        var again = Read(Write(first, DocumentFormat.Docx, out var result), DocumentFormat.Docx);
        Require(!result.HasWarnings && again.Warnings.Count == 0, "DOCX: a second round trip is clean.");
        Require(Inlines(again.Document).OfType<Comment>().Count() == 3, "DOCX: all comments survive the round trip.");

        // Remove every comment from the model: no comments part, no stale anchors.
        var stripped = first with
        {
            Sections = new[]
            {
                new Section(first.Sections[0].Blocks.OfType<Paragraph>()
                    .Select(paragraph => (Block)new Paragraph(paragraph.Inlines.Where(inline => inline is not (Comment or CommentEnd)).ToList())).ToArray()),
            },
        };
        var bytes = Write(stripped, DocumentFormat.Docx, out _);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        Require(zip.GetEntry("word/comments.xml") is null, "DOCX: a removed comment leaves no comments part.");
        Require(!Part(bytes, "word/document.xml").Contains("commentRange"), "DOCX: no stale anchors.");
    }

    private static void ReportsCommentsItCannotWrite()
    {
        var comment = new Comment(0, "Ann", "x");
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new TextRun("t"), new CommentEnd(7) }))
        {
            Footer = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { comment, new TextRun("f") }) }),
        });
        Write(document, DocumentFormat.Docx, out var result);
        var messages = string.Join("|", result.Warnings.Select(w => w.Message));
        Require(messages.Contains("comment in a header, footer or note") && messages.Contains("end of the comment 7 has no start"), "DOCX: both problems are reported: " + messages);
    }

    /// <summary>LibreOffice writes a comment without a range as a bare reference; it is read as a point comment.</summary>
    private static void ReadsAReferenceWithoutRange()
    {
        var bytes = Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new TextRun("a"), new Comment(0, "Ann", "x"), new TextRun("b") }))), DocumentFormat.Docx, out _);
        var body = System.Text.RegularExpressions.Regex.Replace(Part(bytes, "word/document.xml"), "<w:commentRange(Start|End) [^>]*/>", string.Empty);
        Require(!body.Contains("commentRange") && body.Contains("<w:commentReference"), "The test file has a bare reference.");
        var read = Read(TestPackages.ReplacePart(bytes, "word/document.xml", System.Text.Encoding.UTF8.GetBytes(body)), DocumentFormat.Docx);
        var inlines = Inlines(read.Document);
        Require(read.Warnings.Count == 0 && inlines.OfType<Comment>().Count() == 1 && inlines.OfType<CommentEnd>().Count() == 0, "A bare reference is a point comment.");
        Require(inlines.OfType<TextRun>().Select(run => run.Text).SequenceEqual(new[] { "a", "b" }), "The text around it is unchanged.");
    }

    private static string OdtContent(byte[] odt) => Part(odt, "content.xml");

    private static void WritesOdt()
    {
        var bytes = Write(Sample(), DocumentFormat.Odt, out var result);
        Require(!result.HasWarnings, "ODT: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var content = OdtContent(bytes);
        System.Xml.Linq.XDocument.Parse(content);
        Require(Count(content, "<office:annotation ") == 4 && Count(content, "<office:annotation-end ") == 3, "ODT: four annotations, three ends (the point has none).");
        Require(content.Contains(">Ann</dc:creator>") && content.Contains(">2026-11-18T09:30:00</dc:date>") && content.Contains(">A</meta:creator-initials>"), "ODT: author, date, initials.");
        Require(content.Contains("&amp;<text:s />&lt;more&gt;") && content.Contains("Second<text:tab />line"), "ODT: escaped text and tab.");
        Require(content.Contains("loext:parent-name=\"__Annotation__pd0\"") && content.Contains("loext:resolved=\"true\""), "ODT: reply parent and done flag.");
        Require(content.IndexOf("Agreed", StringComparison.Ordinal) < content.IndexOf(">Ann</dc:creator>", StringComparison.Ordinal), "ODT: the reply comes first, as LibreOffice writes it.");
    }

    private static void ReadsOdt()
    {
        var expected = Inlines(Sample());
        var read = Read(Write(Sample(), DocumentFormat.Odt, out _), DocumentFormat.Odt);
        Require(read.Warnings.Count == 0, "ODT: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        var found = Inlines(read.Document);
        var starts = found.OfType<Comment>().ToList();
        Require(starts.Count == 3, "ODT: three anchored comments (the reply has no anchor).");
        // ODT ids are positions in the document, so only the values are compared.
        var originals = expected.OfType<Comment>().ToList();
        for (var i = 0; i < originals.Count; i++)
        {
            var (a, b) = (starts[i], originals[i]);
            Require(a.Author == b.Author && a.Initials == b.Initials && a.Date == b.Date && a.Resolved == b.Resolved
                && a.Paragraphs.SequenceEqual(b.Paragraphs) && a.Replies.Count == b.Replies.Count, "ODT: comment values of " + b.Author);
        }

        Require(starts[0].Replies[0].Author == "Bob" && starts[0].Replies[0].Date == Stamp.AddHours(1), "ODT: the reply is read.");
        var kinds = found.Where(inline => inline is Comment or CommentEnd).Select(inline => inline is Comment c ? "S" + c.Id : "E" + ((CommentEnd)inline).Id).ToList();
        Require(kinds.Count == 5 && kinds[0][0] == 'S' && kinds[1] == "E" + kinds[0][1..] && kinds[2][0] == 'S' && kinds[3] == "E" + kinds[2][1..] && kinds[4][0] == 'S',
            "ODT: anchors keep their order: " + string.Join(",", kinds));
        Require(found.OfType<TextRun>().Select(run => run.Text).SequenceEqual(expected.OfType<TextRun>().Select(run => run.Text)), "ODT: the text around the anchors is unchanged.");
    }

    private static void KeepsOdtCommentsWhenChanged()
    {
        var first = Read(Write(Sample(), DocumentFormat.Odt, out _), DocumentFormat.Odt).Document;
        var again = Read(Write(first, DocumentFormat.Odt, out var result), DocumentFormat.Odt);
        Require(!result.HasWarnings && again.Warnings.Count == 0, "ODT: a second round trip is clean: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        Require(Inlines(again.Document).OfType<Comment>().Count() == 3 && Inlines(again.Document).OfType<CommentEnd>().Count() == 2, "ODT: all comments survive the round trip.");

        var withoutComments = first with
        {
            Sections = new[]
            {
                new Section(first.Sections[0].Blocks.OfType<Paragraph>()
                    .Select(paragraph => (Block)new Paragraph(paragraph.Inlines.Where(inline => inline is not (Comment or CommentEnd)).ToList())).ToArray()),
            },
        };
        Require(!OdtContent(Write(withoutComments, DocumentFormat.Odt, out _)).Contains("office:annotation"), "ODT: removed comments leave no annotation.");

        var report = Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new TextRun("t"), new CommentEnd(7) }))
        {
            Footer = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { new Comment(0, "Ann", "x"), new TextRun("f") }) }),
        }), DocumentFormat.Odt, out var reported);
        _ = report;
        var messages = string.Join("|", reported.Warnings.Select(w => w.Message));
        Require(messages.Contains("comment in a header, footer or note"), "ODT: a comment in a band is reported: " + messages);
    }

    /// <summary>LibreOffice writes a reply before its parent, a missing date as zeros and a point comment with an adjacent end.</summary>
    private static void ReadsLibreOfficeOrder()
    {
        var odt = Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("x")]))), DocumentFormat.Odt, out _);
        var content = OdtContent(odt).Replace("<text:p", "<text:p", StringComparison.Ordinal);
        const string Annotations =
            "<office:annotation office:name=\"R\" loext:parent-name=\"P\" loext:resolved=\"false\" xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"><dc:creator xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Bob</dc:creator><dc:date xmlns:dc=\"http://purl.org/dc/elements/1.1/\">0000-00-00T00:00:00</dc:date><text:p>reply</text:p></office:annotation>"
            + "<office:annotation office:name=\"P\" loext:resolved=\"true\" xmlns:loext=\"urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0\"><dc:creator xmlns:dc=\"http://purl.org/dc/elements/1.1/\">Ann</dc:creator><dc:date xmlns:dc=\"http://purl.org/dc/elements/1.1/\">2026-11-18T09:30:00</dc:date><text:p>parent</text:p></office:annotation>"
            + "<office:annotation-end office:name=\"P\"/><office:annotation-end office:name=\"R\"/>";
        var at = content.IndexOf("<text:p", StringComparison.Ordinal);
        at = content.IndexOf('>', at) + 1;
        var patched = content.Insert(at, Annotations);
        var read = Read(TestPackages.ReplacePart(odt, "content.xml", System.Text.Encoding.UTF8.GetBytes(patched)), DocumentFormat.Odt);
        var inlines = Inlines(read.Document);
        var comment = inlines.OfType<Comment>().SingleOrDefault() ?? throw new InvalidOperationException("ODT: one anchored comment expected.");
        Require(comment.Author == "Ann" && comment.Resolved && comment.Replies.Count == 1 && comment.Replies[0].Author == "Bob" && comment.Replies[0].Date is null,
            "ODT: a reply that comes first is attached to its parent; an unknown date stays null.");
        Require(!inlines.OfType<CommentEnd>().Any(), "ODT: an end right after its start leaves a point comment.");
    }

    private static byte[] WritePdf(PortableDocument document, PdfCommentMode mode, out WriteResult result, PdfAConformance pdfA = PdfAConformance.None)
    {
        using var buffer = new MemoryStream();
        result = new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions
        {
            Comments = mode,
            PdfA = pdfA,
            Fonts = pdfA == PdfAConformance.None ? null : new Dictionary<string, byte[]> { [""] = PdfAChecks.Font() },
        }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static IReadOnlyList<PdfAnnotation> PdfNotes(byte[] pdf) => new PdfDocumentReader().ReadAnnotations(new MemoryStream(pdf));

    private static void WritesPdfNotes()
    {
        var plain = WritePdf(Sample(), PdfCommentMode.None, out var none);
        Require(!none.HasWarnings && PdfNotes(plain).Count == 0, "PDF: no comments by default.");

        var pdf = WritePdf(Sample(), PdfCommentMode.Annotations, out var result);
        Require(!result.HasWarnings, "PDF: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var notes = PdfNotes(pdf);
        Require(notes.Count == 3 && notes.All(note => note.Kind == PdfAnnotationKind.Text && note.Page == 1), "PDF: one note per anchored comment.");
        Require(notes.Select(note => note.Author).SequenceEqual(new[] { "Ann", "Cy", "Di" }), "PDF: authors.");
        Require(notes[0].Content == "First & <more>\nSecond\tline\nBob: Agreed", "PDF: the reply follows the text: " + notes[0].Content);
        Require(notes.All(note => note.Y > 0 && note.Y < 842), "PDF: the notes lie on the page.");
        Require(notes[1].Y < notes[0].Y && notes[2].Y < notes[1].Y, "PDF: the notes follow the order of the text down the page.");

        var report = WritePdf(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph([new TextRun("t")]))
        {
            Footer = new HeaderFooter(new Block[] { new Paragraph(new Inline[] { new Comment(0, "Ann", "x"), new TextRun("f") }) }),
        }), PdfCommentMode.Annotations, out var reported);
        Require(reported.Warnings.Any(w => w.Message.Contains("comment in a header, footer or note")) && PdfNotes(report).Count == 0, "PDF: a comment in a band is reported, not written.");
    }

    private static void PdfNotesFollowPagesAndPdfA()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph([new TextRun("first page")]),
            new PageBreak(),
            new Paragraph(new Inline[] { new Comment(0, "Ann", "second page"), new TextRun("text") }),
        }));
        var notes = PdfNotes(WritePdf(document, PdfCommentMode.Annotations, out _));
        Require(notes.Count == 1 && notes[0].Page == 2, "PDF: the note is on the page of its anchor.");

        // PDF/A: a note needs an appearance stream; the writer gives it one.
        // The test font has only the glyphs of "Hello".
        var small = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new Comment(0, "Ann", "note"), new TextRun("Hello") })));
        var pdfA = WritePdf(small, PdfCommentMode.Annotations, out var result, PdfAConformance.PdfA2b);
        var latin = System.Text.Encoding.Latin1.GetString(pdfA);
        Require(PdfNotes(pdfA).Count == 1 && latin.Contains("/AP "), "PDF/A: the notes carry an appearance stream: " + string.Join("|", result.Warnings.Select(w => w.Message)));
    }
}
