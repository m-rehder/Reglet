namespace Reglet;

/// <summary>The kind of a <see cref="DrawingShape"/> (ADR-0022).</summary>
public enum ShapeKind
{
    /// <summary>A straight line from the top left to the bottom right corner of the bounding box.</summary>
    Line,

    /// <summary>A rectangle.</summary>
    Rectangle,

    /// <summary>A rectangle with rounded corners.</summary>
    RoundRectangle,

    /// <summary>An ellipse (a circle for equal width and height).</summary>
    Ellipse,

    /// <summary>A free path of <see cref="ShapePathStep"/>s.</summary>
    Freeform,

    /// <summary>A rectangle that holds paragraphs.</summary>
    TextBox,
}

/// <summary>The dash pattern of the outline of a shape.</summary>
public enum ShapeDash
{
    /// <summary>A continuous line.</summary>
    Solid,

    /// <summary>Dashes.</summary>
    Dash,

    /// <summary>Dots.</summary>
    Dot,
}

/// <summary>The command of a <see cref="ShapePathStep"/>.</summary>
public enum ShapePathCommand
{
    /// <summary>Starts a new sub path at the point.</summary>
    MoveTo,

    /// <summary>A straight line to the point.</summary>
    LineTo,

    /// <summary>A cubic Bézier curve to the point with two control points.</summary>
    CubicTo,

    /// <summary>Closes the current sub path; the points are ignored.</summary>
    Close,
}

/// <summary>
/// One step of the path of a freeform. Coordinates are measured from the top left corner of the
/// bounding box of the shape.
/// </summary>
/// <param name="Command">The command.</param>
/// <param name="X">The x coordinate of the end point.</param>
/// <param name="Y">The y coordinate of the end point.</param>
/// <param name="X1">The x coordinate of the first control point (<see cref="ShapePathCommand.CubicTo"/>).</param>
/// <param name="Y1">The y coordinate of the first control point.</param>
/// <param name="X2">The x coordinate of the second control point.</param>
/// <param name="Y2">The y coordinate of the second control point.</param>
public readonly record struct ShapePathStep(
    ShapePathCommand Command, Length X, Length Y, Length X1 = default, Length Y1 = default, Length X2 = default, Length Y2 = default);

/// <summary>
/// A vector shape or a text box (ADR-0022), inline or anchored like an <see cref="ImageBlock"/>. In
/// version 1 the fill is a solid color, a text box holds paragraphs and headings only.
/// </summary>
public sealed class DrawingShape : Block
{
    private Length _outlineWidth = Length.FromPoints(1);

    /// <summary>Creates a shape.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="width">The width of the bounding box (may be zero for a vertical line).</param>
    /// <param name="height">The height of the bounding box (may be zero for a horizontal line).</param>
    /// <param name="content">The paragraphs of a <see cref="ShapeKind.TextBox"/>; null for another kind.</param>
    /// <param name="path">The steps of a <see cref="ShapeKind.Freeform"/>; null for another kind.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is unknown or a size is negative or not finite.</exception>
    /// <exception cref="ArgumentException">
    /// Both sizes are zero (or one is zero for a kind other than a line), content or a path is given for
    /// the wrong kind or missing, or the content has a block other than a paragraph or heading.
    /// </exception>
    public DrawingShape(
        ShapeKind kind, Length width, Length height,
        IEnumerable<Block>? content = null, IEnumerable<ShapePathStep>? path = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shape kind.");
        }

        CheckSize(width, nameof(width));
        CheckSize(height, nameof(height));
        var flat = width.Points == 0 || height.Points == 0;
        if (width.Points == 0 && height.Points == 0 || flat && kind != ShapeKind.Line)
        {
            throw new ArgumentException("Only a line may have a size of zero in one direction.");
        }

        if ((content is not null) != (kind == ShapeKind.TextBox))
        {
            throw new ArgumentException("Only a text box has content, and a text box needs it.", nameof(content));
        }

        if ((path is not null) != (kind == ShapeKind.Freeform))
        {
            throw new ArgumentException("Only a freeform has a path, and a freeform needs it.", nameof(path));
        }

        Kind = kind;
        Width = width;
        Height = height;
        if (content is not null)
        {
            Content = ModelCollections.Snapshot(content, nameof(content));
            if (Content.Any(block => block is not (Paragraph or Heading)))
            {
                throw new ArgumentException("A text box holds paragraphs and headings only.", nameof(content));
            }
        }

        if (path is not null)
        {
            Path = ModelCollections.Snapshot(path, nameof(path));
            if (Path.Count == 0 || Path[0].Command != ShapePathCommand.MoveTo
                || Path.Any(step => !Enum.IsDefined(step.Command)
                    || !double.IsFinite(step.X.Points) || !double.IsFinite(step.Y.Points)
                    || !double.IsFinite(step.X1.Points) || !double.IsFinite(step.Y1.Points)
                    || !double.IsFinite(step.X2.Points) || !double.IsFinite(step.Y2.Points)))
            {
                throw new ArgumentException("A path starts with MoveTo and has finite coordinates.", nameof(path));
            }
        }
    }

    /// <summary>The kind of the shape.</summary>
    public ShapeKind Kind { get; }

    /// <summary>The width of the bounding box.</summary>
    public Length Width { get; }

    /// <summary>The height of the bounding box.</summary>
    public Length Height { get; }

    /// <summary>The paragraphs of a text box; null for another kind.</summary>
    public IReadOnlyList<Block>? Content { get; }

    /// <summary>The path of a freeform; null for another kind.</summary>
    public IReadOnlyList<ShapePathStep>? Path { get; }

    /// <summary>The fill color; null means no fill.</summary>
    public Color? Fill { get; init; }

    /// <summary>The outline color; null means no outline.</summary>
    public Color? Outline { get; init; }

    /// <summary>The outline width (default one point).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive or not finite.</exception>
    public Length OutlineWidth
    {
        get => _outlineWidth;
        init
        {
            if (!double.IsFinite(value.Points) || value.Points <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(OutlineWidth), "The outline width must be positive.");
            }

            _outlineWidth = value;
        }
    }

    /// <summary>The dash pattern of the outline.</summary>
    public ShapeDash Dash { get; init; } = ShapeDash.Solid;

    /// <summary>Rotation in degrees, clockwise.</summary>
    public double Rotation { get; init; }

    /// <summary>Wrapping of the text; <see cref="ImageWrap.None"/> is inline.</summary>
    public ImageWrap Wrap { get; init; } = ImageWrap.None;

    /// <summary>Horizontal offset of an anchored shape; null means 0.</summary>
    public Length? OffsetX { get; init; }

    /// <summary>Vertical offset of an anchored shape; null means 0.</summary>
    public Length? OffsetY { get; init; }

    /// <summary>Alternative text for accessibility.</summary>
    public string? AltText { get; init; }

    private static void CheckSize(Length size, string name)
    {
        if (!double.IsFinite(size.Points) || size.Points < 0)
        {
            throw new ArgumentOutOfRangeException(name, "A size must be finite and not negative.");
        }
    }
}
