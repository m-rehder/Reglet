namespace Reglet;

/// <summary>
/// a named target (bookmark) at a position in the text. DOCX and ODT write a zero-width
/// bookmark; PDF records a named destination and adds it to the outline.
/// </summary>
public sealed class Bookmark : Inline
{
    private readonly string _name;

    /// <summary>Creates a bookmark.</summary>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public Bookmark(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>The bookmark name (unique within the document).</summary>
    public string Name => _name;
}
