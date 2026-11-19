using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// a canonicalizer for the subtree of an XML signature (OF-E02). It follows the rules of
/// Canonical XML 1.0 (RFC 3076) that a <c>SignedInfo</c> and the signed properties of a DOCX
/// need: UTF-8 without a declaration, no self-closing tags, attributes sorted by namespace and
/// name, the in-scope namespace declarations rendered on the apex element, comments and
/// processing instructions dropped and the defined character escapes.
/// </summary>
/// <remarks>
/// The full recommendation also covers entity references, xml: attributes and the
/// <c>xml:base</c> handling; none of them occurs in the signature parts of a DOCX, and the parts
/// are parsed with no DTD, so no entity can appear.
/// </remarks>
internal static class XmlCanonicalizer
{
    private static readonly XNamespace XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>the canonical form of the subtree of <paramref name="element"/>.</summary>
    public static byte[] Canonicalize(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var builder = new StringBuilder();
        AppendElement(builder, element);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>the canonical form of a whole document (the root element is the apex).</summary>
    public static byte[] Canonicalize(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Root is { } root
            ? Canonicalize(root)
            : throw new DocumentValidationException("The XML document has no root element.");
    }

    private static void AppendElement(StringBuilder builder, XElement element)
        => AppendElement(builder, element, new Dictionary<string, string>(StringComparer.Ordinal));

    private static void AppendElement(
        StringBuilder builder,
        XElement element,
        Dictionary<string, string> rendered)
    {
        var namespaces = InScopeNamespaces(element);
        builder.Append('<').Append(QualifiedName(element));

        // Namespace declarations first, sorted by prefix (C14N 1.0, 2.3): the default
        // namespace comes first, then the prefixes in byte order. A declaration that an ancestor
        // already rendered with the same value is not repeated.
        foreach (var (prefix, uri) in namespaces)
        {
            if (rendered.TryGetValue(prefix, out var current) && current == uri)
            {
                continue;
            }

            rendered[prefix] = uri;
            builder.Append(' ');
            if (prefix.Length > 0)
            {
                builder.Append("xmlns:").Append(prefix);
            }
            else
            {
                builder.Append("xmlns");
            }

            builder.Append("=\"").Append(EscapeAttribute(uri)).Append('"');
        }

        // Attributes sorted by namespace URI, then by local name; an attribute without a
        // namespace sorts before namespaced attributes (C14N 1.0, 2.3).
        foreach (var attribute in element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(QualifiedName(attribute));
            builder.Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        }

        builder.Append('>');

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    AppendElement(builder, child, new Dictionary<string, string>(rendered, StringComparer.Ordinal));
                    break;
                case XCData cdata:
                    builder.Append(EscapeText(cdata.Value));
                    break;
                case XText text:
                    builder.Append(EscapeText(text.Value));
                    break;
            }
        }

        builder.Append("</").Append(QualifiedName(element)).Append('>');
    }

    /// <summary>
    /// the namespace declarations in scope at the element, sorted by prefix; the <c>xml</c>
    /// prefix is never rendered (RFC 3076, 2.3).
    /// </summary>
    private static List<(string Prefix, string Uri)> InScopeNamespaces(XElement element)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        // Outermost first, so an inner declaration overrides the outer one.
        foreach (var candidate in element.AncestorsAndSelf().Reverse())
        {
            foreach (var attribute in candidate.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                declarations[attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName] = attribute.Value;
            }
        }

        // The element is the apex of the node-set: a namespace it uses but does not declare (for
        // example because it was created detached from its parent) is declared here, exactly as
        // C14N 1.0 renders the inherited declarations of the apex.
        if (element.Name.Namespace != XNamespace.None
            && !declarations.TryGetValue(string.Empty, out var defaultNamespace)
            || element.Name.Namespace.NamespaceName != declarations.GetValueOrDefault(string.Empty))
        {
            var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
            if (string.IsNullOrEmpty(prefix))
            {
                declarations[string.Empty] = element.Name.NamespaceName;
            }
            else
            {
                declarations[prefix] = element.Name.NamespaceName;
            }
        }

        // A prefix that a descendant undeclares (xmlns:p="") is not in scope there; the walk above
        // takes the innermost declaration, which is exactly the in-scope one.
        return declarations
            .Where(entry => entry.Key != "xml" || entry.Value != XmlNamespace.NamespaceName)
            .OrderBy(entry => entry.Key.Length == 0 ? string.Empty : entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
    }

    private static string QualifiedName(XElement element)
        => string.IsNullOrEmpty(element.GetPrefixOfNamespace(element.Name.Namespace))
            ? element.Name.LocalName
            : element.GetPrefixOfNamespace(element.Name.Namespace) + ":" + element.Name.LocalName;

    private static string QualifiedName(XAttribute attribute)
    {
        if (attribute.Name.Namespace == XNamespace.None)
        {
            return attribute.Name.LocalName;
        }

        var prefix = attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace);
        return string.IsNullOrEmpty(prefix)
            ? attribute.Name.LocalName
            : prefix + ":" + attribute.Name.LocalName;
    }

    private static string EscapeAttribute(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("\t", "&#x9;", StringComparison.Ordinal)
            .Replace("\n", "&#xA;", StringComparison.Ordinal)
            .Replace("\r", "&#xD;", StringComparison.Ordinal);

    private static string EscapeText(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\r", "&#xD;", StringComparison.Ordinal);
}
