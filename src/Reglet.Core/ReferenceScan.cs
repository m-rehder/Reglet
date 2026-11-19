namespace Reglet;

/// <summary>What an internal reference of a document refers to.</summary>
internal enum ReferenceKind
{
    /// <summary>A <see cref="Hyperlink"/> with a <c>#name</c> target.</summary>
    Link,

    /// <summary>A <see cref="PageRefField"/> that names the page of a bookmark.</summary>
    PageReference,

    /// <summary>A general <see cref="Field"/> whose code is <c>REF name</c>.</summary>
    FieldReference,
}

/// <summary>Why an internal reference does not reach a bookmark.</summary>
internal enum ReferenceProblem
{
    /// <summary>No bookmark of this name is in the document.</summary>
    Missing,

    /// <summary>
    /// The bookmark is only in a header or a footer. Word and LibreOffice do not jump into a band,
    /// so DOCX and ODT report such a reference; the PDF records a destination on the first page
    /// where the band is drawn and passes <c>bandBookmarksAreTargets</c>.
    /// </summary>
    BandOnly,
}

/// <summary>
/// An internal reference whose target is missing or unreachable. <see cref="Message"/> is the
/// sentence a writer reports; every writer prepends its own format name, so the message starts
/// with a lower-case letter and ends with the full stop.
/// </summary>
internal sealed record UnresolvedReference(ReferenceKind Kind, string Name, ReferenceProblem Problem)
{
    public string Message => (Kind, Problem) switch
    {
        (ReferenceKind.Link, ReferenceProblem.Missing) =>
            $"the link to the bookmark '{Name}' has no target in this document; the text is written, the link leads nowhere.",
        (ReferenceKind.Link, ReferenceProblem.BandOnly) =>
            $"the link to the bookmark '{Name}' points into a header or footer, which no link can jump to; the text is written, the link leads nowhere.",
        (ReferenceKind.PageReference, ReferenceProblem.Missing) =>
            $"the page reference to the bookmark '{Name}' has no target in this document; the number is not resolved.",
        (ReferenceKind.FieldReference, _) =>
            $"the REF field to the bookmark '{Name}' has no target in this document; the result is not resolved.",
        _ =>
            $"the page reference to the bookmark '{Name}' points into a header or footer, which has no page of its own; the number is not resolved.",
    };
}

/// <summary>
/// Compares the internal references of a document (a <see cref="Hyperlink"/> to <c>#name</c> and a
/// <see cref="PageRefField"/>) with the bookmarks the document declares. A bookmark in a header or
/// footer is not a target for DOCX and ODT (Word and LibreOffice do not jump into a band); the PDF
/// records a destination for it. A bookmark in a note is a target in all three formats. The writers
/// use this to report a reference that leads nowhere instead of writing it silently (OF-C03). A
/// general <c>REF</c> field is checked as well; other field codes are written as they are, because
/// their instruction cannot be resolved without the application.
/// </summary>
internal static class ReferenceScan
{
    /// <summary>
    /// The unresolved references of the document (body, notes, headers and footers) in document
    /// order, without repetitions: a name is reported once per kind of reference.
    /// </summary>
    public static IReadOnlyList<UnresolvedReference> Find(PortableDocument document, bool bandBookmarksAreTargets = false)
    {
        ArgumentNullException.ThrowIfNull(document);

        var names = new BookmarkNames();
        var references = new List<(ReferenceKind Kind, string Name)>();
        Collect(document, names, references);

        var unresolved = new List<UnresolvedReference>();
        var reported = new HashSet<(ReferenceKind, string)>();
        foreach (var (kind, name) in references)
        {
            if (names.IsTarget(name, kind, bandBookmarksAreTargets))
            {
                continue;
            }

            var problem = names.OnlyInBand(name) ? ReferenceProblem.BandOnly : ReferenceProblem.Missing;
            if (reported.Add((kind, name)))
            {
                unresolved.Add(new UnresolvedReference(kind, name, problem));
            }
        }

        return unresolved;
    }

    /// <summary>
    /// The bookmarks that only a header or a footer declares, in name order. DOCX and ODT do not
    /// jump into a band; the PDF records a destination on the first page instead.
    /// </summary>
    public static IReadOnlyList<string> BandOnlyBookmarks(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var names = new BookmarkNames();
        Collect(document, names, references: null);
        return names.OnlyInBands();
    }

    /// <summary>Where a bookmark sits, which decides whether a reference reaches it.</summary>
    private enum Place
    {
        /// <summary>In the flow of the body, including a note of that flow.</summary>
        Body,

        /// <summary>In the text of a footnote or endnote.</summary>
        Note,

        /// <summary>In a header or a footer.</summary>
        Band,
    }

