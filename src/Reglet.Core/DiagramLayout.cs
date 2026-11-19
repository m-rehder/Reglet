namespace Reglet;

/// <summary>Turns a <see cref="Diagram"/> into positioned <see cref="DrawingShape"/>s (ADR-0030).</summary>
internal static class DiagramLayout
{
    private const double Kappa = 0.5522847498;
    private const double Stub = 12;
    private const double ArrowLength = 8;
    private const double ArrowHalfWidth = 3.5;
    private const double LabelSize = 9;
    private const double CharWidth = 0.5;
    private const double Inset = 7.2;
    private static readonly Color DefaultFill = new(0xDD, 0xEB, 0xF7);
    private static readonly Color DefaultOutline = new(0x1F, 0x4E, 0x79);

    private readonly record struct Point(double X, double Y);

    internal static IReadOnlyList<PlacedShape> Build(Diagram diagram)
    {
        var shapes = new List<PlacedShape>();
        var byId = diagram.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in diagram.Nodes)
        {
            AddNode(shapes, node);
        }

        foreach (var connector in diagram.Connectors)
        {
            AddConnector(shapes, connector, byId[connector.From], byId[connector.To]);
        }

        return shapes;
    }

    private static Length L(double points) => Length.FromPoints(points);

    private static Length R(double points) => Length.FromPoints(Math.Round(points, 3));

    private static void AddNode(List<PlacedShape> shapes, FlowNode node)
    {
        var (x, y, w, h) = (node.X.Points, node.Y.Points, node.Width.Points, node.Height.Points);
        var fill = node.Fill ?? DefaultFill;
        var outline = node.Outline ?? DefaultOutline;
        if (node.Kind == FlowNodeKind.Process)
        {
            shapes.Add(new PlacedShape(
                new DrawingShape(ShapeKind.TextBox, node.Width, node.Height, TextBlocks(node.Text, node, w, h))
                { Fill = fill, Outline = outline, AltText = node.Text.Length > 0 ? node.Text : null },
                node.X, node.Y));
            return;
        }

        var path = node.Kind switch
        {
            FlowNodeKind.Decision => Polygon((w / 2, 0), (w, h / 2), (w / 2, h), (0, h / 2)),
            FlowNodeKind.InputOutput => Polygon((Math.Min(h * 0.3, w / 4), 0), (w, 0), (w - Math.Min(h * 0.3, w / 4), h), (0, h)),
            _ => Pill(w, h),
        };
        shapes.Add(new PlacedShape(new DrawingShape(ShapeKind.Freeform, node.Width, node.Height, path: path) { Fill = fill, Outline = outline }, node.X, node.Y));

        // The text sits in a box without fill or outline inside the symbol.
        var (ix, iy, iw, ih) = node.Kind switch
        {
            FlowNodeKind.Decision => (x + w * 0.2, y + h * 0.2, w * 0.6, h * 0.6),
            FlowNodeKind.InputOutput => (x + Math.Min(h * 0.3, w / 4), y, w - 2 * Math.Min(h * 0.3, w / 4), h),
            _ => (x + h / 4, y, w - h / 2, h),
        };
        if (node.Text.Length > 0)
        {
            shapes.Add(new PlacedShape(
                new DrawingShape(ShapeKind.TextBox, R(iw), R(ih), TextBlocks(node.Text, node, iw, ih)),
                R(ix), R(iy)));
        }
    }

    private static List<ShapePathStep> Polygon(params (double X, double Y)[] points)
    {
        var steps = new List<ShapePathStep> { new(ShapePathCommand.MoveTo, R(points[0].X), R(points[0].Y)) };
        steps.AddRange(points.Skip(1).Select(p => new ShapePathStep(ShapePathCommand.LineTo, R(p.X), R(p.Y))));
        steps.Add(new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero));
        return steps;
    }

    private static List<ShapePathStep> Pill(double w, double h)
    {
        var r = Math.Min(h / 2, w / 2);
        var k = Kappa * r;
        ShapePathStep Move(double x, double y) => new(ShapePathCommand.MoveTo, R(x), R(y));
        ShapePathStep Line(double x, double y) => new(ShapePathCommand.LineTo, R(x), R(y));
        ShapePathStep Cubic(double x1, double y1, double x2, double y2, double x, double y)
            => new(ShapePathCommand.CubicTo, R(x), R(y), R(x1), R(y1), R(x2), R(y2));
        return
        [
            Move(r, 0),
            Line(w - r, 0),
            Cubic(w - r + k, 0, w, r - k, w, r),
            Cubic(w, r + k, w - r + k, h, w - r, h),
            Line(r, h),
            Cubic(r - k, h, 0, r + k, 0, r),
            Cubic(0, r - k, r - k, 0, r, 0),
            new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero),
        ];
    }

    /// <summary>A centred paragraph in the box; the space above centres it vertically by an estimate of the wrapped height.</summary>
    private static List<Block> TextBlocks(string text, FlowNode node, double width, double height)
    {
        var size = node.FontSize.Points;
        var run = new TextRun(text) { FontSize = node.FontSize, Color = node.TextColor ?? new Color(0, 0, 0) };
        var lines = text.Length == 0 ? 1 : (int)Math.Ceiling(text.Length * size * CharWidth / Math.Max(size, width - 2 * Inset));
        var above = Math.Max(0, (height - lines * size * 1.2) / 2 - Inset / 2);
        var style = new ParagraphStyle { Alignment = TextAlignment.Center, SpaceBefore = R(above), SpaceAfter = Length.Zero };
        return [new Paragraph(run) { Style = style }];
    }

    private static Point Anchor(FlowNode node, FlowSide side)
    {
        var (x, y, w, h) = (node.X.Points, node.Y.Points, node.Width.Points, node.Height.Points);
        return side switch
        {
            FlowSide.Top => new Point(x + w / 2, y),
            FlowSide.Right => new Point(x + w, y + h / 2),
            FlowSide.Bottom => new Point(x + w / 2, y + h),
            _ => new Point(x, y + h / 2),
        };
    }

    private static Point Direction(FlowSide side) => side switch
    {
        FlowSide.Top => new Point(0, -1),
        FlowSide.Right => new Point(1, 0),
        FlowSide.Bottom => new Point(0, 1),
        _ => new Point(-1, 0),
    };

    /// <summary>The points of the path between the two anchors.</summary>
    /// <summary>
    /// The shapes of a diagram scaled by a factor below one: geometry, paths, outline widths, text sizes and paragraph
    /// spacing, so that a diagram shrunk to fit keeps its text inside the boxes (scaling only the group would leave the
    /// text at full size and push it out). The content is the one the layout made: centred paragraphs of plain runs.
    /// </summary>
    internal static IReadOnlyList<PlacedShape> Scale(IReadOnlyList<PlacedShape> shapes, double scale)
    {
        if (scale >= 1)
        {
            return shapes;
        }

        Length S(Length length) => Length.FromPoints(length.Points * scale);
        Block Text(Block block) => block is Paragraph paragraph
            ? new Paragraph(paragraph.Inlines.Select(inline => inline is TextRun run
                ? new TextRun(run.Text) { FontSize = run.FontSize is { } size ? S(size) : null, Color = run.Color, Bold = run.Bold, Italic = run.Italic }
                : inline))
            {
                Style = paragraph.Style with
                {
                    SpaceBefore = paragraph.Style.SpaceBefore is { } before ? S(before) : null,
                    SpaceAfter = paragraph.Style.SpaceAfter is { } after ? S(after) : null,
                },
            }
            : block;

        return shapes.Select(placed =>
        {
            var shape = placed.Shape;
            var scaled = new DrawingShape(
                shape.Kind, S(shape.Width), S(shape.Height),
                shape.Content?.Select(Text),
                shape.Path?.Select(step => new ShapePathStep(step.Command, S(step.X), S(step.Y), S(step.X1), S(step.Y1), S(step.X2), S(step.Y2))))
            {
                Fill = shape.Fill,
                Outline = shape.Outline,
                OutlineWidth = Length.FromPoints(Math.Max(0.1, shape.OutlineWidth.Points * scale)),
                Dash = shape.Dash,
                Rotation = shape.Rotation,
                AltText = shape.AltText,
            };
            return new PlacedShape(scaled, S(placed.X), S(placed.Y));
        }).ToList();
    }

    internal static List<(double X, double Y)> Route(FlowConnector connector, FlowNode from, FlowNode to)
    {
        var a = Anchor(from, connector.FromSide);
        var b = Anchor(to, connector.ToSide);
        if (connector.Route == FlowRoute.Straight)
        {
            return [(a.X, a.Y), (b.X, b.Y)];
        }

        var da = Direction(connector.FromSide);
        var db = Direction(connector.ToSide);
        var stub = Stub;
        var facing = da.X == -db.X && da.Y == -db.Y;
        if (facing)
        {
            var distance = (b.X - a.X) * da.X + (b.Y - a.Y) * da.Y;
            if (distance > 0)
            {
                stub = Math.Min(Stub, distance / 2);
            }
        }

        var p1 = new Point(a.X + da.X * stub, a.Y + da.Y * stub);
        var p3 = new Point(b.X + db.X * stub, b.Y + db.Y * stub);
        var points = new List<Point> { a, p1 };
        if (da.X != 0 && db.X != 0)
        {
            var mid = da.X == db.X ? (da.X > 0 ? Math.Max(p1.X, p3.X) : Math.Min(p1.X, p3.X)) : (p1.X + p3.X) / 2;
            points.Add(new Point(mid, p1.Y));
            points.Add(new Point(mid, p3.Y));
        }
        else if (da.Y != 0 && db.Y != 0)
        {
            var mid = da.Y == db.Y ? (da.Y > 0 ? Math.Max(p1.Y, p3.Y) : Math.Min(p1.Y, p3.Y)) : (p1.Y + p3.Y) / 2;
            points.Add(new Point(p1.X, mid));
            points.Add(new Point(p3.X, mid));
        }
        else if (da.X != 0)
        {
            points.Add(new Point(p3.X, p1.Y));
        }
        else
        {
            points.Add(new Point(p1.X, p3.Y));
        }

        points.Add(p3);
        points.Add(b);

        // Drop repeated points and points in the middle of a straight run.
        var result = new List<Point>();
        foreach (var point in points)
        {
            if (result.Count > 0 && Math.Abs(result[^1].X - point.X) < 0.001 && Math.Abs(result[^1].Y - point.Y) < 0.001)
            {
                continue;
            }

            while (result.Count >= 2 && Collinear(result[^2], result[^1], point))
            {
                result.RemoveAt(result.Count - 1);
            }

            result.Add(point);
        }

        return result.Select(p => (p.X, p.Y)).ToList();
    }

    private static bool Collinear(Point a, Point b, Point c)
        => (Math.Abs(a.X - b.X) < 0.001 && Math.Abs(b.X - c.X) < 0.001) || (Math.Abs(a.Y - b.Y) < 0.001 && Math.Abs(b.Y - c.Y) < 0.001);

    private static void AddConnector(List<PlacedShape> shapes, FlowConnector connector, FlowNode from, FlowNode to)
    {
        var route = Route(connector, from, to);
        var color = connector.Color ?? new Color(0, 0, 0);
        var line = route.Select(p => new Point(p.X, p.Y)).ToList();
        if (line.Count < 2)
        {
            return;
        }

        var triangles = new List<List<Point>>();
        if (connector.ArrowEnd)
        {
            triangles.Add(Arrow(line, atEnd: true));
        }

        if (connector.ArrowStart)
        {
            triangles.Add(Arrow(line, atEnd: false));
        }

        // The line stops at the base of an arrowhead.
        if (connector.ArrowEnd)
        {
            line[^1] = Shorten(line[^1], line[^2]);
        }

        if (connector.ArrowStart)
        {
            line[0] = Shorten(line[0], line[1]);
        }

        shapes.Add(Freeform(line, closed: false, fill: null, outline: color, connector.Dash));
        foreach (var triangle in triangles)
        {
            shapes.Add(Freeform(triangle, closed: true, fill: color, outline: null, ShapeDash.Solid));
        }

        if (!string.IsNullOrEmpty(connector.Label))
        {
            shapes.Add(Label(connector, route));
        }
    }

    private static Point Shorten(Point end, Point previous)
    {
        var dx = previous.X - end.X;
        var dy = previous.Y - end.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            return end;
        }

        var cut = Math.Min(ArrowLength, length * 0.8);
        return new Point(end.X + dx / length * cut, end.Y + dy / length * cut);
    }

    private static List<Point> Arrow(List<Point> line, bool atEnd)
    {
        var tip = atEnd ? line[^1] : line[0];
        var from = atEnd ? line[^2] : line[1];
        var dx = tip.X - from.X;
        var dy = tip.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001)
        {
            return [tip, tip, tip];
        }

        var (ux, uy) = (dx / length, dy / length);
        var length2 = Math.Min(ArrowLength, length);
        var baseX = tip.X - ux * length2;
        var baseY = tip.Y - uy * length2;
        return [tip, new Point(baseX - uy * ArrowHalfWidth, baseY + ux * ArrowHalfWidth), new Point(baseX + uy * ArrowHalfWidth, baseY - ux * ArrowHalfWidth)];
    }

    private static PlacedShape Freeform(List<Point> points, bool closed, Color? fill, Color? outline, ShapeDash dash)
    {
        const double Pad = 0.5;
        var minX = points.Min(p => p.X) - Pad;
        var minY = points.Min(p => p.Y) - Pad;
        var width = points.Max(p => p.X) + Pad - minX;
        var height = points.Max(p => p.Y) + Pad - minY;
        var steps = new List<ShapePathStep>();
        for (var i = 0; i < points.Count; i++)
        {
            steps.Add(new ShapePathStep(i == 0 ? ShapePathCommand.MoveTo : ShapePathCommand.LineTo, R(points[i].X - minX), R(points[i].Y - minY)));
        }

        if (closed)
        {
            steps.Add(new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero));
        }

        return new PlacedShape(
            new DrawingShape(ShapeKind.Freeform, R(width), R(height), path: steps) { Fill = fill, Outline = outline, Dash = dash },
            R(minX), R(minY));
    }

    private static PlacedShape Label(FlowConnector connector, List<(double X, double Y)> route)
    {
        var text = connector.Label!;
        var width = text.Length * LabelSize * CharWidth + 2 * Inset;
        var height = LabelSize * 1.2 + Inset;
        var (ax, ay) = route[0];
        var d = Direction(connector.FromSide);
        var left = d.X < 0 ? ax - 2 - width : ax + 2;
        var top = d.Y > 0 ? ay + 2 : ay - height - (d.Y < 0 ? 2 : 0);
        var run = new TextRun(text) { FontSize = L(LabelSize), Color = connector.Color ?? new Color(0, 0, 0) };
        var style = new ParagraphStyle { Alignment = d.X < 0 ? TextAlignment.Right : TextAlignment.Left, SpaceBefore = Length.Zero, SpaceAfter = Length.Zero };
        return new PlacedShape(
            new DrawingShape(ShapeKind.TextBox, R(width), R(height), [new Paragraph(run) { Style = style }]),
            R(Math.Max(0, left)), R(Math.Max(0, top)));
    }
}
