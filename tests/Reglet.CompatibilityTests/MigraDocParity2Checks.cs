using System.Globalization;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// N1–N7: the second round of what a MigraDoc document rebuilt on the document model needs, from page 2 on: rows kept
/// on one page, inline images, breaks after hyphens, fixed column widths, a header that does not push the body,
/// kerning and ligatures off, and the header and footer distance.
/// </summary>
internal static class MigraDocParity2Checks
{
    internal static int Run()
    {
        try
        {
            RowKeptOnOnePage();
            LineBreaksAfterHyphen();
            TrailingSpacesHangOverTheEdge();
            WidowControlKeepsTwoLinesOnEachPage();
            WidowControlSurvivesDocxAndOdt();
            FixedColumnWidthsOverflow();
            ColumnWidthsInPercent();
            HeaderDistanceAndBodyStart();
            InlineImageStandsOnTheBaseline();
            Console.WriteLine("MigraDoc parity 2: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("MigraDoc parity 2: " + exception);
            return 1;
        }
    }

    /// <summary>The text pieces of every page: font size, x, baseline and text.</summary>
    private static List<List<(double Size, double X, double Y, string Text)>> Pages(PortableDocument document, PdfWriteOptions? options = null)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return PdfStreams.DecompressPages(buffer.ToArray())
            .Select(page => Regex.Matches(page, @"/F\d+ ([\d.]+) Tf\s*(-?[\d.]+) (-?[\d.]+) Td\s*\(([^)]*)\) Tj")
                .Select(match => (Number(match.Groups[1].Value), Number(match.Groups[2].Value), Number(match.Groups[3].Value), match.Groups[4].Value))
                .ToList())
            .ToList();
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static PortableDocument Document(Section section) => new(new DocumentMetadata(), section);

    private static int PageOf(List<List<(double Size, double X, double Y, string Text)>> pages, string text)
        => pages.FindIndex(page => page.Any(piece => piece.Text == text));

    private static void RowKeptOnOnePage()
    {
        PortableDocument Sheet(int fillers, bool keep)
        {
            var rows = new List<TableRow> { new(new TableCell([new Paragraph(new TextRun("HEAD"))])) { IsHeader = true } };
            rows.AddRange(Enumerable.Range(0, fillers).Select(index => new TableRow(new TableCell([new Paragraph(new TextRun("row" + index))]))));
            rows.Add(new TableRow(new TableCell(Enumerable.Range(1, 5).Select(line => (Block)new Paragraph(new TextRun("L" + line))))) { KeepTogether = keep });
            return Document(new Section(new TableBlock(rows)));
        }

        // Find a table whose five-line row is split at the page end today.
        var fillers = Enumerable.Range(20, 60).First(count =>
        {
            var pages = Pages(Sheet(count, keep: false));
            return PageOf(pages, "L1") == 0 && PageOf(pages, "L5") == 1;
        });
        var kept = Pages(Sheet(fillers, keep: true));
        Require(Enumerable.Range(1, 5).All(line => PageOf(kept, "L" + line) == 1), "The kept row starts whole on the next page.");
        Require(kept[1].Single(piece => piece.Text == "HEAD").Y > kept[1].Single(piece => piece.Text == "L1").Y, "Below the repeated header row.");

        foreach (var (name, writer, reader) in new (string, IDocumentWriter, IDocumentReader)[]
        {
            ("DOCX", new DocxDocumentWriter(), new DocxDocumentReader()),
            ("ODT", new OdtDocumentWriter(), new OdtDocumentReader()),
        })
        {
            using var buffer = new MemoryStream();
            writer.WriteAsync(Sheet(3, keep: true), buffer).GetAwaiter().GetResult();
            buffer.Position = 0;
            var rows = reader.Read(buffer).Document.Sections[0].Blocks.OfType<TableBlock>().Single().Rows;
            Require(rows[^1].KeepTogether && !rows[1].KeepTogether, name + ": KeepTogether survives on its row only.");
        }
    }

    private static void WidowControlKeepsTwoLinesOnEachPage()
    {
        // Word 16, measured: a paragraph of N lines (line breaks) with room for k of them at the foot of the page keeps
        // at least two lines on each page, in the text and in a table row, also without w:widowControl; with
        // w:widowControl="0" it splits where the room ends.
        int LinesOnFirstPage(int lines, int room, bool inCell, bool? widowControl)
        {
            var exact = ParagraphStyle.Default with { LineSpacing = LineSpacing.Exactly(Length.FromPoints(12)), SpaceBefore = Length.Zero, SpaceAfter = Length.Zero };
            var spacer = new Paragraph(new TextRun("top")) { Style = exact with { LineSpacing = LineSpacing.Exactly(Length.FromPoints(PageSize.A4.Height.Points - 144 - (room * 12) - 2)) } };
            var paragraph = new Paragraph(new TextRun(string.Join("\n", Enumerable.Range(1, lines).Select(line => $"L{line}")))) { Style = exact with { WidowControl = widowControl } };
            Block block = inCell ? new TableBlock([new TableRow([new TableCell([paragraph]) { PaddingVertical = Length.Zero }])]) : paragraph;
            var document = Document(new Section([spacer, block]) { Margins = new PageMargins(Length.FromPoints(72)) });
            return Pages(document)[0].Count(piece => piece.Text.StartsWith('L'));
        }

        (int Lines, int Room, int First)[] word = [(2, 1, 0), (3, 1, 0), (3, 2, 0), (4, 1, 0), (4, 2, 2), (4, 3, 2), (5, 1, 0), (5, 2, 2), (5, 3, 3), (5, 4, 3)];
        foreach (var inCell in new[] { false, true })
        {
            foreach (var (lines, room, first) in word)
            {
                var on = LinesOnFirstPage(lines, room, inCell, widowControl: null);
                Require(on == first, $"{lines} lines, room for {room}, in a cell {inCell}: {first} on the first page, as in Word ({on}).");
                var off = LinesOnFirstPage(lines, room, inCell, widowControl: false);
                Require(off == room, $"{lines} lines, room for {room}, in a cell {inCell}, widow control off: split where the room ends ({off}).");
            }
        }
    }

    private static void WidowControlSurvivesDocxAndOdt()
    {
        var document = Document(new Section([
            new Paragraph(new TextRun("off")) { Style = ParagraphStyle.Default with { WidowControl = false } },
            new Paragraph(new TextRun("default")),
        ]));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var package = TestPackages.Write(document, format);
            var paragraphs = TestPackages.Read(package, format).Sections[0].Blocks.OfType<Paragraph>().ToList();
            Require(paragraphs[0].Style.WidowControl == false && paragraphs[1].Style.WidowControl is null,
                $"{format}: an explicit off is read back, the default stays unset ({paragraphs[0].Style.WidowControl}, {paragraphs[1].Style.WidowControl}).");
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(package));
            string Part(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
            if (format == DocumentFormat.Docx)
            {
                var body = Part("word/document.xml");
                Require(body.Contains("<w:widowControl w:val=\"0\" />", StringComparison.Ordinal) && body.Split("widowControl").Length == 2,
                    "DOCX: only the explicit off is written (Word takes a missing w:widowControl as on).");
            }
            else
            {
                Require(Part("styles.xml").Contains("fo:widows=\"2\"", StringComparison.Ordinal) && Part("content.xml").Contains("fo:widows=\"0\"", StringComparison.Ordinal),
                    "ODT: the default style keeps two lines (LibreOffice takes none as off), the paragraph switches it off.");
            }
        }
    }

