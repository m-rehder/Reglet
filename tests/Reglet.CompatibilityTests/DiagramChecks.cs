using System.IO.Compression;
using Reglet;

/// <summary>Flow diagrams (ADR-0030): nodes, drawn connectors, the three writers.</summary>
internal static class DiagramChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidDiagrams();
            RoutesConnectors();
            WritesDocxOdtAndPdf();
            if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                foreach (var (format, extension) in new[] { (DocumentFormat.Docx, "docx"), (DocumentFormat.Odt, "odt"), (DocumentFormat.Pdf, "pdf") })
                {
                    File.WriteAllBytes(Path.Combine(dir, "diagram." + extension), Write(Document(), format));
                }
            }

            Console.WriteLine("Diagrams: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Diagrams: " + exception);
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

    private static Length P(double points) => Length.FromPoints(points);

    private static FlowNode Node(string id, FlowNodeKind kind, string text, double x, double y, double w = 120, double h = 44)
        => new(id, kind, text, P(x), P(y), P(w), P(h));

    internal static Diagram Sample() => new(
        P(400), P(330),
        [
            Node("start", FlowNodeKind.Terminator, "Start", 140, 0),
            Node("read", FlowNodeKind.InputOutput, "Read input", 140, 66),
            Node("check", FlowNodeKind.Decision, "Valid?", 140, 132, 120, 70),
            Node("work", FlowNodeKind.Process, "Process", 140, 224),
            Node("fail", FlowNodeKind.Process, "Report error", 290, 145, 110, 44),
            Node("end", FlowNodeKind.Terminator, "End", 140, 286),
        ],
        [
            new FlowConnector("start", FlowSide.Bottom, "read", FlowSide.Top),
            new FlowConnector("read", FlowSide.Bottom, "check", FlowSide.Top),
            new FlowConnector("check", FlowSide.Bottom, "work", FlowSide.Top) { Label = "Ja" },
            new FlowConnector("check", FlowSide.Right, "fail", FlowSide.Left) { Label = "No" },
            new FlowConnector("work", FlowSide.Bottom, "end", FlowSide.Top),
            new FlowConnector("fail", FlowSide.Bottom, "end", FlowSide.Right) { Dash = ShapeDash.Dash },
        ]) { AltText = "Flow of the input check" };

    private static PortableDocument Document()
        => new(new DocumentMetadata(), new Section(new Block[] { new Paragraph(new TextRun("Before")), Sample(), new Paragraph(new TextRun("After")) }));

    private static byte[] Write(PortableDocument document, DocumentFormat format)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void RejectsInvalidDiagrams()
    {
        var a = Node("a", FlowNodeKind.Process, "A", 0, 0);
        Expect<ArgumentException>(() => new Diagram(P(200), P(100), []));
        Expect<ArgumentException>(() => new Diagram(P(200), P(100), [a, a]));
        Expect<ArgumentException>(() => new Diagram(P(100), P(100), [a]));
        Expect<ArgumentException>(() => new Diagram(P(200), P(100), [a], [new FlowConnector("a", FlowSide.Right, "b", FlowSide.Left)]));
        Expect<ArgumentException>(() => new Diagram(P(0), P(100), [a]));
        Expect<ArgumentException>(() => Node("", FlowNodeKind.Process, "x", 0, 0));
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

    private static void RoutesConnectors()
    {
        var diagram = Sample();
        // 6 nodes: 3 plain shapes + 3 symbols with text (shape + text box) -> 1 + 2*5... count only that connectors add arrows.
        var freeforms = diagram.Shapes.Count(placed => placed.Shape.Kind == ShapeKind.Freeform);
        // 3 symbols (start, read, check, end = 4) as freeforms + 6 lines + 6 arrowheads.
        Require(freeforms == 4 + 6 + 6, "freeform count " + freeforms);
        Require(diagram.Shapes.Count(placed => placed.Shape.Kind == ShapeKind.TextBox && placed.Shape.Fill is null) == 4 + 2, "text boxes without fill (symbol texts and labels)");

        // The straight down connector is one vertical segment: width 1 (padding) and the gap minus the arrowhead.
        var straight = new Diagram(P(200), P(200), [Node("a", FlowNodeKind.Process, "A", 40, 0), Node("b", FlowNodeKind.Process, "B", 40, 100)],
            [new FlowConnector("a", FlowSide.Bottom, "b", FlowSide.Top)]);
        var line = straight.Shapes.First(placed => placed.Shape.Kind == ShapeKind.Freeform);
        Require(Math.Abs(line.Shape.Width.Points - 1) < 0.01 && Math.Abs(line.Shape.Height.Points - (56 - 8 + 1)) < 0.01 + 0.5, $"vertical line {line.Shape.Width.Points}x{line.Shape.Height.Points}");
        Require(Math.Abs(line.X.Points - 99.5) < 0.01, "line x " + line.X.Points);

        // An elbow from the right of one node to the top of a lower one has one bend (3 path steps).
        var elbow = new Diagram(P(300), P(200), [Node("a", FlowNodeKind.Process, "A", 0, 0), Node("b", FlowNodeKind.Process, "B", 180, 120)],
            [new FlowConnector("a", FlowSide.Right, "b", FlowSide.Top)]);
        var elbowLine = elbow.Shapes.First(placed => placed.Shape.Kind == ShapeKind.Freeform && placed.Shape.Fill is null).Shape;
        Require(elbowLine.Path!.Count == 3, "elbow steps " + elbowLine.Path.Count);
    }

    private static void WritesDocxOdtAndPdf()
    {
        var document = Document();
        var docx = Write(document, DocumentFormat.Docx);
        var xml = Part(docx, "word/document.xml");
        Require(xml.Contains("<wpg:wgp", StringComparison.Ordinal) && xml.Contains("wpg:grpSpPr", StringComparison.Ordinal), "docx group");
        Require(System.Text.RegularExpressions.Regex.Matches(xml, "<wps:wsp[ >]").Count == document_shapes(), "docx shape count");
        Require(xml.Contains("<wp:extent ", StringComparison.Ordinal) && !xml.Contains("<a:extent", StringComparison.Ordinal), "docx diagram has wp:extent (Word rejects a:extent in wp:inline)");
        Require(xml.Contains("Flow of the input check", StringComparison.Ordinal), "docx alt text");

        var odt = Write(document, DocumentFormat.Odt);
        var content = Part(odt, "content.xml");
        Require(content.Contains("<draw:g ", StringComparison.Ordinal), "odt group");
        Require(content.Contains("Process", StringComparison.Ordinal), "odt text");

        foreach (var (bytes, format) in new[] { (docx, DocumentFormat.Docx), (odt, DocumentFormat.Odt) })
        {
            var read = DocumentReader.Read(new MemoryStream(bytes), format);
            // The group is reported and its parts do not turn into loose shapes or paragraphs.
            Require(read.Document.Sections[0].Blocks.Count == 2, $"{format} reads {read.Document.Sections[0].Blocks.Count} blocks");
            Require(read.Warnings.Any(w => w.Message.Contains("not mapped", StringComparison.Ordinal)), $"{format} reports the group");
        }

        var pdf = Write(document, DocumentFormat.Pdf);
        Require(pdf.Length > 1000, "pdf");
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Require(text.StartsWith("%PDF", StringComparison.Ordinal), "pdf header");
    }

    private static int document_shapes() => Sample().Shapes.Count;

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
