using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>Writes <c>meta.xml</c> with document metadata.</summary>
internal static class OdtMetaWriter
{
    public static void Write(
        OdtPackageWriter package,
        PortableDocument document,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        package.AddXmlPart("meta.xml", writer => WriteMeta(writer, document.Metadata, xmlBehavior));
    }

    internal static void WriteMeta(XmlWriter writer, DocumentMetadata metadata, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("office", "document-meta", OdtNamespaces.Office);
        writer.WriteAttributeString("office", "version", OdtNamespaces.Office, "1.3");

        writer.WriteStartElement("office", "meta", OdtNamespaces.Office);

        WriteElementIfPresent(writer, "dc", "title", OdtNamespaces.DublinCore, metadata.Title, xmlBehavior);
        WriteElementIfPresent(writer, "dc", "creator", OdtNamespaces.DublinCore, metadata.Author, xmlBehavior);
        // text:initial-creator (AUTHOR) shows meta:initial-creator, not dc:creator.
        WriteElementIfPresent(writer, "meta", "initial-creator", OdtNamespaces.Meta, metadata.Author, xmlBehavior);
        WriteElementIfPresent(writer, "dc", "subject", OdtNamespaces.DublinCore, metadata.Subject, xmlBehavior);

        if (metadata.Keywords is { Count: > 0 } keywords)
        {
            foreach (var keyword in keywords)
            {
                writer.WriteElementString("meta", "keyword", OdtNamespaces.Meta, XmlText.Clean(keyword, xmlBehavior));
            }
        }

        WriteElementIfPresent(writer, "dc", "language", OdtNamespaces.DublinCore, metadata.Language, xmlBehavior);

        if (metadata.Created is { } created)
        {
            var value = created
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            writer.WriteElementString("meta", "creation-date", OdtNamespaces.Meta, value);
        }

        writer.WriteElementString("meta", "generator", OdtNamespaces.Meta, "Reglet");

        if (metadata.CustomProperties is { Count: > 0 } customProperties)
        {
            foreach (var (name, value) in customProperties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartElement("meta", "user-defined", OdtNamespaces.Meta);
                writer.WriteAttributeString("meta", "name", OdtNamespaces.Meta, name);
                writer.WriteAttributeString("meta", "value-type", OdtNamespaces.Meta, "string");
                writer.WriteString(XmlText.Clean(value, xmlBehavior));
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteElementIfPresent(
        XmlWriter writer,
        string prefix,
        string localName,
        string namespaceUri,
        string? value,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteElementString(prefix, localName, namespaceUri, XmlText.Clean(value, xmlBehavior));
        }
    }
}