    private static void TrailingSpacesHangOverTheEdge()
    {
        // Word: "Date: " right aligned ends with the colon at the edge, the space hangs over it; centered likewise.
        double X(string text, TextAlignment alignment, bool inCell)
        {
            var paragraph = new Paragraph(new TextRun(text) { Bold = true }) { Style = ParagraphStyle.Default with { Alignment = alignment } };
            Block block = inCell ? new TableBlock([new TableRow([new TableCell([paragraph])])]) : paragraph;
            return Pages(Document(new Section(block)))[0].First(piece => piece.Text == "Date:").X;
        }

        foreach (var alignment in new[] { TextAlignment.Right, TextAlignment.Center })
        {
            foreach (var inCell in new[] { false, true })
            {
                var trailing = X("Date: ", alignment, inCell);
                var plain = X("Date:", alignment, inCell);
                Require(Math.Abs(trailing - plain) < 0.01, $"{alignment}, in a cell {inCell}: the trailing space does not count ({trailing} / {plain}).");
            }
        }
    }

    private static void LineBreaksAfterHyphen()
    {
        List<(double Size, double X, double Y, string Text)> Cell(string text)
            => Pages(Document(new Section(new TableBlock([new TableRow(new TableCell([new Paragraph(new TextRun(text))]))])
            {
                ColumnWidths = [Length.FromCentimeters(7.5)],
            })))[0];

        var pieces = Cell("Typ: UP-Lochung, handelsübliche UP-Kombination, Blindabdeckung 80x80mm");
        var hyphen = pieces.Single(piece => piece.Text == "UP-" && pieces.IndexOf(piece) > 2);
        var word = pieces.Single(piece => piece.Text == "Kombination,");
        Require(word.Y < hyphen.Y - 5 && Math.Abs(word.X - pieces[0].X) < 0.01, "The line breaks after 'UP-', 'Kombination' starts the next line.");

        // A minus before a number and a hyphen at the start of a word stay with what follows.
        var minus = Cell("Temperatur zwischen aaaaaaaaaaaaaaaaa -5 und +5 Grad");
        Require(minus.Any(piece => piece.Text == "-5"), "'-5' is not split.");
        // Word also breaks after a hyphen before a digit: "DIN EN ISO 9170-" / "2", "12-12-" / "2022".
        var dates = Cell("Daten " + string.Join("-", Enumerable.Repeat("12-12-2022", 6)));
        Require(dates.Count > 2 && dates.Skip(1).All(piece => piece.Text is [.., '-'] || piece == dates[^1] || piece.Text == "Daten" || piece.Text == " ")
                && dates[^1].Text is [>= '0' and <= '9', ..] && dates[^1].Y < dates[^2].Y - 5,
            $"The line breaks after a hyphen before a digit ({string.Join(" | ", dates.Select(piece => piece.Text))}).");
    }

