namespace Reglet;

/// <summary>A thing a laid-out formula consists of; positions are measured from the left edge, upward from the baseline of the formula.</summary>
internal abstract record MathItem;

/// <summary>A run of text with its baseline start point and size.</summary>
internal sealed record MathGlyphs(string Text, bool Italic, double X, double Y, double Size) : MathItem;

/// <summary>A filled bar (fraction bar, root bar): left edge, bottom edge, width and thickness.</summary>
internal sealed record MathRule(double X, double Y, double Width, double Thickness) : MathItem;

/// <summary>An open stroked path (the hook of a root sign).</summary>
internal sealed record MathPath(IReadOnlyList<(double X, double Y)> Points, double Thickness) : MathItem;

/// <summary>A laid-out part: its size and what to draw, relative to its own left edge and baseline.</summary>
internal sealed class MathBox
{
    internal double Width;
    internal double Ascent;
    internal double Descent;
    internal List<MathItem> Items = [];

    internal double Height => Ascent + Descent;

    internal void Add(MathBox child, double x, double y)
    {
        foreach (var item in child.Items)
        {
            Items.Add(item switch
            {
                MathGlyphs g => g with { X = g.X + x, Y = g.Y + y },
                MathRule r => r with { X = r.X + x, Y = r.Y + y },
                MathPath p => p with { Points = [.. p.Points.Select(point => (point.X + x, point.Y + y))] },
                _ => item,
            });
        }

        Ascent = Math.Max(Ascent, child.Ascent + y);
        Descent = Math.Max(Descent, child.Descent - y);
        Width = Math.Max(Width, x + child.Width);
    }
}

/// <summary>
/// A simple TeX-like layout of a formula (ADR-0031). The width of text comes from the caller, so the PDF can use the
/// metrics of the document font and the ODT writer an estimate for the frame size.
/// </summary>
internal sealed class MathLayout(Func<string, bool, double, double> measure)
{
    private const string BinaryOperators = "+-−=±×÷<>≤≥≠≈·∓∗";

    /// <summary>The estimate of the text width used where no font is at hand (ODT frame size).</summary>
    internal static double EstimateWidth(string text, bool italic, double size)
    {
        _ = italic;
        return text.Sum(c => char.IsDigit(c) || c == ' ' ? 0.5 : char.IsLetter(c) ? 0.55 : BinaryOperators.Contains(c, StringComparison.Ordinal) ? 0.6 : 0.4) * size;
    }

    internal MathBox Layout(MathNode node, double size)
    {
        switch (node)
        {
            case MathText { Text: "−" or "-" }:
            {
                // The minus sign is drawn as a bar: the standard fonts have no U+2212.
                var box = new MathBox { Width = 0.55 * size, Ascent = 0.75 * size, Descent = 0.25 * size };
                box.Items.Add(new MathRule(0.05 * size, 0.25 * size - 0.03 * size, 0.45 * size, 0.06 * size));
                return box;
            }

            case MathText text:
            {
                var italic = text.Style == MathTextStyle.Italic;
                var box = new MathBox { Width = measure(text.Text, italic, size), Ascent = 0.75 * size, Descent = 0.25 * size };
                box.Items.Add(new MathGlyphs(text.Text, italic, 0, 0, size));
                return box;
            }

            case MathRow row:
                return Sequence(row.Items, size);
            case MathFraction fraction:
            {
                var num = Layout(fraction.Numerator, size);
                var den = Layout(fraction.Denominator, size);
                const double Axis = 0.25;
                var thickness = 0.05 * size;
                var gap = 0.15 * size;
                var width = Math.Max(num.Width, den.Width) + 0.2 * size;
                var box = new MathBox { Width = width };
                box.Items.Add(new MathRule(0, Axis * size - thickness / 2, width, thickness));
                box.Add(num, (width - num.Width) / 2, Axis * size + thickness / 2 + gap + num.Descent);
                box.Add(den, (width - den.Width) / 2, Axis * size - thickness / 2 - gap - den.Ascent);
                box.Ascent = Math.Max(box.Ascent, Axis * size + thickness);
                return box;
            }

            case MathScript script:
            {
                var box = Layout(script.Base, size);
                return Scripts(box, script.Subscript, script.Superscript, size, box.Width);
            }

            case MathRadical radical:
                return Radical(radical, size);
            case MathNary nary:
                return Nary(nary, size);
            case MathDelimiter delimiter:
                return Delimited(delimiter, size);
            default:
                throw new ArgumentException("Unknown math node.", nameof(node));
        }
    }

