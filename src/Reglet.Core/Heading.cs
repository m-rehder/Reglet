namespace Reglet;

/// <summary>
/// a heading of level 1 to 6. Internally it is mapped to a paragraph style
/// (mapped to <c>Heading1</c> to <c>Heading6</c>).
/// </summary>
public sealed class Heading : Block
{
    private ParagraphStyle _style = ParagraphStyle.Default;

    /// <summary>Creates a heading with exactly one inline element.</summary>
    public Heading(HeadingLevel level, Inline inline)
        : this(level, new[] { inline })
    {
    }

    /// <summary>Creates a heading with several inline elements.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1–6.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="inlines"/> is null.</exception>
    public Heading(HeadingLevel level, IEnumerable<Inline> inlines)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), level, "Heading level must be between 1 and 6.");
        }

        ArgumentNullException.ThrowIfNull(inlines);

        Level = level;
        Inlines = ModelCollections.Snapshot(inlines, nameof(inlines));
        _style = ParagraphStyle.Default with { StyleName = StyleNameFor(level) };
    }

    /// <summary>The heading level.</summary>
    public HeadingLevel Level { get; }

    /// <summary>The inline content of the heading.</summary>
    public IReadOnlyList<Inline> Inlines { get; }

    /// <summary>Paragraph style; by default <c>Heading1</c> to <c>Heading6</c>.</summary>
    public ParagraphStyle Style
    {
        get => _style;
        init => _style = value ?? throw new ArgumentNullException(nameof(Style));
    }

    /// <summary>Returns the internal paragraph style name for a level.</summary>
    public static string StyleNameFor(HeadingLevel level)
        => Enum.IsDefined(level)
            ? $"Heading{(int)level}"
            : throw new ArgumentOutOfRangeException(nameof(level), level, "Heading level must be between 1 and 6.");
}
