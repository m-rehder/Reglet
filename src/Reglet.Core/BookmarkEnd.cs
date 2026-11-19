namespace Reglet;

/// <summary>
/// the end of a bookmark range. A <see cref="Bookmark"/> marks the start; the text between the two
/// is the content of the bookmark, which is what a <c>REF</c> field shows. Without an end the
/// bookmark is a zero-width target (a jump destination), which is what DOCX <c>w:bookmarkStart</c>
/// with an adjacent <c>w:bookmarkEnd</c> and ODT <c>text:bookmark</c> mean.
/// </summary>
public sealed class BookmarkEnd : Inline
{
    private readonly string _name;

    /// <summary>Creates the end of a bookmark range.</summary>
    /// <param name="name">The name of the bookmark that starts the range.</param>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public BookmarkEnd(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>The name of the bookmark that starts the range.</summary>
    public string Name => _name;
}
