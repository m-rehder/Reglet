using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// Fields take the run formatting of their paragraph in DOCX; a table without borders, a full set of widths that reaches
/// into the left margin, a width above 100 percent, the empty paragraph after a table at the end of a footer, a header
/// distance larger than the top margin in the PDF, the formatting of the paragraph mark, and columns sized by content.
/// </summary>
internal static class FieldAndTableFixChecks
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    internal static int Run()
    {
        try
        {
            FieldRunsTakeTheParagraphFormat();
            TableWithoutBorders();
            WidthsReachIntoTheLeftMargin();
            WidthPercentAboveHundred();
            FooterEndingWithTableGetsAParagraphWithoutHeight();
            HeaderStartsAtItsDistance();
            ParagraphMarkSetsTheHeight();
            AutoFitDistribution();
            AutoFitTable();
            LastLineBelowAPictureStartsAtTheMargin();
            EmptyTableIsReported();
            MinimumRowHeightLeavesThePaddingOnTop();
            Console.WriteLine("Field and table fixes: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Field and table fixes: " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(Section section) => new(new DocumentMetadata(), section);

    private static TableBlock Table(params string[] texts)
        => new([new TableRow(texts.Select(text => new TableCell([new Paragraph(new TextRun(text))])))]);

    private static XDocument Part(PortableDocument document, Func<string, bool> name)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer);
        using var stream = zip.Entries.Single(entry => name(entry.FullName)).Open();
        return XDocument.Load(stream);
    }

    private static XDocument Body(PortableDocument document) => Part(document, name => name == "word/document.xml");

    private static void FieldRunsTakeTheParagraphFormat()
    {
        var footer = new Paragraph([new TextRun("Page "), new PageNumberField(), new TextRun(" of "), new PageCountField()])
        {
            Style = ParagraphStyle.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(8), Bold = true },
        };
        var xml = Part(Document(new Section(new Paragraph(new TextRun("Body"))) { Footer = new HeaderFooter(footer) }),
            name => name.StartsWith("word/footer", StringComparison.Ordinal));
        var fieldRuns = xml.Descendants(W + "r")
            .Where(run => run.Element(W + "fldChar") is not null || run.Element(W + "instrText") is not null)
            .ToList();
        Require(fieldRuns.Count == 6, $"Two fields of three runs each ({fieldRuns.Count}).");
        Require(fieldRuns.All(run => run.Element(W + "rPr") is { } rPr
                && (string?)rPr.Element(W + "rFonts")?.Attribute(W + "ascii") == "Arial"
                && (string?)rPr.Element(W + "sz")?.Attribute(W + "val") == "16"
                && rPr.Element(W + "b") is not null),
            "Every run of a field carries the paragraph's font, size and bold.");

        var plain = Part(Document(new Section(new Paragraph(new PageNumberField()))), name => name == "word/document.xml");
        Require(!plain.Descendants(W + "rPr").Any(), "A field in an unformatted paragraph writes no run properties.");
    }

    private static void TableWithoutBorders()
    {
        var grid = Body(Document(new Section(Table("A", "B"))));
        Require(grid.Descendants(W + "tblBorders").Single().Elements().All(side => (string?)side.Attribute(W + "val") == "single"),
            "Null keeps the default grid.");

        var table = new TableBlock([new TableRow(new TableCell([new Paragraph(new TextRun("A"))]))]) { Border = ParagraphBorder.None };
        var sides = Body(Document(new Section(table))).Descendants(W + "tblBorders").Single().Elements().ToList();
        Require(sides.Count == 6 && sides.All(side => (string?)side.Attribute(W + "val") == "nil"), "DOCX: ParagraphBorder.None clears all six sides.");

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(new Section(table)), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer);
        var content = XDocument.Load(zip.GetEntry("content.xml")!.Open());
        XNamespace odtTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XNamespace fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
        var cellStyle = (string)content.Descendants(odtTable + "table-cell").Single().Attribute(odtTable + "style-name")!;
        var properties = content.Descendants(style + "style").Single(element => (string?)element.Attribute(style + "name") == cellStyle)
            .Element(style + "table-cell-properties")!;
        Require(new[] { "top", "left", "bottom", "right" }.All(side => (string?)properties.Attribute(fo + ("border-" + side)) == "none"),
            "ODT: the cells of a table without borders have none.");
    }

    private static void WidthsReachIntoTheLeftMargin()
    {
        // A4 with 72 pt margins: 451.28 pt of text; the table starts 20 pt into the left margin and is 20 pt wider.
        var table = new TableBlock(Table("A", "B").Rows)
        {
            ColumnWidths = [Length.FromPoints(200), Length.FromPoints(271.28)],
            LeftIndent = Length.FromPoints(-20),
        };
        var grid = Body(Document(new Section(table))).Descendants(W + "gridCol").Select(column => (long)column.Attribute(W + "w")!).ToList();
        Require(grid.SequenceEqual([4000L, 5426L]), $"The widths are kept, not shrunk ({string.Join(", ", grid)}).");

        var wider = new TableBlock(table.Rows) { ColumnWidths = table.ColumnWidths, LeftIndent = Length.FromPoints(-10) };
        var shrunk = Body(Document(new Section(wider))).Descendants(W + "gridCol").Sum(column => (long)column.Attribute(W + "w")!);
        Require(Math.Abs(shrunk - 9226) <= 1, $"Past the margin room the table shrinks to text plus indent ({shrunk}).");
    }

    private static void WidthPercentAboveHundred()
    {
        Throws<ArgumentOutOfRangeException>(() => new TableBlock([]) { WidthPercent = 600.5 });
        var table = new TableBlock(Table("A", "B").Rows) { WidthPercent = 120 };
        var width = Body(Document(new Section(table))).Descendants(W + "tblW").Single();
        Require((string?)width.Attribute(W + "w") == "6000" && (string?)width.Attribute(W + "type") == "pct", "DOCX: 120 % as 6000 fiftieths.");

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(new Section(table)), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document.Sections[0].Blocks.OfType<TableBlock>().Single();
        Require(read.WidthPercent == 120, $"DOCX: 120 % is read back ({read.WidthPercent}).");
    }

    private static void FooterEndingWithTableGetsAParagraphWithoutHeight()
    {
        var section = new Section(new Paragraph(new TextRun("Body"))) { Footer = new HeaderFooter(Table("A", "B")) };
        var footer = Part(Document(section), name => name.StartsWith("word/footer", StringComparison.Ordinal));
        var last = footer.Root!.Elements().Last();
        var spacing = last.Element(W + "pPr")?.Element(W + "spacing");
        Require(last.Name == W + "p"
                && (string?)spacing?.Attribute(W + "before") == "0" && (string?)spacing?.Attribute(W + "after") == "0"
                && (string?)spacing?.Attribute(W + "line") == "1" && (string?)spacing?.Attribute(W + "lineRule") == "exact",
            "The closing paragraph after the table has no height.");
    }

    /// <summary>The baseline of every text piece on the first page.</summary>
    private static Dictionary<string, double> Baselines(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return Regex.Matches(PdfStreams.DecompressPages(buffer.ToArray())[0], @"Tf\s*(-?[\d.]+) (-?[\d.]+) Td\s*\(([^)]*)\) Tj")
            .GroupBy(match => match.Groups[3].Value)
            .ToDictionary(group => group.Key, group => double.Parse(group.First().Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static void MinimumRowHeightLeavesThePaddingOnTop()
    {
        double Pitch(TableRowHeightRule rule)
        {
            var bold = ParagraphStyle.Default with { FontFamily = "Arial", FontSize = Length.FromPoints(10) };
            var rows = new[] { "ROWA", "ROWB" }.Select(text => new TableRow([
                new TableCell([new Paragraph(new TextRun(text) { Bold = true }) { Style = bold }]) { PaddingHorizontal = Length.FromPoints(5.4), PaddingVertical = Length.FromPoints(1.2) },
            ]) { Height = Length.FromPoints(15), HeightRule = rule });
            var lines = Baselines(Document(new Section(new TableBlock(rows) { Border = ParagraphBorder.None })));
            return lines["ROWA"] - lines["ROWB"];
        }

        // Word: an at-least height of 15 pt for a 10 pt line, plus 1.2 pt of cell padding above and below.
        Require(Math.Abs(Pitch(TableRowHeightRule.AtLeast) - 17.4) < 0.01, $"A minimum row height is for the content ({Pitch(TableRowHeightRule.AtLeast)}).");
        Require(Math.Abs(Pitch(TableRowHeightRule.Exactly) - 15) < 0.01, $"An exact row height includes the padding ({Pitch(TableRowHeightRule.Exactly)}).");
    }

    private static void HeaderStartsAtItsDistance()
    {
        Dictionary<string, double> Page(double distance) => Baselines(Document(new Section(new Paragraph(new TextRun("BODY")))
        {
            Margins = new PageMargins(Length.FromPoints(72)),
            HeaderDistance = Length.FromPoints(distance),
            Header = new HeaderFooter(new Paragraph(new TextRun("HDR"))),
        }));

        var near = Page(36);
        var far = Page(100);
        Require(Math.Abs(near["HDR"] - far["HDR"] - 64) < 0.01, $"The header moves down by the extra distance ({near["HDR"]} / {far["HDR"]}).");
        Require(far["BODY"] < far["HDR"] - 10 && far["BODY"] < 841.89 - 100, $"The body starts below the header ({far["BODY"]}).");
        Require(near["BODY"] > far["BODY"], "A header within the margin leaves the body at the margin.");
    }

    private static void ParagraphMarkSetsTheHeight()
    {
        double Gap(Paragraph middle)
        {
            var lines = Baselines(Document(new Section([new Paragraph(new TextRun("TOP")), middle, new Paragraph(new TextRun("END"))])));
            return lines["TOP"] - lines["END"];
        }

        var plain = Gap(new Paragraph([]));
        Require(Math.Abs(Gap(new Paragraph(new TextRun(string.Empty) { FontSize = Length.FromPoints(40) })) - plain) < 0.01,
            "An empty run does not size an empty paragraph; its mark does.");
        var bigMark = Gap(new Paragraph([]) { Style = ParagraphStyle.Default with { MarkFontSize = Length.FromPoints(30) } });
        Require(bigMark > plain + 15, $"A 30 pt mark makes the empty paragraph taller ({bigMark} / {plain}).");

        var smallText = new TextRun("small") { FontSize = Length.FromPoints(6) };
        var lastLine = Gap(new Paragraph(smallText) { Style = ParagraphStyle.Default with { MarkFontSize = Length.FromPoints(30) } });
        Require(lastLine > Gap(new Paragraph(smallText)) + 15, "The mark raises the last line of a paragraph with text.");

        var marked = new Paragraph([]) { Style = ParagraphStyle.Default with { MarkFontSize = Length.FromPoints(30), MarkFontFamily = "Arial" } };
        var body = Body(Document(new Section(marked)));
        var markProperties = body.Descendants(W + "pPr").Single().Element(W + "rPr");
        Require((string?)markProperties?.Element(W + "sz")?.Attribute(W + "val") == "60"
                && (string?)markProperties?.Element(W + "rFonts")?.Attribute(W + "ascii") == "Arial",
            "DOCX: the mark is written as w:pPr/w:rPr.");
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(new Section(new Paragraph(new TextRun("M")) { Style = marked.Style })), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = (Paragraph)new DocxDocumentReader().Read(buffer).Document.Sections[0].Blocks[0];
        Require(read.Style.MarkFontSize == Length.FromPoints(30) && read.Style.MarkFontFamily == "Arial", "DOCX: the mark is read back.");

        var sized = Body(Document(new Section(new Paragraph([]) { Style = ParagraphStyle.Default with { FontSize = Length.FromPoints(8) } })));
        Require((string?)sized.Descendants(W + "pPr").Single().Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val") == "16",
            "DOCX: without its own font the mark takes the paragraph's, as the PDF does.");
    }

    private static void AutoFitDistribution()
    {
        static bool Same(double[] actual, params double[] expected) => actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);
        Require(Same(TableLayout.AutoFitWidths([10, 20], [30, 40], 100, fill: false), 30, 40), "Room for all: each column takes its maximum.");
        Require(Same(TableLayout.AutoFitWidths([10, 20], [30, 40], 140, fill: true), 60, 80), "A filled table stretches the maximums.");
        Require(Same(TableLayout.AutoFitWidths([10, 20], [30, 120], 70, fill: false), 10 + (20 / 3.0), 20 + (100 / 3.0)), "Between: the rest goes by what each still wants.");
        Require(Same(TableLayout.AutoFitWidths([60, 40], [80, 90], 50, fill: false), 30, 20), "Too narrow: the minimums shrink in proportion.");
        Require(Same(TableLayout.AutoFitWidths([0, 0], [0, 0], 50, fill: false), 25, 25), "Without content: equal columns.");
    }

    private static void AutoFitTable()
    {
        TableBlock Sheet(bool autoFit, double? percent = null) => new(
            [new TableRow([new TableCell([new Paragraph(new TextRun("ID"))]), new TableCell([new Paragraph(new TextRun("A long description of the article"))]), new TableCell([new Paragraph(new TextRun("END"))])])])
        {
            AutoFit = autoFit,
            WidthPercent = percent,
        };

        double X(TableBlock table, string text) => Baselines2(Document(new Section(table)))[text];
        var equal = X(Sheet(false), "END");
        var fitted = X(Sheet(true), "END");
        Require(X(Sheet(true), "A") < X(Sheet(false), "A") - 50, "The short first column gets narrow.");
        Require(fitted < equal, $"The table is only as wide as its content ({fitted} / {equal}).");
        Require(X(Sheet(true, percent: 100), "END") > fitted, "With a width percent the table fills it.");

        var body = Body(Document(new Section(Sheet(true))));
        Require((string?)body.Descendants(W + "tblLayout").Single().Attribute(W + "type") == "autofit"
                && body.Descendants(W + "tcW").All(width => (string?)width.Attribute(W + "type") == "auto"),
            "DOCX: automatic layout and cell widths.");

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(new Section(Sheet(true))), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document.Sections[0].Blocks.OfType<TableBlock>().Single();
        Require(read.AutoFit && read.ColumnWidths is null, "DOCX: AutoFit is read back.");

        using var odt = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(Document(new Section(Sheet(true))), odt).GetAwaiter().GetResult();
        Require(result.Warnings.Any(warning => warning.Message.Contains("AutoFit", StringComparison.Ordinal)), "ODT reports AutoFit.");
    }

    /// <summary>The x position of every text piece on the first page.</summary>
    private static Dictionary<string, double> Baselines2(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return Regex.Matches(PdfStreams.DecompressPages(buffer.ToArray())[0], @"Tf\s*(-?[\d.]+) (-?[\d.]+) Td\s*\(([^)]*)\) Tj")
            .GroupBy(match => match.Groups[3].Value)
            .ToDictionary(group => group.Key, group => double.Parse(group.First().Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The last line of a paragraph beside a square-wrapped picture starts at the margin once the picture has ended, also
    /// when it holds a single word placed right after a wrap (it fell back to the indent of the paragraph's start).
    /// </summary>
    private static void LastLineBelowAPictureStartsAtTheMargin()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var picture = new ImageBlock(png, ImageMediaType.Png) { Width = Length.FromPoints(100), Height = Length.FromPoints(40), Wrap = ImageWrap.Square };
        for (var words = 30; words <= 60; words++)
        {
            var text = string.Join(' ', Enumerable.Range(0, words).Select(index => "w" + index));
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(Document(new Section([picture, new Paragraph(new TextRun(text))])), buffer).GetAwaiter().GetResult();
            var content = PdfStreams.DecompressPages(buffer.ToArray())[0];
            // The picture's bottom edge from its placement matrix "100 0 0 40 x y cm".
            var bottom = double.Parse(Regex.Match(content, @"100 0 0 40 [\d.]+ ([\d.]+) cm").Groups[1].Value, CultureInfo.InvariantCulture);
            var lineStarts = Regex.Matches(content, @"Tf\s*(-?[\d.]+) (-?[\d.]+) Td\s*\((w\d+)\) Tj")
                .GroupBy(match => match.Groups[2].Value)
                .Select(group => (Y: double.Parse(group.Key, CultureInfo.InvariantCulture), X: double.Parse(group.First().Groups[1].Value, CultureInfo.InvariantCulture)));
            foreach (var (y, x) in lineStarts.Where(line => line.Y + 12 < bottom))
            {
                Require(Math.Abs(x - 72) < 0.01, $"{words} words: the line at {y} below the picture (bottom {bottom}) starts at {x}, not at the margin.");
            }
        }
    }

    /// <summary>A table without rows shows in no format; every writer says so once.</summary>
    private static void EmptyTableIsReported()
    {
        var document = Document(new Section([new Paragraph(new TextRun("Annex")), new TableBlock([]), new TableBlock([])]));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf, DocumentFormat.Html })
        {
            using var buffer = new MemoryStream();
            var warnings = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult().Warnings
                .Count(warning => warning.Message.Contains("table without rows", StringComparison.Ordinal));
            Require(warnings == 1, $"{format} reports an empty table once ({warnings}).");
        }

        using var clean = new MemoryStream();
        Require(!DocumentWriter.WriteAsync(Document(new Section(Table("A"))), clean, DocumentFormat.Docx).GetAwaiter().GetResult().Warnings
            .Any(warning => warning.Message.Contains("table without rows", StringComparison.Ordinal)), "A table with rows is not reported.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