    private static void Collect(
        PortableDocument document,
        BookmarkNames names,
        List<(ReferenceKind Kind, string Name)>? references)
    {
        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks, names, Place.Body, references);
            foreach (var band in Bands(section))
            {
                if (band is not null)
                {
                    Blocks(band.Blocks, names, Place.Band, references);
                }
            }
        }
    }

    /// <summary>The six bands of a section, in the order the writers use them.</summary>
    private static HeaderFooter?[] Bands(Section section) =>
        [
            section.Header, section.Footer,
            section.FirstHeader, section.FirstFooter,
            section.EvenHeader, section.EvenFooter,
        ];

    private static void Blocks(
        IReadOnlyList<Block> blocks,
        BookmarkNames names,
        Place place,
        List<(ReferenceKind Kind, string Name)>? references)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Inlines(paragraph.Inlines, names, place, references);
                    break;
                case Heading heading:
                    Inlines(heading.Inlines, names, place, references);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Blocks(item.Blocks, names, place, references);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Blocks(cell.Blocks, names, place, references);
                        }
                    }

                    break;
            }
        }
    }

    private static void Inlines(
        IReadOnlyList<Inline> inlines,
        BookmarkNames names,
        Place place,
        List<(ReferenceKind Kind, string Name)>? references)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Bookmark bookmark:
                    names.Add(bookmark.Name, place);
                    break;
                case Footnote note:
                    // A note of a header or footer stays a band note: the formats carry no notes
                    // in a band, so the writers turn it into text there.
                    Blocks(note.Blocks, names, place == Place.Band ? Place.Band : Place.Note, references);
                    break;
                case Hyperlink hyperlink:
                    if (references is not null && IsInternal(hyperlink.Target))
                    {
                        references.Add((ReferenceKind.Link, hyperlink.Target[1..]));
                    }

                    Inlines(hyperlink.Inlines, names, place, references);
                    break;
                case PageRefField pageRef:
                    references?.Add((ReferenceKind.PageReference, pageRef.BookmarkName));
                    break;
                case Field field when RefTarget(field.Instruction) is { } name:
                    references?.Add((ReferenceKind.FieldReference, name));
                    break;
            }
        }
    }

    private static bool IsInternal(string target) => target.Length > 1 && target[0] == '#';

    /// <summary>
    /// The bookmark a <c>REF</c> field code names, or null for another field code. The name is the
    /// second token of <c>REF name [switches]</c>; it may be quoted.
    /// </summary>
    internal static string? RefTarget(string instruction)
    {
        var tokens = Tokens(instruction);
        if (tokens.Count < 2 || !tokens[0].Equals("REF", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = tokens[1];
        return name.Length == 0 || name[0] == '\\' ? null : name;
    }

    /// <summary>The tokens of a field code, with quoted names as one token.</summary>
    private static List<string> Tokens(string instruction)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in instruction)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>The bookmarks of a document, kept apart by the place that declares them.</summary>
    private sealed class BookmarkNames
    {
        private readonly HashSet<string> _body = new(StringComparer.Ordinal);
        private readonly HashSet<string> _notes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _bands = new(StringComparer.Ordinal);

        public void Add(string name, Place place)
        {
            switch (place)
            {
                case Place.Band:
                    _bands.Add(name);
                    break;
                case Place.Note:
                    _notes.Add(name);
                    break;
                default:
                    _body.Add(name);
                    break;
            }
        }

        /// <summary>
        /// Whether a reference may point at a bookmark of that name. A <c>REF</c> field shows the
        /// text of the bookmark, so a bookmark in a band is a target for it as well. The PDF can
        /// also jump to a band bookmark (the first page), so its scan passes
        /// <paramref name="bandBookmarksAreTargets"/>; DOCX and ODT keep reporting a link or page
        /// reference into a band, which Word and LibreOffice cannot follow. A bookmark in a note is
        /// reachable in all three formats.
        /// </summary>
        public bool IsTarget(string name, ReferenceKind kind, bool bandBookmarksAreTargets = false)
            => _body.Contains(name)
                || _notes.Contains(name)
                || (_bands.Contains(name) && (bandBookmarksAreTargets || kind == ReferenceKind.FieldReference));

        /// <summary>Whether the bookmark exists, but only in a header or a footer.</summary>
        public bool OnlyInBand(string name)
            => _bands.Contains(name) && !_body.Contains(name) && !_notes.Contains(name);

        /// <summary>The bookmarks that only a header or a footer declares, in name order.</summary>
        public IReadOnlyList<string> OnlyInBands() => Only(_bands, _body, _notes);

        private static IReadOnlyList<string> Only(
            HashSet<string> own,
            HashSet<string> otherA,
            HashSet<string> otherB)
        {
            var names = new HashSet<string>(own, StringComparer.Ordinal);
            names.ExceptWith(otherA);
            names.ExceptWith(otherB);
            return names.OrderBy(name => name, StringComparer.Ordinal).ToList();
        }
    }
}
