using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>A node of the tree the tolerant HTML parser builds: an element (Name set) or a text.</summary>
internal sealed class HtmlNode
{
    internal string? Name { get; init; }

    internal string Text { get; set; } = string.Empty;

    internal Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    internal List<HtmlNode> Children { get; } = [];

    internal HtmlNode? Parent { get; init; }

    internal bool IsText => Name is null;

    internal string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;

    internal string TextContent()
    {
        var builder = new StringBuilder();
        var stack = new Stack<HtmlNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsText)
            {
                builder.Append(node.Text);
            }
            else
            {
                for (var i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(node.Children[i]);
                }
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// A tolerant HTML tokenizer and tree builder (ADR-0032): implied end tags for the elements that have them, void
/// elements, raw text elements, character references. It builds the tree without recursion and never reads anything
/// from outside the string.
/// </summary>
internal static class HtmlParser
{
    /// <summary>The deepest nesting of the tree; deeper elements are attached to the deepest one.</summary>
    internal const int MaxDepth = 256;

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "textarea", "title" };

    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "p", "div", "ul", "ol", "dl", "table", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "hr", "form", "fieldset",
        "section", "article", "aside", "header", "footer", "nav", "main", "figure", "address", "center", "details",
    };

    private static readonly HashSet<string> ParagraphBoundary = new(StringComparer.Ordinal)
    {
        "td", "th", "table", "li", "body", "html", "blockquote", "div", "section", "article", "aside", "header", "footer", "nav",
        "main", "figure", "center", "details", "form", "fieldset", "dd", "dt", "caption",
    };

    internal static HtmlNode Parse(string html, int maxNodes = 5_000_000)
    {
        var root = new HtmlNode { Name = "#root" };
        var open = new List<HtmlNode> { root };
        var nodes = 0;
        var i = 0;
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0)
            {
                return;
            }

