namespace Reglet;

/// <summary>What the horizontal position of a floating image or shape counts from.</summary>
public enum HorizontalAnchor
{
    /// <summary>The text column (the default).</summary>
    Column,

    /// <summary>The left edge of the page.</summary>
    Page,

    /// <summary>The left page margin.</summary>
    Margin,

    /// <summary>The anchor character; the PDF writer places a block-level object like <see cref="Column"/>.</summary>
    Character,
}

/// <summary>What the vertical position of a floating image or shape counts from.</summary>
public enum VerticalAnchor
{
    /// <summary>The top of the anchor paragraph (the default).</summary>
    Paragraph,

    /// <summary>The top edge of the page.</summary>
    Page,

    /// <summary>The top page margin.</summary>
    Margin,

    /// <summary>The anchor line; the PDF writer places a block-level object like <see cref="Paragraph"/>.</summary>
    Line,
}

/// <summary>A horizontal alignment of a floating object within its <see cref="HorizontalAnchor"/>, instead of an offset.</summary>
public enum HorizontalFloatAlignment
{
    /// <summary>At the left edge.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the right edge.</summary>
    Right,
}

/// <summary>A vertical alignment of a floating object within its <see cref="VerticalAnchor"/>, instead of an offset.</summary>
public enum VerticalFloatAlignment
{
    /// <summary>At the top edge.</summary>
    Top,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>At the bottom edge.</summary>
    Bottom,
}

/// <summary>The position of a floating image or shape, as the writers read it.</summary>
internal readonly record struct FloatPlacement(
    HorizontalAnchor HorizontalRelativeTo,
    VerticalAnchor VerticalRelativeTo,
    HorizontalFloatAlignment? HorizontalAlignment,
    VerticalFloatAlignment? VerticalAlignment,
    Length? OffsetX,
    Length? OffsetY)
{
    public static FloatPlacement Of(ImageBlock image)
        => new(image.HorizontalRelativeTo, image.VerticalRelativeTo, image.HorizontalAlignment, image.VerticalAlignment, image.OffsetX, image.OffsetY);

    public static FloatPlacement Of(DrawingShape shape)
        => new(shape.HorizontalRelativeTo, shape.VerticalRelativeTo, shape.HorizontalAlignment, shape.VerticalAlignment, shape.OffsetX, shape.OffsetY);

    /// <summary>Whether the position depends on the page rather than on the text flow.</summary>
    public bool IsPageRelative => VerticalRelativeTo is VerticalAnchor.Page or VerticalAnchor.Margin;
}
