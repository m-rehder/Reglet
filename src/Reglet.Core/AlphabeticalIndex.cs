namespace Reglet;

/// <summary>
/// an alphabetical index of the <see cref="IndexEntry"/> marks of the document. DOCX writes an
/// <c>INDEX</c> field and ODT an alphabetical index, so the application fills in the page numbers;
/// PDF renders the sorted entries with page numbers and links.
/// </summary>
public sealed class AlphabeticalIndex : Block
{
    /// <summary>Optional title above the entries.</summary>
    public string? Title { get; init; }
}
