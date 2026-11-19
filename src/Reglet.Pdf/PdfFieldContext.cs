using System.Globalization;

namespace Reglet;

/// <summary>
/// The values the PDF resolves fields with: document properties and variables from the
/// metadata, and the page of every bookmark from the previous layout pass (for PAGEREF).
/// A layout pass records the bookmark pages it places into <see cref="CollectedBookmarkPages"/>.
/// </summary>
internal sealed class PdfFieldContext
{
    private static readonly IReadOnlySet<string> NoBandBookmarks = new HashSet<string>(StringComparer.Ordinal);

    private readonly DocumentMetadata _metadata;
    private readonly IReadOnlyDictionary<string, int>? _bookmarkPages;
    private readonly IReadOnlySet<string> _bandBookmarks;

    /// <summary>
    /// The values for the calculation of the general fields (phase AJ, AJ02). The context is
    /// created once per layout pass, so a <c>SEQ</c> field counts over the whole document.
    /// </summary>
    public FieldContext Context { get; }

    public PdfFieldContext(
        DocumentMetadata metadata,
        IReadOnlyDictionary<string, int>? bookmarkPages,
        Dictionary<string, int>? collectedBookmarkPages,
        IReadOnlyList<string>? bandBookmarks = null,
        IReadOnlyDictionary<string, string>? bookmarkTexts = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        _bookmarkPages = bookmarkPages;
        CollectedBookmarkPages = collectedBookmarkPages;
        _bandBookmarks = bandBookmarks is null || bandBookmarks.Count == 0
            ? NoBandBookmarks
            : new HashSet<string>(bandBookmarks, StringComparer.Ordinal);
        // OF-C03: a REF field shows the text of its bookmark.
        Context = bookmarkTexts is null
            ? FieldContext.FromMetadata(metadata)
            : FieldContext.FromMetadata(metadata).WithBookmarks(bookmarkTexts);
    }

    /// <summary>The anchors of the captions and index marks; null for a header or footer.</summary>
    public PdfListMarks? Lists { get; init; }

    /// <summary>Bookmark pages recorded by the current pass (null when not collecting).</summary>
    public Dictionary<string, int>? CollectedBookmarkPages { get; }

    /// <summary>Whether a previous pass determined the bookmark pages.</summary>
    public bool HasBookmarkPages => _bookmarkPages is not null;

    /// <summary>
    /// Whether the bookmark of that name is only in a header or a footer. The PDF records a
    /// destination on the first page for it; the flag is only used for a page reference that still
    /// cannot be resolved.
    /// </summary>
    public bool IsBandBookmark(string name) => _bandBookmarks.Contains(name);

    /// <summary>Records the page of a bookmark; the first occurrence wins (as in Word).</summary>
    public void RecordBookmark(string name, int page) => CollectedBookmarkPages?.TryAdd(name, page);

    /// <summary>The displayed page number of a bookmark, or null if it is unknown.</summary>
    public int? BookmarkPage(string name)
        => _bookmarkPages is not null && _bookmarkPages.TryGetValue(name, out var page) ? page : null;

    /// <summary>
    /// The value of a document property: the built-in names Word uses for DOCPROPERTY (Title,
    /// Subject, Author, Keywords, CreateTime) and otherwise a custom property. Names are
    /// case-insensitive as in Word; null when the property is not set.
    /// </summary>
    public string? DocumentProperty(string name)
    {
        var builtIn = name.ToUpperInvariant() switch
        {
            "TITLE" => _metadata.Title,
            "SUBJECT" => _metadata.Subject,
            "AUTHOR" => _metadata.Author,
            "KEYWORDS" => _metadata.Keywords is { Count: > 0 } keywords ? string.Join(", ", keywords) : null,
            "CREATETIME" => _metadata.Created?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };

        return builtIn ?? Lookup(_metadata.CustomProperties, name);
    }

    /// <summary>The value of a document variable (case-insensitive), or null when it is not set.</summary>
    public string? DocumentVariable(string name) => Lookup(_metadata.Variables, name);

    private static string? Lookup(IReadOnlyDictionary<string, string>? values, string name)
    {
        if (values is null)
        {
            return null;
        }

        if (values.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
