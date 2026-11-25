using System.Globalization;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// M1–M5: what a MigraDoc document rebuilt on the document model needs. Page fields take the paragraph's font, a line's
/// least height comes from its paragraph (not the document), a floating image can do without its anchor line, and cells
/// take horizontal and vertical padding apart, beside a table indent.
/// </summary>
internal static class MigraDocParityChecks
{
    internal static int Run()
    {
        try
        {
            PageFieldsTakeTheParagraphFont();
            CellLineHeightComesFromTheParagraph();
            EmptyParagraphTakesItsRunsSize();
            FloatingImageWithoutAnchorLine();
            CellPaddingPerAxisAndTableIndent();
            Console.WriteLine("MigraDoc parity: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("MigraDoc parity: " + exception);
            return 1;
        }
    }

    /// <summary>The text pieces of the first page: font size, x, baseline and text.</summary>
    private static List<(double Size, double X, double Y, string Text)> Pieces(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return Regex.Matches(PdfStreams.DecompressPages(buffer.ToArray())[0], @"/F\d+ ([\d.]+) Tf\s*(-?[\d.]+) (-?[\d.]+) Td\s*\(([^)]*)\) Tj")
            .Select(match => (Number(match.Groups[1].Value), Number(match.Groups[2].Value), Number(match.Groups[3].Value), match.Groups[4].Value))
            .ToList();
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static PortableDocument Document(DocumentDefaults defaults, Section section)
        => new(new DocumentMetadata(), section) { Defaults = defaults };

    private static readonly DocumentDefaults Tiny = DocumentDefaults.Default with { FontSize = Length.FromPoints(1) };

    private static void PageFieldsTakeTheParagraphFont()
    {
        var style = new ParagraphStyle { Alignment = TextAlignment.Right, FontSize = Length.FromPoints(10) };
        var table = new TableBlock([new TableRow([
            new TableCell([new Paragraph(new TextRun("Left"))]),
            new TableCell([new Paragraph([new PageNumberField(), new TextRun(" von "), new PageCountField()]) { Style = style }]),
        ])]);
        var pieces = Pieces(Document(Tiny, new Section(new Paragraph(new TextRun("Body"))) { PageSize = PageSize.A4, Header = new HeaderFooter([table]) }));
        var von = pieces.FindIndex(piece => piece.Text == "von");
        Require(von > 1, "The header line is drawn.");
        var number = pieces[von - 2];
        var count = pieces[von + 2];
        Require(number.Text == "1" && count.Text == "1" && number.Size == 10 && count.Size == 10, $"Both fields are 10 pt ({number.Size}, {count.Size}).");
        // Helvetica: "1" is 5.56 pt wide at 10 pt, the space 2.78 pt.
        Require(Math.Abs(pieces[von].X - number.X - 5.56 - 2.78) < 0.05, "' von ' follows the page number without overlap.");
        // A4 523.28 pt of content, the right cell padding 5.4 pt.
        Require(Math.Abs(count.X + 5.56 - (72 + 451.28 - 5.4)) < 0.05, "The count ends at the right edge of the cell: " + count.X);
    }

    private static void CellLineHeightComesFromTheParagraph()
    {
        static double Pitch(double documentSize)
        {
            var nine = new ParagraphStyle { FontSize = Length.FromPoints(9) };
            var table = new TableBlock(new[] { "A", "B" }.Select(text => new TableRow([new TableCell([new Paragraph(new TextRun(text)) { Style = nine }])])));
            var pieces = Pieces(Document(DocumentDefaults.Default with { FontSize = Length.FromPoints(documentSize) }, new Section(table)));
            return pieces.Single(piece => piece.Text == "A").Y - pieces.Single(piece => piece.Text == "B").Y;
        }

        var pitch = Pitch(9);
        Require(Math.Abs(Pitch(10) - pitch) < 0.01 && Math.Abs(Pitch(1) - pitch) < 0.01, $"A 9 pt row is as high under a 10 pt or 1 pt document ({Pitch(10)} / {Pitch(1)} / {pitch}).");
    }

    private static void EmptyParagraphTakesItsRunsSize()
    {
        static double Gap(Inline[] spacer, double? styleSize = null, double documentSize = 11)
        {
            var tight = new ParagraphStyle { SpaceAfter = Length.Zero };
            var pieces = Pieces(Document(DocumentDefaults.Default with { FontSize = Length.FromPoints(documentSize) },
                new Section([
                    new Paragraph(new TextRun("x")) { Style = tight },
                    new Paragraph(spacer) { Style = tight with { FontSize = styleSize is { } size ? Length.FromPoints(size) : null } },
                    new Paragraph(new TextRun("y"))])));
            return pieces.Single(piece => piece.Text == "x").Y - pieces.Single(piece => piece.Text == "y").Y;
        }

        var reference = Gap([new TextRun("\u00A0") { FontSize = Length.FromPoints(20) }]);
        // As in Word, an empty run has no height: the paragraph mark sizes the line (1.7.3).
        Require(Math.Abs(Gap([new TextRun("") { FontSize = Length.FromPoints(20) }]) - Gap([])) < 0.01,
            "An empty 20 pt run leaves the paragraph at its mark's size.");
        Require(Math.Abs(Gap([], styleSize: 20) - reference) < 0.01,
            "A 20 pt paragraph mark gives a 20 pt line, as a no-break space does.");
        Require(Math.Abs(Gap([], styleSize: 20, documentSize: 1) - Gap([new TextRun("\u00A0") { FontSize = Length.FromPoints(20) }], documentSize: 1)) < 0.01,
            "An empty paragraph takes its style's size, not the document's.");
    }

    private static void FloatingImageWithoutAnchorLine()
    {
        ImageBlock Logo(bool takesLine) => new(TestImages.RgbPng(4, 4, 0, 0, 255), ImageMediaType.Png)
        {
            Width = Length.FromPoints(60),
            Height = Length.FromPoints(30),
            Wrap = ImageWrap.InFront,
            HorizontalRelativeTo = HorizontalAnchor.Margin,
            HorizontalAlignment = HorizontalFloatAlignment.Right,
            TakesAnchorLine = takesLine,
        };

        double HeaderBaseline(params Block[] header)
            => Pieces(Document(DocumentDefaults.Default, new Section(new Paragraph(new TextRun("Body")))
            {
                Header = new HeaderFooter(header.Append(new Paragraph(new TextRun("HEADERTEXT")))),
            })).Single(piece => piece.Text == "HEADERTEXT").Y;

        var plain = HeaderBaseline();
        Require(Math.Abs(HeaderBaseline(Logo(takesLine: false)) - plain) < 0.01, "Without its anchor line the logo leaves the header text where it was.");
        Require(HeaderBaseline(Logo(takesLine: true)) < plain - 5, "With its anchor line (the default) the header text moves down a line.");

        var document = Document(DocumentDefaults.Default, new Section([Logo(takesLine: false), new Paragraph(new TextRun("Text"))]));
        var docx = new DocxDocumentWriter().WriteAsync(document, new MemoryStream()).GetAwaiter().GetResult();
        Require(docx.Warnings.Any(warning => warning.Message.Contains("without an anchor line", StringComparison.Ordinal)), "DOCX reports the anchor paragraph it keeps.");
        var odt = new OdtDocumentWriter().WriteAsync(document, new MemoryStream()).GetAwaiter().GetResult();
        Require(odt.Warnings.Any(warning => warning.Message.Contains("without an anchor line", StringComparison.Ordinal)), "ODT reports the anchor paragraph it keeps.");
    }

    private static void CellPaddingPerAxisAndTableIndent()
    {
        var padding = Length.FromMillimeters(1.2);
        TableBlock Table() => new([new TableRow([new TableCell([new Paragraph(new TextRun("FIRST"))]), new TableCell([new Paragraph(new TextRun("SECOND"))])])])
        {
            CellPaddingHorizontal = padding,
            CellPaddingVertical = Length.Zero,
            LeftIndent = Length.FromPoints(-padding.Points),
        };

        var pieces = Pieces(Document(DocumentDefaults.Default, new Section([new Paragraph(new TextRun("TOP")) { Style = new ParagraphStyle { SpaceAfter = Length.Zero } }, Table()])));
        Require(Math.Abs(pieces.Single(piece => piece.Text == "FIRST").X - 72) < 0.01, "The first column's text stands at the margin.");
        var plainPieces = Pieces(Document(DocumentDefaults.Default, new Section([new Paragraph(new TextRun("TOP")) { Style = new ParagraphStyle { SpaceAfter = Length.Zero } },
            new TableBlock([new TableRow([new TableCell([new Paragraph(new TextRun("FIRST"))])])]) { CellPadding = padding }])));
        var gap = pieces.Single(piece => piece.Text == "TOP").Y - pieces.Single(piece => piece.Text == "FIRST").Y;
        var paddedGap = plainPieces.Single(piece => piece.Text == "TOP").Y - plainPieces.Single(piece => piece.Text == "FIRST").Y;
        Require(Math.Abs(paddedGap - gap - padding.Points) < 0.01, "No vertical padding above the text, where a uniform padding adds it.");

        var cell = new TableCell([new Paragraph(new TextRun("C"))]) { PaddingHorizontal = Length.FromPoints(2), PaddingVertical = Length.FromPoints(1) };
        Require(TableLayout.CellPadding(Table(), cell) == (Length.FromPoints(2), Length.FromPoints(1)), "A cell's axis padding wins over the table's.");
        Require(TableLayout.CellPadding(Table(), new TableCell([]) { Padding = Length.FromPoints(3) }) == (Length.FromPoints(3), Length.FromPoints(3)), "A cell's uniform padding wins over the table's axes.");

        var document = Document(DocumentDefaults.Default, new Section(Table()));
        using (var buffer = new MemoryStream())
        {
            new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            buffer.Position = 0;
            CheckRead(new DocxDocumentReader().Read(buffer).Document, "DOCX");
        }

        using (var buffer = new MemoryStream())
        {
            new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            buffer.Position = 0;
            CheckRead(new OdtDocumentReader().Read(buffer).Document, "ODT");
        }

        void CheckRead(PortableDocument read, string format)
        {
            var table = read.Sections[0].Blocks.OfType<TableBlock>().Single();
            Require(table.CellPadding is null && Near(table.CellPaddingHorizontal, padding.Points) && Near(table.CellPaddingVertical, 0),
                $"{format}: the padding per axis survives ({table.CellPadding}, {table.CellPaddingHorizontal}, {table.CellPaddingVertical}).");
            Require(Near(table.LeftIndent, -padding.Points), $"{format}: the table indent survives ({table.LeftIndent}).");
        }
    }

    private static bool Near(Length? value, double points) => value is { } length && Math.Abs(length.Points - points) < 0.05;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
