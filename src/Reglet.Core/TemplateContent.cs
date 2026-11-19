namespace Reglet;

/// <summary>
/// format-neutral content that is bound to a template placeholder when writing.
/// content is either <see cref="TemplateContentKind.Inline"/> (within a
/// paragraph) or <see cref="TemplateContentKind.Block"/> (its own paragraph area); the
/// the respective other collection is empty.
/// </summary>
/// <remarks>
/// the content is adopted as a snapshot and is immutable. The writers apply
/// it in the placeholder's style (the template's styles are preserved).
/// </remarks>
public sealed class TemplateContent
{
    private static readonly IReadOnlyList<Inline> NoInlines = Array.Empty<Inline>();
    private static readonly IReadOnlyList<Block> NoBlocks = Array.Empty<Block>();

    private TemplateContent(TemplateContentKind kind, IReadOnlyList<Inline> inlines, IReadOnlyList<Block> blocks)
    {
        Kind = kind;
        Inlines = inlines;
        Blocks = blocks;
    }

    /// <summary>The kind of content.</summary>
    public TemplateContentKind Kind { get; }

    /// <summary>
    /// The inline content for <see cref="TemplateContentKind.Inline"/>, otherwise empty.
    /// </summary>
    public IReadOnlyList<Inline> Inlines { get; }

    /// <summary>
    /// The block content for <see cref="TemplateContentKind.Block"/>, otherwise empty.
    /// </summary>
    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>Creates inline content from exactly one element.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="inline"/> is null.</exception>
    public static TemplateContent FromInline(Inline inline)
    {
        ArgumentNullException.ThrowIfNull(inline);
        return new TemplateContent(TemplateContentKind.Inline, Array.AsReadOnly(new[] { inline }), NoBlocks);
    }

    /// <summary>Creates inline content from several elements (also empty).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="inlines"/> is null.</exception>
    /// <exception cref="ArgumentException">The collection contains null elements.</exception>
    public static TemplateContent FromInlines(IEnumerable<Inline> inlines)
        => new(TemplateContentKind.Inline, ModelCollections.Snapshot(inlines, nameof(inlines)), NoBlocks);

    /// <summary>Creates block content from exactly one element.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
    public static TemplateContent FromBlock(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new TemplateContent(TemplateContentKind.Block, NoInlines, Array.AsReadOnly(new[] { block }));
    }

    /// <summary>Creates block content from several elements (also empty).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    /// <exception cref="ArgumentException">The collection contains null elements.</exception>
    public static TemplateContent FromBlocks(IEnumerable<Block> blocks)
        => new(TemplateContentKind.Block, NoInlines, ModelCollections.Snapshot(blocks, nameof(blocks)));
}
