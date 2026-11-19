using System.Xml;

namespace Reglet;

/// <summary>
/// writes <c>office:font-face-decls</c>. ODF applications resolve <c>style:font-name</c>
/// only via a matching <c>style:font-face</c> declaration; without it the
/// font family ignored.
/// </summary>
internal static class OdtFontFaces
{
    public static void Write(XmlWriter writer, IEnumerable<string> families, InvalidXmlCharacterBehavior xmlBehavior, OdtEmbeddedFonts? embedded = null)
    {
        var faces = families
            .Select(FontFamilies.Resolve)
            .Select(face => (Name: XmlText.Clean(face.Name, xmlBehavior), face.Generic))
            .DistinctBy(face => face.Name, StringComparer.Ordinal)
            .OrderBy(face => face.Name, StringComparer.Ordinal)
            .ToList();
        if (faces.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("office", "font-face-decls", OdtNamespaces.Office);
        foreach (var (name, generic) in faces)
        {
            writer.WriteStartElement("style", "font-face", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
            writer.WriteAttributeString("svg", "font-family", OdtNamespaces.Svg, CssFamily(name));
            if (generic is { } category)
            {
                // If the font is missing, the application falls back to a font of the same category.
                writer.WriteAttributeString("style", "font-family-generic", OdtNamespaces.Style, category switch
                {
                    GenericFontFamily.Serif => "roman",
                    GenericFontFamily.SansSerif => "swiss",
                    _ => "modern",
                });
            }

            if (embedded is not null && embedded.PathsOf(name) is { Count: > 0 } paths)
            {
                writer.WriteStartElement("svg", "font-face-src", OdtNamespaces.Svg);
                foreach (var path in paths)
                {
                    writer.WriteStartElement("svg", "font-face-uri", OdtNamespaces.Svg);
                    writer.WriteAttributeString("xlink", "href", "http://www.w3.org/1999/xlink", path);
                    writer.WriteAttributeString("xlink", "type", "http://www.w3.org/1999/xlink", "simple");
                    writer.WriteStartElement("svg", "font-face-format", OdtNamespaces.Svg);
                    writer.WriteAttributeString("svg", "string", OdtNamespaces.Svg, "truetype");
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>Name that <c>style:font-name</c> refers to in the declaration.</summary>
    public static string Name(string family, InvalidXmlCharacterBehavior xmlBehavior)
        => XmlText.Clean(FontFamilies.Resolve(family).Name, xmlBehavior);

    // svg:font-family follows CSS syntax: names with spaces are quoted.
    private static string CssFamily(string family)
    {
        if (!family.Any(char.IsWhiteSpace))
        {
            return family;
        }

        return family.Contains('\'', StringComparison.Ordinal) ? "\"" + family + "\"" : "'" + family + "'";
    }
}
