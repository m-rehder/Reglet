namespace Reglet;

/// <summary>Output format of a document.</summary>
public enum DocumentFormat
{
    /// <summary>WordprocessingML according to ECMA-376 (<c>.docx</c>).</summary>
    Docx,

    /// <summary>OpenDocument Text according to ODF 1.3 (<c>.odt</c>).</summary>
    Odt,

    /// <summary>Portable Document Format according to ISO 32000-1 (<c>.pdf</c>).</summary>
    Pdf,

    /// <summary>HTML (a subset, ADR-0032).</summary>
    Html,
}
