using System.Globalization;

namespace Reglet;

/// <summary>The symbol of a <see cref="FlowNode"/> (ADR-0030).</summary>
public enum FlowNodeKind
{
    /// <summary>A step: a rectangle.</summary>
    Process,

    /// <summary>A decision: a diamond.</summary>
    Decision,

    /// <summary>Start or end: a pill.</summary>
    Terminator,

    /// <summary>Input or output: a parallelogram.</summary>
    InputOutput,
}

/// <summary>The side of a node a connector leaves or reaches.</summary>
public enum FlowSide
{
    /// <summary>The middle of the top edge.</summary>
    Top,

    /// <summary>The middle of the right edge.</summary>
    Right,

    /// <summary>The middle of the bottom edge.</summary>
    Bottom,

    /// <summary>The middle of the left edge.</summary>
    Left,
}

/// <summary>The path of a <see cref="FlowConnector"/>.</summary>
public enum FlowRoute
{
    /// <summary>Horizontal and vertical segments (default).</summary>
    Elbow,

    /// <summary>One straight line between the two points.</summary>
    Straight,
}

/// <summary>A symbol of a <see cref="Diagram"/>: its look, text and place.</summary>
public sealed class FlowNode
{
    private Length _fontSize = Length.FromPoints(10);

    /// <summary>Creates a node.</summary>
    /// <param name="id">The identifier connectors refer to; not empty, unique in the diagram.</param>
    /// <param name="kind">The symbol.</param>
    /// <param name="text">The text, centred; may be empty.</param>
    /// <param name="x">The left edge, measured from the left edge of the diagram.</param>
    /// <param name="y">The top edge, measured from the top edge of the diagram.</param>
    /// <param name="width">The width; positive.</param>
    /// <param name="height">The height; positive.</param>
    /// <exception cref="ArgumentException">The id is empty, the kind unknown, or a value not finite, a size not positive or a position negative.</exception>
    public FlowNode(string id, FlowNodeKind kind, string text, Length x, Length y, Length width, Length height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind.");
        }

        if (!double.IsFinite(x.Points) || !double.IsFinite(y.Points) || x.Points < 0 || y.Points < 0)
        {
            throw new ArgumentException("A node position must be finite and not negative.");
        }

        if (!double.IsFinite(width.Points) || !double.IsFinite(height.Points) || width.Points <= 0 || height.Points <= 0)
        {
            throw new ArgumentException("A node size must be finite and positive.");
        }

        Id = id;
        Kind = kind;
        Text = text;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>The identifier.</summary>
    public string Id { get; }

    /// <summary>The symbol.</summary>
    public FlowNodeKind Kind { get; }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>The left edge in the diagram.</summary>
    public Length X { get; }

    /// <summary>The top edge in the diagram.</summary>
    public Length Y { get; }

    /// <summary>The width.</summary>
    public Length Width { get; }

    /// <summary>The height.</summary>
    public Length Height { get; }

    /// <summary>The fill; a light blue when null.</summary>
    public Color? Fill { get; init; }

    /// <summary>The outline; a dark blue when null.</summary>
    public Color? Outline { get; init; }

    /// <summary>The text color; black when null.</summary>
    public Color? TextColor { get; init; }

    /// <summary>The font size of the text (default 10 points).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive or not finite.</exception>
    public Length FontSize
    {
        get => _fontSize;
        init
        {
            _fontSize = double.IsFinite(value.Points) && value.Points > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(FontSize), "The font size must be positive.");
        }
    }
}

/// <summary>An arrow between two nodes of a <see cref="Diagram"/>, drawn as a path (not glued to the nodes).</summary>
public sealed class FlowConnector
{
    /// <summary>Creates a connector.</summary>
    /// <param name="from">The id of the node it leaves.</param>
    /// <param name="fromSide">The side it leaves.</param>
    /// <param name="to">The id of the node it reaches.</param>
    /// <param name="toSide">The side it reaches.</param>
    /// <exception cref="ArgumentException">An id is empty or a side unknown.</exception>
    public FlowConnector(string from, FlowSide fromSide, string to, FlowSide toSide)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (!Enum.IsDefined(fromSide) || !Enum.IsDefined(toSide))
        {
            throw new ArgumentOutOfRangeException(nameof(fromSide), "Unknown side.");
        }

