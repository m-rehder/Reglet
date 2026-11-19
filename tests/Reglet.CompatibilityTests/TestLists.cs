using Reglet;

/// <summary>
/// builds lists with their numbering definition. The model keeps list formatting only in
/// <see cref="PortableDocument.Numbering"/>, so a test list carries its level here and
/// <c>Document</c> adds the definitions of the lists the document contains.
/// </summary>
internal static class TestLists
{
    private static readonly Dictionary<ListBlock, NumberingDefinition> Own = new(ReferenceEqualityComparer.Instance);
    private static int _counter;

    /// <summary>A list with its own one-level numbering definition.</summary>
    public static ListBlock List(
        ListKind kind,
        IEnumerable<ListItem> items,
        ListNumberFormat format = ListNumberFormat.Decimal,
        int start = 1,
        Color? numberColor = null,
        bool numberBold = false,
        int nestingLevel = 1)
    {
        var name = "Numbering" + (++_counter);
        var definition = new NumberingDefinition(
            name,
            [new NumberingLevel
            {
                Kind = kind,
                Format = format,
                Start = start,
                NumberColor = numberColor,
                NumberBold = numberBold,
            }]);
        var list = new ListBlock(name, items) { NestingLevel = nestingLevel };
        Own[list] = definition;
        return list;
    }

    /// <summary>Adds the definitions of all lists the document contains.</summary>
    public static PortableDocument Fix(PortableDocument document)
    {
        var lists = document.Sections
            .SelectMany(section => ListScan.Collect(section.Blocks)
                .Concat(ListScan.Collect(Band(section.Header)))
                .Concat(ListScan.Collect(Band(section.Footer))))
            .Where(Own.ContainsKey)
            .ToList();
        return lists.Count == 0
            ? document
            : document with { Numbering = [.. document.Numbering, .. lists.Select(list => Own[list])] };
    }

    /// <summary>A document with the numbering definitions of all its lists.</summary>
    public static PortableDocument Document(DocumentMetadata metadata, params Section[] sections)
        => Fix(new PortableDocument(metadata, sections));

    /// <summary>Adds the definition of a list that was appended to an existing document.</summary>
    public static PortableDocument WithList(PortableDocument document, ListBlock list)
        => document with { Numbering = [.. document.Numbering, Own[list]] };

    /// <summary>A document with one list as its only content.</summary>
    public static PortableDocument Document(Block block) => Document(new DocumentMetadata(), new Section(block));

    /// <summary>A document with the given blocks as its only section.</summary>
    public static PortableDocument Document(IEnumerable<Block> blocks) => Document(new DocumentMetadata(), new Section(blocks));

    /// <summary>The level a list of a read document uses.</summary>
    public static NumberingLevel Level(PortableDocument document, ListBlock list)
    {
        var definition = document.Numbering.Single(entry => entry.Name == list.NumberingName);
        return definition.Levels[list.NestingLevel - 1];
    }

    private static IEnumerable<Block> Band(HeaderFooter? band) => band?.Blocks ?? [];
}
