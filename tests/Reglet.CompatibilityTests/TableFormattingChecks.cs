using System.IO.Compression;
using Reglet;

/// <summary>
/// tables (phase Y, Y01–Y04): column span, row height, vertical alignment,
/// cell background/padding/borders, table width and borders. DOCX, ODT and PDF implement
/// them; only PDF reports the row span (RowSpan) (no silent data loss).
/// </summary>
internal static class TableFormattingChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxTableFeatures();
            ReportsUnsTableFeatures();
            RendersPdfTableFeatures();
            AppliesParagraphIndentsInCells();
            HangsListMarkersInCells();
            RejectsInvalidValues();
            Console.WriteLine("Tables (Y): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Tables (Y): " + exception);
            return 1;
        }
    }

    private static PortableDocument DecoratedDocument()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[]
            {
                new TableCell("Header A") { ColumnSpan = 2 },
                new TableCell("Header B"),
            })
            {
                IsHeader = true,
            },
            new TableRow(new[]
            {
                new TableCell("Value")
                {
                    VerticalAlignment = TableCellVerticalAlignment.Center,
                    Background = Color.FromHex("EEEEEE"),
                    Padding = Length.FromPoints(4),
                    Borders = ParagraphBorders.All(new ParagraphBorder(BorderStyle.Single, Length.FromPoints(0.5), Color.FromHex("FF0000"))),
                },
                new TableCell("x") { RowSpan = 2 },
                new TableCell("y"),
            })
            {
                Height = Length.FromPoints(30),
                HeightRule = TableRowHeightRule.AtLeast,
            },
            new TableRow(new[] { new TableCell("a"), new TableCell("b") }),
        })
        {
            Border = new ParagraphBorder(BorderStyle.Double, Length.FromPoints(1), Color.FromHex("0000FF")),
            CellPadding = Length.FromPoints(4),
            WidthPercent = 80,
        };

        return new PortableDocument(new DocumentMetadata(), new Section(table));
    }

    private static void WritesDocxTableFeatures()
    {
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();
        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");

        Require(body.Contains("w:tblW w:w=\"4000\" w:type=\"pct\"", StringComparison.Ordinal), "DOCX: table width in percent (80 % = 4000).");
        Require(body.Contains("<w:tblBorders>", StringComparison.Ordinal) && body.Contains("w:top w:val=\"double\"", StringComparison.Ordinal), "DOCX: table borders.");
        Require(body.Contains("<w:tblCellMar>", StringComparison.Ordinal), "DOCX: cell padding.");
        Require(body.Contains("w:trHeight w:val=\"600\" w:hRule=\"atLeast\"", StringComparison.Ordinal), "DOCX: row height (30 pt = 600).");
        Require(body.Contains("w:gridSpan w:val=\"2\"", StringComparison.Ordinal), "DOCX: column span.");
        Require(body.Contains("w:vAlign w:val=\"center\"", StringComparison.Ordinal), "DOCX: vertical alignment.");
        Require(body.Contains("w:fill=\"EEEEEE\"", StringComparison.Ordinal), "DOCX: cell background.");
        Require(body.Contains("<w:tcBorders>", StringComparison.Ordinal) && body.Contains("w:color=\"FF0000\"", StringComparison.Ordinal), "DOCX: cell borders.");
        Require(body.Contains("<w:tcMar>", StringComparison.Ordinal), "DOCX: custom cell padding.");
        Require(body.Contains("<w:vMerge w:val=\"restart\"", StringComparison.Ordinal), "DOCX: row span restart.");
        Require(body.Contains("<w:vMerge", StringComparison.Ordinal), "DOCX: row span continuation.");
    }

    private static void ReportsUnsTableFeatures()
    {
        using var odtBuffer = new MemoryStream();
        var odt = new OdtDocumentWriter().WriteAsync(DecoratedDocument(), odtBuffer).GetAwaiter().GetResult();
        Require(!odt.Warnings.Any(warning => warning.Message.Contains("row spanning", StringComparison.Ordinal)), "ODT: row span is rendered.");
        Require(!odt.Warnings.Any(warning => warning.Message.Contains("column spanning", StringComparison.Ordinal)), "ODT: cell span is rendered.");
        Require(!odt.Warnings.Any(warning => warning.Message.Contains("table width", StringComparison.Ordinal)), "ODT: table width is rendered.");

        var content = ReadZipEntry(odtBuffer.ToArray(), "content.xml");
        Require(content.Contains("table:number-columns-spanned=\"2\"", StringComparison.Ordinal), "ODT: column span.");
        Require(content.Contains("<table:covered-table-cell", StringComparison.Ordinal), "ODT: covered cell.");
        Require(content.Contains("table:number-rows-spanned=\"2\"", StringComparison.Ordinal), "ODT: number-rows-spanned.");
        Require(content.Contains("fo:background-color=\"#EEEEEE\"", StringComparison.Ordinal), "ODT: cell background.");
        Require(content.Contains("fo:border-top=\"", StringComparison.Ordinal), "ODT: cell borders.");
        Require(content.Contains("style:rel-width=\"80%\"", StringComparison.Ordinal), "ODT: table width in percent.");
        // 30 pt for the content plus 4 pt of cell padding above and below and the table's 1 pt border (ODF counts the whole row).
        Require(content.Contains("style:min-row-height=\"39pt\"", StringComparison.Ordinal), "ODT: minimum row height.");
        Require(content.Contains("style:vertical-align=\"middle\"", StringComparison.Ordinal), "ODT: vertical alignment.");

        using var pdfBuffer = new MemoryStream();
        var pdf = new PdfDocumentWriter().WriteAsync(DecoratedDocument(), pdfBuffer).GetAwaiter().GetResult();
        Require(!pdf.Warnings.Any(warning => warning.Message.Contains("row spanning", StringComparison.Ordinal)), "PDF: row span is rendered.");
        Require(!pdf.Warnings.Any(warning => warning.Message.Contains("column spanning", StringComparison.Ordinal)), "PDF: cell span is rendered.");
        Require(!pdf.Warnings.Any(warning => warning.Message.Contains("cell background", StringComparison.Ordinal)), "PDF: cell background is rendered.");
    }

    private static void RendersPdfTableFeatures()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("0.933 0.933 0.933 rg", StringComparison.Ordinal), "PDF: cell background is filled.");
        Require(content.Contains("(Header", StringComparison.Ordinal), "PDF: spanned cell text is rendered.");
        Require(content.Contains("(Value)", StringComparison.Ordinal), "PDF: cell text is rendered.");
    }

    /// <summary>Paragraph indents apply inside a table cell (left indent, first line, and the first line's wrap width).</summary>
    private static void AppliesParagraphIndentsInCells()
    {
        // Every word is its own text piece: group them by baseline into lines.
        static List<(double Start, int Words)> Lines(Paragraph paragraph)
        {
            var table = new TableBlock([new TableRow(new TableCell(paragraph))]);
            var document = new PortableDocument(new DocumentMetadata(), new Section(table));
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            return System.Text.RegularExpressions.Regex.Matches(PdfStreams.Decompress(buffer.ToArray()), @"([0-9.]+) ([0-9.]+) Td\n\(([^)]*)\) Tj")
                .Where(match => match.Groups[3].Value.Trim().Length > 0)
                .GroupBy(match => match.Groups[2].Value)
                .Select(line => (line.Min(match => double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)), line.Count()))
                .ToList();
        }

        var plain = Lines(new Paragraph(new TextRun("Plain")))[0].Start;
        var indented = Lines(new Paragraph(new TextRun("Left")) { Style = new ParagraphStyle { LeftIndent = Length.FromPoints(36) } })[0].Start;
        Require(Math.Abs(indented - plain - 36) < 0.01, $"PDF: a left indent moves cell text by 36 pt ({plain} -> {indented}).");

        var words = string.Join(" ", Enumerable.Repeat("Wort", 60));
        var lines = Lines(new Paragraph(new TextRun(words)) { Style = new ParagraphStyle { FirstLineIndent = Length.FromPoints(72) } });
        Require(lines.Count > 2 && Math.Abs(lines[0].Start - plain - 72) < 0.01 && Math.Abs(lines[1].Start - plain) < 0.01,
            $"PDF: only the first cell line is indented 72 pt ({string.Join(", ", lines.Select(line => line.Start))}).");
        Require(lines[0].Words < lines[1].Words, $"PDF: the indented first cell line wraps earlier ({lines[0].Words} vs {lines[1].Words} words).");
    }

    /// <summary>
    /// A list in a cell hangs its marker 18 pt before the text, which is indented 36 pt per level;
    /// wrapped lines align with the text, not with the marker.
    /// </summary>
    private static void HangsListMarkersInCells()
    {
        static List<(double X, double Y, string Text)> Pieces(Block content)
        {
            var table = new TableBlock([new TableRow(new TableCell(content))]);
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(TestLists.Document(table), buffer).GetAwaiter().GetResult();
            return System.Text.RegularExpressions.Regex.Matches(PdfStreams.Decompress(buffer.ToArray()), @"([0-9.]+) ([0-9.]+) Td\n\(([^)]*)\) Tj")
                .Where(match => match.Groups[3].Value.Trim().Length > 0)
                .Select(match => (
                    double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    match.Groups[3].Value))
                .ToList();
        }

        var left = Pieces(new Paragraph(new TextRun("Plain")))[0].X;
        var nested = TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("Inner")))], nestingLevel: 2);
        var words = string.Join(" ", Enumerable.Repeat("Wort", 40));
        var list = TestLists.List(ListKind.Number, [new ListItem([new Paragraph(new TextRun(words)), nested])]);
        var pieces = Pieces(list);

        var marker = pieces.First(piece => piece.Text == "1.");
        var firstWord = pieces.First(piece => piece.Text.StartsWith("Wort", StringComparison.Ordinal));
        var secondLine = pieces.Where(piece => piece.Y < firstWord.Y - 1 && piece.Text.StartsWith("Wort", StringComparison.Ordinal)).Min(piece => piece.X);
        var inner = pieces.First(piece => piece.Text == "Inner");
        var innerMarker = pieces.Last(piece => piece.Text == "1.");
        Require(Math.Abs(marker.X - left - 18) < 0.01, $"PDF cell list: the marker hangs at 18 pt ({marker.X - left}).");
        Require(Math.Abs(firstWord.X - left - 36) < 0.01, $"PDF cell list: the text starts at 36 pt ({firstWord.X - left}).");
        Require(Math.Abs(secondLine - left - 36) < 0.01, $"PDF cell list: a wrapped line aligns with the text ({secondLine - left}).");
        Require(Math.Abs(innerMarker.X - left - 54) < 0.01 && Math.Abs(inner.X - left - 72) < 0.01,
            $"PDF cell list: the nested level is indented 36 pt further ({innerMarker.X - left}, {inner.X - left}).");

        // NestingLevel raises the level as in the page body: a top-level list with level 3 starts at 3 * 36 pt.
        var deep = Pieces(TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("Deep")))], nestingLevel: 3));
        Require(Math.Abs(deep.First(piece => piece.Text == "1.").X - left - 90) < 0.01 && Math.Abs(deep.First(piece => piece.Text == "Deep").X - left - 108) < 0.01,
            "PDF cell list: NestingLevel 3 indents the text 108 pt with the marker at 90 pt.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => new TableCell("x") { ColumnSpan = 0 });
        Throws<ArgumentOutOfRangeException>(() => new TableCell("x") { RowSpan = 0 });
        Throws<ArgumentOutOfRangeException>(() => new TableCell("x") { Padding = Length.FromPoints(-1) });
        Throws<ArgumentOutOfRangeException>(() => new TableRow(new[] { new TableCell("x") }) { Height = Length.Zero });
        Throws<ArgumentOutOfRangeException>(() => new TableBlock([]) { WidthPercent = 0 });
        Throws<ArgumentOutOfRangeException>(() => new TableBlock([]) { WidthPercent = 601 });
        Throws<ArgumentOutOfRangeException>(() => new TableBlock([]) { CellPadding = Length.FromPoints(-1) });
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