    private MathBox Sequence(IReadOnlyList<MathNode> items, double size)
    {
        var box = new MathBox();
        double x = 0;
        foreach (var item in items)
        {
            var child = Layout(item, size);
            var isOperator = item is MathText { Text.Length: 1 } text && BinaryOperators.Contains(text.Text[0], StringComparison.Ordinal);
            if (isOperator)
            {
                x += 0.2 * size;
            }

            box.Add(child, x, 0);
            x += child.Width + (isOperator ? 0.2 * size : 0);
        }

        box.Width = x;
        return box;
    }

    private MathBox Scripts(MathBox box, MathNode? subscript, MathNode? superscript, double size, double x)
    {
        var small = size * 0.7;
        var sup = superscript is null ? null : Layout(superscript, small);
        var sub = subscript is null ? null : Layout(subscript, small);
        var supShift = Math.Max(0.4 * size, box.Ascent - 0.45 * small);
        var subShift = Math.Max(0.22 * size, box.Descent + 0.1 * size);
        if (sup is not null && sub is not null)
        {
            var gap = (supShift - sup.Descent) - (-subShift + sub.Ascent);
            if (gap < 0.15 * size)
            {
                subShift += 0.15 * size - gap;
            }
        }

        var width = x;
        if (sup is not null)
        {
            box.Add(sup, x, supShift);
            width = Math.Max(width, x + sup.Width);
        }

        if (sub is not null)
        {
            box.Add(sub, x, -subShift);
            width = Math.Max(width, x + sub.Width);
        }

        box.Width = width + 0.05 * size;
        return box;
    }

    private MathBox Radical(MathRadical radical, double size)
    {
        var body = Layout(radical.Radicand, size);
        var degree = radical.Degree is null ? null : Layout(radical.Degree, size * 0.5);
        var top = body.Ascent + 0.15 * size;
        var bottom = -body.Descent;
        var lead = degree is null ? 0 : Math.Max(0, degree.Width - 0.3 * size);
        var thickness = 0.05 * size;
        var box = new MathBox();
        var hook = new List<(double, double)>
        {
            (lead, bottom + 0.45 * (top - bottom)),
            (lead + 0.15 * size, bottom + 0.4 * (top - bottom)),
            (lead + 0.3 * size, bottom),
            (lead + 0.55 * size, top),
        };
        box.Items.Add(new MathPath(hook, thickness));
        box.Items.Add(new MathRule(lead + 0.55 * size, top - thickness, body.Width + 0.15 * size, thickness));
        box.Add(body, lead + 0.6 * size, 0);
        if (degree is not null)
        {
            box.Add(degree, 0, bottom + 0.6 * (top - bottom));
        }

        box.Ascent = Math.Max(box.Ascent, top);
        box.Width = lead + 0.6 * size + body.Width + 0.15 * size;
        return box;
    }

    private MathBox Nary(MathNary nary, double size)
    {
        var opBox = OperatorShape(nary.Operator, size);

        MathBox head;
        if (nary.LimitsAbove)
        {
            var small = size * 0.7;
            var upper = nary.Upper is null ? null : Layout(nary.Upper, small);
            var lower = nary.Lower is null ? null : Layout(nary.Lower, small);
            var width = Math.Max(opBox.Width, Math.Max(upper?.Width ?? 0, lower?.Width ?? 0));
            head = new MathBox { Width = width };
            head.Add(opBox, (width - opBox.Width) / 2, 0);
            if (upper is not null)
            {
                head.Add(upper, (width - upper.Width) / 2, opBox.Ascent + 0.1 * size + upper.Descent);
            }

            if (lower is not null)
            {
                head.Add(lower, (width - lower.Width) / 2, -opBox.Descent - 0.1 * size - lower.Ascent);
            }
        }
        else
        {
            head = Scripts(opBox, nary.Lower, nary.Upper, size, opBox.Width);
            head.Width += 0.1 * size;
        }

        var body = Layout(nary.Body, size);
        var box = new MathBox();
        box.Add(head, 0, 0);
        box.Add(body, head.Width + 0.15 * size, 0);
        return box;
    }

