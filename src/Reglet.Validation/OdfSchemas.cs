using System.Xml;
using System.Xml.Linq;
using Reglet.RelaxNg;

namespace Reglet;

/// <summary>
/// The ODF 1.3 and 1.4 RELAX NG schemas (OASIS, embedded unmodified): the document schema for content, styles, meta and
/// settings parts, and the manifest schema. A part declaring version 1.4 is checked against 1.4, any other against 1.3
/// (the earlier versions' documents are, with few exceptions, valid 1.3). Loaded once each; validation against one
/// schema is serialised, because the derivative algorithm grows the schema's shared patterns.
/// </summary>
internal static class OdfSchemas
{
    private static readonly Lazy<RngSchema> Document13 = new(() => Load("OpenDocument-v1.3-schema.rng"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RngSchema> Document14 = new(() => Load("OpenDocument-v1.4-schema.rng"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RngSchema> Manifest13 = new(() => Load("OpenDocument-v1.3-manifest-schema.rng"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RngSchema> Manifest14 = new(() => Load("OpenDocument-v1.4-manifest-schema.rng"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The document schema for a part's office:version.</summary>
    public static RngSchema Document(string? version) => version == "1.4" ? Document14.Value : Document13.Value;

    /// <summary>The manifest schema for a manifest's manifest:version.</summary>
    public static RngSchema Manifest(string? version) => version == "1.4" ? Manifest14.Value : Manifest13.Value;

    /// <summary>
    /// Validates a part. Markup in namespaces the schema does not know is removed first: ODF calls a document with such
    /// foreign markup (LibreOffice's loext, for example) an extended conforming document when it is valid without it.
    /// </summary>
    public static void Validate(RngSchema schema, XElement root, string part, IssueList issues)
    {
        RemoveForeign(root, schema.Namespaces);
        lock (schema)
        {
            new RngValidator(schema, (node, message) => issues.Error(part, MarkupCompatibility.Line(node), message)).Validate(root);
        }
    }

    private static void RemoveForeign(XElement root, HashSet<string> namespaces)
    {
        foreach (var element in root.Descendants().Where(element => !namespaces.Contains(element.Name.NamespaceName)).ToList())
        {
            element.Remove();
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && !namespaces.Contains(attribute.Name.NamespaceName)).ToList())
            {
                attribute.Remove();
            }
        }
    }

    private static RngSchema Load(string file)
    {
        using var stream = typeof(OdfSchemas).Assembly.GetManifestResourceStream("Reglet.Validation.Schemas." + file)
            ?? throw new InvalidOperationException($"The schema {file} is not embedded.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return new RngSchema(XDocument.Load(reader).Root!);
    }
}
