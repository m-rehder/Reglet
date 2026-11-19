namespace Reglet;

/// <summary>Options for the PDF writer. Deliberately kept small and optional.</summary>
public sealed record PdfWriteOptions : WriteOptions
{
    /// <summary>The default options.</summary>
    public static PdfWriteOptions Default { get; } = new();

    /// <summary>
    /// optional caller-provided TrueType fonts (font family → bytes), per family
    /// a file. If a text run's family is contained, the font is embedded
    /// (Unicode); bold and italic are produced synthetically. For real bold/italic files
    /// <see cref="FontCollection"/>. Without a matching font, a default font is used
    /// with WinAnsi are used.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]>? Fonts { get; init; }

    /// <summary>
    /// optional font collection with styles per family (normal, bold, italic,
    /// bold-italic). For a text run the matching style is embedded; only missing
    /// attributes are simulated synthetically. Is merged with <see cref="Fonts"/>;
    /// a family may appear in only one of the two specifications.
    /// </summary>
    public FontCollection? FontCollection { get; init; }

    /// <summary>Desired PDF/A conformance (phase R). Default: <see cref="PdfAConformance.None"/>.</summary>
    public PdfAConformance PdfA { get; init; }

    /// <summary>
    /// optional attachments (phase R, R03). Require PDF/A-3 (or no PDF/A); with PDF/A-2
    /// the writer raises an <see cref="ArgumentException"/>.
    /// </summary>
    public IReadOnlyList<PdfAttachment>? Attachments { get; init; }

    /// <summary>
    /// optional ICC output profile for PDF/A. If missing, a bundled
    /// a minimal sRGB profile is used.
    /// </summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>
    /// optional page template (phase S, S04): headers/footers, watermark and
    /// page frame. Headers/footers of the template apply to sections without their own
    /// header/footer; watermark and frame are drawn on every page.
    /// </summary>
    public PageTemplate? PageTemplate { get; init; }

    /// <summary>
    /// optional encryption (phase AD, AD02): standard security handler with AES-256-CBC (R6).
    /// The output is not deterministic (random IVs). Encryption cannot be combined with
    /// PDF/A.
    /// </summary>
    public PdfEncryptionOptions? Encryption { get; init; }

    /// <summary>
    /// optional hyphenation patterns (phase AJ, BJ01). With patterns the writer breaks the
    /// words at the allowed positions and draws the hyphen; without them only the soft
    /// hyphens (U+00AD) in the text are break opportunities. The library ships no
    /// dictionary; the caller supplies the patterns of the language.
    /// </summary>
    public HyphenationPatterns? Hyphenation { get; init; }

    /// <summary>
    /// optional digital signature (phase BJ, BJ02). The document is signed with the private
    /// key of the certificate; the signature is a detached CMS SignedData in the signature
    /// dictionary of the PDF. A signed document is built in memory (the signature covers the
    /// written bytes) and cannot be encrypted.
    /// </summary>
    public PdfSignatureOptions? Signature { get; init; }

    /// <summary>
    /// optional non-link annotations (OF-F01): sticky notes, highlights, stamps and free text
    /// boxes. An annotation whose page does not exist is reported and skipped.
    /// </summary>
    public IReadOnlyList<PdfAnnotation>? Annotations { get; init; }

    /// <summary>
    /// how the comments of the document are written (ADR-0026); <see cref="PdfCommentMode.None"/> by default.
    /// </summary>
    public PdfCommentMode Comments { get; init; } = PdfCommentMode.None;

    /// <summary>
    /// which state of the tracked changes the PDF shows (ADR-0027); <see cref="PdfRevisionMode.Final"/> by default.
    /// </summary>
    public PdfRevisionMode Revisions { get; init; } = PdfRevisionMode.Final;

    /// <summary>
    /// optional window flags of the viewer (OF-F02). The flags are written into the
    /// <c>/ViewerPreferences</c> dictionary of the catalog.
    /// </summary>
    public PdfViewerPreferences ViewerPreferences { get; init; } = PdfViewerPreferences.None;

    /// <summary>
    /// optional page layout of the viewer (OF-F02). Written as <c>/PageLayout</c> into the
    /// catalog; <see cref="PdfPageLayout.Default"/> writes nothing.
    /// </summary>
    public PdfPageLayout PageLayout { get; init; } = PdfPageLayout.Default;

    /// <summary>
    /// optional mode the viewer opens the document in (OF-F02). Written as <c>/PageMode</c>
    /// into the catalog; <see cref="PdfPageMode.Default"/> writes nothing.
    /// </summary>
    public PdfPageMode PageMode { get; init; } = PdfPageMode.Default;
}
