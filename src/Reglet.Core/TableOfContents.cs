namespace Reglet;

/// <summary>
/// a table of contents (phase Z, Z04). The entries are the headings of the document up to
/// <see cref="MaxLevel"/>; DOCX and ODT write a field so the application can fill in the page
/// numbers, PDF renders the entries statically.
/// </summary>
public sealed class TableOfContents : Block
{
    private int _maxLevel = 3;

    /// <summary>Optional title above the entries.</summary>
    public string? Title { get; init; }

    /// <summary>Deepest heading level that appears (1–6; default 3).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1–6.</exception>
    public int MaxLevel
    {
        get => _maxLevel;
        init
        {
            if (value is < 1 or > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Heading level must be between 1 and 6.");
            }

            _maxLevel = value;
        }
    }
}
