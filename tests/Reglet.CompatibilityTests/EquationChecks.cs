using System.IO.Compression;
using Reglet;

/// <summary>Equations (ADR-0031): the math tree, OMML, MathML and the PDF drawing.</summary>
internal static class EquationChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidNodes();
            WritesAndReadsDocx();
            WritesAndReadsOdt();
            DrawsPdfAndReportsInBands();
            DrawsInTableCells();
            if (Environment.GetEnvironmentVariable("EQUATION_OUT") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                foreach (var (format, extension) in new[] { (DocumentFormat.Docx, "docx"), (DocumentFormat.Odt, "odt"), (DocumentFormat.Pdf, "pdf") })
                {
                    File.WriteAllBytes(Path.Combine(dir, "equation." + extension), Write(Document(), format));
                }
            }

            Console.WriteLine("Equations: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Equations: " + exception);
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

    private static MathText T(string text, MathTextStyle? style = null) => new(text, style);

    private static MathRow Row(params MathNode[] items) => new(items);

    internal static Equation Quadratic() => new(Row(
        T("x"), T("="),
        new MathFraction(
            Row(T("−"), T("b"), T("±"), new MathRadical(Row(new MathScript(T("b"), superscript: T("2")), T("−"), T("4"), T("a"), T("c")))),
            Row(T("2"), T("a")))))
    { AltText = "Mitternachtsformel" };

    private static Equation Sum() => new(Row(
        new MathNary('∑', new MathScript(T("x"), subscript: T("i")), new MathRow([T("i"), T("="), T("1")]), T("n")),
        T("="),
        new MathNary('∫', Row(T("f"), new MathDelimiter('(', ')', [T("t")]), T("d"), T("t")), T("0"), T("1")),
        T("+"), new MathRadical(T("y"), T("3"))));

    private static PortableDocument Document()
        => new(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("Vorher")), Quadratic(), Sum(), new Paragraph(new TextRun("Nachher")),
        }));

    private static byte[] Write(PortableDocument document, DocumentFormat format, WriteResultSink? sink = null)
    {
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        sink?.Add(result.Warnings.Select(warning => warning.Message));
        return buffer.ToArray();
    }

    private sealed class WriteResultSink
    {
        internal List<string> Messages { get; } = [];

        internal void Add(IEnumerable<string> messages) => Messages.AddRange(messages);
    }

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void RejectsInvalidNodes()
    {
        Expect<ArgumentException>(() => new MathText(""));
        Expect<ArgumentException>(() => new MathRow([]));
        Expect<ArgumentException>(() => new MathScript(T("x")));
        Expect<ArgumentException>(() => new MathNary('x', T("y")));
        Expect<ArgumentException>(() => new MathDelimiter('x', ')', [T("y")]));
        Require(new MathText("x").Style == MathTextStyle.Italic && new MathText("2").Style == MathTextStyle.Normal, "automatic style");
        MathNode deep = T("x");
        Expect<ArgumentException>(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                deep = new MathScript(deep, superscript: T("2"));
            }
        });
    }

    /// <summary>The kinds of a formula in order, so two trees can be compared.</summary>
    private static string Shape(MathNode node) => node switch
    {
        MathText text => $"t[{text.Text}|{text.Style}]",
        MathRow row => "r(" + string.Join(",", row.Items.Select(Shape)) + ")",
        MathFraction fraction => $"f({Shape(fraction.Numerator)},{Shape(fraction.Denominator)})",
        MathScript script => $"s({Shape(script.Base)},{(script.Subscript is null ? "-" : Shape(script.Subscript))},{(script.Superscript is null ? "-" : Shape(script.Superscript))})",
        MathRadical radical => $"q({Shape(radical.Radicand)},{(radical.Degree is null ? "-" : Shape(radical.Degree))})",
        MathNary nary => $"n{nary.Operator}({(nary.Lower is null ? "-" : Shape(nary.Lower))},{(nary.Upper is null ? "-" : Shape(nary.Upper))},{Shape(nary.Body)})",
        MathDelimiter delimiter => $"d{delimiter.Open}{delimiter.Close}(" + string.Join(",", delimiter.Items.Select(Shape)) + ")",
        _ => "?",
    };

    private static List<Equation> Equations(ReadResult read) => read.Document.Sections[0].Blocks.OfType<Equation>().ToList();

    private static void WritesAndReadsDocx()
    {
        var bytes = Write(Document(), DocumentFormat.Docx);
        var xml = Part(bytes, "word/document.xml");
        Require(xml.Contains("<m:oMathPara", StringComparison.Ordinal) && xml.Contains("<m:f>", StringComparison.Ordinal)
            && xml.Contains("<m:rad>", StringComparison.Ordinal) && xml.Contains("<m:nary>", StringComparison.Ordinal), "omml elements");
        var read = DocumentReader.Read(new MemoryStream(bytes), DocumentFormat.Docx);
        var equations = Equations(read);
        Require(equations.Count == 2, "docx equations " + equations.Count);
        Require(Shape(equations[0].Root) == Shape(Quadratic().Root), "docx quadratic:\n" + Shape(equations[0].Root) + "\n" + Shape(Quadratic().Root));
        Require(Shape(equations[1].Root) == Shape(Sum().Root), "docx sum:\n" + Shape(equations[1].Root) + "\n" + Shape(Sum().Root));
        Require(read.Document.Sections[0].Blocks.Count == 4, "docx blocks " + read.Document.Sections[0].Blocks.Count);
    }

    private static void WritesAndReadsOdt()
    {
        var bytes = Write(Document(), DocumentFormat.Odt);
        var content = Part(bytes, "Object 1/content.xml");
        Require(content.Contains("<math:mfrac>", StringComparison.Ordinal) && content.Contains("<math:msqrt>", StringComparison.Ordinal), "mathml elements");
        Require(Part(bytes, "META-INF/manifest.xml").Contains("application/vnd.oasis.opendocument.formula", StringComparison.Ordinal), "manifest");
        var read = DocumentReader.Read(new MemoryStream(bytes), DocumentFormat.Odt);
        var equations = Equations(read);
        Require(equations.Count == 2, "odt equations " + equations.Count);
        Require(Shape(equations[0].Root) == Shape(Quadratic().Root), "odt quadratic:\n" + Shape(equations[0].Root) + "\n" + Shape(Quadratic().Root));
        Require(Shape(equations[1].Root) == Shape(Sum().Root), "odt sum:\n" + Shape(equations[1].Root) + "\n" + Shape(Sum().Root));
        Require(equations[0].AltText == "Mitternachtsformel", "odt alt text");

        // The alignment goes into the paragraph style of the equation and comes back.
        var aligned = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Equation(T("a")) { Alignment = TextAlignment.Left }, new Equation(T("b")), new Equation(T("c")) { Alignment = TextAlignment.Right },
        }));
        var alignedBytes = Write(aligned, DocumentFormat.Odt);
        Require(Part(alignedBytes, "content.xml").Contains("<text:p text:style-name=\"MathRight\">", StringComparison.Ordinal), "odt right-aligned paragraph");
        var back = Equations(DocumentReader.Read(new MemoryStream(alignedBytes), DocumentFormat.Odt));
        Require(back.Select(e => e.Alignment).SequenceEqual(new TextAlignment?[] { TextAlignment.Left, null, TextAlignment.Right }), "odt alignment round trip: " + string.Join(",", back.Select(e => e.Alignment)));
    }

    private static void DrawsInTableCells()
    {
        // A formula and a diagram in table cells are drawn (they were left out with a warning).
        var table = new TableBlock([new TableRow(new TableCell(new Block[] { Quadratic() }), new TableCell(new Block[] { DiagramChecks.Sample() }))]);
        var sink = new WriteResultSink();
        var pdf = Write(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { table })), DocumentFormat.Pdf, sink);
        Require(!sink.Messages.Any(m => m.Contains("not rendered", StringComparison.Ordinal)), "nothing left out: " + string.Join(" | ", sink.Messages));
        var text = string.Concat(new PdfDocumentReader().Read(new MemoryStream(pdf)).Document.Sections
            .SelectMany(section => section.Blocks).OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<TextRun>().Select(r => r.Text));
        Require(text.Contains("Verarbeiten", StringComparison.Ordinal), "diagram node text in the cell: " + text);
        Require(System.Text.Encoding.Latin1.GetString(pdf).Contains("/S /Formula", StringComparison.Ordinal), "formula tagged in the cell");

        // DOCX: the formula in the cell comes back as a formula, without an extra paragraph or warning.
        var docxSink = new WriteResultSink();
        var docx = Write(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { table })), DocumentFormat.Docx, docxSink);
        var back = new Reglet.DocxDocumentReader().Read(new MemoryStream(docx));
        var cellBlocks = ((TableBlock)back.Document.Sections[0].Blocks[0]).Rows[0].Cells[0].Blocks;
        Require(cellBlocks.Count == 1 && cellBlocks[0] is Equation, "docx cell holds the equation only: " + string.Join(", ", cellBlocks.Select(b => b.GetType().Name)));
        // The diagram is shrunk to the cell with its text: the node text in the DOCX is smaller than the 11 pt it has at full size.
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx)))
        {
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            var group = xml[xml.IndexOf("<wpg:wgp", StringComparison.Ordinal)..];
            var sizes = System.Text.RegularExpressions.Regex.Matches(group, "<w:sz w:val=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Require(sizes.Count > 0 && sizes.Max() < 22, "node text scaled with the diagram: " + string.Join(",", sizes));
            Require(group.Contains("lIns=\"", StringComparison.Ordinal) && !group.Contains("lIns=\"91440\"", StringComparison.Ordinal), "text margins scaled with the diagram");
        }

        // ODT: the diagram in the cell is shrunk with its text, and node text is centred vertically.
        var odt = Write(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { table })), DocumentFormat.Odt);
        var odtContent = Part(odt, "content.xml");
        var fontSizes = System.Text.RegularExpressions.Regex.Matches(odtContent, "fo:font-size=\"([0-9.]+)pt\"")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Require(fontSizes.Count > 0 && fontSizes.Max() < 11, "odt node text scaled with the diagram: " + string.Join(",", fontSizes));
        Require(odtContent.Contains("draw:textarea-vertical-align=\"middle\"", StringComparison.Ordinal), "odt node text centred vertically");

        Require(!back.Warnings.Any(w => w.Message.Contains("equation", StringComparison.OrdinalIgnoreCase)), "no equation warning reading it back: " + string.Join(" | ", back.Warnings.Select(w => w.Message)));
    }

    private static void DrawsPdfAndReportsInBands()
    {
        var pdf = Write(Document(), DocumentFormat.Pdf);
        Require(System.Text.Encoding.Latin1.GetString(pdf).StartsWith("%PDF", StringComparison.Ordinal), "pdf");

        var withHeader = new PortableDocument(new DocumentMetadata(), new Section(new Block[] { new Paragraph(new TextRun("x")) })
        {
            Header = new HeaderFooter(new Block[] { Quadratic() }),
        });
        var sink = new WriteResultSink();
        Write(withHeader, DocumentFormat.Docx, sink);
        Write(withHeader, DocumentFormat.Odt, sink);
        Require(sink.Messages.Any(m => m.Contains("equation in a header", StringComparison.Ordinal)), "docx reports");
        Require(sink.Messages.Any(m => m.Contains("equation in the header", StringComparison.Ordinal)), "odt reports");
    }
}
