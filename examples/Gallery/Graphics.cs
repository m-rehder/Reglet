using Reglet;

namespace Gallery;

/// <summary>Graphics: pictures, shapes, charts, barcodes, flow diagrams and formulas.</summary>
public static class Graphics
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("graphics", "pictures", "A picture in the text, one scaled to a width and one with text flowing around it.")
        {
            Document = Pictures,
        };
        yield return new Example("graphics", "shapes", "Rectangles, an ellipse, a dashed line and a text box.")
        {
            Document = Shapes,
        };
        yield return new Example("graphics", "charts", "Column, line and pie charts that Word and LibreOffice can still edit.")
        {
            Document = Charts,
        };
        yield return new Example("graphics", "barcodes", "A QR code, an EAN-13 and a Code 128 barcode, drawn as vector shapes.")
        {
            Document = Barcodes,
        };
        yield return new Example("graphics", "flow-diagram", "A flow chart with start, steps, a decision and labelled arrows.")
        {
            Document = FlowDiagram,
        };
        yield return new Example("graphics", "formulas", "Fractions, roots, sums and integrals as real, editable formulas.")
        {
            Document = Formulas,
        };
    }

    private static PortableDocument Pictures()
    {
        var picture = Assets.GradientPng(320, 200);
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Pictures")),
            new Paragraph(new TextRun("In its own size:")),
            new ImageBlock(picture, ImageMediaType.Png) { AltText = "A petrol and orange colour gradient" },
            new Paragraph(new TextRun("Scaled to 5 cm wide, the height follows:")),
            new ImageBlock(picture, ImageMediaType.Png) { Width = Length.FromCentimeters(5), AltText = "The gradient, smaller" },
            new ImageBlock(picture, ImageMediaType.Png) { Width = Length.FromCentimeters(4), Wrap = ImageWrap.Square, AltText = "The gradient beside the text" },
            new Paragraph(new TextRun("The text flows around this picture. " + Words.Paragraph(9, 80))),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Pictures" }, new Section(blocks));
    }

    private static PortableDocument Shapes()
    {
        var cm = Length.FromCentimeters;
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Shapes")),
            new Paragraph(new TextRun("Shapes are vector drawings; Word and LibreOffice can still move and edit them.")),
            new DrawingShape(ShapeKind.Rectangle, cm(6), cm(1.5)) { Fill = Assets.Petrol },
            new DrawingShape(ShapeKind.RoundRectangle, cm(6), cm(1.5)) { Fill = Assets.Tint, Outline = Assets.Petrol },
            new DrawingShape(ShapeKind.Ellipse, cm(3), cm(3)) { Fill = Assets.Accent },
            new DrawingShape(ShapeKind.Line, cm(10), Length.Zero) { Outline = Assets.Petrol, Dash = ShapeDash.Dash, OutlineWidth = Length.FromPoints(2) },
            new DrawingShape(ShapeKind.TextBox, cm(10), cm(2.5),
                [new Paragraph(new TextRun("A text box holds its own paragraphs. It is useful for a pull quote or a note beside the text.") { Italic = true })])
            {
                Outline = Assets.Accent,
            },
        };
        return new PortableDocument(new DocumentMetadata { Title = "Shapes" }, new Section(blocks));
    }

    private static PortableDocument Charts()
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"];
        var column = new Chart(ChartKind.Column,
            [new ChartSeries("2025", [12, 15, 14, 18, 21, 24]) { Color = Assets.Tint }, new ChartSeries("2026", [14, 17, 19, 22, 26, 29]) { Color = Assets.Petrol }],
            months)
        { Title = "Visitors (thousands)", ValueAxisTitle = "Visitors" };
        var line = new Chart(ChartKind.Line, [new ChartSeries("Temperature", [3.1, 4.5, 8.2, 12.6, 16.9, 20.4]) { Color = Assets.Accent }], months)
        { Title = "Average temperature (°C)" };
        var pie = new Chart(ChartKind.Pie, [new ChartSeries("Share", [45, 30, 15, 10])], ["Print", "Web", "Mobile", "Other"])
        { Title = "Readers by channel", Legend = ChartLegendPosition.Right };

        return new PortableDocument(new DocumentMetadata { Title = "Charts" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Charts")), column, line, pie,
        }));
    }

    private static PortableDocument Barcodes()
    {
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Barcodes")),
            new Paragraph(new TextRun("QR code with a web address:")),
            Barcode.Create(BarcodeSymbology.QrCode, "https://github.com/m-rehder/Reglet", moduleWidth: Length.FromMillimeters(0.8)),
            new Paragraph(new TextRun("EAN-13 (the check digit is computed):")),
            Barcode.Create(BarcodeSymbology.Ean13, "400638133393" + Barcode.EanCheckDigit("400638133393")),
            new Paragraph(new TextRun("Code 128 with letters and digits:")),
            Barcode.Create(BarcodeSymbology.Code128, "REGLET-2026-0042", color: Assets.Petrol),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Barcodes" }, new Section(blocks));
    }

    private static PortableDocument FlowDiagram()
    {
        static Length P(double points) => Length.FromPoints(points);
        static FlowNode Node(string id, FlowNodeKind kind, string text, double x, double y, double width = 130)
            => new(id, kind, text, P(x), P(y), P(width), P(44)) { Fill = Assets.Tint, Outline = Assets.Petrol };

        var diagram = new Diagram(P(420), P(330),
        [
            Node("order", FlowNodeKind.Terminator, "Order received", 140, 0),
            Node("stock", FlowNodeKind.Decision, "In stock?", 140, 80),
            Node("ship", FlowNodeKind.Process, "Pack and ship", 140, 180),
            Node("reorder", FlowNodeKind.Process, "Reorder from supplier", 300, 80, 120),
            Node("done", FlowNodeKind.Terminator, "Done", 140, 280),
        ],
        [
            new FlowConnector("order", FlowSide.Bottom, "stock", FlowSide.Top),
            new FlowConnector("stock", FlowSide.Bottom, "ship", FlowSide.Top) { Label = "Yes" },
            new FlowConnector("stock", FlowSide.Right, "reorder", FlowSide.Left) { Label = "No" },
            new FlowConnector("reorder", FlowSide.Bottom, "ship", FlowSide.Right) { Dash = ShapeDash.Dash },
            new FlowConnector("ship", FlowSide.Bottom, "done", FlowSide.Top),
        ])
        { AltText = "How an order is handled" };

        return new PortableDocument(new DocumentMetadata { Title = "Flow diagram" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Order handling")), diagram,
        }));
    }

    private static PortableDocument Formulas()
    {
        static MathNode T(string text) => new MathText(text);
        static MathRow R(params MathNode[] items) => new(items);

        var quadratic = new Equation(R(T("x"), T("="),
            new MathFraction(R(T("−"), T("b"), T("±"), new MathRadical(R(new MathScript(T("b"), superscript: T("2")), T("−"), T("4"), T("a"), T("c")))), R(T("2"), T("a")))))
        { AltText = "The quadratic formula" };
        var series = new Equation(R(new MathNary('∑', new MathScript(T("k"), superscript: T("2")), R(T("k"), T("="), T("1")), T("n")), T("="),
            new MathFraction(R(T("n"), new MathDelimiter('(', ')', [R(T("n"), T("+"), T("1"))]), new MathDelimiter('(', ')', [R(T("2"), T("n"), T("+"), T("1"))])), T("6"))));
        var integral = new Equation(R(new MathNary('∫', R(new MathScript(T("x"), superscript: T("2")), T("d"), T("x")), T("0"), T("1")), T("="),
            new MathFraction(T("1"), T("3"))));

        return new PortableDocument(new DocumentMetadata { Title = "Formulas" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Formulas")),
            new Paragraph(new TextRun("Formulas are written as Office Math in Word and as MathML in LibreOffice, so they stay editable.")),
            quadratic, series, integral,
        }));
    }
}
