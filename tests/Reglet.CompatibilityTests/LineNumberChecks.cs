using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// line numbering (phase D): the numbers are written as <c>w:lnNumType</c> (DOCX), as
/// <c>text:linenumbering-configuration</c> (ODT) and drawn in the left margin (PDF).
/// </summary>
internal static class LineNumberChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxLineNumbers();
            WritesOdtLineNumbers();
            DrawsPdfLineNumbers();
            RestartsPerPage();
            RejectsInvalidValues();
            Console.WriteLine("Line numbering (D): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Line numbering (D): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(LineNumbering? numbering = null, int paragraphs = 3)
    {
        var blocks = new List<Block>();
        for (var index = 0; index < paragraphs; index++)
        {
            blocks.Add(new Paragraph(new TextRun("Zeile")));
        }

        var section = new Section(blocks);
        if (numbering is not null)
        {
            section = section with { LineNumbering = numbering };
        }

        return new PortableDocument(new DocumentMetadata(), section);
    }

    private static void WritesDocxLineNumbers()
    {
        var numbering = new LineNumbering
        {
            CountBy = 5,
            Start = 10,
            Distance = Length.FromPoints(18),
            Restart = LineNumberRestart.NewSection,
        };

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(numbering), buffer).GetAwaiter().GetResult();

        var document = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(document.Contains("<w:lnNumType", StringComparison.Ordinal), "DOCX: the line numbering is written.");
        Require(document.Contains("w:countBy=\"5\"", StringComparison.Ordinal), "DOCX: count by.");
        Require(document.Contains("w:start=\"10\"", StringComparison.Ordinal), "DOCX: start number.");
        Require(document.Contains("w:distance=\"360\"", StringComparison.Ordinal), "DOCX: distance in twips.");
        Require(document.Contains("w:restart=\"newSection\"", StringComparison.Ordinal), "DOCX: restart.");

        // The element order of w:sectPr is fixed by the schema.
        var lineNumber = document.IndexOf("<w:lnNumType", StringComparison.Ordinal);
        var pageNumber = document.IndexOf("<w:pgNumType", StringComparison.Ordinal);
        Require(lineNumber >= 0 && (pageNumber < 0 || lineNumber < pageNumber), "DOCX: lnNumType comes before pgNumType.");

        // Without line numbering there is no element.
        using var plain = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), plain).GetAwaiter().GetResult();
        Require(
            !ReadZipEntry(plain.ToArray(), "word/document.xml").Contains("<w:lnNumType", StringComparison.Ordinal),
            "DOCX: without line numbering nothing is written.");
    }

    private static void WritesOdtLineNumbers()
    {
        var numbering = new LineNumbering
        {
            CountBy = 3,
            Start = 1,
            Distance = Length.FromPoints(12),
            Restart = LineNumberRestart.NewPage,
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(numbering), buffer).GetAwaiter().GetResult();

        var styles = ReadZipEntry(buffer.ToArray(), "styles.xml");
        Require(styles.Contains("<text:linenumbering-configuration", StringComparison.Ordinal), "ODT: the line numbering is written.");
        Require(styles.Contains("text:increment=\"3\"", StringComparison.Ordinal), "ODT: increment.");
        Require(styles.Contains("text:offset=\"12pt\"", StringComparison.Ordinal), "ODT: offset.");
        Require(styles.Contains("text:restart-on-page=\"true\"", StringComparison.Ordinal), "ODT: restart on page.");

        using var plain = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(), plain).GetAwaiter().GetResult();
        Require(
            !ReadZipEntry(plain.ToArray(), "styles.xml").Contains("linenumbering-configuration", StringComparison.Ordinal),
            "ODT: without line numbering nothing is written.");
    }

    private static void DrawsPdfLineNumbers()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(new LineNumbering()), buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(1) Tj", StringComparison.Ordinal), "PDF: the first line number is drawn.");
        Require(content.Contains("(2) Tj", StringComparison.Ordinal), "PDF: the second line number is drawn.");
        Require(content.Contains("(3) Tj", StringComparison.Ordinal), "PDF: the third line number is drawn.");

        // The numbers stand in the margin, that is left of the text edge (72 pt).
        var at = content.IndexOf("(1) Tj", StringComparison.Ordinal);
        var positions = LineNumberPositions(content);
        Require(positions.Count == 3, "PDF: three line numbers.");
        foreach (var position in positions)
        {
            Require(position < 72, "PDF: the line number stands left of the text.");
        }
    }

    private static void RestartsPerPage()
    {
        var blocks = new List<Block>();
        for (var index = 0; index < 120; index++)
        {
            blocks.Add(new Paragraph(new TextRun("Absatz")));
        }

        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(blocks) { LineNumbering = new LineNumbering { Restart = LineNumberRestart.NewPage } });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        // The numbering restarts on every page, so the number 1 appears again.
        Require(content.Contains("(1) Tj", StringComparison.Ordinal), "PDF: the numbering starts at 1.");
        var raw = Encoding.Latin1.GetString(buffer.ToArray());
        var count = raw.IndexOf("/Count ", StringComparison.Ordinal);
        Require(count >= 0, "PDF: the page tree has a count.");
        var pages = int.Parse(
            raw[(count + "/Count ".Length)..].TrimStart(' ').Split(' ', '\r', '\n')[0],
            System.Globalization.CultureInfo.InvariantCulture);
        Require(pages > 1, "PDF: the document has several pages.");
        Require(
            content.Split("(1) Tj", StringSplitOptions.None).Length - 1 >= 2,
            "PDF: the numbering restarts on every page.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => new LineNumbering { CountBy = 0 });
        Throws<ArgumentOutOfRangeException>(() => new LineNumbering { Start = -1 });
        Throws<ArgumentOutOfRangeException>(() => new LineNumbering { Distance = Length.FromPoints(-1) });
    }

    /// <summary>The x positions of the drawn line numbers ("x y Td" before "(n) Tj").</summary>
    private static List<double> LineNumberPositions(string content)
    {
        var positions = new List<double>();
        var index = 0;
        while ((index = content.IndexOf(") Tj", index, StringComparison.Ordinal)) >= 0)
        {
            // Only the line numbers are a single number in parentheses.
            if (index > 0 && char.IsDigit(content[index - 1]))
            {
                var end = content.LastIndexOf(" Td", index, StringComparison.Ordinal);
                if (end > 0)
                {
                    var start = content.LastIndexOf('\n', end);
                    if (start > 0)
                    {
                        var numbers = content[(start + 1)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (numbers.Length == 2
                            && double.TryParse(
                                numbers[0],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var x))
                        {
                            positions.Add(x);
                        }
                    }
                }
            }

            index += 4;
        }

        return positions;
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
