using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Reglet;

/// <summary>
/// internal ODT package writer. Encapsulates ZIP order, compression and entry names
/// only with System.IO.Compression and System.Xml.
/// </summary>
internal sealed class OdtPackageWriter : IDisposable
{
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
    };

    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ZipArchive _archive;
    private readonly OdtEncryptionOptions? _encryption;
    private readonly List<Part> _parts = [];
    private bool _disposed;

    public OdtPackageWriter(Stream output)
        : this(output, null)
    {
    }

    public OdtPackageWriter(Stream output, OdtEncryptionOptions? encryption)
    {
        ArgumentNullException.ThrowIfNull(output);
        _encryption = encryption;
        _archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // ODF package rule: mimetype MUST be the first entry and uncompressed.
        var mimeEntry = _archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
        mimeEntry.LastWriteTime = FixedTimestamp;
        using var stream = mimeEntry.Open();
        stream.Write(Encoding.ASCII.GetBytes(OdtNamespaces.MimeType));
    }

    /// <summary>The parts written so far with their media type (for the manifest).</summary>
    public IReadOnlyList<(string PartName, string MediaType)> Parts
        => _parts.Select(part => (part.PartName, part.MediaType)).ToArray();

    /// <summary>Whether a part has already been written by the builder.</summary>
    public bool HasPart(string partName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        return _parts.Any(part => string.Equals(part.PartName, partName, StringComparison.Ordinal));
    }

    /// <summary>Writes an XML part (media type <c>text/xml</c>).</summary>
    public void AddXmlPart(string partName, Action<XmlWriter> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentNullException.ThrowIfNull(write);
        PackagePath.Validate(partName);

        if (_encryption is null)
        {
            var entry = _archive.CreateEntry(partName, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedTimestamp;
            using (var stream = entry.Open())
            using (var writer = XmlWriter.Create(stream, XmlSettings))
            {
                WriteChecked(writer, write);
            }

            _parts.Add(new Part(partName, OdtNamespaces.XmlContentType, 0, null));
            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, XmlSettings))
        {
            WriteChecked(writer, write);
        }

        AddPart(partName, OdtNamespaces.XmlContentType, buffer.ToArray());
    }

    /// <summary>Lists a sub-document directory (e.g. an embedded chart) in the manifest; the zip needs no entry for it.</summary>
    public void AddDirectoryEntry(string directory, string mediaType)
    {
        PackagePath.Validate(directory);
        _parts.Add(new Part(directory + "/", mediaType, 0, null));
    }

    /// <summary>Writes a binary part (e.g. an image) with a media type.</summary>
    public void AddBinaryPart(string partName, string mediaType, ReadOnlyMemory<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);
        ArgumentException.ThrowIfNullOrEmpty(mediaType);
        PackagePath.Validate(partName);

        if (_encryption is null)
        {
            var entry = _archive.CreateEntry(partName, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedTimestamp;
            using (var stream = entry.Open())
            {
                stream.Write(content.Span);
            }

            _parts.Add(new Part(partName, mediaType, 0, null));
            return;
        }

        AddPart(partName, mediaType, content.ToArray());
    }

    private void AddPart(string partName, string mediaType, byte[] content)
    {
        // The manifest itself and the mimetype entry stay plain; every other part is encrypted
        // when encryption is requested (ODF 1.3 part 2, 3.4).
        if (_encryption is null || partName == "META-INF/manifest.xml")
        {
            WriteRaw(partName, content, CompressionLevel.Optimal);
            _parts.Add(new Part(partName, mediaType, content.Length, null));
            return;
        }

        var encrypted = OdtEncryption.Encrypt(content, _encryption);
        WriteRaw(partName, encrypted.Cipher, CompressionLevel.NoCompression);
        _parts.Add(new Part(partName, mediaType, encrypted.PlainSize, encrypted.Info));
    }

    private void WriteRaw(string partName, byte[] content, CompressionLevel compression)
    {
        var entry = _archive.CreateEntry(partName, compression);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private sealed record Part(string PartName, string MediaType, long Size, OdtEncryptionInfo? Encryption);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WriteManifest();
        _archive.Dispose();
    }

    private void WriteManifest()
    {
        AddXmlPart("META-INF/manifest.xml", writer =>
        {
            writer.WriteStartElement("manifest", "manifest", OdtNamespaces.Manifest);
            writer.WriteAttributeString("manifest", "version", OdtNamespaces.Manifest, "1.3");

            WriteFileEntry(writer, "/", OdtNamespaces.MimeType, "1.3", null);
            foreach (var part in _parts)
            {
                WriteFileEntry(writer, part.PartName, part.MediaType, null, part);
            }

            writer.WriteEndElement();
        });
    }

    private static void WriteFileEntry(
        XmlWriter writer,
        string fullPath,
        string mediaType,
        string? version,
        Part? part)
    {
        writer.WriteStartElement("manifest", "file-entry", OdtNamespaces.Manifest);
        writer.WriteAttributeString("manifest", "full-path", OdtNamespaces.Manifest, fullPath);
        if (version is not null)
        {
            writer.WriteAttributeString("manifest", "version", OdtNamespaces.Manifest, version);
        }

        writer.WriteAttributeString("manifest", "media-type", OdtNamespaces.Manifest, mediaType);
        if (part?.Encryption is { } encryption)
        {
            writer.WriteAttributeString(
                "manifest", "size", OdtNamespaces.Manifest,
                part.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartElement("manifest", "encryption-data", OdtNamespaces.Manifest);
            writer.WriteAttributeString("manifest", "checksum", OdtNamespaces.Manifest, encryption.Checksum);
            writer.WriteAttributeString("manifest", "checksum-type", OdtNamespaces.Manifest, encryption.ChecksumType);

            writer.WriteStartElement("manifest", "algorithm", OdtNamespaces.Manifest);
            writer.WriteAttributeString("manifest", "algorithm-name", OdtNamespaces.Manifest, encryption.Algorithm);
            writer.WriteAttributeString("manifest", "initialisation-vector", OdtNamespaces.Manifest, encryption.Iv);
            writer.WriteEndElement();

            writer.WriteStartElement("manifest", "key-derivation", OdtNamespaces.Manifest);
            writer.WriteAttributeString("manifest", "key-derivation-name", OdtNamespaces.Manifest, encryption.KeyDerivation);
            writer.WriteAttributeString("manifest", "key-size", OdtNamespaces.Manifest, encryption.KeySize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("manifest", "iteration-count", OdtNamespaces.Manifest, encryption.Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("manifest", "salt", OdtNamespaces.Manifest, encryption.Salt);
            writer.WriteEndElement();

            writer.WriteStartElement("manifest", "start-key-generation", OdtNamespaces.Manifest);
            writer.WriteAttributeString("manifest", "start-key-generation-name", OdtNamespaces.Manifest, encryption.StartKeyGeneration);
            writer.WriteAttributeString("manifest", "key-size", OdtNamespaces.Manifest, encryption.StartKeySize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndElement();

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>Turns XmlWriter's refusal of a character in a name or field code into a library error.</summary>
    private static void WriteChecked(XmlWriter writer, Action<XmlWriter> write)
    {
        try
        {
            write(writer);
        }
        catch (ArgumentException exception) when (exception.GetType() == typeof(ArgumentException))
        {
            throw new DocumentValidationException($"ODT: the document cannot be written: {exception.Message}");
        }
    }
}
