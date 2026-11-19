using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// creates an XML fragment via an <see cref="XmlWriter"/> and reads it as nodes
/// back. The generated elements carry the WordprocessingML or ODF namespaces.
/// </summary>
internal static class XmlFragment
{
    /// <summary>
    /// writes the fragment and returns the topmost nodes (texts and elements). The
    /// passed namespace prefixes are declared on the root element.
    /// </summary>
    public static List<XNode> Write(Action<XmlWriter> write, params (string Prefix, string Namespace)[] namespaces)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(namespaces);

        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Indent = false,
        }))
        {
            write(writer);
        }

        if (builder.Length == 0)
        {
            return [];
        }

        var wrapper = new StringBuilder("<root");
        foreach (var (prefix, ns) in namespaces)
        {
            wrapper.Append($" xmlns:{prefix}=\"{ns}\"");
        }

        wrapper.Append('>').Append(builder).Append("</root>");
        return XElement.Parse(wrapper.ToString()).Nodes().ToList();
    }
}
