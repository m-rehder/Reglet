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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
