using Reglet;

/// <summary>
/// Model → DOCX → model for what the reader used to lose: the padding of a single cell, the alignment of a paragraph that
/// holds only a picture, empty paragraphs (in the text and in cells), an empty first-page footer, and a paragraph font
/// that Word stores on the mark and on every run.
/// </summary>
internal static class DocxRoundTripChecks
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    internal static int Run()
    {
        try
        {
            CellPaddingSurvives();
            AlignedPictureStaysInItsParagraph();
            EmptyParagraphsSurvive();
            EmptyFirstFooterSurvives();
            ParagraphFontComesBack();
            TableWithoutBorderSurvives();
            Console.WriteLine("DOCX round trip: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DOCX round trip: " + exception);
            return 1;
        }
    }

    private static PortableDocument RoundTrip(params Section[] sections)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(new PortableDocument(new DocumentMetadata(), sections), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        return new DocxDocumentReader().Read(buffer).Document;
    }

    private static TableCell Cell(string text) => new([new Paragraph(new TextRun(text))]);

    private static void CellPaddingSurvives()
    {
        var padded = Cell("own") with { Padding = Length.FromPoints(1.2) };
        var axes = Cell("axes") with { PaddingHorizontal = Length.FromPoints(2), PaddingVertical = Length.FromPoints(1) };
        var table = new TableBlock([new TableRow([padded, axes, Cell("plain")])]);
        var tablePadded = new TableBlock([new TableRow([Cell("a"), Cell("b")])]) { CellPadding = Length.FromPoints(4) };
        var read = RoundTrip(new Section([table, tablePadded])).Sections[0].Blocks.OfType<TableBlock>().ToList();
        var cells = read[0].Rows[0].Cells;
        Require(cells[0].Padding == Length.FromPoints(1.2), $"A cell's own padding is read back ({cells[0].Padding}).");
        Require(cells[1].PaddingHorizontal == Length.FromPoints(2) && cells[1].PaddingVertical == Length.FromPoints(1), "Padding per axis is read back.");
        Require(cells[2].Padding is null && cells[2].PaddingHorizontal is null, "A cell without its own padding gets none.");
        Require(read[1].CellPadding == Length.FromPoints(4) && read[1].Rows[0].Cells.All(cell => cell.Padding is null && cell.PaddingHorizontal is null),
            "The table's padding, repeated on each cell by the writer, stays on the table.");
    }

    private static void AlignedPictureStaysInItsParagraph()
    {
        var logo = new Paragraph(new InlineImage(Png, ImageMediaType.Png, Length.FromPoints(40), Length.FromPoints(20)))
        {
            Style = ParagraphStyle.Default with { Alignment = TextAlignment.Right },
        };
        var section = new Section(new Paragraph(new TextRun("Body"))) { Header = new HeaderFooter(logo) };
        var header = RoundTrip(section).Sections[0].Header!.Blocks.Single();
        Require(header is Paragraph { Style.Alignment: TextAlignment.Right } paragraph && paragraph.Inlines.Single() is InlineImage,
            $"A right-aligned picture stays in its paragraph ({header.GetType().Name}).");

        var block = RoundTrip(new Section(new ImageBlock(Png, ImageMediaType.Png))).Sections[0].Blocks.Single();
        Require(block is ImageBlock, $"A picture without alignment is still an image block ({block.GetType().Name}).");
    }

    private static void EmptyParagraphsSurvive()
    {
        var small = ParagraphStyle.Default with { FontSize = Length.FromPoints(8) };
        var nested = new TableBlock([new TableRow([Cell("inner")])]);
        var table = new TableBlock([new TableRow([
            new TableCell([new Paragraph([]) { Style = small }]),
            new TableCell([]),
            new TableCell([nested]),
        ])]);
        var first = new Section([new Paragraph(new TextRun("Before")), new Paragraph([]) { Style = small }, new Paragraph(new TextRun("After")), table])
        {
            Footer = new HeaderFooter(new TableBlock([new TableRow([Cell("F")])])),
        };
        var second = new Section(new Paragraph(new TextRun("Next section")));
        var read = RoundTrip(first, second);

        var blocks = read.Sections[0].Blocks;
        Require(blocks.Count == 4 && blocks[1] is Paragraph { Inlines.Count: 0 } empty && empty.Style.FontSize == Length.FromPoints(8),
            $"An empty paragraph in the text keeps its place and size ({string.Join(", ", blocks.Select(block => block.GetType().Name))}).");
        var cells = ((TableBlock)blocks[3]).Rows[0].Cells;
        Require(cells[0].Blocks is [Paragraph { Inlines.Count: 0 } inCell] && inCell.Style.FontSize == Length.FromPoints(8),
            "An empty paragraph in a cell is read back.");
        Require(cells[1].Blocks.Count == 0, "A cell without blocks stays empty (the bare closing paragraph is not a block).");
        Require(cells[2].Blocks is [TableBlock], "The paragraph that closes a cell after a table is not a block.");
        Require(read.Sections[0].Footer!.Blocks is [TableBlock], "The paragraph without height after a footer table is not a block.");
        Require(read.Sections.Count == 2 && read.Sections[1].Blocks.Count == 1, "A section boundary adds no paragraph.");
    }

    private static void EmptyFirstFooterSurvives()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Footer = new HeaderFooter(new Paragraph(new TextRun("Page footer"))),
            FirstFooter = new HeaderFooter([]),
        };
        var read = RoundTrip(section).Sections[0];
        Require(read.FirstFooter is { Blocks.Count: 0 } && read.Footer is { Blocks.Count: 1 }, "An empty first-page footer stays (no footer on page 1).");
        Require(RoundTrip(new Section(new Paragraph(new TextRun("Body")))).Sections[0].FirstFooter is null, "Without a first footer there is none.");
    }

    private static void ParagraphFontComesBack()
    {
        var style = ParagraphStyle.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(8), Alignment = TextAlignment.Center };
        var footer = new Paragraph([new TextRun("Page "), new PageNumberField(), new TextRun(" of "), new PageCountField()]) { Style = style };
        var read = (Paragraph)RoundTrip(new Section(new Paragraph(new TextRun("Body"))) { Footer = new HeaderFooter(footer) })
            .Sections[0].Footer!.Blocks.Single();
        Require(read.Style.FontFamily == "Arial" && read.Style.FontSize == Length.FromPoints(8)
                && read.Style.MarkFontFamily is null && read.Style.MarkFontSize is null,
            $"The paragraph font comes back as the paragraph's ({read.Style.FontFamily}/{read.Style.FontSize}, mark {read.Style.MarkFontFamily}/{read.Style.MarkFontSize}).");
        Require(read.Inlines.OfType<TextRun>().All(run => run.FontFamily is null && run.FontSize is null), "The runs no longer carry it one by one.");

        // Runs with their own size beside a field: the field runs carry the paragraph's size, so the paragraph gets it back.
        var nine = Length.FromPoints(9);
        var calculation = new Paragraph([new TextRun("Seite ") { FontSize = nine }, new PageNumberField(), new TextRun(" / ") { FontSize = nine },
            new PageCountField(), new TextRun(" ")])
        {
            Style = ParagraphStyle.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(8) },
        };
        var readCalculation = (Paragraph)RoundTrip(new Section(new Paragraph(new TextRun("Body"))) { Footer = new HeaderFooter(calculation) })
            .Sections[0].Footer!.Blocks.Single();
        var calculationRuns = readCalculation.Inlines.OfType<TextRun>().ToList();
        Require(readCalculation.Style.FontSize == Length.FromPoints(8) && readCalculation.Style.FontFamily == "Arial"
                && readCalculation.Style.MarkFontSize is null && readCalculation.Style.MarkFontFamily is null,
            $"Mixed run sizes beside fields: the paragraph keeps 8 pt ({readCalculation.Style.FontSize}, mark {readCalculation.Style.MarkFontSize}).");
        Require(calculationRuns[0].FontSize == nine && calculationRuns[1].FontSize == nine && calculationRuns[2].FontSize is null,
            "The 9 pt runs keep their size, the plain run leaves 8 pt to the paragraph.");

        var mixed = new Paragraph([new TextRun("big") { FontSize = Length.FromPoints(20) }]) { Style = ParagraphStyle.Default with { MarkFontSize = Length.FromPoints(30) } };
        var readMixed = (Paragraph)RoundTrip(new Section(mixed)).Sections[0].Blocks.Single();
        Require(readMixed.Style.MarkFontSize == Length.FromPoints(30) && readMixed.Style.FontSize is null
                && readMixed.Inlines.OfType<TextRun>().Single().FontSize == Length.FromPoints(20),
            "A mark that differs from the runs stays the mark.");
    }

    private static void TableWithoutBorderSurvives()
    {
        var plain = new TableBlock([new TableRow([Cell("none")])]) { Border = ParagraphBorder.None };
        var thick = new TableBlock([new TableRow([Cell("thick")])]) { Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(2)) };
        var read = RoundTrip(new Section([plain, thick, new TableBlock([new TableRow([Cell("default")])])])).Sections[0].Blocks.OfType<TableBlock>().ToList();
        Require(read[0].Border == ParagraphBorder.None, $"A table without a border is read back without one ({read[0].Border}).");
        Require(read[1].Border is { Style: BorderStyle.Single } thickRead && thickRead.Width == Length.FromPoints(2), "A table's own border is read back.");
        Require(read[2].Border != ParagraphBorder.None, "A table with the default grid keeps a visible border.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