            open[^1].Children.Add(new HtmlNode { Text = DecodeReferences(text.ToString()), Parent = open[^1] });
            text.Clear();
        }

        while (i < html.Length)
        {
            if (++nodes > maxNodes)
            {
                throw new DocumentValidationException("The HTML document has too many elements.", "input");
            }

            var c = html[i];
            if (c != '<')
            {
                var next = html.IndexOf('<', i);
                if (next < 0)
                {
                    next = html.Length;
                }

                text.Append(html, i, next - i);
                i = next;
                continue;
            }

            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (i + 1 < html.Length && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                var end = html.IndexOf('>', i);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            if (i + 1 < html.Length && html[i + 1] == '/')
            {
                var j = i + 2;
                var nameStart = j;
                while (j < html.Length && (char.IsLetterOrDigit(html[j]) || html[j] == '-' || html[j] == ':'))
                {
                    j++;
                }

                if (j == nameStart)
                {
                    text.Append('<');
                    i++;
                    continue;
                }

                var name = html[nameStart..j].ToLowerInvariant();
                var close = html.IndexOf('>', j);
                i = close < 0 ? html.Length : close + 1;
                FlushText();
                for (var k = open.Count - 1; k > 0; k--)
                {
                    if (open[k].Name == name)
                    {
                        open.RemoveRange(k, open.Count - k);
                        break;
                    }
                }

                continue;
            }

            if (i + 1 >= html.Length || !char.IsLetter(html[i + 1]))
            {
                text.Append('<');
                i++;
                continue;
            }

            // A start tag.
            FlushText();
            var p = i + 1;
            var start = p;
            while (p < html.Length && (char.IsLetterOrDigit(html[p]) || html[p] == '-' || html[p] == ':'))
            {
                p++;
            }

            var tag = html[start..p].ToLowerInvariant();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var selfClosing = false;
            while (p < html.Length && html[p] != '>')
            {
                if (char.IsWhiteSpace(html[p]))
                {
                    p++;
                    continue;
                }

                if (html[p] == '/')
                {
                    selfClosing = true;
                    p++;
                    continue;
                }

                selfClosing = false;
                var attributeStart = p;
                while (p < html.Length && !char.IsWhiteSpace(html[p]) && html[p] != '=' && html[p] != '>' && html[p] != '/')
                {
                    p++;
                }

                var attributeName = html[attributeStart..p].ToLowerInvariant();
                while (p < html.Length && char.IsWhiteSpace(html[p]))
                {
                    p++;
                }

                var value = string.Empty;
                if (p < html.Length && html[p] == '=')
                {
                    p++;
                    while (p < html.Length && char.IsWhiteSpace(html[p]))
                    {
                        p++;
                    }

                    if (p < html.Length && (html[p] == '"' || html[p] == '\''))
                    {
                        var quote = html[p++];
                        var valueEnd = html.IndexOf(quote, p);
                        if (valueEnd < 0)
                        {
                            valueEnd = html.Length;
                        }

                        value = html[p..valueEnd];
                        p = Math.Min(html.Length, valueEnd + 1);
                    }
                    else
                    {
                        var valueStart = p;
                        while (p < html.Length && !char.IsWhiteSpace(html[p]) && html[p] != '>')
                        {
                            p++;
                        }

                        value = html[valueStart..p];
                    }
                }

                if (attributeName.Length > 0 && !attributes.ContainsKey(attributeName))
                {
                    attributes[attributeName] = DecodeReferences(value);
                }
            }

            i = p < html.Length ? p + 1 : html.Length;

            ImplyEndTags(open, tag);
            var element = new HtmlNode { Name = tag, Parent = open[^1] };
            foreach (var (key, value) in attributes)
            {
                element.Attributes[key] = value;
            }

            open[^1].Children.Add(element);
            if (RawText.Contains(tag) && !selfClosing)
            {
                var closeTag = html.IndexOf("</" + tag, i, StringComparison.OrdinalIgnoreCase);
                var contentEnd = closeTag < 0 ? html.Length : closeTag;
                if (tag is "title" or "textarea")
                {
                    element.Children.Add(new HtmlNode { Text = DecodeReferences(html[i..contentEnd]), Parent = element });
                }

                var after = closeTag < 0 ? -1 : html.IndexOf('>', closeTag);
                i = after < 0 ? html.Length : after + 1;
                continue;
            }

            if (!Void.Contains(tag) && !selfClosing && open.Count < MaxDepth)
            {
                open.Add(element);
            }
        }

        FlushText();
        return root;
    }

    private static int Find(List<HtmlNode> open, Func<string, bool> match, Func<string, bool> stop)
    {
        for (var k = open.Count - 1; k > 0; k--)
        {
            var name = open[k].Name!;
            if (match(name))
            {
                return k;
            }

            if (stop(name))
            {
                return -1;
            }
        }

        return -1;
    }

    private static void ImplyEndTags(List<HtmlNode> open, string tag)
    {
        void CloseTo(int index)
        {
            if (index > 0)
            {
                open.RemoveRange(index, open.Count - index);
            }
        }

        if (ClosesParagraph.Contains(tag))
        {
            CloseTo(Find(open, name => name == "p", name => ParagraphBoundary.Contains(name)));
        }

        switch (tag)
        {
            case "li":
                CloseTo(Find(open, name => name == "li", name => name is "ul" or "ol" or "table"));
                break;
            case "dt" or "dd":
                CloseTo(Find(open, name => name is "dt" or "dd", name => name == "dl"));
                break;
            case "td" or "th":
                CloseTo(Find(open, name => name is "td" or "th", name => name is "tr" or "table"));
                break;
            case "tr":
                CloseTo(Find(open, name => name == "tr", name => name is "table" or "thead" or "tbody" or "tfoot"));
                break;
            case "thead" or "tbody" or "tfoot":
                CloseTo(Find(open, name => name is "thead" or "tbody" or "tfoot", name => name == "table"));
                break;
            case "option":
                CloseTo(Find(open, name => name == "option", name => name == "select"));
                break;
        }
    }

    private static readonly string[] Latin1 =
    [
        "nbsp", "iexcl", "cent", "pound", "curren", "yen", "brvbar", "sect", "uml", "copy", "ordf", "laquo", "not", "shy", "reg", "macr",
        "deg", "plusmn", "sup2", "sup3", "acute", "micro", "para", "middot", "cedil", "sup1", "ordm", "raquo", "frac14", "frac12", "frac34", "iquest",
        "Agrave", "Aacute", "Acirc", "Atilde", "Auml", "Aring", "AElig", "Ccedil", "Egrave", "Eacute", "Ecirc", "Euml", "Igrave", "Iacute", "Icirc", "Iuml",
        "ETH", "Ntilde", "Ograve", "Oacute", "Ocirc", "Otilde", "Ouml", "times", "Oslash", "Ugrave", "Uacute", "Ucirc", "Uuml", "Yacute", "THORN", "szlig",
        "agrave", "aacute", "acirc", "atilde", "auml", "aring", "aelig", "ccedil", "egrave", "eacute", "ecirc", "euml", "igrave", "iacute", "icirc", "iuml",
        "eth", "ntilde", "ograve", "oacute", "ocirc", "otilde", "ouml", "divide", "oslash", "ugrave", "uacute", "ucirc", "uuml", "yacute", "thorn", "yuml",
    ];

    private static readonly Dictionary<string, string> Named = BuildNamed();

    private static Dictionary<string, string> BuildNamed()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
            ["ndash"] = "–", ["mdash"] = "—", ["lsquo"] = "‘", ["rsquo"] = "’", ["sbquo"] = "‚",
            ["ldquo"] = "“", ["rdquo"] = "”", ["bdquo"] = "„", ["dagger"] = "†", ["Dagger"] = "‡",
            ["bull"] = "•", ["hellip"] = "…", ["permil"] = "‰", ["lsaquo"] = "‹", ["rsaquo"] = "›",
            ["euro"] = "€", ["trade"] = "™", ["larr"] = "←", ["uarr"] = "↑", ["rarr"] = "→", ["darr"] = "↓",
            ["harr"] = "↔", ["minus"] = "−", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ",
            ["zwnj"] = "‌", ["zwj"] = "‍", ["infin"] = "∞", ["ne"] = "≠", ["le"] = "≤", ["ge"] = "≥",
            ["asymp"] = "≈", ["sum"] = "∑", ["radic"] = "√", ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ",
            ["delta"] = "δ", ["mu"] = "μ", ["pi"] = "π", ["sigma"] = "σ", ["omega"] = "ω", ["Omega"] = "Ω",
        };
        for (var k = 0; k < Latin1.Length; k++)
        {
            map[Latin1[k]] = ((char)(160 + k)).ToString();
        }

        return map;
    }

    /// <summary>Replaces character references (numeric and the named ones above); an unknown one stays as written.</summary>
    internal static string DecodeReferences(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                builder.Append(text[i]);
                continue;
            }

            var end = text.IndexOf(';', i + 1);
            if (end < 0 || end - i > 32)
            {
                builder.Append('&');
                continue;
            }

            var body = text.AsSpan(i + 1, end - i - 1);
            string? replacement = null;
            if (body.Length > 1 && body[0] == '#')
            {
                var hex = body[1] is 'x' or 'X';
                var digits = hex ? body[2..] : body[1..];
                if (int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                    && code is > 0 and <= 0x10FFFF && !(code is >= 0xD800 and <= 0xDFFF))
                {
                    replacement = char.ConvertFromUtf32(code);
                }
            }
            else if (Named.TryGetValue(body.ToString(), out var named))
            {
                replacement = named;
            }

            if (replacement is null)
            {
                builder.Append('&');
                continue;
            }

            builder.Append(replacement);
            i = end;
        }

        return builder.ToString();
    }
}
