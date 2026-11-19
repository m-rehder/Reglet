namespace Reglet;

/// <summary>Desired PDF/A conformance (phase R).</summary>
public enum PdfAConformance
{
    /// <summary>No PDF/A (standard PDF 1.7).</summary>
    None,

    /// <summary>PDF/A-2b (ISO 19005-2, Basic).</summary>
    PdfA2b,

    /// <summary>PDF/A-3b (ISO 19005-3, basic, allows attachments).</summary>
    PdfA3b,
}

/// <summary>An embedded attachment for PDF/A-3 (phase R, R03).</summary>
public sealed record PdfAttachment
{
    /// <summary>File name in the PDF (also as a Unicode name).</summary>
    public required string Name { get; init; }

    /// <summary>The attachment data.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>MIME-Typ (z. B. <c>text/plain</c>).</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Relationship to the document (AFRelationship, e.g. <c>Data</c>, <c>Source</c>).</summary>
    public string Relationship { get; init; } = "Data";
}
