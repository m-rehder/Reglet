using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Reglet;

/// <summary>
/// <see cref="EmbeddedObject"/> as a Word object (ADR-0029): <c>w:object</c> with a VML shape for the preview picture
/// and <c>o:OLEObject</c> that points at the data in <c>word/embeddings/</c>.
/// </summary>
internal static class DocxEmbeddedObjects
{
    internal const string OleObjectRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject";
    internal const string PackageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/package";
    internal const string OleObjectContentType = "application/vnd.openxmlformats-officedocument.oleObject";
    internal const string Vml = "urn:schemas-microsoft-com:vml";
    internal const string Office = "urn:schemas-microsoft-com:office:office";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>The numbered parts of one object and the relationships that point at them.</summary>
    internal sealed record Parts(int Number, string DataRelationship, string PreviewRelationship);

    /// <summary>Writes the data and preview part of every embedded object of the body and registers them.</summary>
    internal static Dictionary<EmbeddedObject, Parts> Register(
        OpcPackageWriter package, PortableDocument document, ref int relationshipIndex)
    {
        var result = new Dictionary<EmbeddedObject, Parts>(ReferenceEqualityComparer.Instance);
        var number = 0;
        foreach (var obj in document.Sections.SelectMany(section => BlockScan.Enumerate<EmbeddedObject>(section.Blocks)))
        {
            number++;
            var isOle = obj.Kind == EmbeddedObjectKind.OleStorage;
            var extension = isOle ? "bin" : PackageExtension(obj);
            var dataPart = isOle ? $"word/embeddings/oleObject{number}.bin" : $"word/embeddings/Package{number}.{extension}";
            package.AddBinaryPart(dataPart, obj.RawData);
            package.AddOverride("/" + dataPart, isOle ? OleObjectContentType : PackageContentType(extension));
            var dataId = $"rId{relationshipIndex++}";
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship(dataId, isOle ? OleObjectRelationship : PackageRelationship, dataPart["word/".Length..]));

            var preview = obj.Preview;
            var (previewExtension, previewType) = preview?.MediaType == ImageMediaType.Jpeg
                ? ("jpeg", DocxConstants.JpegContentType)
                : ("png", DocxConstants.PngContentType);
            var previewPart = $"word/media/object{number}.{previewExtension}";
            package.AddDefault(previewExtension, previewType);
            package.AddBinaryPart(previewPart, preview is null ? EmbeddedObject.PlaceholderPreviewPng : preview.RawData);
            var previewId = $"rId{relationshipIndex++}";
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship(previewId, DocxConstants.ImageRelationship, previewPart["word/".Length..]));

            result[obj] = new Parts(number, dataId, previewId);
        }

        return result;
    }

    /// <summary>
    /// The file extension of an embedded package: the one given, or, when none was given ("bin"), the one its ProgID
    /// names. Word picks the program by the part's extension and content type; "Package1.bin" with an Excel ProgID
    /// fails with "server application not found".
    /// </summary>
    private static string PackageExtension(EmbeddedObject obj)
        => obj.FileExtension != "bin" ? obj.FileExtension : obj.ProgId switch
        {
            { } id when id.StartsWith("Excel.Sheet", StringComparison.OrdinalIgnoreCase) => "xlsx",
            { } id when id.StartsWith("Word.Document", StringComparison.OrdinalIgnoreCase) => "docx",
            { } id when id.StartsWith("PowerPoint.", StringComparison.OrdinalIgnoreCase) => "pptx",
            _ => "bin",
        };

    private static string PackageContentType(string extension) => extension switch
    {
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    /// <summary>Writes the paragraph with the object.</summary>
    internal static void Write(XmlWriter writer, EmbeddedObject obj, Parts parts, long availableWidth, InvalidXmlCharacterBehavior xmlBehavior)
    {
        // An object wider than the text area (a table cell) is scaled down with its proportions.
        var scale = Math.Min(1, availableWidth / 20.0 / obj.Width.Points);
        var width = Math.Round(obj.Width.Points * scale, 2);
        var height = Math.Round(obj.Height.Points * scale, 2);
        const string Wml = DocxConstants.WordprocessingML;
        writer.WriteStartElement("w", "p", Wml);
        writer.WriteStartElement("w", "r", Wml);
        writer.WriteStartElement("w", "object", Wml);
        writer.WriteAttributeString("w", "dxaOrig", Wml, Twips(width).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("w", "dyaOrig", Wml, Twips(height).ToString(CultureInfo.InvariantCulture));

        var shapeId = $"_x0000_i{1024 + parts.Number}";
        writer.WriteStartElement("v", "shape", Vml);
        writer.WriteAttributeString("id", shapeId);
        writer.WriteAttributeString("type", "#_x0000_t75");
        writer.WriteAttributeString("style", $"width:{Points(width)}pt;height:{Points(height)}pt");
        writer.WriteAttributeString("o", "ole", Office, string.Empty);
        writer.WriteStartElement("v", "imagedata", Vml);
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, parts.PreviewRelationship);
        writer.WriteAttributeString("o", "title", Office, string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("o", "OLEObject", Office);
        writer.WriteAttributeString("Type", "Embed");
        if (obj.ProgId.Length > 0)
        {
            writer.WriteAttributeString("ProgID", XmlText.Clean(obj.ProgId, xmlBehavior));
        }

        writer.WriteAttributeString("ShapeID", shapeId);
        writer.WriteAttributeString("DrawAspect", "Content");
        writer.WriteAttributeString("ObjectID", "_" + (1_000_000_000 + parts.Number).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, parts.DataRelationship);
        writer.WriteEndElement();

        writer.WriteEndElement(); // object
        writer.WriteEndElement(); // r
        writer.WriteEndElement(); // p
    }

    private static long Twips(double points) => DocxUnits.Scaled(points, 20);

    /// <summary>Points with two decimals, as integer arithmetic so every platform writes the same text.</summary>
    private static string Points(double points)
    {
        var hundredths = (long)Math.Round(points * 100);
        return $"{hundredths / 100}.{hundredths % 100:00}";
    }

    /// <summary>A length of a VML style value (<c>150pt</c>, <c>2in</c>, <c>4cm</c>, <c>40mm</c>, <c>200px</c>), or null.</summary>
    internal static Length? ParseStyleLength(string? style, string property)
    {
        if (style is null)
        {
            return null;
        }

        var match = StyleLength(property).Match(style);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        var points = match.Groups[2].Value switch
        {
            "in" => value * 72,
            "cm" => value * 72 / 2.54,
            "mm" => value * 72 / 25.4,
            "px" => value * 0.75,
            _ => value,
        };
        return points > 0 && points < 1_000_000 ? Length.FromPoints(points) : null;
    }

    private static Regex StyleLength(string property)
        => new($@"(?:^|;)\s*{property}\s*:\s*([0-9]*\.?[0-9]+)\s*(pt|in|cm|mm|px)?\s*(?:;|$)", RegexOptions.CultureInvariant);
}
