namespace Reglet;

/// <summary>
/// the values a field calculation uses (phase AJ, AJ02): the document properties, the document
/// variables, the values of a mail merge, the text of the bookmarks, the page numbers and the
/// date. The names are compared case-insensitively, as in Word.
/// </summary>
public sealed class FieldContext
{
    private readonly Dictionary<string, int> _sequences = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty context.</summary>
    public FieldContext()
    {
    }

    /// <summary>
    /// The context with the text of the document's bookmarks (<c>REF</c>); the other values are kept
    /// (the writers collect them from the model).
    /// </summary>
    internal FieldContext WithBookmarks(IReadOnlyDictionary<string, string> bookmarks)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        return new FieldContext(this, PageNumber, TotalPages) { Bookmarks = bookmarks };
    }

    /// <summary>Creates a context that shares the sequence counters of another context.</summary>
    private FieldContext(FieldContext source, int? pageNumber, int? totalPages)
    {
        _sequences = source._sequences;
        Properties = source.Properties;
        Variables = source.Variables;
        MergeValues = source.MergeValues;
        Bookmarks = source.Bookmarks;
        PageNumber = pageNumber ?? source.PageNumber;
        TotalPages = totalPages ?? source.TotalPages;
        SectionPages = source.SectionPages;
        Created = source.Created;
        Saved = source.Saved;
        Printed = source.Printed;
        Now = source.Now;
    }

    /// <summary>A context without any values.</summary>
    public static FieldContext Empty { get; } = new();

    /// <summary>The document properties (<c>DOCPROPERTY</c>).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The document variables (<c>DOCVARIABLE</c>).</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The values of a mail merge (<c>MERGEFIELD</c>).</summary>
    public IReadOnlyDictionary<string, string> MergeValues { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The text of the bookmarks (<c>REF</c>).</summary>
    public IReadOnlyDictionary<string, string> Bookmarks { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current page number (<c>PAGE</c>); null when it is unknown.</summary>
    public int? PageNumber { get; init; }

    /// <summary>The total number of pages (<c>NUMPAGES</c>); null when it is unknown.</summary>
    public int? TotalPages { get; init; }

    /// <summary>
    /// The number of pages in the current section (<c>SECTIONPAGES</c>); null when unknown.
    /// Callers provide this value; the built-in writers do not paginate DOCX/ODT.
    /// </summary>
    public int? SectionPages { get; init; }

    /// <summary>The document creation time (<c>CREATEDATE</c>); null means unknown.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>The last save time (<c>SAVEDATE</c>); caller-supplied, null means unknown.</summary>
    public DateTimeOffset? Saved { get; init; }

    /// <summary>The last print time (<c>PRINTDATE</c>); caller-supplied, null means unknown.</summary>
    public DateTimeOffset? Printed { get; init; }

    /// <summary>The date of the calculation (<c>DATE</c>); null means "now".</summary>
    public DateTimeOffset? Now { get; init; }

    /// <summary>
    /// the number of the next <c>SEQ</c> field with the given name. The counter belongs to the
    /// context, so the sequence continues over several fields.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public int NextSequenceValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        _sequences.TryGetValue(name, out var value);
        if (value < int.MaxValue)
        {
            value++;
        }

        _sequences[name] = value;
        return value;
    }

    /// <summary>
    /// the current number of the <c>SEQ</c> counter with the given name, without counting on
    /// (<c>\c</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public int PeekSequenceValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _sequences.TryGetValue(name, out var value) ? value : 0;
    }

    /// <summary>
    /// sets the number of the <c>SEQ</c> counter with the given name (<c>\r</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public int ResetSequenceValue(string name, int value)
    {
        ArgumentNullException.ThrowIfNull(name);

        _sequences[name] = value;
        return value;
    }

    /// <summary>
    /// a context for the given page numbers. The sequence counters are shared with this
    /// context, so a <c>SEQ</c> field continues over several fields.
    /// </summary>
    public FieldContext ForPage(int? pageNumber, int? totalPages)
        => new(this, pageNumber, totalPages);

    /// <summary>
    /// a context with the properties and the variables of the document metadata
    /// (<c>DOCPROPERTY</c> and <c>DOCVARIABLE</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is null.</exception>
    public static FieldContext FromMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata.Title is { } title)
        {
            properties["Title"] = title;
        }

        if (metadata.Subject is { } subject)
        {
            properties["Subject"] = subject;
        }

        if (metadata.Author is { } author)
        {
            properties["Author"] = author;
        }

        if (metadata.Keywords is { Count: > 0 } keywords)
        {
            properties["Keywords"] = string.Join(", ", keywords);
        }

        if (metadata.Created is { } created)
        {
            properties["CreateTime"] = created.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (metadata.CustomProperties is { } custom)
        {
            foreach (var (name, value) in custom)
            {
                properties[name] = value;
            }
        }

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata.Variables is { } documentVariables)
        {
            foreach (var (name, value) in documentVariables)
            {
                variables[name] = value;
            }
        }

        return new FieldContext { Properties = properties, Variables = variables, Created = metadata.Created };
    }

    /// <summary>Creates a context with the given values.</summary>
    public static FieldContext Create(
        IReadOnlyDictionary<string, string>? properties = null,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? mergeValues = null,
        IReadOnlyDictionary<string, string>? bookmarks = null)
        => new()
        {
            Properties = properties ?? EmptyDictionary,
            Variables = variables ?? EmptyDictionary,
            MergeValues = mergeValues ?? EmptyDictionary,
            Bookmarks = bookmarks ?? EmptyDictionary,
        };

    private static readonly IReadOnlyDictionary<string, string> EmptyDictionary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