    private static void FixedColumnWidthsOverflow()
    {
        // Four columns 0.8 cm wider than the 451.28 pt of A4 with normal margins.
        var widths = new[] { 4.0, 4.0, 4.0, 4.72 }.Select(Length.FromCentimeters).ToList();
        TableBlock Header(bool fixedWidths) => new([new TableRow(new[] { "A", "B", "C", "PAGE" }.Select(text => new TableCell([new Paragraph(new TextRun(text))])))])
        {
            ColumnWidths = widths,
            FixedColumnWidths = fixedWidths,
        };

        double PageX(bool fixedWidths) => Pages(Document(new Section(Header(fixedWidths))))[0].Single(piece => piece.Text == "PAGE").X;
        var expected = 72 + widths.Take(3).Sum(width => width.Points) + 5.4;
        Require(Math.Abs(PageX(fixedWidths: true) - expected) < 0.01, $"Fixed widths are kept past the margin ({PageX(true)} / {expected}).");
        Require(PageX(fixedWidths: false) < expected - 10, "By default the table is shrunk to the text width.");

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(new Section(Header(fixedWidths: true))), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document.Sections[0].Blocks.OfType<TableBlock>().Single();
        Require(read.FixedColumnWidths && Math.Abs(read.ColumnWidths!.Sum(width => width.Points) - widths.Sum(width => width.Points)) < 0.1,
            "DOCX: the fixed layout and its widths survive.");
    }

