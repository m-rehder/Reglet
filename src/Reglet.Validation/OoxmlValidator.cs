using System.IO.Compression;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Reglet;

/// <summary>
/// Office Open XML packages: part names, content types, relationships and their targets, well-formed XML and the
/// ECMA-376 Transitional schemas after markup compatibility.
/// </summary>
internal static class OoxmlValidator
{
    private const string Package = "[package]";
    private const string ContentTypesPart = "[Content_Types].xml";
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static string Validate(ZipArchive zip, IssueList issues, CancellationToken cancellationToken)
    {
        issues.Format = "OOXML";
        var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!names.Add(entry.FullName))
            {
                issues.Error(Package, null, $"The part name '{entry.FullName}' occurs twice (part names ignore case).");
            }

            try
            {
                PackagePath.Validate(entry.FullName);
            }
            catch (Exception exception) when (exception is DocumentValidationException or ArgumentException)
            {
                issues.Error(Package, null, $"'{entry.FullName}' is not a valid part name: {exception.Message}");
            }
        }

        var parts = entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var contentTypes = ReadContentTypes(parts, issues);
        var format = ReadRelationships(parts, contentTypes, issues);
        issues.Format = format;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name == ContentTypesPart || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = contentTypes(name);
            if (type is null)
            {
                issues.Error(name, null, "The part has no content type ([Content_Types].xml has neither a Default for its extension nor an Override for it).");
                continue;
            }

            if (type.EndsWith("+xml", StringComparison.Ordinal) || type is "application/xml" or "text/xml")
            {
                ValidateXml(entry, issues);
            }
        }

        return format;
    }

    private static void ValidateXml(ZipArchiveEntry entry, IssueList issues)
    {
        if (PackageXml.Load(entry, issues) is not { Root: { } root } document)
        {
            return;
        }

        var ns = root.Name.NamespaceName;
        if (!OoxmlSchemas.IsKnown(ns))
        {
            // Custom XML, core properties, Office extension parts: no ECMA-376 schema to check them against.
            return;
        }

        MarkupCompatibility.Apply(root, entry.FullName, issues);
        Validate(document, entry.FullName, issues);
    }

    private static void Validate(XDocument document, string part, IssueList issues)
    {
        // The sender is the node at fault; the document was loaded with line info.
        document.Validate(OoxmlSchemas.Set, (sender, args) =>
        {
            if (args.Severity == XmlSeverityType.Error)
            {
                issues.Error(part, sender is XObject node ? MarkupCompatibility.Line(node) : null, args.Message);
            }
        });
    }

    /// <summary>Checks [Content_Types].xml and returns the content type of a part name (null when it has none).</summary>
    private static Func<string, string?> ReadContentTypes(Dictionary<string, ZipArchiveEntry> parts, IssueList issues)
    {
        if (!parts.TryGetValue(ContentTypesPart, out var entry))
        {
            issues.Error(Package, null, "The package has no [Content_Types].xml.");
            return _ => null;
        }

        if (PackageXml.Load(entry, issues) is not { Root: { } root } document)
        {
            return _ => null;
        }

        Validate(document, ContentTypesPart, issues);
        var defaults = root.Elements(ContentTypes + "Default")
            .Where(element => element.Attribute("Extension") is not null && element.Attribute("ContentType") is not null)
            .GroupBy(element => (string)element.Attribute("Extension")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (string)group.First().Attribute("ContentType")!, StringComparer.OrdinalIgnoreCase);
        var overrides = root.Elements(ContentTypes + "Override")
            .Where(element => element.Attribute("PartName") is not null && element.Attribute("ContentType") is not null)
            .GroupBy(element => ((string)element.Attribute("PartName")!).TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (string)group.First().Attribute("ContentType")!, StringComparer.OrdinalIgnoreCase);

        foreach (var name in overrides.Keys.Where(name => !parts.ContainsKey(name)))
        {
            issues.Warning(ContentTypesPart, null, $"An Override names the part '/{name}', which is not in the package.");
        }

        return name => overrides.TryGetValue(name, out var type) ? type
            : defaults.TryGetValue(Path.GetExtension(name).TrimStart('.'), out var byExtension) ? byExtension : null;
    }

    /// <summary>Checks every relationships part and its targets; returns the format named by the main part's content type.</summary>
    private static string ReadRelationships(Dictionary<string, ZipArchiveEntry> parts, Func<string, string?> contentTypes, IssueList issues)
    {
        var format = "OOXML";
        if (!parts.ContainsKey("_rels/.rels"))
        {
            issues.Error(Package, null, "The package has no _rels/.rels, so no program finds its main part.");
        }

        foreach (var (name, entry) in parts.Where(part => part.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var folder = name.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
            if (folder < 0)
            {
                issues.Warning(name, null, "A relationships part outside a _rels folder is not used by any program.");
                continue;
            }

            var source = name[..folder] + name[(folder + "_rels/".Length)..^".rels".Length];
            if (source.Length > 0 && !parts.ContainsKey(source))
            {
                issues.Warning(name, null, $"The relationships belong to the part '{source}', which is not in the package.");
            }

            if (PackageXml.Load(entry, issues) is not { Root: { } root } document)
            {
                continue;
            }

            Validate(document, name, issues);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var main = false;
            foreach (var relationship in root.Elements(Relationships + "Relationship"))
            {
                var id = (string?)relationship.Attribute("Id") ?? string.Empty;
                var line = MarkupCompatibility.Line(relationship);
                if (!ids.Add(id))
                {
                    issues.Error(name, line, $"The relationship id '{id}' occurs twice.");
                }

                if ((string?)relationship.Attribute("TargetMode") == "External" || (string?)relationship.Attribute("Target") is not { } target)
                {
                    continue;
                }

                var hash = target.IndexOf('#');
                var resolved = OpcPackageWriter.ResolveTarget(source, hash < 0 ? target : target[..hash]);
                if (resolved.Length > 0 && !parts.ContainsKey(resolved))
                {
                    issues.Error(name, line, $"The relationship '{id}' points to '{resolved}', which is not in the package.");
                }

                if (source.Length == 0 && (string?)relationship.Attribute("Type") == OfficeDocument)
                {
                    main = true;
                    format = contentTypes(resolved) switch
                    {
                        { } type when type.Contains("wordprocessingml", StringComparison.Ordinal) => "DOCX",
                        { } type when type.Contains("spreadsheetml", StringComparison.Ordinal) => "XLSX",
                        { } type when type.Contains("presentationml", StringComparison.Ordinal) => "PPTX",
                        _ => format,
                    };
                }
            }

            if (source.Length == 0 && !main)
            {
                issues.Error(name, null, "The package relationships name no main part (officeDocument).");
            }
        }

        return format;
    }
}
