using System.Xml;

namespace Reglet;

/// <summary>
/// writes <c>settings.xml</c> with only interoperable, required
/// settings.
/// </summary>
internal static class OdtSettingsWriter
{
    public static void Write(OdtPackageWriter package, PortableDocument document)
    {
        package.AddXmlPart("settings.xml", WriteSettings);
    }

    private static void WriteSettings(XmlWriter writer)
    {
        writer.WriteStartElement("office", "document-settings", OdtNamespaces.Office);
        writer.WriteAttributeString("office", "version", OdtNamespaces.Office, "1.3");

        writer.WriteStartElement("office", "settings", OdtNamespaces.Office);

        writer.WriteStartElement("config", "config-item-set", OdtNamespaces.Config);
        writer.WriteAttributeString("config", "name", OdtNamespaces.Config, "ooo:view-settings");
        WriteConfigItem(writer, "ViewAreaTop", "long", "0");
        WriteConfigItem(writer, "ViewAreaLeft", "long", "0");
        WriteConfigItem(writer, "ZoomValue", "int", "100");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteConfigItem(XmlWriter writer, string name, string type, string value)
    {
        writer.WriteStartElement("config", "config-item", OdtNamespaces.Config);
        writer.WriteAttributeString("config", "name", OdtNamespaces.Config, name);
        writer.WriteAttributeString("config", "type", OdtNamespaces.Config, type);
        writer.WriteString(value);
        writer.WriteEndElement();
    }
}
