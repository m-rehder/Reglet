namespace Reglet;

/// <summary>
/// immutable root object of a document: metadata, default style and at least
/// one section. A built document can be written to several formats in parallel.
/// </summary>
public sealed record PortableDocument
{
    private DocumentDefaults _defaults = DocumentDefaults.Default;
    private DocumentDefaults? _sourceDefaults;
    private IReadOnlyList<ParagraphStyle> _styles = [];
    private IReadOnlyList<CharacterStyle> _characterStyles = [];
    private IReadOnlyList<NumberingDefinition> _numbering = [];
    private DocumentMetadata _metadata = new();
    private IReadOnlyList<Section> _sections = [];
    private DocumentMetadata? _sourceMetadata;
    private IReadOnlyList<Section>? _sourceSections;

    /// <summary>Creates a document with exactly one section.</summary>
    public PortableDocument(DocumentMetadata metadata, Section section)
        : this(metadata, new[] { section })
    {
    }

    /// <summary>Creates a document with several sections.</summary>
    /// <exception cref="ArgumentNullException">Metadata or sections are null.</exception>
    /// <exception cref="ArgumentException">There is no section.</exception>
    public PortableDocument(DocumentMetadata metadata, IEnumerable<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(sections);

        Metadata = metadata;
        SetSections(sections);
    }

    /// <summary>The document metadata. Settable via <c>with</c>, e.g. to adjust a read document.</summary>
    public DocumentMetadata Metadata
    {
        get => _metadata;
        init
        {
            _metadata = value ?? throw new ArgumentNullException(nameof(Metadata));
            _sourceMetadata ??= _metadata;
        }
    }

    /// <summary>The sections of the document (at least one). Settable via <c>with</c>.</summary>
    /// <exception cref="ArgumentException">There is no section.</exception>
    public IReadOnlyList<Section> Sections
    {
        get => _sections;
        init => SetSections(value);
    }

    /// <summary>The document-wide default values (default style).</summary>
    public DocumentDefaults Defaults
    {
        get => _defaults;
        init
        {
            _defaults = value ?? throw new ArgumentNullException(nameof(Defaults));
            _sourceDefaults ??= _defaults;
        }
    }

    /// <summary>
    /// named paragraph styles defined by the document (each with
    /// <see cref="ParagraphStyle.StyleName"/> set); empty means none. DOCX and ODT write the
    /// definitions, the PDF uses them for named style references.
    /// </summary>
    public IReadOnlyList<ParagraphStyle> Styles
    {
        get => _styles;
        init => _styles = value is null ? [] : ModelCollections.Snapshot(value, nameof(Styles));
    }

    /// <summary>
    /// named character styles defined by the document (see <see cref="CharacterStyle"/>); empty
    /// means none. A run references a style by name (<see cref="TextRun.CharacterStyleName"/>).
    /// </summary>
    public IReadOnlyList<CharacterStyle> CharacterStyles
    {
        get => _characterStyles;
        init => _characterStyles = value is null
            ? []
            : ModelCollections.Snapshot(value, nameof(CharacterStyles));
    }

    /// <summary>
    /// the numbering definitions of the document (empty means none). A list references one by
    /// <see cref="ListBlock.NumberingName"/>; the writers reject a name that does not resolve.
    /// </summary>
    /// <exception cref="DocumentValidationException">Two definitions share a name.</exception>
    public IReadOnlyList<NumberingDefinition> Numbering
    {
        get => _numbering;
        init
        {
            var definitions = value is null ? [] : ModelCollections.Snapshot(value, nameof(Numbering));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (!names.Add(definition.Name))
                {
                    throw new DocumentValidationException(
                        $"Duplicate numbering definition '{definition.Name}'.", nameof(Numbering));
                }
            }

            _numbering = definitions;
        }
    }

    /// <summary>
    /// opaque bytes of the source package the model was read from, used by the writers to carry
    /// over package parts they do not generate (round-trip preservation). Null for a model
    /// created from scratch. Format-specific; a writer ignores data it does not understand.
    /// </summary>
    internal ReadOnlyMemory<byte>? SourcePackage { get; init; }

    /// <summary>
    /// the named styles as they were read from the source package. The writers use them to
    /// recognize styles that the reader has not changed, so that the source definitions can be
    /// preserved (phase AC, AC01).
    /// </summary>
    internal IReadOnlyList<ParagraphStyle>? SourceStyles { get; init; }

    /// <summary>
    /// the named character styles as they were read from the source package. The writers use
    /// them to recognize styles that the reader has not changed, so that the source
    /// definitions can be preserved.
    /// </summary>
    internal IReadOnlyList<CharacterStyle>? SourceCharacterStyles { get; init; }

    /// <summary>The metadata as it was when the document was created or read (change detection).</summary>
    internal DocumentMetadata? SourceMetadata => _sourceMetadata;

    /// <summary>The defaults as they were when the document was created or read (change detection).</summary>
    internal DocumentDefaults SourceDefaults => _sourceDefaults ?? DocumentDefaults.Default;

    /// <summary>The sections list as first set (change detection: reference equality means untouched).</summary>
    internal IReadOnlyList<Section>? SourceSections => _sourceSections;

    /// <summary>
    /// For a document read from ODT: the source <c>content.xml</c> element index of every
    /// top-level block (a paragraph with an inline frame contributes a paragraph and image blocks
    /// that share one index). The ODT writer uses it to preserve unchanged blocks verbatim (AF12).
    /// </summary>
    internal IReadOnlyDictionary<Block, int>? SourceBlockOrigins { get; init; }

    /// <summary>
    /// whether the source package contained content the reader does not model (recorded at read
    /// time). Used to warn when such content would be lost on a rewrite (phase AF, AF09).
    /// </summary>
    internal bool SourceHasUnmodelledContent { get; init; }

    /// <summary>The reader warnings that explain which source content was not modelled.</summary>
    internal IReadOnlyList<string>? SourceUnmodelledWarnings { get; init; }

    /// <summary>Creates a new fluent builder for a document.</summary>
    public static PortableDocumentBuilder Create() => new();

    private void SetSections(IEnumerable<Section> sections)
    {
        var snapshot = ModelCollections.Snapshot(sections, nameof(Sections));
        if (snapshot.Count == 0)
        {
            throw new ArgumentException("A document requires at least one Section.", nameof(Sections));
        }

        _sections = snapshot;
        _sourceSections ??= snapshot;
    }

    /// <summary>Computes the content statistics of the document (phase AE, AE02).</summary>
    public DocumentStatistics Statistics() => DocumentMetrics.Collect(this);
}
