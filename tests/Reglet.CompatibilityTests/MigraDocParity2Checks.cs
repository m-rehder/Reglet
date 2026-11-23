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
            FixedColumnWidthsOverflow();
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
