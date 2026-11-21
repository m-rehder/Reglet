using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>An <see cref="Equation"/> as Office Math (OMML, ADR-0031): <c>m:oMathPara</c> with one <c>m:oMath</c>.</summary>
internal static class DocxMath
{
    internal const string MathNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace M = MathNamespace;

    internal static void Write(XmlWriter writer, Equation equation, bool leadingEmptyRun = false)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        if (leadingEmptyRun)
        {
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "t", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteStartElement("m", "oMathPara", MathNamespace);
        writer.WriteStartElement("m", "oMathParaPr", MathNamespace);
        writer.WriteStartElement("m", "jc", MathNamespace);
        writer.WriteAttributeString("m", "val", MathNamespace, equation.Alignment switch
        {
            TextAlignment.Left => "left",
            TextAlignment.Right => "right",
            _ => "center",
        });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("m", "oMath", MathNamespace);
        WriteNode(writer, equation.Root);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Start(XmlWriter writer, string name) => writer.WriteStartElement("m", name, MathNamespace);

    private static void Val(XmlWriter writer, string name, string value)
    {
        Start(writer, name);
        writer.WriteAttributeString("m", "val", MathNamespace, value);
        writer.WriteEndElement();
    }

    private static void Argument(XmlWriter writer, string name, MathNode? node)
    {
        Start(writer, name);
        if (node is not null)
        {
            WriteNode(writer, node);
        }

        writer.WriteEndElement();
    }

    private static void WriteNode(XmlWriter writer, MathNode node)
    {
        switch (node)
        {
            case MathText text:
                Start(writer, "r");
                Start(writer, "rPr");
                Val(writer, "sty", text.Style == MathTextStyle.Italic ? "i" : "p");
                writer.WriteEndElement();
                Start(writer, "t");
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(XmlText.Clean(text.Text, InvalidXmlCharacterBehavior.Replace));
                writer.WriteEndElement();
                writer.WriteEndElement();
                break;
            case MathRow row:
                foreach (var item in row.Items)
                {
                    WriteNode(writer, item);
                }

                break;
            case MathFraction fraction:
                Start(writer, "f");
                Argument(writer, "num", fraction.Numerator);
                Argument(writer, "den", fraction.Denominator);
                writer.WriteEndElement();
                break;
            case MathScript script:
            {
                var name = script is { Superscript: not null, Subscript: not null } ? "sSubSup" : script.Superscript is not null ? "sSup" : "sSub";
                Start(writer, name);
                Argument(writer, "e", script.Base);
                if (script.Subscript is not null)
                {
                    Argument(writer, "sub", script.Subscript);
                }

                if (script.Superscript is not null)
                {
                    Argument(writer, "sup", script.Superscript);
                }

                writer.WriteEndElement();
                break;
            }

            case MathRadical radical:
                Start(writer, "rad");
                Start(writer, "radPr");
                if (radical.Degree is null)
                {
                    Val(writer, "degHide", "1");
                }

                writer.WriteEndElement();
                Argument(writer, "deg", radical.Degree);
                Argument(writer, "e", radical.Radicand);
                writer.WriteEndElement();
                break;
            case MathNary nary:
                Start(writer, "nary");
                Start(writer, "naryPr");
                Val(writer, "chr", nary.Operator.ToString());
                Val(writer, "limLoc", nary.LimitsAbove ? "undOvr" : "subSup");
                if (nary.Lower is null)
                {
                    Val(writer, "subHide", "1");
                }

                if (nary.Upper is null)
                {
                    Val(writer, "supHide", "1");
                }

                writer.WriteEndElement();
                Argument(writer, "sub", nary.Lower);
                Argument(writer, "sup", nary.Upper);
                Argument(writer, "e", nary.Body);
                writer.WriteEndElement();
                break;
            case MathDelimiter delimiter:
                Start(writer, "d");
                Start(writer, "dPr");
                Val(writer, "begChr", delimiter.Open?.ToString() ?? string.Empty);
                Val(writer, "sepChr", ",");
                Val(writer, "endChr", delimiter.Close?.ToString() ?? string.Empty);
                writer.WriteEndElement();
                foreach (var item in delimiter.Items)
                {
                    Argument(writer, "e", item);
                }

                writer.WriteEndElement();
                break;
        }
    }

    /// <summary>Reads an <c>m:oMathPara</c> or <c>m:oMath</c>; null (with a warning) when it holds nothing readable.</summary>
    internal static Equation? Read(XElement element, Action<string> warn)
    {
        var math = element.Name == M + "oMath" ? element : element.Element(M + "oMath");
        if (math is null)
        {
            return null;
        }

        var alignment = (string?)element.Element(M + "oMathParaPr")?.Element(M + "jc")?.Attribute(M + "val");
        var reader = new Reader(warn);
        try
        {
            return new Equation(reader.Content(math))
            {
                Alignment = alignment switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, _ => null },
            };
        }
        catch (ArgumentException)
        {
            // Too large for the model: this formula is left out, the rest of the document stays readable.
            warn($"DOCX: a formula has more than {MathNode.MaxNodes} nodes or {MathNode.MaxDepth} levels and is not mapped.");
            return null;
        }
    }

