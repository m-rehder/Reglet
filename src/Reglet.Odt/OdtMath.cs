using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// An <see cref="Equation"/> as an ODF formula object (ADR-0031): a frame with <c>draw:object</c> that points at the
/// sub document <c>Object N</c> holding MathML.
/// </summary>
internal static class OdtMath
{
    internal const string FormulaMediaType = "application/vnd.oasis.opendocument.formula";
    internal const string MathMlNamespace = "http://www.w3.org/1998/Math/MathML";
    private const double BaseSize = 12;
    private static readonly XNamespace Ml = MathMlNamespace;

    /// <summary>The equations of the body in document order with their object names; numbered after charts and objects.</summary>
    internal static Dictionary<Equation, string> Collect(PortableDocument document, int firstNumber)
    {
        var names = new Dictionary<Equation, string>(ReferenceEqualityComparer.Instance);
        var number = firstNumber;
        foreach (var equation in document.Sections.SelectMany(section => OdtCharts.Enumerate<Equation>(section.Blocks)))
        {
            names[equation] = "Object " + number++.ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }

    internal static bool Has(IEnumerable<Block> blocks) => OdtCharts.Enumerate<Equation>(blocks).Any();

    internal static void WriteParts(OdtPackageWriter package, IReadOnlyDictionary<Equation, string> equations)
    {
        foreach (var (equation, name) in equations)
        {
            package.AddDirectoryEntry(name, FormulaMediaType);
            package.AddXmlPart(name + "/content.xml", writer =>
            {
                writer.WriteStartElement("math", "math", MathMlNamespace);
                writer.WriteStartElement("math", "semantics", MathMlNamespace);
                WriteNode(writer, equation.Root);
                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }
    }

    private static string Cm(double points) => (points / 72 * 2.54).ToString("0.####", CultureInfo.InvariantCulture) + "cm";

    internal static void WritePlacement(XmlWriter writer, Equation equation, string objectName)
    {
        var box = new MathLayout(MathLayout.EstimateWidth).Layout(equation.Root, BaseSize);
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, AlignmentStyle(equation.Alignment));
        writer.WriteStartElement("draw", "frame", OdtNamespaces.Draw);
        writer.WriteAttributeString("draw", "name", OdtNamespaces.Draw, "Equation " + objectName["Object ".Length..]);
        writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, "as-char");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Cm(Math.Max(1, box.Width)));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Cm(Math.Max(1, box.Height)));
        writer.WriteStartElement("draw", "object", OdtNamespaces.Draw);
        writer.WriteAttributeString("xlink", "href", OdtNamespaces.XLink, "./" + objectName);
        writer.WriteAttributeString("xlink", "type", OdtNamespaces.XLink, "simple");
        writer.WriteAttributeString("xlink", "show", OdtNamespaces.XLink, "embed");
        writer.WriteAttributeString("xlink", "actuate", OdtNamespaces.XLink, "onLoad");
        writer.WriteEndElement();
        if (!string.IsNullOrEmpty(equation.AltText))
        {
            writer.WriteElementString("svg", "title", OdtNamespaces.Svg, XmlText.Clean(equation.AltText, InvalidXmlCharacterBehavior.Replace));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>The paragraph style that aligns an equation (centred unless it asks for left or right).</summary>
    internal static string AlignmentStyle(TextAlignment? alignment) => alignment switch
    {
        TextAlignment.Left => "MathLeft",
        TextAlignment.Right => "MathRight",
        _ => "MathCenter",
    };

    /// <summary>The alignment an equation paragraph style stands for; null for any other style (centred).</summary>
    internal static TextAlignment? AlignmentOf(string? styleName) => styleName switch
    {
        "MathLeft" => TextAlignment.Left,
        "MathRight" => TextAlignment.Right,
        _ => null,
    };

    /// <summary>The three automatic paragraph styles of equation paragraphs.</summary>
    internal static void WriteAlignmentStyles(XmlWriter writer)
    {
        foreach (var (name, align) in new[] { ("MathLeft", "start"), ("MathCenter", "center"), ("MathRight", "end") })
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "text-align", OdtNamespaces.Fo, align);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static void Leaf(XmlWriter writer, string name, string text, string? variant = null)
    {
        writer.WriteStartElement("math", name, MathMlNamespace);
        if (variant is not null)
        {
            writer.WriteAttributeString("mathvariant", variant);
        }

        writer.WriteString(XmlText.Clean(text, InvalidXmlCharacterBehavior.Replace));
        writer.WriteEndElement();
    }

    private static void Wrapped(XmlWriter writer, string name, params MathNode?[] children)
    {
        writer.WriteStartElement("math", name, MathMlNamespace);
        foreach (var child in children)
        {
            if (child is not null)
            {
                WriteNode(writer, child);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteNode(XmlWriter writer, MathNode node)
    {
        switch (node)
        {
            case MathText text when text.Style == MathTextStyle.Italic:
                Leaf(writer, "mi", text.Text, "italic");
                break;
            case MathText text when text.Text.All(c => char.IsDigit(c) || c == '.'):
                Leaf(writer, "mn", text.Text);
                break;
            case MathText text when text.Text.All(char.IsLetter):
                Leaf(writer, "mi", text.Text, "normal");
                break;
            case MathText text:
                Leaf(writer, text.Text.Length == 1 ? "mo" : "mtext", text.Text);
                break;
            case MathRow row:
                Wrapped(writer, "mrow", [.. row.Items]);
                break;
            case MathFraction fraction:
                Wrapped(writer, "mfrac", fraction.Numerator, fraction.Denominator);
                break;
            case MathScript script:
                Wrapped(
                    writer,
                    script is { Superscript: not null, Subscript: not null } ? "msubsup" : script.Superscript is not null ? "msup" : "msub",
                    script.Base, script.Subscript, script.Superscript);
                break;
            case MathRadical { Degree: null } radical:
                Wrapped(writer, "msqrt", radical.Radicand);
                break;
            case MathRadical radical:
                Wrapped(writer, "mroot", radical.Radicand, radical.Degree);
                break;
            case MathNary nary:
            {
                writer.WriteStartElement("math", "mrow", MathMlNamespace);
                var name = (nary.LimitsAbove, nary.Lower is not null, nary.Upper is not null) switch
                {
                    (_, false, false) => null,
                    (true, true, true) => "munderover",
                    (true, true, false) => "munder",
                    (true, false, true) => "mover",
                    (false, true, true) => "msubsup",
                    (false, true, false) => "msub",
                    _ => "msup",
                };
                if (name is null)
                {
                    Leaf(writer, "mo", nary.Operator.ToString());
                }
                else
                {
                    writer.WriteStartElement("math", name, MathMlNamespace);
                    writer.WriteStartElement("math", "mo", MathMlNamespace);
                    writer.WriteAttributeString("stretchy", "false");
                    writer.WriteString(nary.Operator.ToString());
                    writer.WriteEndElement();
                    foreach (var limit in new[] { nary.Lower, nary.Upper })
                    {
                        if (limit is not null)
                        {
                            WriteNode(writer, limit);
                        }
                    }

                    writer.WriteEndElement();
                }

                WriteNode(writer, nary.Body);
                writer.WriteEndElement();
                break;
            }

            case MathDelimiter delimiter:
                writer.WriteStartElement("math", "mfenced", MathMlNamespace);
                writer.WriteAttributeString("open", delimiter.Open?.ToString() ?? string.Empty);
                writer.WriteAttributeString("close", delimiter.Close?.ToString() ?? string.Empty);
                writer.WriteAttributeString("separators", ",");
                foreach (var item in delimiter.Items)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndElement();
                break;
        }
    }

    /// <summary>Reads the root of a formula part; null when it is not MathML.</summary>
    internal static Equation? Read(XElement root, string? altText, Action<string> warn)
    {
        if (root.Name != Ml + "math")
        {
            return null;
        }

        var holder = root.Element(Ml + "semantics") ?? root;
        var reader = new Reader(warn);
        try
        {
            return new Equation(reader.Content(holder.Elements().Where(e => e.Name != Ml + "annotation" && e.Name != Ml + "annotation-xml")))
            {
                AltText = altText,
            };
        }
        catch (ArgumentException)
        {
            // Too large for the model: this formula is left out, the rest of the document stays readable.
            warn($"ODT: a formula has more than {MathNode.MaxNodes} nodes or {MathNode.MaxDepth} levels and is not mapped.");
            return null;
        }
    }

    private sealed class Reader(Action<string> warn)
    {
        private int _count;

        internal MathNode Content(IEnumerable<XElement> elements)
        {
            var list = elements.ToList();
            var items = new List<MathNode>();
            for (var i = 0; i < list.Count; i++)
            {
                if (++_count > MathNode.MaxNodes)
                {
                    warn("ODT: a formula is too large and is cut off when reading.");
                    break;
                }

                var e = list[i];
                if (NaryHead(e) is { } head)
                {
                    var body = i + 1 < list.Count ? Node(list[++i]) ?? Empty() : Empty();
                    items.Add(new MathNary(head.Operator, body, head.Lower, head.Upper));
                    continue;
                }

                if (Node(e) is { } node)
                {
                    items.Add(node);
                }
            }

            return items.Count switch { 0 => Empty(), 1 => items[0], _ => new MathRow(items) };
        }

        private static MathNode Empty() => new MathText(" ", MathTextStyle.Normal);

        private (char Operator, MathNode? Lower, MathNode? Upper)? NaryHead(XElement e)
        {
            var kind = e.Name.LocalName;
            if (e.Name.Namespace != Ml || kind is not ("munderover" or "munder" or "mover" or "msubsup" or "msub" or "msup")
                || e.Elements().FirstOrDefault() is not { } first || first.Name != Ml + "mo"
                || first.Value.Length != 1 || !MathNary.Operators.Contains(first.Value[0], StringComparison.Ordinal))
            {
                return null;
            }

            var rest = e.Elements().Skip(1).ToList();
            MathNode? Arg(int index) => index < rest.Count ? Node(rest[index]) : null;
            return kind switch
            {
                "munderover" or "msubsup" => (first.Value[0], Arg(0), Arg(1)),
                "munder" or "msub" => (first.Value[0], Arg(0), null),
                _ => (first.Value[0], null, Arg(0)),
            };
        }

        private MathNode Child(XElement parent, int index)
            => parent.Elements().ElementAtOrDefault(index) is { } e ? Node(e) ?? Empty() : Empty();

        private MathNode? Node(XElement e)
        {
            if (e.Name.Namespace != Ml)
            {
                return null;
            }

            var name = e.Name.LocalName;
            switch (name)
            {
                case "mi":
                {
                    var variant = (string?)e.Attribute("mathvariant");
                    return e.Value.Length == 0 ? null : new MathText(e.Value, variant == "normal" ? MathTextStyle.Normal : variant == "italic" ? MathTextStyle.Italic : e.Value.Length == 1 ? MathTextStyle.Italic : MathTextStyle.Normal);
                }

                case "mn" or "mo" or "mtext":
                    return e.Value.Length == 0 ? null : new MathText(e.Value, MathTextStyle.Normal);
                case "mrow" or "mstyle" or "mpadded":
                    return Group(e);
                case "mfrac":
                    return new MathFraction(Child(e, 0), Child(e, 1));
                case "msup":
                    return new MathScript(Child(e, 0), superscript: Child(e, 1));
                case "msub":
                    return new MathScript(Child(e, 0), subscript: Child(e, 1));
                case "msubsup":
                    return new MathScript(Child(e, 0), Child(e, 2), Child(e, 1));
                case "msqrt":
                    return new MathRadical(Content(e.Elements()));
                case "mroot":
                    return new MathRadical(Child(e, 0), Child(e, 1));
                case "mfenced":
                {
                    var open = Bracket((string?)e.Attribute("open") ?? "(");
                    var close = Bracket((string?)e.Attribute("close") ?? ")");
                    var items = e.Elements().Select(child => Node(child) ?? Empty()).ToList();
                    return new MathDelimiter(open, close, items.Count == 0 ? [Empty()] : items);
                }

                case "semantics":
                    return Content(e.Elements().Where(x => x.Name != Ml + "annotation" && x.Name != Ml + "annotation-xml"));
                case "annotation" or "annotation-xml" or "mspace":
                    return null;
                default:
                {
                    warn($"ODT: the formula element 'math:{name}' is not mapped when reading; its text is kept.");
                    return e.Value.Length == 0 ? null : new MathText(e.Value, MathTextStyle.Normal);
                }
            }
        }

        private static char? Bracket(string value)
            => value.Length == 1 && MathDelimiter.Brackets.Contains(value[0], StringComparison.Ordinal) ? value[0] : null;

        /// <summary>An <c>mrow</c>; one that starts and ends with matching bracket operators is a delimiter.</summary>
        private MathNode Group(XElement row)
        {
            var children = row.Elements().ToList();
            if (children.Count >= 2 && children[0].Name == Ml + "mo" && children[^1].Name == Ml + "mo"
                && Bracket(children[0].Value) is { } open && Bracket(children[^1].Value) is { } close
                && "([{⌊⌈⟨|‖".Contains(open, StringComparison.Ordinal) && ")]}⌋⌉⟩|‖".Contains(close, StringComparison.Ordinal))
            {
                var items = new List<List<XElement>> { new() };
                foreach (var inner in children.Skip(1).Take(children.Count - 2))
                {
                    if (inner.Name == Ml + "mo" && inner.Value == ",")
                    {
                        items.Add([]);
                    }
                    else
                    {
                        items[^1].Add(inner);
                    }
                }

                return new MathDelimiter(open, close, items.Select(Content));
            }

            return Content(children);
        }
    }
}
