using Reglet;

/// <summary>PDF structure recovery (ADR-0033): headings, lists, tables, paragraphs, headers/footers, round trip.</summary>
internal static class PdfStructureChecks
{
    internal static int Run()
    {
        try
        {
            RecoversStructure();
            DropsRepeatedFooters();
            RecoversImages();
            RecoversImageInForm();
            LeavesOutRotatedText();
            ReadsNarrowGutter();
            ReadsColumnsAroundAWideLine();
            RecoversFractionAndScript();
            LeavesProseRulesAlone();
            LeavesChartLabelsAlone();
            ReadsUnicodeMathItalics();
            RecoversFormulaFromOwnPdf();
            RecoversOperatorsDrawnAsPaths();
            ReadsGlyphNamesFromDifferences();
            KeepsSubscriptsInTheLine();
            JoinsBlocksAcrossPages();
            ReadsRealColumns();
            KeepsListWhenNextStartsFurtherLeft();
            LeavesDefaultReaderAlone();
            Console.WriteLine("PDF structure: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF structure: " + exception);
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

    private static string Long(string word) => string.Join(' ', Enumerable.Repeat(word, 60));

    private static PortableDocument Source()
    {
        var numbering = new[]
        {
            new NumberingDefinition("bul", Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Bullet })),
            new NumberingDefinition("num", Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Number })),
        };
        var blocks = new List<Block>
        {
            new Heading(HeadingLevel.Level1, new TextRun("Haupttitel")),
            new Paragraph(new TextRun(Long("Alpha"))),
            new Paragraph(new TextRun(Long("Beta"))),
            new Heading(HeadingLevel.Level2, new TextRun("Unterkapitel")),
            new ListBlock("bul", [new ListItem(new Paragraph(new TextRun("erster Punkt"))), new ListItem(new Paragraph(new TextRun("zweiter Punkt")))]),
            new ListBlock("num", [new ListItem(new Paragraph(new TextRun("eins"))), new ListItem(new Paragraph(new TextRun("zwei"))), new ListItem(new Paragraph(new TextRun("drei")))]),
            new TableBlock([
                new TableRow(new TableCell("Name"), new TableCell("Wert"), new TableCell("Einheit")),
                new TableRow(new TableCell("Länge"), new TableCell("12"), new TableCell("cm")),
                new TableRow(new TableCell("Breite"), new TableCell("7"), new TableCell("cm")),
            ]),
            new Paragraph(new TextRun("Schluss.")),
        };
        return new PortableDocument(new DocumentMetadata(), new Section(blocks)) { Numbering = numbering };
    }

    private static byte[] WritePdf(PortableDocument document)
    {
        var stream = new MemoryStream();
        DocumentWriter.WriteAsync(document, stream, DocumentFormat.Pdf).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static void RecoversStructure()
    {
        var bytes = WritePdf(Source());
        var read = new PdfDocumentReader().Read(new MemoryStream(bytes), new PdfReadOptions { RecoverStructure = true });
        var blocks = read.Document.Sections.SelectMany(section => section.Blocks).ToList();
        Console.WriteLine("  recovered: " + string.Join(", ", blocks.Select(block => block.GetType().Name)));
        Require(blocks.OfType<Heading>().Count() == 2, "headings: " + blocks.OfType<Heading>().Count());
        Require(blocks.OfType<Heading>().First().Level == HeadingLevel.Level1 && blocks.OfType<Heading>().Last().Level == HeadingLevel.Level2, "heading levels");
        Require(blocks.OfType<Paragraph>().Count(p => p.Inlines.Sum(i => i is TextRun r ? r.Text.Length : 0) > 100) == 2, "two long paragraphs");
        Require(blocks.OfType<ListBlock>().Count() == 2 && blocks.OfType<ListBlock>().Sum(l => l.Items.Count) == 5, "lists");
        var table = blocks.OfType<TableBlock>().Single();
        Require(table.Rows.Count == 3 && table.Rows[0].Cells.Count == 3, "table");
        Require(blocks[^1] is Paragraph, "last paragraph");

        var docx = new MemoryStream();
        DocumentWriter.WriteAsync(read.Document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
        Require(docx.Length > 500, "DOCX written");

        // Round trip: the text of the source survives PDF and recovery, in order (list markers and cell borders aside).
        var back = new DocxDocumentReader().Read(new MemoryStream(docx.ToArray())).Document;
        static string Words(IEnumerable<Block> blocks) => string.Join(' ', blocks.SelectMany(Texts).SelectMany(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        var expected = Words(Source().Sections.SelectMany(section => section.Blocks));
        var actual = Words(back.Sections.SelectMany(section => section.Blocks));
        Require(expected.Replace("•", string.Empty, StringComparison.Ordinal) == actual.Replace("•", string.Empty, StringComparison.Ordinal), "round trip text:\n" + expected + "\n" + actual);
    }

    private static void DropsRepeatedFooters()
    {
        var blocks = new List<Block>();
        for (var page = 0; page < 4; page++)
        {
            blocks.Add(new Paragraph(new TextRun("Seite " + page + ": " + Long("Gamma"))));
            if (page < 3)
            {
                blocks.Add(new PageBreak());
            }
        }

        var document = new PortableDocument(new DocumentMetadata(), new Section(blocks)
        {
            Footer = new HeaderFooter(new Paragraph(new TextRun("Vertraulich"))),
        });
        var read = new PdfDocumentReader().Read(new MemoryStream(WritePdf(document)), new PdfReadOptions { RecoverStructure = true });
        var text = string.Concat(read.Document.Sections.SelectMany(s => s.Blocks).OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<TextRun>()).Select(r => r.Text));
        Require(!text.Contains("Vertraulich", StringComparison.Ordinal) && text.Contains("Gamma", StringComparison.Ordinal), "footer dropped");
        Require(read.Warnings.Any(w => w.Message.Contains("header and footer", StringComparison.Ordinal)), "footer reported");
    }

    private static void RecoversImages()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("Vor dem Bild")),
            new ImageBlock(png, ImageMediaType.Png) { Width = Length.FromPoints(100), Height = Length.FromPoints(60) },
            new Paragraph(new TextRun("Nach dem Bild")),
        }));
        var read = new PdfDocumentReader().Read(new MemoryStream(WritePdf(document)), new PdfReadOptions { RecoverStructure = true });
        var blocks = read.Document.Sections.SelectMany(section => section.Blocks).ToList();
        Console.WriteLine("  images: " + string.Join(", ", blocks.Select(block => block.GetType().Name)) + string.Concat(read.Warnings.Select(w => " | " + w.Message)));
        Require(blocks.Count == 3 && blocks[0] is Paragraph && blocks[1] is ImageBlock { MediaType: ImageMediaType.Png } && blocks[2] is Paragraph, "image between paragraphs");
        var image = (ImageBlock)blocks[1];
        Require(Math.Abs(image.Width!.Value.Points - 100) < 1 && Math.Abs(image.Height!.Value.Points - 60) < 1, "image size");
        var meta = ImageMetadataReader.Read(image.Data.ToArray(), ImageMediaType.Png);
        Require(meta.PixelWidth == 1 && meta.PixelHeight == 1, "re-encoded PNG is valid");
    }

    private static void KeepsListWhenNextStartsFurtherLeft()
    {
        var bullets = new NumberingDefinition("bul", Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Bullet }));
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new ListBlock("bul", [new ListItem(new Paragraph(new TextRun("tief a"))), new ListItem(new Paragraph(new TextRun("tief b")))]) { NestingLevel = 2 },
            new ListBlock("bul", [new ListItem(new Paragraph(new TextRun("flach c")))]),
        })) { Numbering = [bullets] };
        var read = new PdfDocumentReader().Read(new MemoryStream(WritePdf(document)), new PdfReadOptions { RecoverStructure = true });
        var text = string.Concat(read.Document.Sections.SelectMany(s => s.Blocks).OfType<ListBlock>().SelectMany(Flatten));
        Require(text.Contains("tief a", StringComparison.Ordinal) && text.Contains("tief b", StringComparison.Ordinal) && text.Contains("flach c", StringComparison.Ordinal), "no list item lost: " + text);
    }

    private static IEnumerable<string> Texts(Block block) => block switch
    {
        Paragraph p => [string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))],
        Heading h => [string.Concat(h.Inlines.OfType<TextRun>().Select(r => r.Text))],
        ListBlock l => l.Items.SelectMany(item => item.Blocks.SelectMany(Texts)),
        TableBlock t => t.Rows.SelectMany(row => row.Cells.SelectMany(cell => cell.Blocks.SelectMany(Texts))),
        _ => [],
    };

    private static IEnumerable<string> Flatten(ListBlock list)
        => list.Items.SelectMany(item => item.Blocks.SelectMany(block => block switch
        {
            Paragraph p => [string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))],
            ListBlock inner => Flatten(inner),
            _ => [],
        }));

    private static void JoinsBlocksAcrossPages()
    {
        var blocks = new List<Block>();
        for (var i = 0; i < 30; i++)
        {
            blocks.Add(new Heading(HeadingLevel.Level2, new TextRun("Abschnitt " + i)));
            blocks.Add(new Paragraph(new TextRun(string.Join(' ', Enumerable.Range(0, 140 + i).Select(n => "wort" + (n % 13))) + ".")));
        }

        blocks.Add(new TableBlock(Enumerable.Range(0, 70).Select(r => new TableRow(new TableCell("Zeile " + r), new TableCell("x"), new TableCell(r.ToString()))).ToList()));
        var read = new PdfDocumentReader().Read(new MemoryStream(WritePdf(new PortableDocument(new DocumentMetadata(), new Section(blocks)))), new PdfReadOptions { RecoverStructure = true });
        var recovered = read.Document.Sections.SelectMany(section => section.Blocks).ToList();
        Require(read.Document.Sections.Count == 1, "one flowing section");
        Require(recovered.OfType<Heading>().Count() == 30, "headings: " + recovered.OfType<Heading>().Count());
        Require(recovered.OfType<Paragraph>().Count() == 30, "paragraphs not split at pages: " + recovered.OfType<Paragraph>().Count());
        Require(recovered.OfType<TableBlock>().Count() == 1 && recovered.OfType<TableBlock>().Single().Rows.Count == 70, "table joined");

        // Two columns of prose, written as a borderless table so that the lines of both sit at the same height.
        var proseRows = Enumerable.Range(0, 10).Select(i => new TableRow(
            new TableCell("Zeile links " + i + " mit etwas Fließtext"), new TableCell("Zeile rechts " + i + " mit etwas Fließtext"))).ToList();
        var twoColumns = new PdfDocumentReader().Read(
            new MemoryStream(WritePdf(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { new TableBlock(proseRows) { Border = new ParagraphBorder(BorderStyle.None) } })))),
            new PdfReadOptions { RecoverStructure = true });
        Require(!twoColumns.Document.Sections.SelectMany(x => x.Blocks).OfType<TableBlock>().Any(), "columns are not a table");
        var columnText = string.Concat(twoColumns.Document.Sections.SelectMany(x => x.Blocks).OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<TextRun>()).Select(r => r.Text));
        Require(columnText.LastIndexOf("links", StringComparison.Ordinal) < columnText.IndexOf("rechts", StringComparison.Ordinal), "left column before right column");
        Require(twoColumns.Warnings.Any(w => w.Message.Contains("multi-column", StringComparison.Ordinal)), "columns reported");

        var scan = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new ImageBlock(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), ImageMediaType.Png) { Width = Length.FromPoints(200), Height = Length.FromPoints(200) },
        }));
        var scanned = new PdfDocumentReader().Read(new MemoryStream(WritePdf(scan)), new PdfReadOptions { RecoverStructure = true });
        Require(scanned.Warnings.Any(w => w.Message.Contains("OCR", StringComparison.Ordinal)), "scan reported");
    }

    private static void ReadsRealColumns()
    {
        // Twelve long paragraphs set in two text columns by the writer itself (the paragraphs end at different heights
        // in the two columns, so the lines of the columns do not share their baselines).
        var paragraphs = Enumerable.Range(0, 12).Select(i => (Block)new Paragraph(new TextRun(
            string.Join(' ', Enumerable.Range(0, 70 + i * 3).Select(n => "spalte" + i + "x" + (n % 9))) + "."))
        {
            Style = ParagraphStyle.Default with { SpaceAfter = Length.FromPoints(8) },
        }).ToList();
        var read = new PdfDocumentReader().Read(
            new MemoryStream(WritePdf(new PortableDocument(new DocumentMetadata(), new Section(paragraphs) { TextColumns = 2 }))),
            new PdfReadOptions { RecoverStructure = true });
        var recovered = read.Document.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>().ToList();
        Require(read.Warnings.Any(w => w.Message.Contains("multi-column", StringComparison.Ordinal)), "columns reported");
        // Each paragraph reads in one piece: the words of one paragraph stay in order and no word of another comes between.
        foreach (var paragraph in recovered)
        {
            var words = string.Concat(paragraph.Inlines.OfType<TextRun>().Select(r => r.Text)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ids = words.Select(word => word[..word.IndexOf('x')]).Distinct().ToList();
            Require(ids.Count == 1, "a recovered paragraph mixes " + ids.Count + " source paragraphs");
        }
    }

    private static byte[] HandMadePdf(params string[] objects)
    {
        var latin1 = System.Text.Encoding.Latin1;
        using var output = new MemoryStream();
        void Write(string text) => output.Write(latin1.GetBytes(text));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static string Stream(string dictionary, string data) => $"<< {dictionary} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static void RecoversImageInForm()
    {
        var pdf = HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 7 0 R >> /XObject << /Fm1 5 0 R >> >> >>",
            Stream("", "BT /F1 12 Tf 72 780 Td (Text oben) Tj ET q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 50] /Resources << /XObject << /Im1 6 0 R >> >>", "q 100 0 0 50 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\u00FF\u0000\u0000"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var read = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true });
        var blocks = read.Document.Sections.SelectMany(section => section.Blocks).ToList();
        Require(blocks.Count == 2 && blocks[0] is Paragraph && blocks[1] is ImageBlock { MediaType: ImageMediaType.Png }, "image in form: " + string.Join(", ", blocks.Select(b => b.GetType().Name)) + string.Concat(read.Warnings.Select(w => " | " + w.Message)));
    }

    private static List<Block> RecoverBlocks(string content, out IReadOnlyList<ReadWarning> warnings)
    {
        var pdf = HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var read = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true });
        warnings = read.Warnings;
        return read.Document.Sections.SelectMany(section => section.Blocks).ToList();
    }

    private static string AllText(IEnumerable<Block> blocks)
        => string.Join(" ", blocks.OfType<Paragraph>().Select(p => string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))));

    private static void LeavesOutRotatedText()
    {
        var blocks = RecoverBlocks("BT /F1 12 Tf 72 780 Td (Haupttext) Tj ET BT /F1 8 Tf 0 1 -1 0 20 400 Tm (Stempel) Tj ET", out var warnings);
        var text = AllText(blocks);
        Require(text.Contains("Haupttext", StringComparison.Ordinal) && !text.Contains("Stempel", StringComparison.Ordinal), "rotated text left out: " + text);
        Require(warnings.Any(w => w.Message.Contains("rotated", StringComparison.Ordinal)), "rotated text is reported");
    }

    private static void ReadsGlyphNamesFromDifferences()
    {
        // LaTeX/dvips fonts name their ligatures and Greek letters in /Differences and have no ToUnicode map.
        var pdf = HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 12 Tf 72 780 Td (\\013rst \\014 \\015 o\\016ce) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [11 /fi /nu /alpha /ffi] >> >>");
        var read = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true });
        var text = AllText(read.Document.Sections.SelectMany(section => section.Blocks));
        Require(text.Contains("first \u03BD \u03B1 office", StringComparison.Ordinal), "glyph names from Differences: " + text);
    }

    private static void KeepsSubscriptsInTheLine()
    {
        var blocks = RecoverBlocks("BT /F1 10 Tf 72 700 Td (m) Tj ET BT /F1 6 Tf 79 697 Td (2) Tj ET BT /F1 10 Tf 90 700 Td (und mehr) Tj ET", out _);
        var text = AllText(blocks);
        Require(text.Contains("m2 und mehr", StringComparison.Ordinal), "subscript stays in its line: " + text);
    }

    private static void ReadsColumnsAroundAWideLine()
    {
        // Two columns with a few lines across both (a figure caption, labels): the gutter is still found and the wide
        // lines stay between the column bands.
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 24; line++)
        {
            var y = 780 - (line * 14);
            if (line is >= 10 and < 14)
            {
                content.Append($"BT /F1 10 Tf 72 {y} Td (breit{line:D2} aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk lll mmm nnn ooo ppp qqq rrr sss ttt uuu vvv www xxx) Tj ET ");
                continue;
            }

            content.Append($"BT /F1 10 Tf 72 {y} Td (links{line:D2} aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk) Tj ET ");
            content.Append($"BT /F1 10 Tf 306 {y} Td (rechts{line:D2} aaa bbb ccc ddd eee fff ggg hhh iii jjj kk) Tj ET ");
        }

        var text = AllText(RecoverBlocks(content.ToString(), out _));
        int At(string key) => text.IndexOf(key, StringComparison.Ordinal);
        Require(At("links09") >= 0 && At("links09") < At("rechts00") && At("rechts09") < At("breit10") && At("breit13") < At("links14") && At("links23") < At("rechts14"),
            "columns around a wide line: " + text[..Math.Min(300, text.Length)]);
    }

    private static List<Block> RecoverWithFormulas(string content)
    {
        var pdf = HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true })
            .Document.Sections.SelectMany(section => section.Blocks).ToList();
    }

    private static IEnumerable<MathNode> Walk(MathNode node)
    {
        yield return node;
        var children = node switch
        {
            MathRow row => row.Items,
            MathFraction fraction => [fraction.Numerator, fraction.Denominator],
            MathScript script => new[] { script.Base, script.Superscript, script.Subscript }.OfType<MathNode>().ToList(),
            MathRadical radical => new[] { radical.Radicand, radical.Degree }.OfType<MathNode>().ToList(),
            _ => (IReadOnlyList<MathNode>)[],
        };
        foreach (var inner in children.SelectMany(Walk))
        {
            yield return inner;
        }
    }

    private static void RecoversFractionAndScript()
    {
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 4; line++)
        {
            content.Append($"BT /F1 10 Tf 72 {780 - (line * 14)} Td (Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.) Tj ET ");
        }

        // x = (a+b)/(2c): bar 2.5 pt above the baseline of "x =", numerator above, denominator below.
        content.Append("BT /F1 10 Tf 200 600 Td (x =) Tj ET 228 602 34 0.5 re f BT /F1 10 Tf 238 610 Td (a+b) Tj ET BT /F1 10 Tf 242 592 Td (2c) Tj ET ");

        // y = x^2 as a line of its own, indented, the 2 smaller and higher.
        content.Append("BT /F1 10 Tf 250 520 Td (y = x) Tj ET BT /F1 6 Tf 277 524 Td (2) Tj ET ");

        var blocks = RecoverWithFormulas(content.ToString());
        var equations = blocks.OfType<Equation>().ToList();
        Require(equations.Count == 2, "two equations: " + string.Join(", ", blocks.Select(b => b.GetType().Name)));
        Require(Walk(equations[0].Root).OfType<MathFraction>().Any(), "fraction recovered");
        Require(Walk(equations[1].Root).OfType<MathScript>().Any(script => script.Superscript is MathText { Text: "2" }), "superscript recovered");
        var withoutOption = new PdfDocumentReader().Read(new MemoryStream(HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", content.ToString()), "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")), new PdfReadOptions { RecoverStructure = true });
        Require(!withoutOption.Document.Sections.SelectMany(section => section.Blocks).Any(block => block is Equation), "formulas are opt-in");
    }

    // A PDF that this library's writer made (EQUATION_OUT) for x = (-b ± sqrt(b^2 - 4ac)) / 2a and a sum, an integral and a cube root (operators drawn as lines).
    private const string OwnFormulaPdf = "JVBERi0xLjcKJeLjz9MKOSAwIG9iago8PC9UeXBlIC9Gb250IC9TdWJ0eXBlIC9UeXBlMSAvQmFzZUZvbnQgL0hlbHZldGljYSAvRW5jb2RpbmcgL1dpbkFuc2lFbmNvZGluZyA+PgplbmRvYmoKMTEgMCBvYmoKPDwvVHlwZSAvRm9udCAvU3VidHlwZSAvVHlwZTEgL0Jhc2VGb250IC9IZWx2ZXRpY2EtT2JsaXF1ZSAvRW5jb2RpbmcgL1dpbkFuc2lFbmNvZGluZyA+PgplbmRvYmoKMTUgMCBvYmoKPDwgL0xlbmd0aCA3MTYgL0ZpbHRlciAvRmxhdGVEZWNvZGUgPj4Kc3RyZWFtCniclVbJbhRBDL33V9QRhPRSZZc3CTgQFnEAsYw45RKWsCiAGIGAv0dVPUt3mEmD5ubxs5/tZ1efPEu3b588OX18P+W7d9O9+6fDvdWQU/ut3w8nD0sqJa0uBqNkEgiWtHo73Hj1df3h3fpmWn0aHqyGB09Oh5OHX9eff1ye7+KVg/FoE4+koFRJxgVZxqC/tvEOUyBx+BXInS3k2x5BaqgayZghnEyhWlKGSFq/Gy6G5zNnK+ASyWpGpIqQlKG69TxC3gK5gdhRvRN5vcDdDcwyRZyRlqvsXzwaOs+fA4VApSSrhKqUPg8UBs7NUmClpsuBwhHNhwXG6XLgXGDNRTI00uXwcl7s9P9SU80wi1lnDtfLmUA90XK9BhshDlZLVhmk47DowLC4CKL6v/V/200mhjbQnk89zGdXAQVM6xRxvoBggc9SvFkYcQTUa7JGjucFH2urwKf+59cuFG0WaiYWsyYWLXDjZLnCsjSx7CyK7E0rUhBzg6EWTxoByjJx0SBwk9M2yAED9zyjwK4Wt1EASYV5yxgoeVTAl2MN2WG6MNUFdbPhHxeERsJgjxnmziImUElnmN0uHll5FbBY0tAd5NdSNZbhHUPQvFDO9rIIuMzT/HXj+uCd+pnw6Otl2VCqtcm7t/1pXSe2NjS3jRQCmfro2zHy6FoQ6gZFdFAGdSm4QioljQKPjSGLJ/UA+4gRkFG3lH59yCtUG/cCUj6ojv0IPBBROocS103gL4i6QTe9yQtTC4M0kpN2Xlw/Ac65reMUcXZjaY9ruxFTyPelJAbhOeTs5kKWUsA+x7xdghgk1/9iRgTTecduHX+mmBsp7VeEo+mPmZG5WRymTUzMFdp9tleD2VC56U3Bsr0j01dh5yDwLMlQ2ruw+E6xIXqmPfnfx+oVyIgZK7BcQDFieHaH959IvPSJpNr0NgZ5ev7mw5VvpD85HfIRCmVuZHN0cmVhbQplbmRvYmoKOCAwIG9iago8PC9UeXBlIC9QYWdlIC9QYXJlbnQgMiAwIFIgL1Jlc291cmNlcyA0IDAgUiAvTWVkaWFCb3ggWzAgMCA1OTUuMjc1NTkgODQxLjg4OTc2IF0vU3RydWN0UGFyZW50cyAwIC9Db250ZW50cyAxNSAwIFIgPj4KZW5kb2JqCjcgMCBvYmoKPDwvVHlwZSAvU3RydWN0RWxlbSAvUyAvRG9jdW1lbnQgL1AgNiAwIFIgL0sgWzEwIDAgUiAxMiAwIFIgMTMgMCBSIDE0IDAgUiBdPj4KZW5kb2JqCjEwIDAgb2JqCjw8L1R5cGUgL1N0cnVjdEVsZW0gL1MgL1AgL1AgNyAwIFIgL1BnIDggMCBSIC9LIDw8L1R5cGUgL01DUiAvUGcgOCAwIFIgL01DSUQgMCA+Pj4+CmVuZG9iagoxMiAwIG9iago8PC9UeXBlIC9TdHJ1Y3RFbGVtIC9TIC9Gb3JtdWxhIC9QIDcgMCBSIC9QZyA4IDAgUiAvQWx0IChNaXR0ZXJuYWNodHNmb3JtZWwpIC9LIDw8L1R5cGUgL01DUiAvUGcgOCAwIFIgL01DSUQgMSA+Pj4+CmVuZG9iagoxMyAwIG9iago8PC9UeXBlIC9TdHJ1Y3RFbGVtIC9TIC9Gb3JtdWxhIC9QIDcgMCBSIC9QZyA4IDAgUiAvSyA8PC9UeXBlIC9NQ1IgL1BnIDggMCBSIC9NQ0lEIDIgPj4+PgplbmRvYmoKMTQgMCBvYmoKPDwvVHlwZSAvU3RydWN0RWxlbSAvUyAvUCAvUCA3IDAgUiAvUGcgOCAwIFIgL0sgPDwvVHlwZSAvTUNSIC9QZyA4IDAgUiAvTUNJRCAzID4+Pj4KZW5kb2JqCjE2IDAgb2JqCjw8L051bXMgWzAgWzEwIDAgUiAxMiAwIFIgMTMgMCBSIDE0IDAgUiBdXT4+CmVuZG9iago2IDAgb2JqCjw8L1R5cGUgL1N0cnVjdFRyZWVSb290IC9LIFs3IDAgUiBdL1BhcmVudFRyZWUgMTYgMCBSIC9QYXJlbnRUcmVlTmV4dEtleSAxID4+CmVuZG9iagoyIDAgb2JqCjw8L1R5cGUgL1BhZ2VzIC9LaWRzIFs4IDAgUiBdL0NvdW50IDEgPj4KZW5kb2JqCjEgMCBvYmoKPDwvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgL1N0cnVjdFRyZWVSb290IDYgMCBSIC9NYXJrSW5mbyA8PC9NYXJrZWQgdHJ1ZSA+Pi9WaWV3ZXJQcmVmZXJlbmNlcyA8PC9EaXNwbGF5RG9jVGl0bGUgdHJ1ZSA+Pi9NZXRhZGF0YSA1IDAgUiA+PgplbmRvYmoKMyAwIG9iago8PC9Qcm9kdWNlciAoUG9ydGFibGVEb2NzKSA+PgplbmRvYmoKNSAwIG9iago8PCAvTGVuZ3RoIDM3NyAvVHlwZSAvTWV0YWRhdGEgL1N1YnR5cGUgL1hNTCA+PgpzdHJlYW0KPD94cGFja2V0IGJlZ2luPSLvu78iIGlkPSJXNU0wTXBDZWhpSHpyZVN6TlRjemtjOWQiPz4KPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczptZXRhLyI+CjxyZGY6UkRGIHhtbG5zOnJkZj0iaHR0cDovL3d3dy53My5vcmcvMTk5OS8wMi8yMi1yZGYtc3ludGF4LW5zIyI+CjxyZGY6RGVzY3JpcHRpb24gcmRmOmFib3V0PSIiIHhtbG5zOnBkZj0iaHR0cDovL25zLmFkb2JlLmNvbS9wZGYvMS4zLyIgeG1sbnM6ZGM9Imh0dHA6Ly9wdXJsLm9yZy9kYy9lbGVtZW50cy8xLjEvIj4KPHBkZjpQcm9kdWNlcj5Qb3J0YWJsZURvY3M8L3BkZjpQcm9kdWNlcj4KPC9yZGY6RGVzY3JpcHRpb24+CjwvcmRmOlJERj4KPC94OnhtcG1ldGE+Cjw/eHBhY2tldCBlbmQ9InciPz4KZW5kc3RyZWFtCmVuZG9iago0IDAgb2JqCjw8L0ZvbnQgPDwvRjEgOSAwIFIgL0YyIDExIDAgUiA+Pj4+CmVuZG9iagp4cmVmCjAgMTcKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAxODc5IDAwMDAwIG4gCjAwMDAwMDE4MjMgMDAwMDAgbiAKMDAwMDAwMjAzNyAwMDAwMCBuIAowMDAwMDAyNTQwIDAwMDAwIG4gCjAwMDAwMDIwODIgMDAwMDAgbiAKMDAwMDAwMTczMCAwMDAwMCBuIAowMDAwMDAxMTM4IDAwMDAwIG4gCjAwMDAwMDEwMDUgMDAwMDAgbiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAxMjMxIDAwMDAwIG4gCjAwMDAwMDAxMTEgMDAwMDAgbiAKMDAwMDAwMTMzMSAwMDAwMCBuIAowMDAwMDAxNDYzIDAwMDAwIG4gCjAwMDAwMDE1NjkgMDAwMDAgbiAKMDAwMDAwMDIxNiAwMDAwMCBuIAowMDAwMDAxNjY5IDAwMDAwIG4gCnRyYWlsZXIKPDwvU2l6ZSAxNyAvUm9vdCAxIDAgUiAvSW5mbyAzIDAgUiA+PgpzdGFydHhyZWYKMjU5MQolJUVPRgo=";

    private static void RecoversFormulaFromOwnPdf()
    {
        var bytes = Convert.FromBase64String(OwnFormulaPdf);
        var blocks = new PdfDocumentReader().Read(new MemoryStream(bytes), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true })
            .Document.Sections.SelectMany(section => section.Blocks).ToList();
        var all = blocks.OfType<Equation>().ToList();
        Require(all.Count == 2, "own PDF: two equations");
        var equation = all[0];
        var second = Walk(all[1].Root).ToList();
        Require(second.OfType<MathNary>().Select(nary => nary.Operator).SequenceEqual("∑∫") && second.OfType<MathRadical>().Any(radical => radical.Degree is MathText { Text: "3" }),
            "own PDF: sum, integral and cube root drawn as lines");
        var fraction = Walk(equation!.Root).OfType<MathFraction>().FirstOrDefault();
        Require(fraction is not null, "own PDF: fraction");
        Require(Walk(fraction!.Numerator).Any(node => node is MathRadical), "own PDF: radical in the numerator");
        Require(Walk(fraction.Numerator).Any(node => node is MathScript { Superscript: MathText { Text: "2" } }), "own PDF: b squared");
        Require(string.Concat(Walk(fraction.Denominator).OfType<MathText>().Select(text => text.Text)) == "2a", "own PDF: denominator 2a");
    }

    private static void RecoversOperatorsDrawnAsPaths()
    {
        // The library's own PDF writer draws big operators and root signs as lines (no math font): every one of them must
        // come back as the same operator with its limits.
        MathNode T(string text) => new MathText(text);
        var blocks = new List<Block> { new Paragraph(new TextRun("Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.")) };
        foreach (var op in "∑∏∫∬∮⋃⋂")
        {
            blocks.Add(new Equation(new MathRow([T("y"), T("="), new MathNary(op, T("x"), T("0"), T("9"))])));
            blocks.Add(new Paragraph(new TextRun("Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.")));
        }

        blocks.Add(new Equation(new MathRow([T("z"), T("="), new MathRadical(T("w"), T("3"))])));
        var pdf = WritePdf(new PortableDocument(new DocumentMetadata(), new Section(blocks)));
        var read = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true });
        var equations = read.Document.Sections.SelectMany(section => section.Blocks).OfType<Equation>().ToList();
        var found = equations.SelectMany(equation => Walk(equation.Root)).OfType<MathNary>().Select(nary => nary.Operator).ToList();
        Require(found.SequenceEqual("∑∏∫∬∮⋃⋂"), "operators drawn as paths: " + new string([.. found]) + " in " + equations.Count + " equations");
        Require(equations.SelectMany(equation => Walk(equation.Root)).OfType<MathNary>().All(nary => nary.Lower is not null && nary.Upper is not null), "limits of the operators");
        Require(equations.SelectMany(equation => Walk(equation.Root)).OfType<MathRadical>().Any(radical => radical.Degree is MathText { Text: "3" }), "root with degree");
    }

    private static void ReadsUnicodeMathItalics()
    {
        // Word writes variables as mathematical italic letters (U+1D465 for x) and function names as plain letters.
        const string Cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T def 1 begincodespacerange <00> <FF> endcodespacerange 3 beginbfchar <41> <D835DC65> <42> <0073> <43> <0069> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 4; line++)
        {
            content.Append($"BT /F2 10 Tf 72 {780 - (line * 14)} Td (Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.) Tj ET ");
        }

        // "x = sin x" with x as U+1D465, "s" and "i" plain; n plain too (code n is WinAnsi).
        content.Append("BT /F1 10 Tf 250 600 Td (A) Tj ET BT /F2 10 Tf 258 600 Td (=) Tj ET BT /F1 10 Tf 268 600 Td (BCn) Tj ET BT /F1 10 Tf 288 600 Td (A) Tj ET");
        var pdf = HandMadePdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 7 0 R >> >> >>",
            Stream("", content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            Stream("", Cmap),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var equation = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true })
            .Document.Sections.SelectMany(section => section.Blocks).OfType<Equation>().SingleOrDefault();
        Require(equation is not null, "Unicode math line is an equation");
        var texts = Walk(equation!.Root).OfType<MathText>().ToList();
        Require(texts.Any(text => text.Text == "x" && text.Style == MathTextStyle.Italic), "𝑥 becomes x, italic");
        Require(texts.Any(text => text.Text.Contains("sin", StringComparison.Ordinal) && text.Style == MathTextStyle.Normal), "sin stays upright");
        Require(!texts.Any(text => text.Text.Contains('\uD835')), "no mathematical alphanumerics left");
    }

    private static void LeavesChartLabelsAlone()
    {
        // The same "y = x²" that is recovered on its own stays text inside a chart (axes, ticks and a curve around it).
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 4; line++)
        {
            content.Append($"BT /F1 10 Tf 72 {780 - (line * 14)} Td (Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.) Tj ET ");
        }

        // A vertical axis with ticks, a horizontal axis and a curve; the label sits next to the ticks.
        content.Append("200 470 m 200 580 l S 200 470 m 400 470 l S 196 500 m 204 500 l S 196 520 m 204 520 l S 196 540 m 204 540 l S 196 560 m 204 560 l S ");
        content.Append("210 480 m 260 560 l 330 500 l 390 570 l S ");
        content.Append("BT /F1 10 Tf 212 545 Td (y = x) Tj ET BT /F1 6 Tf 239 549 Td (2) Tj ET ");
        Require(!RecoverWithFormulas(content.ToString()).OfType<Equation>().Any(), "a label inside a chart is not a formula");
    }

    private static void LeavesProseRulesAlone()
    {
        // A link underline and a table-like rule between prose lines are not fraction bars.
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 4; line++)
        {
            content.Append($"BT /F1 10 Tf 72 {780 - (line * 14)} Td (Dies ist ein langer Absatz mit vielen Worten damit die Zeile breit genug wird und Fliesstext bleibt.) Tj ET ");
        }

        content.Append("72 776.5 200 0.5 re f 72 738 300 0.5 re f 10 10 m 5 c 7 y S"); // also a broken curve: no exception
        Require(!RecoverWithFormulas(content.ToString()).OfType<Equation>().Any(), "prose with rules stays text");
    }

    private static void ReadsNarrowGutter()
    {
        // Two columns of prose with only 9 pt between them (narrower than the gap of table cells).
        var content = new System.Text.StringBuilder();
        for (var line = 0; line < 20; line++)
        {
            var y = 780 - (line * 14);
            content.Append($"BT /F1 10 Tf 72 {y} Td (links{line:D2} aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk) Tj ET ");
            content.Append($"BT /F1 10 Tf 306 {y} Td (rechts{line:D2} aaa bbb ccc ddd eee fff ggg hhh iii jjj kk) Tj ET ");
        }

        var blocks = RecoverBlocks(content.ToString(), out _);
        Require(!blocks.OfType<TableBlock>().Any(), "narrow gutter: no table");
        var text = AllText(blocks);
        Require(text.IndexOf("links19", StringComparison.Ordinal) is var last and >= 0 && last < text.IndexOf("rechts00", StringComparison.Ordinal),
            "narrow gutter: left column before right column: " + text[..Math.Min(200, text.Length)]);
    }

    private static void LeavesDefaultReaderAlone()
    {
        var read = new PdfDocumentReader().Read(new MemoryStream(WritePdf(Source())));
        Require(!read.Document.Sections.SelectMany(s => s.Blocks).Any(block => block is Heading or ListBlock or TableBlock), "default stays plain");
    }
}
