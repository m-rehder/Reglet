namespace Reglet;

/// <summary>
/// mutable state during writing of WordprocessingML content (fresh
/// document or template): image relationships, list state, drawing IDs and header/footer context.
/// </summary>
internal sealed class DocxWriteContext
{
    private long _nextDrawingId;
    private long _nextBookmarkId;
    private long _nextSdtId;
    private long _nextRevisionId;
    private long _nextFootnoteId;
    private long _nextEndnoteId;
    private long _nextCommentId;
    private readonly List<(NumberingDefinition Definition, int NumId)> _usedNumbering = [];
    private readonly Dictionary<string, int> _numIds = new(StringComparer.Ordinal);
    private readonly int _firstNumId;
    private readonly List<(int Level, string Text)> _headings = [];
    private readonly List<(int Id, Footnote Note)> _footnotes = [];
    private readonly List<(int Id, Footnote Note)> _endnotes = [];
    private readonly Dictionary<Footnote, (int Id, NoteKind Kind)> _noteIds = [];

    /// <summary>Creates a context.</summary>
    /// <param name="imageRelationships">Relationship IDs of the images of the part currently being written.</param>
    /// <param name="xmlBehavior">Handling of invalid XML characters.</param>
    /// <param name="handler">Reporter for unmappable content.</param>
    /// <param name="firstNumId">numId of the first generated list definition (default 1).</param>
    /// <param name="numbering">The numbering definitions of the document.</param>
    /// <param name="firstDrawingId">First docPr ID (default 1).</param>
    /// <param name="firstBookmarkId">First bookmark ID (default 0).</param>
    /// <param name="hyperlinkRelationships">Relationship IDs of the hyperlinks (target → rId).</param>
    /// <param name="firstSdtId">First content control ID (default 1).</param>
    /// <param name="firstRevisionId">First tracked change ID (default 1).</param>
    /// <param name="firstFootnoteId">First cloned footnote ID (default 1).</param>
    /// <param name="firstEndnoteId">First cloned endnote ID (default 1).</param>
    /// <param name="firstCommentId">First cloned comment ID (default 1).</param>
    public DocxWriteContext(
        IReadOnlyDictionary<ImageBlock, string> imageRelationships,
        InvalidXmlCharacterBehavior xmlBehavior,
        UnsupportedFeatureHandler handler,
        int firstNumId = 1,
        int firstDrawingId = 1,
        int firstBookmarkId = 0,
        IReadOnlyDictionary<string, string>? hyperlinkRelationships = null,
        NumberingLookup? numbering = null,
        int firstSdtId = 1,
        int firstRevisionId = 1,
        int firstFootnoteId = 1,
        int firstEndnoteId = 1,
        int firstCommentId = 1)
    {
        ImageRelationships = imageRelationships;
        XmlBehavior = xmlBehavior;
        Handler = handler;
        _firstNumId = firstNumId;
        Numbering = numbering ?? new NumberingLookup([]);
        _nextDrawingId = firstDrawingId;
        _nextBookmarkId = firstBookmarkId;
        _nextSdtId = firstSdtId;
        _nextRevisionId = firstRevisionId;
        _nextFootnoteId = firstFootnoteId;
        _nextEndnoteId = firstEndnoteId;
        _nextCommentId = firstCommentId;
        HyperlinkRelationships = hyperlinkRelationships ?? EmptyHyperlinks;
    }

    internal static readonly IReadOnlyDictionary<string, string> EmptyHyperlinks = new Dictionary<string, string>();
    /// <summary>Relationship IDs of the images in the part currently being written.</summary>
    public IReadOnlyDictionary<ImageBlock, string> ImageRelationships { get; set; }

    /// <summary>The relationship ids of the body's charts (ADR-0025).</summary>
    public IReadOnlyDictionary<Chart, string> ChartRelationships { get; set; } = new Dictionary<Chart, string>();

    /// <summary>The parts and relationships of the body's embedded objects (ADR-0029).</summary>
    public IReadOnlyDictionary<EmbeddedObject, DocxEmbeddedObjects.Parts> ObjectParts { get; set; }
        = new Dictionary<EmbeddedObject, DocxEmbeddedObjects.Parts>();

