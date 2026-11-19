using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Reglet;

/// <summary>
/// AF12 (body-unchanged): writes an ODT read from a package whose sections were not touched by
/// adopting the original package and patching only <c>meta.xml</c> (the metadata). Everything
/// else — <c>content.xml</c>, styles, automatic styles, pictures, unmodelled parts — is copied
/// byte-identically.
/// </summary>
internal static class OdtPreservedPackageWriter
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
        OdtWriteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // ODF package rule: mimetype MUST be the first entry and uncompressed.
        var mimeEntry = target.CreateEntry("mimetype", CompressionLevel.NoCompression);
        mimeEntry.LastWriteTime = FixedTimestamp;
        using (var mimeStream = mimeEntry.Open())
        {
            mimeStream.Write(Encoding.ASCII.GetBytes(OdtNamespaces.MimeType));
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0 || entry.FullName == "mimetype")
            {
                continue;
            }

            var created = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            created.LastWriteTime = FixedTimestamp;
            using var destination = created.Open();

            if (entry.FullName == "meta.xml")
            {
                WriteXml(destination, writer =>
                    OdtMetaWriter.WriteMeta(writer, document.Metadata, options.InvalidXmlCharacters));
            }
            else
            {
                using var content = ReaderGuard.OpenBounded(entry);
                content.CopyTo(destination);
            }
        }
    }

    private static void WriteXml(Stream destination, Action<XmlWriter> write)
    {
        using var writer = XmlWriter.Create(destination, XmlSettings);
        write(writer);
    }
}
