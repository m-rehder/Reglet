namespace Reglet;

/// <summary>Options for the ODT writer. Deliberately kept small and optional.</summary>
public sealed record OdtWriteOptions : WriteOptions
{
    /// <summary>The default options.</summary>
    public static OdtWriteOptions Default { get; } = new();

    /// <summary>
    /// optional ODT template. If set, the template's package is adopted,
    /// <c>content.xml</c> is patched and the placeholders are bound; named styles and
    /// page design of the template are preserved.
    /// </summary>
    public DocumentTemplate? Template { get; init; }

    /// <summary>
    /// lets the target program hyphenate the words with its own dictionary
    /// (<c>fo:hyphenate="true"</c> on the default paragraph style). The soft hyphens of the text
    /// remain break opportunities in any case.
    /// </summary>
    public bool AutoHyphenation { get; init; }

    /// <summary>
    /// optional encryption of the package (OF-E05): every file entry except the <c>mimetype</c>
    /// and the manifest is encrypted with AES-256-CBC and a password-derived key. The output is
    /// not deterministic (random salt and IV). Encryption cannot be combined with a template.
    /// </summary>
    public OdtEncryptionOptions? Encryption { get; init; }

    /// <summary>
    /// optional digital signature (OF-E05): the finished package gets
    /// <c>META-INF/documentsignatures.xml</c> with an XML signature over its parts. With
    /// <see cref="Encryption"/> the signature is added after encryption, so encrypted parts are
    /// signed in their encrypted form and the signature file stays unencrypted.
    /// </summary>
    public OdtSignatureOptions? Signature { get; init; }

    /// <summary>
    /// fonts to embed in the file: every style of the families the document uses that this
    /// collection holds is written to <c>Fonts/</c> and declared with <c>svg:font-face-uri</c>, so
    /// LibreOffice shows the text with them on a computer that does not have them. Fonts whose
    /// licence forbids embedding are skipped and reported.
    /// </summary>
    public FontCollection? EmbeddedFonts { get; init; }
}
