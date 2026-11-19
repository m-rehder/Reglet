namespace Reglet;

/// <summary>
/// the border of a page (phase AJ, AJ05): the four sides (optional) and the distance between
/// the border and the text. A page border belongs to the section; the formats draw it around
/// the text area of every page of the section.
/// </summary>
public sealed record PageBorders
{
    /// <summary>The top border; null means no border on this side.</summary>
    public ParagraphBorder? Top { get; init; }

    /// <summary>The right border; null means no border on this side.</summary>
    public ParagraphBorder? Right { get; init; }

    /// <summary>The bottom border; null means no border on this side.</summary>
    public ParagraphBorder? Bottom { get; init; }

    /// <summary>The left border; null means no border on this side.</summary>
    public ParagraphBorder? Left { get; init; }

    /// <summary>
    /// the distance between the border and the text (default: 24 points). The formats that
    /// measure the border from the edge of the page use the value as the distance from the
    /// text.
    /// </summary>
    public Length? Offset { get; init; }

    /// <summary>Whether no side has a border.</summary>
    public bool IsEmpty
        => Top is null && Right is null && Bottom is null && Left is null;
}
