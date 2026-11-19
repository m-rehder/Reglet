namespace Reglet;

/// <summary>
/// inline placeholder for a cross-reference to a bookmark (PAGEREF). In DOCX/ODT the field is
/// written (the application resolves the page number); the PDF shows the displayed page number
/// of the bookmark.
/// </summary>
public sealed class PageRefField : Inline
{
    /// <summary>Creates a page reference field.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="bookmarkName"/> is null.</exception>
    public PageRefField(string bookmarkName)
    {
        ArgumentNullException.ThrowIfNull(bookmarkName);
        BookmarkName = bookmarkName;
    }

    /// <summary>Name of the referenced bookmark.</summary>
    public string BookmarkName { get; }
}
