using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// embeds fonts in a DOCX: <c>word/fontTable.xml</c> names them, the files are obfuscated
/// (<c>.odttf</c>, ECMA-376 Part 2, 17.8.1) with the GUID of their <c>w:fontKey</c>.
/// </summary>
internal static class DocxFontEmbedding
{
    public const string FontTablePart = "word/fontTable.xml";
    private const string FontTableContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml";
    private const string FontTableRelationship = DocxConstants.Relationships + "/fontTable";
    private const string FontRelationship = DocxConstants.Relationships + "/font";
    private const string ObfuscatedContentType = "application/vnd.openxmlformats-officedocument.obfuscatedFont";

    /// <summary>Writes the font parts; returns false when nothing was embedded.</summary>
    public static bool Embed(
        OpcPackageWriter package,
        PortableDocument document,
        FontCollection fonts,
        ref int nextRelationshipIndex,
        UnsupportedFeatureHandler handler)
    {
        if (document.SourcePackage is { IsEmpty: false })
        {
            // shortcut: the font table of a source package stays as it is; embedding applies to documents built in memory.
            handler.Report("DOCX: fonts are not embedded in a document that adopts a source package.");
            return false;
        }

        var faces = FontEmbedding.Select(fonts, FontUsage.Families(document), message => handler.Report("DOCX: " + message));
        if (faces.Count == 0)
        {
            return false;
        }

        package.AddDefault("odttf", ObfuscatedContentType);
        package.AddOverride("/" + FontTablePart, FontTableContentType);
        package.AddRelationship(
            DocxConstants.DocumentPart,
            new OpcRelationship($"rId{nextRelationshipIndex++}", FontTableRelationship, "fontTable.xml"));

        var entries = new List<(string Family, FontFace Face, string Id, Guid Key)>();
        for (var index = 0; index < faces.Count; index++)
        {
            var (family, face) = faces[index];
            // Deterministic key: the same document gives the same bytes.
            var key = new Guid(System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(family + "|" + face.PostScriptName + "|" + index)));
            var id = "rId" + (index + 1).ToString(CultureInfo.InvariantCulture);
            var name = $"fonts/font{index + 1}.odttf";
            package.AddBinaryPart("word/" + name, Obfuscate(face.Data, key));
            package.AddRelationship(FontTablePart, new OpcRelationship(id, FontRelationship, name));
            entries.Add((family, face, id, key));
        }

        package.WriteXmlPart(FontTablePart, writer =>
        {
            writer.WriteStartElement("w", "fonts", DocxConstants.WordprocessingML);
            writer.WriteAttributeString("xmlns", "r", null, DocxConstants.Relationships);
            foreach (var group in entries.GroupBy(entry => entry.Family, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("w", "font", DocxConstants.WordprocessingML);
                writer.WriteAttributeString("w", "name", DocxConstants.WordprocessingML, FontFamilies.Resolve(group.Key).Name);
                // CT_Font: embedRegular, embedBold, embedItalic, embedBoldItalic in this order.
                foreach (var (bold, italic, element) in new[]
                {
                    (false, false, "embedRegular"), (true, false, "embedBold"),
                    (false, true, "embedItalic"), (true, true, "embedBoldItalic"),
                })
                {
                    var entry = group.FirstOrDefault(candidate => candidate.Face.IsBold == bold && candidate.Face.IsItalic == italic);
                    if (entry.Face is null)
                    {
                        continue;
                    }

                    writer.WriteStartElement("w", element, DocxConstants.WordprocessingML);
                    writer.WriteAttributeString("r", "id", DocxConstants.Relationships, entry.Id);
                    writer.WriteAttributeString("w", "fontKey", DocxConstants.WordprocessingML, entry.Key.ToString("B").ToUpperInvariant());
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });
        return true;
    }

    /// <summary>XORs the first 32 bytes with the GUID bytes in reverse order of its string form.</summary>
    internal static byte[] Obfuscate(byte[] data, Guid key)
    {
        var hex = key.ToString("N");
        var keyBytes = new byte[16];
        for (var index = 0; index < 16; index++)
        {
            keyBytes[15 - index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        var result = (byte[])data.Clone();
        for (var index = 0; index < Math.Min(32, result.Length); index++)
        {
            result[index] ^= keyBytes[index % 16];
        }

        return result;
    }
}
