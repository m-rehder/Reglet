namespace Reglet;

/// <summary>Options for the DOCX writer. Deliberately kept small and optional.</summary>
public sealed record DocxWriteOptions : WriteOptions
{
    /// <summary>Default options.</summary>
    public static DocxWriteOptions Default { get; } = new();

    /// <summary>
    /// optional DOCX template. If set, the template's package is adopted,
    /// <c>word/document.xml</c> is patched and the placeholders are bound; styles of the template
    /// are preserved.
    /// </summary>
    public DocumentTemplate? Template { get; init; }

    /// <summary>
    /// editing protection written as <c>w:documentProtection</c> (phase AD, AD03). This is
    /// protection without encryption. <see cref="DocxProtection.None"/> leaves an existing
    /// protection from a read source unchanged; <see cref="DocxProtection.Remove"/> removes it.
    /// </summary>
    public DocxProtection Protection { get; init; } = DocxProtection.None;

    /// <summary>
    /// optional password of the editing protection (phase AD, AD03). Without a password the
    /// protection can be removed without a prompt; with one Word asks for the password before
    /// the protection is removed. The password is stored as a salted hash, never in clear text.
    /// </summary>
    public string? ProtectionPassword { get; init; }

    /// <summary>
    /// enables the automatic hyphenation of Word (phase AJ, BJ01): the document is written
    /// with <c>w:autoHyphenation</c>, so Word hyphenates the words with its own dictionary.
    /// The library itself does not hyphenate the text of a DOCX.
    /// </summary>
    public bool AutoHyphenation { get; init; }

    /// <summary>
    /// the distance between the word and the hyphen (phase AJ, BJ01); the default is the
    /// Word default of 0.25 inch. Only used with <see cref="AutoHyphenation"/>.
    /// </summary>
    public Length? HyphenationZone { get; init; }

    /// <summary>
    /// optional encryption of the package (OF-E01): the DOCX is wrapped in an ECMA-376 agile
    /// encrypted container and opens with the password only.
    /// </summary>
    public DocxEncryptionOptions? Encryption { get; init; }

    /// <summary>
    /// optional digital signature (OF-E02): the finished package gets an OPC signature part with
    /// an XML signature over its parts. With <see cref="Encryption"/> the signature is added
    /// before the package is encrypted.
    /// </summary>
    public DocxSignatureOptions? Signature { get; init; }

    /// <summary>
    /// fonts to embed in the file: every style of the families the document uses that this
    /// collection holds is written obfuscated (<c>.odttf</c>) with <c>w:embedTrueTypeFonts</c>, so Word
    /// shows the text with them on a computer that does not have them. Fonts whose licence forbids
    /// embedding are skipped and reported. Not applied to a document that adopts a source package.
    /// </summary>
    public FontCollection? EmbeddedFonts { get; init; }

    /// <summary>
    /// names of bookmark ranges (a <see cref="Bookmark"/> with its <see cref="BookmarkEnd"/>) that stay
    /// editable for everyone while <see cref="Protection"/> is on: they are wrapped in
    /// <c>w:permStart</c> and <c>w:permEnd</c>. A name without a bookmark range is reported and ignored.
    /// Editing groups of single users are not modelled.
    /// </summary>
    public IReadOnlyCollection<string>? EditableRanges { get; init; }
}
