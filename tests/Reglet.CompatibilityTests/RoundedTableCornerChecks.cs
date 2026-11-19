using System.Text;
using Reglet;

/// <summary>
/// OF-B04: rounded table corners. The model carries the radius, the PDF draws the outer corners
/// rounded, DOCX and ODT have no rounded cells and report the value.
/// </summary>
internal static class RoundedTableCornerChecks
{
    internal static int Run()
    {
        try
        {
            RejectsNegativeRadius();
            RoundsTheOuterCornersInPdf();
            KeepsInnerCornersSquare();
            ClampsLargeCornerRadii();
            SkipsRoundingWithoutRadius();
            ReportsInDocxAndOdt();
            Console.WriteLine("Rounded table corners (OF-B04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Rounded table corners (OF-B04): " + exception);
            return 1;
        }
    }

    private static void RejectsNegativeRadius()
    {
        var table = new TableBlock([new TableRow([new TableCell("a")])]);
        Throws<ArgumentOutOfRangeException>(() => _ = new TableBlock(table.Rows) { CornerRadius = Length.FromPoints(-1) });
    }

    /// <summary>The background of the corner cells is filled with a rounded path.</summary>
    private static void RoundsTheOuterCornersInPdf()
    {
        var content = PdfContent(Document(Length.FromPoints(6)));

        // A rounded fill is a path with Bézier segments that is closed and filled (h f), not a
        // rectangle (re f).
        Require(
            content.Contains("h\nf\n", StringComparison.Ordinal),
            "The table background is filled with rounded corners: " + content);
        Require(
            content.Contains(" c\n", StringComparison.Ordinal),
            "The rounded corners are Bézier segments: " + content);
    }

    /// <summary>A cell inside the table keeps square corners.</summary>
    private static void KeepsInnerCornersSquare()
    {
        var content = PdfContent(Document(Length.FromPoints(6)));

        // Only the four corner cells round: two arcs for the top row and two for the bottom row.
        var arcs = CountOccurrences(content, "c\nS\n");
        Require(arcs == 4, $"Four corner arcs are stroked (found {arcs}).");
    }

    private static void ClampsLargeCornerRadii()
    {
        var content = PdfContent(Document(Length.FromPoints(1000)));
        var paths = System.Text.RegularExpressions.Regex.Matches(content, @"(?m)^((?:-?[0-9.]+ ){2,6})(?:m|l|c)$");
        Require(paths.Count > 0, "The table path is missing.");
        foreach (System.Text.RegularExpressions.Match path in paths)
        {
            foreach (var value in path.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var point = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                Require(point >= 0 && point <= PageSize.A4.Height.Points, "A rounded corner extends outside the page: " + path.Value);
            }
        }
    }

    private static void SkipsRoundingWithoutRadius()
    {
        var content = PdfContent(Document(null));
        Require(!content.Contains(" c\n", StringComparison.Ordinal), "Without a radius no Bézier segment is written.");
        Require(content.Contains(" re\n", StringComparison.Ordinal), "Without a radius the cells stay rectangles.");
    }

    private static void ReportsInDocxAndOdt()
    {
        var document = Document(Length.FromPoints(6));

        using var docx = new MemoryStream();
        var docxResult = new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        Require(
            docxResult.Warnings.Any(warning => warning.Message.Contains("rounded table corners", StringComparison.Ordinal)),
            "DOCX reports the rounded corners: " + string.Join(" | ", docxResult.Warnings.Select(warning => warning.Message)));

        using var odt = new MemoryStream();
        var odtResult = new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(
            odtResult.Warnings.Any(warning => warning.Message.Contains("rounded table corners", StringComparison.Ordinal)),
            "ODT reports the rounded corners: " + string.Join(" | ", odtResult.Warnings.Select(warning => warning.Message)));
    }

    /// <summary>A 2×2 table with a background on every cell and a border around the grid.</summary>
    private static PortableDocument Document(Length? radius)
    {
        var border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(0.5), Color.Black);
        TableCell Cell(string text) => new(text) { Background = Color.FromRgb(0xEE, 0xEE, 0xEE) };

        var table = new TableBlock(new[]
        {
            new TableRow([Cell("a"), Cell("b")]),
            new TableRow([Cell("c"), Cell("d")]),
        })
        {
            Border = border,
            CornerRadius = radius,
        };

        return new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { table }));
    }

    private static string PdfContent(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return PdfStreams.Decompress(buffer.ToArray());
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Erwartete {typeof(TException).Name} blieb aus.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