    /// <summary>Relationship IDs of the hyperlinks (target → rId) in the part currently being written.</summary>
    public IReadOnlyDictionary<string, string> HyperlinkRelationships { get; set; }

    /// <summary>
    /// the values for the calculation of the general fields (phase AJ, AJ02); null means that
    /// the fields are written without a cached result.
    /// </summary>
    public FieldContext? Fields { get; set; }

    public InvalidXmlCharacterBehavior XmlBehavior { get; }

    public UnsupportedFeatureHandler Handler { get; }

    /// <summary>"header" or "footer" while one is being written.</summary>
    public string? BandName { get; set; }

    /// <summary>
    /// the named character styles of the document (ADR-0010); a run resolves the style it
    /// references when it is written.
    /// </summary>
    public IReadOnlyList<CharacterStyle> CharacterStyles { get; set; } = [];

    /// <summary>
    /// the named paragraph styles of the document (ADR-0011); a paragraph that references one keeps
    /// the reference and states only its direct formatting.
    /// </summary>
    public IReadOnlyList<ParagraphStyle> Styles { get; set; } = [];

    /// <summary>
    /// the named paragraph styles as they were read. An edit in <see cref="Styles"/> is resolved
    /// against them (ADR-0011).
    /// </summary>
    public IReadOnlyList<ParagraphStyle> SourceStyles { get; set; } = [];

    /// <summary>
    /// the paragraph whose runs are being written (ADR-0010); its style is the base the runs resolve
    /// against. Null outside a paragraph (e.g. a note reference).
    /// </summary>
    public ParagraphStyle? Paragraph { get; set; }

    /// <summary>Current list level (0 = outside a list).</summary>
    public int ListLevel { get; set; }

    /// <summary>Rule for the spacing after paragraphs (lists, cells, headers/footers).</summary>
    public ParagraphSpacing Spacing { get; } = new();

    /// <summary>The numbering definitions of the document (level lookup).</summary>
    public NumberingLookup Numbering { get; }

    /// <summary>The definitions a list uses, in order of first use, with their numId.</summary>
    public IReadOnlyList<(NumberingDefinition Definition, int NumId)> UsedNumbering => _usedNumbering;

    /// <summary>
    /// the numId of the numbering definition of the list. Lists with the same definition share
    /// it, so Word continues their numbering.
    /// </summary>
    /// <exception cref="DocumentValidationException">The list references an unknown definition.</exception>
    public int NumId(ListBlock list)
    {
        var definition = Numbering.Definition(list);
        if (_numIds.TryGetValue(definition.Name, out var existing))
        {
            return existing;
        }

        var numId = _firstNumId + _usedNumbering.Count;
        _numIds[definition.Name] = numId;
        _usedNumbering.Add((definition, numId));
        return numId;
    }

    /// <summary>
    /// unique docPr ID per drawing. The same image can occur multiple times; the IDs
    /// must be unique in the document including the headers/footers.
    /// </summary>
    public int NextDrawingId()
        => _nextDrawingId <= int.MaxValue
            ? (int)_nextDrawingId++
            : throw new DocumentValidationException("DOCX: no drawing ID is available.");

    /// <summary>Unique content control (<c>w:sdt</c>) ID for a cloned region.</summary>
    public int NextSdtId()
        => _nextSdtId <= int.MaxValue
            ? (int)_nextSdtId++
            : throw new DocumentValidationException("DOCX: no content control ID is available.");

    /// <summary>Unique ID for a cloned tracked change (<c>w:ins</c>, <c>w:del</c>, …).</summary>
    public int NextRevisionId()
        => _nextRevisionId <= int.MaxValue
            ? (int)_nextRevisionId++
            : throw new DocumentValidationException("DOCX: no tracked change ID is available.");

    /// <summary>Unique ID for a cloned footnote.</summary>
    public int NextFootnoteId()
        => _nextFootnoteId <= int.MaxValue
            ? (int)_nextFootnoteId++
            : throw new DocumentValidationException("DOCX: no footnote ID is available.");

