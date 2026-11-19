using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// general fields that ODF can express (OF-C02): <c>SEQ</c> → <c>text:sequence</c>,
/// <c>REF</c> → <c>text:bookmark-ref</c> (text), <c>TITLE</c>/<c>SUBJECT</c>/<c>AUTHOR</c>/
/// <c>KEYWORDS</c>/<c>FILENAME</c> → the document information fields, <c>PAGE</c>/<c>NUMPAGES</c>
/// → page number and count. Other fields (<c>IF</c>, <c>MERGEFIELD</c>, …) and switches ODF
/// cannot express keep only their result as text; the writer reports that.
/// </summary>
internal static class OdtFields
{
    /// <summary>An ODF field element: local name in the text namespace, attributes, sequence name.</summary>
    internal sealed record Mapping(string LocalName, IReadOnlyList<(XNamespace Namespace, string Prefix, string Name, string Value)> Attributes, string? Sequence = null);

    private static readonly XNamespace Text = OdtNamespaces.Text;
    private static readonly XNamespace Style = OdtNamespaces.Style;

    private static readonly IReadOnlyDictionary<string, string> InfoFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TITLE"] = "title",
        ["SUBJECT"] = "subject",
        ["AUTHOR"] = "initial-creator",
        ["KEYWORDS"] = "keywords",
        ["FILENAME"] = "file-name",
        ["PAGE"] = "page-number",
        ["NUMPAGES"] = "page-count",
    };

    /// <summary>The document information element for a built-in property name (DOCPROPERTY), or null.</summary>
    public static string? InfoElementOf(string propertyName)
        => propertyName.ToUpperInvariant() switch
        {
            "TITLE" => "title",
            "SUBJECT" => "subject",
            "AUTHOR" => "initial-creator",
            "KEYWORDS" => "keywords",
            _ => null,
        };

    /// <summary>The ODF element for a field code, or null if ODF has none.</summary>
    public static Mapping? Map(string instruction)
    {
        var tokens = instruction.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();

        // \* MERGEFORMAT only keeps Word's formatting of the result; it does not change the value.
        for (var index = tokens.Count - 2; index >= 0; index--)
        {
            if (tokens[index] == "\\*" && tokens[index + 1].Equals("MERGEFORMAT", StringComparison.OrdinalIgnoreCase))
            {
                tokens.RemoveRange(index, 2);
            }
        }

        if (tokens.Count == 0)
        {
            return null;
        }

        var command = tokens[0].ToUpperInvariant();
        if (InfoFields.TryGetValue(command, out var info) && tokens.Count == 1)
        {
            return command switch
            {
                "FILENAME" => new Mapping(info, [(Text, "text", "display", "name-and-extension")]),
                "PAGE" => new Mapping(info, [(Text, "text", "select-page", "current")]),
                // LibreOffice shows meta:initial-creator only with a creation date; fixed keeps the written text.
                "AUTHOR" => new Mapping(info, [(Text, "text", "fixed", "true")]),
                _ => new Mapping(info, []),
            };
        }

        if (command == "REF" && tokens.Count >= 2 && tokens.Skip(2).All(token => token == "\\h"))
        {
            return new Mapping("bookmark-ref", [(Text, "text", "reference-format", "text"), (Text, "text", "ref-name", tokens[1])]);
        }

        if (command == "SEQ" && tokens.Count >= 2 && IsSequenceName(tokens[1]))
        {
            var format = "1";
            var rest = tokens.Skip(2).ToList();
            if (rest is ["\\*", var picture])
            {
                format = picture switch
                {
                    "ARABIC" => "1",
                    "ROMAN" => "I",
                    "roman" => "i",
                    "ALPHABETIC" => "A",
                    "alphabetic" => "a",
                    _ => string.Empty,
                };
                if (format.Length == 0)
                {
                    return null;
                }
            }
            else if (rest.Count > 0)
            {
                // \r (restart), \c (repeat), \h (hidden) have no ODF counterpart here.
                return null;
            }

            var name = tokens[1];
            return new Mapping(
                "sequence",
                [(Text, "text", "name", name), (Text, "text", "formula", "ooow:" + name + "+1"), (Style, "style", "num-format", format)],
                name);
        }

        return null;
    }

    /// <summary>The field code for an ODF field element, or null if it is not a general field.</summary>
    public static string? InstructionOf(XElement element)
    {
        if (element.Name.Namespace != Text)
        {
            return null;
        }

        switch (element.Name.LocalName)
        {
            case "sequence" when element.Attribute(Text + "name")?.Value is { Length: > 0 } name:
                var picture = element.Attribute(Style + "num-format")?.Value switch
                {
                    "I" => "ROMAN",
                    "i" => "roman",
                    "A" => "ALPHABETIC",
                    "a" => "alphabetic",
                    _ => "ARABIC",
                };
                return $"SEQ {name} \\* {picture}";
            case "bookmark-ref" when element.Attribute(Text + "reference-format")?.Value == "text"
                && element.Attribute(Text + "ref-name")?.Value is { Length: > 0 } reference:
                return $"REF {reference} \\h";
            case "title":
                return "TITLE";
            case "subject":
                return "SUBJECT";
            case "initial-creator":
                return "AUTHOR";
            case "keywords":
                return "KEYWORDS";
            case "file-name":
                return "FILENAME";
            default:
                return null;
        }
    }

    /// <summary>Sequence names must be valid ODF variable names (letters, digits, underscore).</summary>
    private static bool IsSequenceName(string name)
        => name.Length > 0 && char.IsLetter(name[0]) && name.All(character => char.IsLetterOrDigit(character) || character == '_');
}
