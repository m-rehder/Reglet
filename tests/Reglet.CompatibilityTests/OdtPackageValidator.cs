using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// own subset validator for the produced ODT packages: mimetype rule, required parts,
/// manifest coverage, root elements and XML well-formedness. No test framework.
/// </summary>
internal static class OdtPackageValidator
{
    private static readonly XNamespace ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string MimeType = "application/vnd.oasis.opendocument.text";

    private static readonly string[] RequiredParts =
    [
        "mimetype",
        "META-INF/manifest.xml",
        "content.xml",
        "styles.xml",
        "meta.xml",
    ];

    public static IReadOnlyList<string> Validate(byte[] package)
    {
        var errors = new List<string>();
        ValidateMimeType(errors, package);

        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);

        foreach (var part in RequiredParts)
        {
            if (!entries.ContainsKey(part))
            {
                errors.Add($"Required part missing: {part}");
            }
        }

        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var stream = entry.Open();
                documents[entry.FullName] = XDocument.Load(stream);
            }
            catch (XmlException exception)
            {
                errors.Add($"XML fehlerhaft: {entry.FullName} ({exception.Message})");
            }
        }

        ValidateManifest(errors, entries, documents);

        if (documents.TryGetValue("content.xml", out var content)
            && content.Root?.Name != OfficeNs + "document-content")
        {
            errors.Add("content.xml has no office:document-content as root.");
        }

        return errors;
    }

    private static void ValidateMimeType(List<string> errors, byte[] package)
    {
        if (package.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(0, 4)) != 0x04034B50)
        {
            errors.Add("mimetype is not the first ZIP entry.");
            return;
        }

        var method = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(8, 2));
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(18, 4));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(28, 2));
        var name = Encoding.ASCII.GetString(package, 30, nameLength);

        if (name != "mimetype")
        {
            errors.Add($"First entry is '{name}' instead of 'mimetype'.");
        }

        if (method != 0)
        {
            errors.Add("mimetype is compressed.");
        }

        var dataStart = 30 + nameLength + extraLength;
        var data = Encoding.ASCII.GetString(package, dataStart, (int)compressedSize);
        if (data != MimeType)
        {
            errors.Add($"mimetype content is '{data}'.");
        }
    }

    private static void ValidateManifest(
        List<string> errors,
        Dictionary<string, ZipArchiveEntry> entries,
        Dictionary<string, XDocument> documents)
    {
        if (!documents.TryGetValue("META-INF/manifest.xml", out var manifest))
        {
            errors.Add("Manifest is missing or invalid.");
            return;
        }

        var listed = manifest.Root!
            .Elements(ManifestNs + "file-entry")
            .Select(element => (string?)element.Attribute(ManifestNs + "full-path"))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var name in entries.Keys)
        {
            if (name is "mimetype" or "META-INF/manifest.xml")
            {
                continue;
            }

            if (!listed.Contains(name))
            {
                errors.Add($"Manifest does not list {name}.");
            }
        }

        if (!listed.Contains("/"))
        {
            errors.Add("Manifest root entry missing.");
        }
    }
}