    /// <summary>Unique ID for a cloned endnote.</summary>
    public int NextEndnoteId()
        => _nextEndnoteId <= int.MaxValue
            ? (int)_nextEndnoteId++
            : throw new DocumentValidationException("DOCX: no endnote ID is available.");

    /// <summary>Unique ID for a cloned comment.</summary>
    public int NextCommentId()
        => _nextCommentId <= int.MaxValue
            ? (int)_nextCommentId++
            : throw new DocumentValidationException("DOCX: no comment ID is available.");

    /// <summary>The comments of the body (ADR-0026) with the ids they get in the comments part, replies after their parent.</summary>
    public Dictionary<Comment, int> CommentWordIds { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The model ids of the comments that have an explicit <see cref="CommentEnd"/>; the others are points.</summary>
    public IReadOnlySet<int> CommentEnded { get; set; } = new HashSet<int>();

    /// <summary>The comments whose range is open: model id → comment.</summary>
    public Dictionary<int, Comment> OpenComments { get; } = [];

    /// <summary>The tracked changes of the body (ADR-0027); the ones elsewhere are reported, not written.</summary>
    public HashSet<TrackedChange> RevisionBody { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The model ids of the tracked changes that have an explicit <see cref="TrackedChangeEnd"/>.</summary>
    public IReadOnlySet<int> RevisionEnded { get; set; } = new HashSet<int>();

    /// <summary>The tracked changes whose range is open, outermost first.</summary>
    public List<TrackedChange> OpenChanges { get; } = [];

    /// <summary>Whether the <c>w:ins</c>/<c>w:del</c> elements of the open changes are currently written (not at a paragraph or hyperlink border).</summary>
    public bool RevisionElementsOpen { get; set; } = true;

    private readonly Dictionary<string, int> _bookmarkIds = new(StringComparer.Ordinal);

    /// <summary>
    /// the names of the bookmarks that have an explicit end (a range, OF-C03). A bookmark of the
    /// other kind is a zero-width target and writes its end right after the start.
    /// </summary>
    public IReadOnlySet<string> BookmarkRanges { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The bookmark ranges the protection leaves editable (<c>w:permStart</c>); empty when none.</summary>
    public IReadOnlySet<string> EditableRanges { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _permissionIds = new(StringComparer.Ordinal);

    /// <summary>Starts the editable range of a bookmark and returns its permission id.</summary>
    public string OpenPermission(string bookmarkName)
        => _permissionIds[bookmarkName] = "perm" + (_permissionIds.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The permission id of an open editable range, or null.</summary>
    public string? PermissionId(string bookmarkName) => _permissionIds.GetValueOrDefault(bookmarkName);

    /// <summary>Remembers the id of a bookmark start so its end finds it.</summary>
    public void OpenBookmark(string name, int id) => _bookmarkIds[name] = id;

    /// <summary>The id of an open bookmark start, or null when no start of that name was written.</summary>
    public int? BookmarkId(string name) => _bookmarkIds.TryGetValue(name, out var id) ? id : null;

    /// <summary>The name a bookmark has in the file; distinct model names stay distinct, Word ignores case.</summary>
    public string BookmarkName(string name)
    {
        if (_bookmarkNames.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var clean = DocxPackageBuilder.DocxBookmarkName(name);
        var unique = clean;
        for (var suffix = 2; !_usedBookmarkNames.Add(unique); suffix++)
        {
            unique = $"{clean}_{suffix}";
        }

        _bookmarkNames[name] = unique;
        return unique;
    }

    private readonly Dictionary<string, string> _bookmarkNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedBookmarkNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the next unique bookmark ID.</summary>
    public int NextBookmarkId()
        => _nextBookmarkId <= int.MaxValue
            ? (int)_nextBookmarkId++
            : throw new DocumentValidationException("DOCX: no bookmark ID is available.");

    /// <summary>Whether the document contains footnotes.</summary>
    public bool HasFootnotes => _footnotes.Count > 0;

    /// <summary>Whether the document contains endnotes.</summary>
    public bool HasEndnotes => _endnotes.Count > 0;

    /// <summary>The footnotes with their assigned IDs in document order.</summary>
    public IReadOnlyList<(int Id, Footnote Note)> Footnotes => _footnotes;

    /// <summary>The endnotes with their assigned IDs in document order.</summary>
    public IReadOnlyList<(int Id, Footnote Note)> Endnotes => _endnotes;

    /// <summary>
    /// The source note ids that could be kept. A preserved block may keep a note reference only
    /// when the referenced note is in this set; otherwise the reference would point at another note
    /// after the notes part is regenerated.
    /// </summary>
    public IReadOnlySet<(bool Endnote, int Id)> PreservedNoteIds => _preservedNoteIds;

    private readonly HashSet<(bool Endnote, int Id)> _preservedNoteIds = [];

    /// <summary>
    /// Assigns IDs to the notes (per kind). A note read from a DOCX keeps its source id when it is
    /// unique, so a preserved block that references it stays valid; notes without one (or with a
    /// collision) get the next free id.
    /// </summary>
    public void PrepareNotes(IReadOnlyList<Footnote> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        var footnoteIds = new Dictionary<Footnote, int>();
        var endnoteIds = new Dictionary<Footnote, int>();
        var usedFootnotes = new HashSet<int>();
        var usedEndnotes = new HashSet<int>();

        // First pass: keep source ids. This must happen before new notes get ids, otherwise a new
        // note inserted before a source note would take its id.
        foreach (var note in notes)
        {
            if (note.SourceId is not { } sourceId || sourceId < 1)
            {
                continue;
            }

            if (note.Kind == NoteKind.Endnote)
            {
                if (usedEndnotes.Add(sourceId))
                {
                    endnoteIds[note] = sourceId;
                    _preservedNoteIds.Add((true, sourceId));
                }
            }
            else if (usedFootnotes.Add(sourceId))
            {
                footnoteIds[note] = sourceId;
                _preservedNoteIds.Add((false, sourceId));
            }
        }

        // Second pass: assign a free id above the preserved maximum.
        var nextFootnote = usedFootnotes.Count == 0 ? 1 : usedFootnotes.Max() + 1;
        var nextEndnote = usedEndnotes.Count == 0 ? 1 : usedEndnotes.Max() + 1;
        foreach (var note in notes)
        {
            if (note.Kind == NoteKind.Endnote)
            {
                if (!endnoteIds.ContainsKey(note))
                {
                    while (usedEndnotes.Contains(nextEndnote))
                    {
                        nextEndnote++;
                    }

                    endnoteIds[note] = nextEndnote++;
                }
            }
            else if (!footnoteIds.ContainsKey(note))
            {
                while (usedFootnotes.Contains(nextFootnote))
                {
                    nextFootnote++;
                }

                footnoteIds[note] = nextFootnote++;
            }
        }

        // Third pass: keep document order in the notes part.
        foreach (var note in notes)
        {
            if (note.Kind == NoteKind.Endnote)
            {
                var id = endnoteIds[note];
                _endnotes.Add((id, note));
                _noteIds[note] = (id, NoteKind.Endnote);
            }
            else
            {
                var id = footnoteIds[note];
                _footnotes.Add((id, note));
                _noteIds[note] = (id, NoteKind.Footnote);
            }
        }
    }

    /// <summary>Returns the assigned ID and kind for a note, or <c>false</c> if unknown.</summary>
    public (int Id, NoteKind Kind, bool Found) NoteReference(Footnote note)
        => _noteIds.TryGetValue(note, out var entry)
            ? (entry.Id, entry.Kind, true)
            : (0, NoteKind.Footnote, false);

    /// <summary>The headings of the document (for a table of contents).</summary>
    public IReadOnlyList<(int Level, string Text)> Headings => _headings;

    /// <summary>The document that is written, for the captions and index entries of a list block.</summary>
    public PortableDocument? Document { get; private set; }

    /// <summary>Collects the headings for a table of contents.</summary>
    public void PrepareHeadings(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _headings.AddRange(HeadingScan.Collect(document, 6));
    }
}
