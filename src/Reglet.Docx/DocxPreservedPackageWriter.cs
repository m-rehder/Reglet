using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// AF07 (body-unchanged): writes a document read from a DOCX whose sections were not touched by
/// adopting the original package and patching only the parts that may legitimately change
/// (metadata in <c>docProps/core.xml</c>, managed elements in <c>word/settings.xml</c>).
/// Everything else — <c>word/document.xml</c>, styles, numbering, relationships, media and
/// unmodelled parts — is copied byte-identically, so unmodelled body content survives.
/// </summary>
internal static class DocxPreservedPackageWriter
{
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    public static void Build(
        PortableDocument document,
        ReadOnlyMemory<byte> source,
        Stream output,
        DocxWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var evenAndOddHeaders = document.Sections.Any(
            section => section.EvenHeader is not null || section.EvenFooter is not null);

        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // A signature does not cover the changed metadata.
        var signed = archive.Entries.Any(entry => entry.FullName.StartsWith(DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase));
        if (signed)
        {
            handler.Report("DOCX: the signature of the source document is removed; it does not cover the changed document.");
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0
                || entry.FullName.StartsWith(DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ADR-0028: the VBA project travels only when the model carries it.
            var dropMacro = document.Metadata.MacroProject is null;
            if (dropMacro && string.Equals(entry.FullName, DocxConstants.VbaProjectPart, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            created.LastWriteTime = FixedTimestamp;
            using var destination = created.Open();

            switch (entry.FullName)
            {
                case DocxConstants.CorePropertiesPart:
                    WriteXml(destination, writer =>
                        DocxPackageBuilder.WriteCoreProperties(writer, document.Metadata, options.InvalidXmlCharacters));
                    break;
                case DocxConstants.VbaProjectPart:
                    destination.Write(document.Metadata.MacroProject!.Data.Span);
                    break;
                case "[Content_Types].xml" when dropMacro || signed:
                    WriteXml(destination, writer => StripTypes(entry, dropMacro).WriteTo(writer));
                    break;
                case "_rels/.rels" when signed:
                    WriteXml(destination, writer => StripRelationships(entry, DocxSignatureWriter.OriginRelationship).WriteTo(writer));
                    break;
                case "word/_rels/document.xml.rels" when dropMacro:
                    WriteXml(destination, writer => StripRelationships(entry, DocxConstants.VbaProjectRelationship).WriteTo(writer));
                    break;
                case DocxConstants.SettingsPart:
                    WriteSettings(entry, destination, evenAndOddHeaders, options, document.Metadata.Variables);
                    break;
                default:
                    using (var content = ReaderGuard.OpenBounded(entry))
                    {
                        content.CopyTo(destination);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// The content types without the signature parts and, when the macros are dropped, without the VBA project
    /// (the main part is a plain document again).
    /// </summary>
    private static XElement StripTypes(ZipArchiveEntry entry, bool dropMacro)
    {
        var types = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root!;
        foreach (var element in types.Elements().ToList())
        {
            var contentType = element.Attribute("ContentType")?.Value;
            if (((string?)element.Attribute("PartName"))?.StartsWith("/" + DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase) == true)
            {
                element.Remove();
            }
            else if (!dropMacro)
            {
                continue;
            }
            else if (contentType == DocxConstants.VbaProjectContentType)
            {
                element.Remove();
            }
            else if (contentType == DocxConstants.MacroEnabledDocumentContentType)
            {
                element.SetAttributeValue("ContentType", DocxConstants.DocumentContentType);
            }
        }

        return types;
    }

    private static XElement StripRelationships(ZipArchiveEntry entry, string type)
    {
        var relationships = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root!;
        relationships.Elements().Where(r => r.Attribute("Type")?.Value == type).Remove();
        return relationships;
    }

    private static void WriteSettings(
        ZipArchiveEntry entry,
        Stream destination,
        bool evenAndOddHeaders,
        DocxWriteOptions options,
        IReadOnlyDictionary<string, string>? variables)
    {
        var settings = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root;
        if (settings is null)
        {
            using var content = ReaderGuard.OpenBounded(entry);
            content.CopyTo(destination);
            return;
        }

        DocxPackageBuilder.MergeSettings(
            settings,
            evenAndOddHeaders,
            options.Protection,
            options.ProtectionPassword,
            variables,
            options.InvalidXmlCharacters,
            options.AutoHyphenation,
            options.HyphenationZone);
        WriteXml(destination, writer => settings.WriteTo(writer));
    }

    private static void WriteXml(Stream destination, Action<XmlWriter> write)
    {
        using var writer = XmlWriter.Create(destination, XmlSettings);
        write(writer);
    }
}
