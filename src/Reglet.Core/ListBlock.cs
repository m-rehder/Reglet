namespace Reglet;

/// <summary>An ordered or unordered list that uses a numbering definition of the document.</summary>
public sealed class ListBlock : Block
{
    /// <summary>Maximum supported nesting depth.</summary>
    public const int MaxNestingLevel = NumberingDefinition.MaxLevels;

    private readonly int _nestingLevel = 1;

    /// <summary>Creates a list.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="DocumentValidationException"><paramref name="numberingName"/> is not a valid NCName.</exception>
    public ListBlock(string numberingName, IEnumerable<ListItem> items)
    {
        if (!NumberingDefinition.IsNcName(numberingName))
        {
            throw new DocumentValidationException(
                "The numbering name must be a valid NCName.", nameof(numberingName));
        }

        ArgumentNullException.ThrowIfNull(items);

        NumberingName = numberingName;
        Items = ModelCollections.Snapshot(items, nameof(items));
    }

    /// <summary>
    /// name of the numbering definition the list uses
    /// (<see cref="PortableDocument.Numbering"/>); later lists with the same name continue.
    /// </summary>
    public string NumberingName { get; }

    /// <summary>The entries of the list.</summary>
    public IReadOnlyList<ListItem> Items { get; }

    /// <summary>
    /// minimum nesting depth (1 = top level). A list in a list item
    /// sits in DOCX and PDF automatically at least one level deeper than the surrounding
    /// list; ODT represents the level solely via the nesting. The depth selects the level of
    /// the numbering definition; a deeper list repeats the last defined level.
    /// </summary>
    public int NestingLevel
    {
        get => _nestingLevel;
        init
        {
            if (value is < 1 or > MaxNestingLevel)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, $"Nesting depth must be between 1 and {MaxNestingLevel}.");
            }

            _nestingLevel = value;
        }
    }
}