    /// <summary>Column widths in percent of the table follow the section's text width in every writer.</summary>
    private static void ColumnWidthsInPercent()
    {
        TableBlock Table(params double[] percents) => new([new TableRow(new[] { "A", "B", "C" }.Select(text => new TableCell([new Paragraph(new TextRun(text))])))])
        {
            ColumnWidthPercents = percents,
        };

        // A4 with 72 pt margins: a 451.28 pt text width; 20 % listed, the other two share 80 %.
        var page = Pages(Document(new Section(Table(20))))[0];
        double X(string text) => page.Single(piece => piece.Text == text).X;
        var text = 595.28 - 144;
        Require(Math.Abs(X("B") - (72 + text * 0.2 + 5.4)) < 0.01 && Math.Abs(X("C") - (72 + text * 0.6 + 5.4)) < 0.01,
            $"PDF: 20 % and two columns of 40 % ({X("B")}, {X("C")}).");

        var landscape = new Section(Table(20, 30, 50)) { Orientation = PageOrientation.Landscape };
        var body = new StreamReader(new System.IO.Compression.ZipArchive(Write(new DocxDocumentWriter(), Document(landscape)))
            .GetEntry("word/document.xml")!.Open()).ReadToEnd();
        Require(body.Contains("w:tblW w:w=\"5000\" w:type=\"pct\"", StringComparison.Ordinal)
            && body.Contains("w:tcW w:w=\"1000\" w:type=\"pct\"", StringComparison.Ordinal)
            && body.Contains("w:tcW w:w=\"2500\" w:type=\"pct\"", StringComparison.Ordinal),
            "DOCX: a percent table with percent cells.");
        var landscapeText = (841.89 - 144) * 20;
        Require(body.Contains($"w:gridCol w:w=\"{Math.Round(landscapeText * 0.5, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}\"", StringComparison.Ordinal),
            "DOCX: the grid comes from the section's text width.");

        var read = new DocxDocumentReader().Read(Write(new DocxDocumentWriter(), Document(landscape))).Document.Sections[0].Blocks.OfType<TableBlock>().Single();
        Require(read.ColumnWidthPercents is [20, 30, 50], "DOCX: the column percents are read back.");

        Write(new OdtDocumentWriter(), Document(landscape));
        var rejected = false;
        try
        {
            _ = Table(60, 50);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Require(rejected, "Column percents past 100 are rejected.");
    }

    private static void HeaderDistanceAndBodyStart()
    {
        var margins = new PageMargins(Length.FromCentimeters(4), Length.FromPoints(72), Length.FromPoints(72), Length.FromPoints(72));
        List<(double Size, double X, double Y, string Text)> Page(int headerLines, Section? template = null)
        {
            var header = new HeaderFooter(Enumerable.Range(1, headerLines).Select(line => (Block)new Paragraph(new TextRun("H" + line))));
            var section = (template ?? new Section(new Paragraph(new TextRun("BODY")))) with { Margins = margins, Header = header, Blocks = [new Paragraph(new TextRun("BODY"))] };
            return Pages(Document(section))[0];
        }

        double Y(List<(double Size, double X, double Y, string Text)> page, string text) => page.Single(piece => piece.Text == text).Y;

        // N7: the header starts at its distance from the top edge.
        var defaultTop = Y(Page(1), "H1");
        var near = Y(Page(1, new Section([]) { HeaderDistance = Length.FromCentimeters(1.25) }), "H1");
        Require(Math.Abs(near - defaultTop - (36 - Length.FromCentimeters(1.25).Points)) < 0.01, $"A 1.25 cm header distance lifts the header by 0.57 pt ({near - defaultTop}).");

        // N5: a header taller than the top margin pushes the body, unless HeaderPushesBody is false.
        var bodyAtMargin = Y(Page(1), "BODY");
        Require(Y(Page(8), "BODY") < bodyAtMargin - 10, "By default a tall header pushes the body down.");
        Require(Math.Abs(Y(Page(8, new Section([]) { HeaderPushesBody = false }), "BODY") - bodyAtMargin) < 0.01, "Without pushing, the body stays at the top margin.");

        var document = Document(new Section(new Paragraph(new TextRun("BODY"))) { HeaderDistance = Length.FromCentimeters(1.25), FooterDistance = Length.FromPoints(20), HeaderPushesBody = false });
        using var buffer = new MemoryStream();
        var written = new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(written.Warnings.Any(warning => warning.Message.Contains("does not push the body", StringComparison.Ordinal)), "DOCX reports HeaderPushesBody = false.");
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document.Sections[0];
        Require(Math.Abs(read.HeaderDistance!.Value.Points - Length.FromCentimeters(1.25).Points) < 0.05 && read.FooterDistance == Length.FromPoints(20),
            "DOCX: the header and footer distance survive.");
        Require(new DocxDocumentReader().Read(Write(new DocxDocumentWriter(), Document(new Section(new Paragraph(new TextRun("x")))))).Document.Sections[0].HeaderDistance is null,
            "DOCX: Word's 0.5 in default reads as null.");
    }

    private static void InlineImageStandsOnTheBaseline()
    {
        var ten = new ParagraphStyle { FontSize = Length.FromPoints(10), SpaceAfter = Length.Zero };
        var picture = new InlineImage(TestImages.RgbPng(4, 4, 0, 128, 0), ImageMediaType.Png, Length.FromPoints(100), Length.FromPoints(227.2)) ;
        (double Gap, string Content) Sheet(Paragraph middle)
        {
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(Document(new Section([new Paragraph(new TextRun("TOP")) { Style = ten }, middle, new Paragraph(new TextRun("NEXT")) { Style = ten }])), buffer)
                .GetAwaiter().GetResult();
            var content = PdfStreams.DecompressPages(buffer.ToArray())[0];
            var pieces = Pages(Document(new Section([new Paragraph(new TextRun("TOP")) { Style = ten }, middle, new Paragraph(new TextRun("NEXT")) { Style = ten }])))[0];
            return (pieces.Single(piece => piece.Text == "TOP").Y - pieces.Single(piece => piece.Text == "NEXT").Y, content);
        }

        var text = Sheet(new Paragraph(new TextRun("MID")) { Style = ten });
        var (gap, content) = Sheet(new Paragraph([new TextRun("A "), picture, new TextRun(" B")]) { Style = ten with { Alignment = TextAlignment.Right } });
        // Line gap, image and descent: 227.2 pt more than a text line, less the 9.05 pt ascent of 10 pt Helvetica
        // (with Arial's 11.5 pt line, MigraDoc's 229.65 pt).
        Require(Math.Abs(gap - text.Gap - (227.2 - 9.05)) < 0.02, $"The image line is 218.15 pt higher than a text line ({gap - text.Gap}).");

        // The image's bottom stands on the baseline of its line, right-aligned with the text around it.
        var draw = Regex.Match(content, @"([\d.]+) 0 0 ([\d.]+) (-?[\d.]+) (-?[\d.]+) cm\s*/\w+ Do");
        Require(draw.Success && Number(draw.Groups[1].Value) == 100 && Math.Abs(Number(draw.Groups[2].Value) - 227.2) < 0.01, "The image is drawn at its size.");
        var pieces = Pages(Document(new Section([new Paragraph(new TextRun("TOP")) { Style = ten },
            new Paragraph([new TextRun("A "), picture, new TextRun(" B")]) { Style = ten with { Alignment = TextAlignment.Right } }])))[0];
        var b = pieces.Single(piece => piece.Text == "B");
        Require(Math.Abs(Number(draw.Groups[4].Value) - b.Y) < 0.01, "The image's bottom is on the text's baseline.");
        Require(b.X > Number(draw.Groups[3].Value) + 100, "Text, image and text follow each other in the line.");

        // In a table cell the image raises its own line only.
        var cell = Pages(Document(new Section(new TableBlock([new TableRow(
            new TableCell([new Paragraph(new TextRun("L1")) { Style = ten }, new Paragraph([picture]) { Style = ten }, new Paragraph(new TextRun("L3")) { Style = ten }]),
            new TableCell([new Paragraph(new TextRun("R1")) { Style = ten }, new Paragraph(new TextRun("R2")) { Style = ten }]))]))))[0];
        double Y(string name) => cell.Single(piece => piece.Text == name).Y;
        var pitch = Y("R1") - Y("R2");
        Require(Math.Abs(Y("L1") - Y("L3") - (2 * pitch) - (227.2 - 9.05)) < 0.02,
            $"Only the image line grows, as in a paragraph ({pitch}, {Y("L1") - Y("L3")}).");

        // DOCX: written as wp:inline in a run; read back in its paragraph. A paragraph of a picture alone stays an image block.
        var mixed = Document(new Section([
            new Paragraph([new TextRun("A "), picture, new TextRun(" B")]),
            new ImageBlock(TestImages.RgbPng(2, 2, 255, 0, 0), ImageMediaType.Png),
        ]));
        var blocks = new DocxDocumentReader().Read(Write(new DocxDocumentWriter(), mixed)).Document.Sections[0].Blocks;
        var read = blocks.OfType<Paragraph>().First().Inlines;
        Require(read.Count == 3 && read[1] is InlineImage { Image.Height: { } height } && Math.Abs(height.Points - 227.2) < 0.01,
            "DOCX: the inline image survives in its paragraph.");
        Require(blocks.OfType<ImageBlock>().Count() == 1, "DOCX: an image block stays an image block.");

        // ODT anchors it as a character in the paragraph; HTML writes an <img> in the line.
        using var odt = new System.IO.Compression.ZipArchive(Write(new OdtDocumentWriter(), mixed));
        using var contentReader = new StreamReader(odt.GetEntry("content.xml")!.Open());
        var contentXml = contentReader.ReadToEnd();
        Require(Regex.IsMatch(contentXml, @"<text:p[^>]*>(<text:span[^>]*>)?A( |<text:s ?/>)(</text:span>)?<draw:frame[^>]*text:anchor-type=""as-char"""), "ODT: the frame stands in the text, as a character.");
        var html = new StreamReader(Write(new HtmlDocumentWriter(), mixed)).ReadToEnd();
        Require(Regex.IsMatch(html, @"<p[^>]*>A <img style=""vertical-align:baseline"""), "HTML: the image stands in the paragraph.");
    }

    private static MemoryStream Write(IDocumentWriter writer, PortableDocument document)
    {
        var buffer = new MemoryStream();
        writer.WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        return buffer;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
