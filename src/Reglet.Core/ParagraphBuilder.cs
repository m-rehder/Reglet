namespace Reglet;

/// <summary>Fluent builder for a paragraph.</summary>
public sealed class ParagraphBuilder
{
    private readonly List<Inline> _inlines = [];
    private ParagraphStyle _style = ParagraphStyle.Default;

    internal ParagraphBuilder()
    {
    }

    /// <summary>Appends a normal text run.</summary>
    public ParagraphBuilder Text(string text) => Add(new TextRun(text));

    /// <summary>Appends a bold text run.</summary>
    public ParagraphBuilder Bold(string text) => Add(new TextRun(text) { Bold = true });

    /// <summary>Appends an italic text run.</summary>
    public ParagraphBuilder Italic(string text) => Add(new TextRun(text) { Italic = true });

    /// <summary>Appends an underlined text run.</summary>
    public ParagraphBuilder Underline(string text) => Add(new TextRun(text) { Underline = true });

    /// <summary>Appends a text run with freely choosable formatting.</summary>
    public ParagraphBuilder Span(
        string text,
        bool bold = false,
        bool italic = false,
        bool underline = false,
        Color? color = null,
        Length? fontSize = null,
        string? fontFamily = null)
        => Add(new TextRun(text)
        {
            Bold = bold,
            Italic = italic,
            Underline = underline,
            Color = color,
            FontSize = fontSize,
            FontFamily = fontFamily,
        });

    /// <summary>Appends a finished text run with full formatting.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public ParagraphBuilder Run(TextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Add(run);
    }

    /// <summary>Sets the horizontal alignment.</summary>
    public ParagraphBuilder Alignment(TextAlignment alignment)
    {
        _style = _style with { Alignment = alignment };
        return this;
    }

    /// <summary>Sets the spacing before the paragraph.</summary>
    public ParagraphBuilder SpaceBefore(Length length)
    {
        _style = _style with { SpaceBefore = length };
        return this;
    }

    /// <summary>Sets the spacing after the paragraph.</summary>
    public ParagraphBuilder SpaceAfter(Length length)
    {
        _style = _style with { SpaceAfter = length };
        return this;
    }

    /// <summary>Sets the left indent.</summary>
    public ParagraphBuilder LeftIndent(Length length)
    {
        _style = _style with { LeftIndent = length };
        return this;
    }

    /// <summary>Sets the right indent.</summary>
    public ParagraphBuilder RightIndent(Length length)
    {
        _style = _style with { RightIndent = length };
        return this;
    }

    /// <summary>Sets a complete paragraph style.</summary>
    public ParagraphBuilder Style(ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _style = style;
        return this;
    }

    /// <summary>Creates the paragraph.</summary>
    public Paragraph Build() => new(_inlines) { Style = _style };

    private ParagraphBuilder Add(Inline inline)
    {
        _inlines.Add(inline);
        return this;
    }
}
