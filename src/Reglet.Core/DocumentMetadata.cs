using System.Collections.ObjectModel;

namespace Reglet;

/// <summary>Optional document metadata.</summary>
public sealed record DocumentMetadata
{
    private IReadOnlyList<string>? _keywords;
    private IReadOnlyDictionary<string, string>? _customProperties;
    private IReadOnlyDictionary<string, string>? _variables;
    private IReadOnlyList<CustomXmlPart>? _customXmlParts;

    /// <summary>Title of the document.</summary>
    public string? Title { get; init; }

    /// <summary>Author or creator.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// The application that created the document. Written to PDF as Info <c>/Creator</c> and XMP
    /// <c>xmp:CreatorTool</c>; DOCX and ODT do not store it.
    /// </summary>
    public string? Creator { get; init; }

    /// <summary>Subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Keywords.</summary>
    public IReadOnlyList<string>? Keywords
    {
        get => _keywords;
        init => _keywords = value is null ? null : ModelCollections.Snapshot(value, nameof(Keywords));
    }

    /// <summary>Language (e.g. <c>en-GB</c>).</summary>
    public string? Language { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// custom properties (name → value). Are written in DOCX as
    /// <c>docProps/custom.xml</c>, in ODT as <c>meta:user-defined</c> and in PDF as non-standard
    /// entries of the Info dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomProperties
    {
        get => _customProperties;
        init => _customProperties = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>
    /// document variables (name → value) that <see cref="DocVariableField"/> refers to. Are
    /// written in DOCX as <c>w:docVars</c> and in ODT as <c>text:user-field-decl</c>; the PDF
    /// resolves the fields with them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Variables
    {
        get => _variables;
        init => _variables = value is null
            ? null
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>
    /// The VBA project (DOCX only, ADR-0028). Opaque bytes, never executed; the DOCX reader fills it only with
    /// <c>DocxReadOptions.KeepMacros</c>. Setting it makes the DOCX writer produce a macro-enabled package.
    /// </summary>
    public MacroProject? MacroProject { get; init; }

    /// <summary>
    /// Custom XML parts, written to DOCX as <c>customXml/itemN.xml</c> and read back from there.
    /// ODT and PDF do not store them.
    /// </summary>
    public IReadOnlyList<CustomXmlPart>? CustomXmlParts
    {
        get => _customXmlParts;
        init => _customXmlParts = value is null ? null : ModelCollections.Snapshot(value, nameof(CustomXmlParts));
    }
}
