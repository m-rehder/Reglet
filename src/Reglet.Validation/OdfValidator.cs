using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// ODF packages (ODF 1.3 part 2): the mimetype entry first, stored and without an extra field; the manifest listing every
/// file and only existing ones, with the package's media type; every XML part well-formed. shortcut: no schema check
/// against the ODF RELAX NG schema yet (stage 2 of ADR-0044).
/// </summary>
internal static class OdfValidator
{
    private const string Manifest = "META-INF/manifest.xml";
    private static readonly XNamespace M = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";

    public static string Validate(byte[] data, ZipArchive zip, IssueList issues, CancellationToken cancellationToken)
    {
        var mimetype = zip.GetEntry("mimetype") is { } mimeEntry ? ReadText(mimeEntry) : null;
        var format = mimetype switch
        {
            "application/vnd.oasis.opendocument.text" or "application/vnd.oasis.opendocument.text-template" => "ODT",
            "application/vnd.oasis.opendocument.spreadsheet" or "application/vnd.oasis.opendocument.spreadsheet-template" => "ODS",
            "application/vnd.oasis.opendocument.presentation" => "ODP",
            "application/vnd.oasis.opendocument.graphics" => "ODG",
            _ => "ODF",
        };
        issues.Format = format;
        CheckMimetypeEntry(data, mimetype, issues);

        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        var encrypted = new HashSet<string>(StringComparer.Ordinal);
        if (zip.GetEntry(Manifest) is not { } manifestEntry)
        {
            issues.Error("[package]", null, $"The package has no {Manifest}.");
        }
        else if (PackageXml.Load(manifestEntry, issues) is { Root: { } root })
        {
            if (root.Name != M + "manifest")
            {
                issues.Error(Manifest, MarkupCompatibility.Line(root), "The root element is not manifest:manifest.");
            }

            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in root.Elements(M + "file-entry"))
            {
                var path = (string?)entry.Attribute(M + "full-path");
                var line = MarkupCompatibility.Line(entry);
                if (path is null)
                {
                    issues.Error(Manifest, line, "A file-entry has no manifest:full-path.");
                    continue;
                }

                listed.Add(path);
                if (entry.Element(M + "encryption-data") is not null)
                {
                    encrypted.Add(path);
                }

                if (path == "/")
                {
                    if (mimetype is not null && (string?)entry.Attribute(M + "media-type") != mimetype)
                    {
                        issues.Error(Manifest, line, $"The media type of '/' is '{(string?)entry.Attribute(M + "media-type")}', but the mimetype entry says '{mimetype}'.");
                    }
                }
                else if (!path.EndsWith('/') && !files.ContainsKey(path))
                {
                    issues.Error(Manifest, line, $"The manifest lists '{path}', which is not in the package.");
                }
            }

            if (!listed.Contains("/"))
            {
                issues.Error(Manifest, null, "The manifest has no entry for the package itself ('/').");
            }

            foreach (var name in files.Keys.Where(name => name != "mimetype" && !name.StartsWith("META-INF/", StringComparison.Ordinal) && !listed.Contains(name)))
            {
                issues.Error(Manifest, null, $"'{name}' is in the package but not in the manifest; LibreOffice reports such a file as corrupt.");
            }
        }

        foreach (var (name, entry) in files.Where(file => file.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && file.Key != Manifest && !encrypted.Contains(file.Key)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageXml.Load(entry, issues);
        }

        return format;
    }

    /// <summary>The first local file header: the name, the compression method and the extra field length.</summary>
    private static void CheckMimetypeEntry(byte[] data, string? mimetype, IssueList issues)
    {
        if (data.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x04034B50)
        {
            issues.Error("mimetype", null, "The package does not start with a ZIP entry.");
            return;
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26));
        var name = data.Length >= 30 + nameLength ? Encoding.ASCII.GetString(data, 30, nameLength) : string.Empty;
        if (name != "mimetype")
        {
            issues.Error("mimetype", null, mimetype is null
                ? "The package has no mimetype entry."
                : $"The mimetype entry must be the first in the package, but '{name}' is.");
            return;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)) != 0)
        {
            issues.Error("mimetype", null, "The mimetype entry must be stored uncompressed.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28)) != 0)
        {
            issues.Warning("mimetype", null, "The mimetype entry has an extra field, which ODF does not allow (most programs still read it).");
        }

        if (mimetype is { Length: 0 } || (mimetype is not null && !mimetype.StartsWith("application/vnd.oasis.opendocument.", StringComparison.Ordinal)))
        {
            issues.Error("mimetype", null, $"'{mimetype}' is not an ODF media type.");
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(ReaderGuard.OpenBounded(entry), Encoding.ASCII);
        return reader.ReadToEnd();
    }
}
