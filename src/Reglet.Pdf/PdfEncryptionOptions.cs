namespace Reglet;

/// <summary>
/// Permissions of an encrypted PDF (ISO 32000-1, 7.6.3.2, the bits of the
/// <c>/P</c> entry). They are a declaration to the reader; a reader may ignore them.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permission (the document may only be opened).</summary>
    None = 0,

    /// <summary>Printing (bit 3).</summary>
    Print = 1 << 2,

    /// <summary>Changing the contents (bit 4).</summary>
    ModifyContents = 1 << 3,

    /// <summary>Copying text and graphics (bit 5).</summary>
    Copy = 1 << 4,

    /// <summary>Annotations and form fields (bit 6).</summary>
    Annotate = 1 << 5,

    /// <summary>Filling in form fields (bit 9).</summary>
    FillForms = 1 << 8,

    /// <summary>Extraction for accessibility (bit 10).</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Assembling the document (bit 11).</summary>
    Assemble = 1 << 10,

    /// <summary>High-quality printing (bit 12).</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>All permissions (default).</summary>
    All = Print | ModifyContents | Copy | Annotate | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality,
}

/// <summary>
/// PDF encryption (phase AD, AD02): the standard security handler with revision 6 and
/// AES-256-CBC (<c>/CFM /AESV3</c>, ISO 32000-2, 7.6). The document is encrypted with
/// <see cref="UserPassword"/>; <see cref="OwnerPassword"/> may additionally be used to open
/// and administrate it.
/// </summary>
/// <remarks>
/// Because the file key and every string and stream use random values, the output is
/// intentionally not deterministic. Encryption and PDF/A are mutually exclusive (PDF/A
/// requires unencrypted files).
/// </remarks>
public sealed record PdfEncryptionOptions
{
    /// <summary>User password; may be empty (the file can then be opened without a password).</summary>
    public string UserPassword { get; init; } = string.Empty;

    /// <summary>
    /// Owner password. If it is null or empty, a random one is used, so that <see cref="Permissions"/>
    /// also bind whoever can open the file; nobody can then administrate the document.
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>Permissions for readers that honour them. Default: <see cref="PdfPermissions.All"/>.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;
}