        From = from;
        FromSide = fromSide;
        To = to;
        ToSide = toSide;
    }

    /// <summary>The id of the node it leaves.</summary>
    public string From { get; }

    /// <summary>The side it leaves.</summary>
    public FlowSide FromSide { get; }

    /// <summary>The id of the node it reaches.</summary>
    public string To { get; }

    /// <summary>The side it reaches.</summary>
    public FlowSide ToSide { get; }

    /// <summary>The path; elbow by default.</summary>
    public FlowRoute Route { get; init; } = FlowRoute.Elbow;

    /// <summary>A short text next to the start ("Yes", "No"); null for none.</summary>
    public string? Label { get; init; }

    /// <summary>The color of line and arrowheads; black when null.</summary>
    public Color? Color { get; init; }

    /// <summary>The dash pattern of the line.</summary>
    public ShapeDash Dash { get; init; } = ShapeDash.Solid;

    /// <summary>Whether an arrowhead stands at the end (default true).</summary>
    public bool ArrowEnd { get; init; } = true;

    /// <summary>Whether an arrowhead stands at the start (default false).</summary>
    public bool ArrowStart { get; init; }
}

/// <summary>
/// A flow diagram (ADR-0030): nodes with fixed positions and connectors between them, in an area of fixed size. It is
/// drawn as a group of shapes; the connectors are paths with arrowheads, not glued to the nodes. The caller places
/// the nodes; there is no automatic layout and the elbow route does not avoid other nodes.
/// </summary>
public sealed class Diagram : Block
{
    /// <summary>The most nodes in one diagram.</summary>
    public const int MaxNodes = 1000;

    /// <summary>The most connectors in one diagram.</summary>
    public const int MaxConnectors = 4000;

    private IReadOnlyList<PlacedShape>? _shapes;

    /// <summary>Creates a diagram.</summary>
    /// <param name="width">The width of the area; positive.</param>
    /// <param name="height">The height of the area; positive.</param>
    /// <param name="nodes">The nodes; at least one, ids unique, each inside the area.</param>
    /// <param name="connectors">The connectors; each between two known nodes.</param>
    /// <exception cref="ArgumentException">A rule above is broken.</exception>
    public Diagram(Length width, Length height, IEnumerable<FlowNode> nodes, IEnumerable<FlowConnector>? connectors = null)
    {
        if (!double.IsFinite(width.Points) || !double.IsFinite(height.Points) || width.Points <= 0 || height.Points <= 0)
        {
            throw new ArgumentException("The size of a diagram must be finite and positive.");
        }

        Width = width;
        Height = height;
        Nodes = ModelCollections.Snapshot(nodes, nameof(nodes));
        Connectors = connectors is null ? [] : ModelCollections.Snapshot(connectors, nameof(connectors));
        if (Nodes.Count == 0 || Nodes.Count > MaxNodes || Connectors.Count > MaxConnectors)
        {
            throw new ArgumentException($"A diagram has 1 to {MaxNodes} nodes and at most {MaxConnectors} connectors.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (!ids.Add(node.Id))
            {
                throw new ArgumentException($"The node id '{node.Id}' is used twice.", nameof(nodes));
            }

            if (node.X.Points + node.Width.Points > width.Points + 0.001 || node.Y.Points + node.Height.Points > height.Points + 0.001)
            {
                throw new ArgumentException($"The node '{node.Id}' lies outside the diagram.", nameof(nodes));
            }
        }

        foreach (var connector in Connectors)
        {
            if (!ids.Contains(connector.From) || !ids.Contains(connector.To))
            {
                throw new ArgumentException($"The connector from '{connector.From}' to '{connector.To}' refers to an unknown node.", nameof(connectors));
            }
        }
    }

    /// <summary>The width of the area.</summary>
    public Length Width { get; }

    /// <summary>The height of the area.</summary>
    public Length Height { get; }

    /// <summary>The nodes.</summary>
    public IReadOnlyList<FlowNode> Nodes { get; }

    /// <summary>The connectors.</summary>
    public IReadOnlyList<FlowConnector> Connectors { get; }

    /// <summary>Alternative text for accessibility.</summary>
    public string? AltText { get; init; }

    /// <summary>The shapes the diagram is drawn as, with their positions in the area, in drawing order.</summary>
    internal IReadOnlyList<PlacedShape> Shapes => _shapes ??= DiagramLayout.Build(this);

    internal static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A shape of an expanded diagram and the position of its top left corner in the diagram.</summary>
internal readonly record struct PlacedShape(DrawingShape Shape, Length X, Length Y);
