using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// own subset validator for the produced DOCX packages: required parts, content types,
/// relationships, unique IDs and XML well-formedness. No test framework.
/// </summary>
internal static class DocxPackageValidator
{
    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly string[] RequiredParts =
    [
        "[Content_Types].xml",
        "_rels/.rels",
        "word/document.xml",
        "word/_rels/document.xml.rels",
    ];

    public static IReadOnlyList<string> Validate(byte[] package)
    {
        var errors = new List<string>();
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
            if (!entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
                && !entry.FullName.EndsWith(".rels", StringComparison.Ordinal))
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

        ValidateContentTypes(errors, entries, documents);
        ValidateRelationships(errors, entries, documents);
        return errors;
    }

    private static void ValidateContentTypes(
        List<string> errors,
        Dictionary<string, ZipArchiveEntry> entries,
        Dictionary<string, XDocument> documents)
    {
        if (!documents.TryGetValue("[Content_Types].xml", out var contentTypes))
        {
            errors.Add("Content types are missing or invalid.");
            return;
        }

        var defaults = contentTypes.Root!
            .Elements(ContentTypesNs + "Default")
            .Select(element => (string?)element.Attribute("Extension"))
            .Where(extension => extension is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var overrides = contentTypes.Root
            .Elements(ContentTypesNs + "Override")
            .Select(element => (string?)element.Attribute("PartName"))
            .Where(part => part is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var name in entries.Keys)
        {
            if (name == "[Content_Types].xml")
            {
                continue;
            }

            if (overrides.Contains("/" + name))
            {
                continue;
            }

            var extension = Path.GetExtension(name).TrimStart('.');
            if (!defaults.Contains(extension))
            {
                errors.Add($"No content type for {name}");
            }
        }
    }

    private static void ValidateRelationships(
        List<string> errors,
        Dictionary<string, ZipArchiveEntry> entries,
        Dictionary<string, XDocument> documents)
    {
        foreach (var (name, document) in documents)
        {
            if (!name.EndsWith(".rels", StringComparison.Ordinal))
            {
                continue;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var baseDirectory = RelsBaseDirectory(name);

            foreach (var relationship in document.Root!.Elements(RelationshipsNs + "Relationship"))
            {
                var id = (string?)relationship.Attribute("Id") ?? string.Empty;
                if (!ids.Add(id))
                {
                    errors.Add($"Duplicate relationship ID in {name}: {id}");
                }

                if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = (string?)relationship.Attribute("Target") ?? string.Empty;
                var resolved = ResolveTarget(baseDirectory, target);
                if (!entries.ContainsKey(resolved))
                {
                    errors.Add($"Relationship target missing ({name}): {target}");
                }
            }
        }
    }

    private static string RelsBaseDirectory(string relsName)
    {
        if (relsName == "_rels/.rels")
        {
            return string.Empty;
        }

        const string marker = "/_rels/";
        var index = relsName.LastIndexOf(marker, StringComparison.Ordinal);
        var sourcePart = relsName[..index] + "/" + relsName[(index + marker.Length)..^".rels".Length];
        return sourcePart[..(sourcePart.LastIndexOf('/') + 1)];
    }

    private static string ResolveTarget(string baseDirectory, string target)
    {
        var segments = new List<string>();
        foreach (var segment in (baseDirectory + target).Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return string.Join('/', segments);
    }
}