    /// <summary>The big operator as vector strokes (no math font is needed), centred on the formula axis.</summary>
    private static MathBox OperatorShape(char op, double size)
    {
        var integral = op is '∫' or '∬' or '∭' or '∮';
        var count = op switch { '∬' => 2, '∭' => 3, _ => 1 };
        double w = integral ? 0.55 * size : 0.9 * size, h = integral ? 1.9 * size : 1.25 * size;
        var thickness = (integral ? 0.075 : 0.07) * size;
        var bottom = 0.25 * size - h / 2;
        var box = new MathBox { Width = w + (count - 1) * 0.35 * w, Ascent = bottom + h, Descent = -bottom };

        void Line(double dx, params (double X, double Y)[] points)
            => box.Items.Add(new MathPath([.. points.Select(p => (dx + p.X * w, bottom + p.Y * h))], thickness));

        switch (op)
        {
            case '∑':
                Line(0, (1, 0.9), (1, 1), (0, 1), (0.55, 0.5), (0, 0), (1, 0), (1, 0.1));
                break;
            case '∏':
                Line(0, (0, 1), (1, 1));
                Line(0, (0.15, 1), (0.15, 0));
                Line(0, (0.85, 1), (0.85, 0));
                break;
            case '⋃':
                Line(0, (0, 1), (0, 0.4), (0.1, 0.15), (0.3, 0.02), (0.5, 0), (0.7, 0.02), (0.9, 0.15), (1, 0.4), (1, 1));
                break;
            case '⋂':
                Line(0, (0, 0), (0, 0.6), (0.1, 0.85), (0.3, 0.98), (0.5, 1), (0.7, 0.98), (0.9, 0.85), (1, 0.6), (1, 0));
                break;
            default:
                for (var i = 0; i < count; i++)
                {
                    Line(
                        i * 0.35 * w, (0.88, 0.88), (0.78, 0.98), (0.65, 0.97), (0.57, 0.85), (0.5, 0.55), (0.43, 0.15), (0.35, 0.03), (0.22, 0.02), (0.12, 0.12));
                }

                if (op == '∮')
                {
                    Line(0, [.. Enumerable.Range(0, 13).Select(k => (0.5 + 0.45 * Math.Cos(k * Math.PI / 6), 0.45 + 0.1 * Math.Sin(k * Math.PI / 6) * 1.8))]);
                }

                break;
        }

        return box;
    }

    private static double BracketWidth(char bracket) => bracket switch
    {
        '|' => 0.2,
        '‖' => 0.3,
        '⌊' or '⌋' or '⌈' or '⌉' or '⟨' or '⟩' => 0.4,
        _ => 0.35,
    };

    private MathBox Delimited(MathDelimiter delimiter, double size)
    {
        var content = new MathBox();
        double x = 0;
        for (var i = 0; i < delimiter.Items.Count; i++)
        {
            if (i > 0)
            {
                var comma = Layout(new MathText(",", MathTextStyle.Normal), size);
                content.Add(comma, x, 0);
                x += comma.Width + 0.15 * size;
            }

            var child = Layout(delimiter.Items[i], size);
            content.Add(child, x, 0);
            x += child.Width;
        }

        content.Width = x;
        var glyph = Math.Max(size, content.Height);
        var center = (content.Ascent - content.Descent) / 2;
        var box = new MathBox();
        double left = 0;
        if (delimiter.Open is { } open)
        {
            box.Items.Add(new MathGlyphs(open.ToString(), false, 0, center - 0.25 * glyph, glyph));
            left = BracketWidth(open) * glyph;
            box.Width = left;
        }

        box.Add(content, left, 0);
        box.Width = left + content.Width;
        if (delimiter.Close is { } close)
        {
            box.Items.Add(new MathGlyphs(close.ToString(), false, box.Width, center - 0.25 * glyph, glyph));
            box.Width += BracketWidth(close) * glyph;
        }

        box.Ascent = Math.Max(box.Ascent, center + 0.5 * glyph);
        box.Descent = Math.Max(box.Descent, 0.5 * glyph - center);
        return box;
    }
}
