namespace Reglet;

/// <summary>A paragraph with inline content and paragraph style.</summary>
public sealed class Paragraph : Block
{
    private ParagraphStyle _style = ParagraphStyle.Default;

    /// <summary>Creates a paragraph with exactly one inline element.</summary>
    public Paragraph(Inline inline)
        : this(new[] { inline })
    {
    }

    /// <summary>Creates a paragraph with several inline elements.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="inlines"/> is null.</exception>
    public Paragraph(IEnumerable<Inline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        Inlines = ModelCollections.Snapshot(inlines, nameof(inlines));
    }

    /// <summary>The inline content of the paragraph.</summary>
    public IReadOnlyList<Inline> Inlines { get; }

    /// <summary>Paragraph style.</summary>
    public ParagraphStyle Style
    {
        get => _style;
        init => _style = value ?? throw new ArgumentNullException(nameof(Style));
    }

    /// <summary>
    /// the formatting the paragraph inherited from its named style when it was read, without its
    /// direct formatting (ADR-0011). The writers use it to state only the direct formatting and
    /// to let an edit of <see cref="PortableDocument.Styles"/> reach the paragraph; null for a
    /// paragraph built from the model or without a preserved named style.
    /// </summary>
    internal ParagraphStyle? SourceStyleBase { get; init; }
}
