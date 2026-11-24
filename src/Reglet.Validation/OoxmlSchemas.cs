using System.Xml;
using System.Xml.Schema;

namespace Reglet;

/// <summary>
/// The ECMA-376 5th edition Transitional schemas (part 4) and the OPC content-types and relationships schemas (part 2),
/// embedded unmodified and compiled once. Imports resolve to the embedded files only; nothing is fetched.
/// </summary>
internal static class OoxmlSchemas
{
    private const string Prefix = "Reglet.Validation.Schemas.";
    private static readonly Uri BaseUri = new("file:///reglet/schemas/");
    private static readonly Lazy<(XmlSchemaSet Set, HashSet<string> Namespaces)> Compiled = new(Compile, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The compiled set; shared read-only by every validation.</summary>
    public static XmlSchemaSet Set => Compiled.Value.Set;

    /// <summary>Whether a namespace has a schema here.</summary>
    public static bool IsKnown(string ns) => Compiled.Value.Namespaces.Contains(ns);

    /// <summary>Microsoft's Office 2010+ extension namespaces, which Office 2019 understands; their content is not schema-checked.</summary>
    public static bool IsOfficeExtension(string ns) => ns.StartsWith("http://schemas.microsoft.com/office/", StringComparison.Ordinal);

    private static (XmlSchemaSet, HashSet<string>) Compile()
    {
        var assembly = typeof(OoxmlSchemas).Assembly;
        var resolver = new EmbeddedResolver(file => assembly.GetManifestResourceStream(Prefix + file));
        var set = new XmlSchemaSet { XmlResolver = resolver };
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".xsd", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var file = name[Prefix.Length..];
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = resolver }, new Uri(BaseUri, file).AbsoluteUri);
            set.Add(null, reader);
        }

        set.Compile();
        var namespaces = set.Schemas().Cast<XmlSchema>().Select(schema => schema.TargetNamespace ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        return (set, namespaces);
    }

    /// <summary>Serves the embedded schemas by file name and refuses everything else.</summary>
    private sealed class EmbeddedResolver(Func<string, Stream?> open) : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var file = absoluteUri.Segments.LastOrDefault();
            return absoluteUri.AbsoluteUri.StartsWith(BaseUri.AbsoluteUri, StringComparison.Ordinal) && file is not null
                && open(file) is { } stream
                ? stream
                : throw new XmlException($"The schema '{absoluteUri}' is not bundled with the validator.");
        }
    }
}
