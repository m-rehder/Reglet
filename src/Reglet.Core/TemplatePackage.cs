using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// helpers for reading and writing template packages (DOCX/ODT): read ZIP entries and
/// load XML without DTD/entities or write serial UTF-8.
/// </summary>
internal static class TemplatePackage
{
    /// <summary>Reads a ZIP entry completely into a byte array.</summary>
    public static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var stream = ReaderGuard.OpenBounded(entry);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Loads XML without DTD and entity processing (protection against XXE).</summary>
    public static XDocument LoadXml(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var stream = new MemoryStream(data, writable: false);
        using var reader = XmlReader.Create(
            stream,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(ReaderGuard.LimitXmlDepth(reader));
    }

    /// <summary>Serializes an XML document compactly with an XML declaration as UTF-8 without a BOM.</summary>
    public static byte[] SaveXml(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false,
            OmitXmlDeclaration = false,
        };

        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        return stream.ToArray();
    }
}