    private sealed class Reader(Action<string> warn)
    {
        private int _count;

        internal MathNode Content(XElement parent)
        {
            var items = new List<MathNode>();
            foreach (var child in parent.Elements())
            {
                if (++_count > MathNode.MaxNodes)
                {
                    warn("DOCX: an equation is too large and is cut off when reading.");
                    break;
                }

                if (Node(child) is { } node)
                {
                    items.Add(node);
                }
            }

            return items.Count switch
            {
                0 => new MathText(" ", MathTextStyle.Normal),
                1 => items[0],
                _ => new MathRow(items),
            };
        }

        private MathNode? Node(XElement e)
        {
            var name = e.Name.LocalName;
            if (e.Name.Namespace != M)
            {
                return null;
            }

            switch (name)
            {
                case "r":
                {
                    var text = string.Concat(e.Elements(M + "t").Select(t => t.Value));
                    if (text.Length == 0)
                    {
                        return null;
                    }

                    var sty = (string?)e.Element(M + "rPr")?.Element(M + "sty")?.Attribute(M + "val");
                    var normal = e.Element(M + "rPr")?.Element(M + "nor") is not null;
                    MathTextStyle? style = normal || sty == "p" || sty == "b" ? MathTextStyle.Normal : sty is "i" or "bi" ? MathTextStyle.Italic : null;
                    return new MathText(text, style);
                }

                case "f":
                    return new MathFraction(Arg(e, "num"), Arg(e, "den"));
                case "sSup":
                    return new MathScript(Arg(e, "e"), superscript: Arg(e, "sup"));
                case "sSub":
                    return new MathScript(Arg(e, "e"), subscript: Arg(e, "sub"));
                case "sSubSup":
                    return new MathScript(Arg(e, "e"), Arg(e, "sup"), Arg(e, "sub"));
                case "rad":
                    return new MathRadical(Arg(e, "e"), Flag(e.Element(M + "radPr"), "degHide") ? null : Arg(e, "deg"));
                case "nary":
                {
                    var pr = e.Element(M + "naryPr");
                    var chr = (string?)pr?.Element(M + "chr")?.Attribute(M + "val") ?? "∫";
                    var op = chr.Length == 1 && MathNary.Operators.Contains(chr[0], StringComparison.Ordinal) ? chr[0] : '∫';
                    if (op.ToString() != chr)
                    {
                        warn($"DOCX: the n-ary operator '{chr}' is read as an integral sign.");
                    }

                    return new MathNary(
                        op, Arg(e, "e"),
                        Flag(pr, "subHide") ? null : Arg(e, "sub"),
                        Flag(pr, "supHide") ? null : Arg(e, "sup"));
                }

                case "d":
                {
                    var pr = e.Element(M + "dPr");
                    var open = Bracket(pr, "begChr", '(');
                    var close = Bracket(pr, "endChr", ')');
                    if (((string?)pr?.Element(M + "sepChr")?.Attribute(M + "val") ?? "|") != ",")
                    {
                        warn("DOCX: a separator other than a comma in brackets is read as a comma.");
                    }

                    var items = e.Elements(M + "e").Select(Content).ToList();
                    return new MathDelimiter(open, close, items.Count == 0 ? [new MathText(" ", MathTextStyle.Normal)] : items);
                }

                case "func":
                {
                    var function = Content(e.Element(M + "fName") ?? new XElement(M + "fName"));
                    return new MathRow([function, Arg(e, "e")]);
                }

                case "oMathParaPr" or "ctrlPr" or "rPr" or "fPr" or "radPr" or "naryPr" or "dPr":
                    return null;
                default:
                {
                    warn($"DOCX: the equation element 'm:{name}' is not mapped when reading; its text is kept.");
                    var text = string.Concat(e.Descendants(M + "t").Select(t => t.Value));
                    return text.Length == 0 ? null : new MathText(text, MathTextStyle.Normal);
                }
            }
        }

        private MathNode Arg(XElement parent, string name)
            => Content(parent.Element(M + name) ?? new XElement(M + name));

        private static bool Flag(XElement? properties, string name)
        {
            var value = (string?)properties?.Element(M + name)?.Attribute(M + "val");
            return value is "1" or "true" or "on" || (properties?.Element(M + name) is not null && value is null);
        }

        private static char? Bracket(XElement? properties, string name, char fallback)
        {
            var element = properties?.Element(M + name);
            if (element is null)
            {
                return fallback;
            }

            var value = (string?)element.Attribute(M + "val") ?? string.Empty;
            return value.Length == 1 && MathDelimiter.Brackets.Contains(value[0], StringComparison.Ordinal) ? value[0] : null;
        }
    }
}
