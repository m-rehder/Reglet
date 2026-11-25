namespace Reglet;

/// <summary>Line style of a paragraph or frame border.</summary>
public enum BorderStyle
{
    /// <summary>No border.</summary>
    None,

    /// <summary>Solid line.</summary>
    Single,

    /// <summary>Double line.</summary>
    Double,

    /// <summary>Dotted line.</summary>
    Dotted,

    /// <summary>Dashed line.</summary>
    Dashed,
}

/// <summary>A border (line style, width, color).</summary>
public sealed record ParagraphBorder
{
    /// <summary>Creates a border with style, width and color.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The style is unknown or the width is not positive.</exception>
    public ParagraphBorder(BorderStyle style = BorderStyle.Single, Length? width = null, Color? color = null)
    {
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown line style.");
        }

        if (width is { Points: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Border width must be positive.");
        }

        Style = style;
        Width = width ?? Length.FromPoints(0.5);
        Color = color ?? Color.Black;
    }

    /// <summary>No border; on a <see cref="TableBlock"/> it removes the default grid.</summary>
    public static ParagraphBorder None { get; } = new(BorderStyle.None);

    /// <summary>Line style.</summary>
    public BorderStyle Style { get; }

    /// <summary>Line width.</summary>
    public Length Width { get; }

    /// <summary>Line color.</summary>
    public Color Color { get; }
}

/// <summary>Borders of a paragraph (per side optional).</summary>
public sealed record ParagraphBorders
{
    /// <summary>
    /// The explicit empty border set (no borders on any side). A <c>null</c>
    /// <see cref="ParagraphStyle.Borders"/> means "not set" and inherits; this instance removes an
    /// inherited border.
    /// </summary>
    public static ParagraphBorders None { get; } = new();

    /// <summary>Top border.</summary>
    public ParagraphBorder? Top { get; init; }

    /// <summary>Right border.</summary>
    public ParagraphBorder? Right { get; init; }

    /// <summary>Bottom border.</summary>
    public ParagraphBorder? Bottom { get; init; }

    /// <summary>Left border.</summary>
    public ParagraphBorder? Left { get; init; }

    /// <summary>Creates margins on all four sides.</summary>
    public static ParagraphBorders All(ParagraphBorder border)
    {
        ArgumentNullException.ThrowIfNull(border);
        return new ParagraphBorders { Top = border, Right = border, Bottom = border, Left = border };
    }
}
