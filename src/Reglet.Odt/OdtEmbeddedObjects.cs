using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// <see cref="EmbeddedObject"/> as an ODF OLE object (ADR-0029): a frame with <c>draw:object-ole</c> that points at
/// the data part <c>Object N</c> and a preview picture <c>ObjectReplacements/Object N</c>.
/// </summary>
internal static class OdtEmbeddedObjects
{
    internal const string OleMediaType = "application/vnd.sun.star.oleobject";

    /// <summary>The storable objects of the body in document order with their part names; numbered after the charts.</summary>
    internal static Dictionary<EmbeddedObject, string> Collect(PortableDocument document, int firstNumber)
    {
        var names = new Dictionary<EmbeddedObject, string>(ReferenceEqualityComparer.Instance);
        var number = firstNumber;
        foreach (var obj in document.Sections.SelectMany(section => OdtCharts.Enumerate<EmbeddedObject>(section.Blocks)))
        {
            if (obj.Kind == EmbeddedObjectKind.OleStorage)
            {
                names[obj] = "Object " + number++.ToString(CultureInfo.InvariantCulture);
            }
        }

        return names;
    }

    /// <summary>Whether the body has an object that cannot be stored (an Open XML package).</summary>
    internal static bool HasUnstorable(PortableDocument document)
        => document.Sections.SelectMany(section => OdtCharts.Enumerate<EmbeddedObject>(section.Blocks))
            .Any(obj => obj.Kind != EmbeddedObjectKind.OleStorage);

    internal static bool Has(IEnumerable<Block> blocks) => OdtCharts.Enumerate<EmbeddedObject>(blocks).Any();

    internal static void WriteParts(OdtPackageWriter package, IReadOnlyDictionary<EmbeddedObject, string> objects)
    {
        foreach (var (obj, name) in objects)
        {
            package.AddBinaryPart(name, OleMediaType, obj.RawData);
            var jpeg = obj.Preview?.MediaType == ImageMediaType.Jpeg;
            package.AddBinaryPart(
                "ObjectReplacements/" + name,
                jpeg ? "image/jpeg" : "image/png",
                obj.Preview is { } preview ? preview.RawData : EmbeddedObject.PlaceholderPreviewPng);
        }
    }

    private static string Cm(Length length) => (length.Points / 72 * 2.54).ToString("0.####", CultureInfo.InvariantCulture) + "cm";

    /// <summary>The paragraph with the frame, the object and its preview.</summary>
    internal static void WritePlacement(XmlWriter writer, EmbeddedObject obj, string objectName)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteStartElement("draw", "frame", OdtNamespaces.Draw);
        writer.WriteAttributeString("draw", "name", OdtNamespaces.Draw, objectName);
        writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, "as-char");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Cm(obj.Width));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Cm(obj.Height));
        WriteLink(writer, "object-ole", "./" + objectName);
        WriteLink(writer, "image", "./ObjectReplacements/" + objectName);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteLink(XmlWriter writer, string element, string href)
    {
        writer.WriteStartElement("draw", element, OdtNamespaces.Draw);
        writer.WriteAttributeString("xlink", "href", OdtNamespaces.XLink, href);
        writer.WriteAttributeString("xlink", "type", OdtNamespaces.XLink, "simple");
        writer.WriteAttributeString("xlink", "show", OdtNamespaces.XLink, "embed");
        writer.WriteAttributeString("xlink", "actuate", OdtNamespaces.XLink, "onLoad");
        writer.WriteEndElement();
    }
}
