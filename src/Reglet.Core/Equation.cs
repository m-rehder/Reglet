namespace Reglet;

/// <summary>How the text of a <see cref="MathText"/> is set.</summary>
public enum MathTextStyle
{
    /// <summary>Upright: numbers, operators, function names.</summary>
    Normal,

    /// <summary>Slanted: variables.</summary>
    Italic,
}

/// <summary>A part of a formula (ADR-0031). The kinds are fixed: text, row, fraction, script, radical, n-ary operator, delimiter.</summary>
public abstract class MathNode
{
    /// <summary>The most nodes in one formula.</summary>
    public const int MaxNodes = 2000;

    /// <summary>The deepest nesting of one formula.</summary>
    public const int MaxDepth = 40;

    private protected MathNode(IEnumerable<MathNode?> children)
    {
        var count = 1;
        var depth = 1;
        foreach (var child in children)
        {
            if (child is null)
            {
                continue;
            }

            count += child.NodeCount;
            depth = Math.Max(depth, child.Depth + 1);
        }

        if (count > MaxNodes || depth > MaxDepth)
        {
            throw new ArgumentException($"A formula has at most {MaxNodes} nodes and {MaxDepth} levels.");
        }

        NodeCount = count;
        Depth = depth;
    }

    /// <summary>The number of nodes in this part, itself included.</summary>
    internal int NodeCount { get; }

    /// <summary>The levels of this part, itself included.</summary>
    internal int Depth { get; }

    private protected static T Need<T>(T? node, string name) where T : MathNode
        => node ?? throw new ArgumentNullException(name);
}

/// <summary>A run of characters in a formula.</summary>
public sealed class MathText : MathNode
{
    /// <summary>Creates a run.</summary>
    /// <param name="text">The characters; not empty.</param>
    /// <param name="style">Upright or slanted; null sets letters slanted and everything else upright.</param>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    public MathText(string text, MathTextStyle? style = null)
        : base([])
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (style is { } value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown text style.");
        }

        Text = text;
        Style = style ?? (text.All(char.IsLetter) ? MathTextStyle.Italic : MathTextStyle.Normal);
    }

    /// <summary>The characters.</summary>
    public string Text { get; }

    /// <summary>How the text is set (never null: the automatic choice is resolved).</summary>
    public MathTextStyle Style { get; }
}

/// <summary>A sequence of parts, set side by side.</summary>
public sealed class MathRow : MathNode
{
    /// <summary>Creates a row.</summary>
    /// <param name="items">The parts; at least one.</param>
    /// <exception cref="ArgumentException">There is no part.</exception>
    public MathRow(IEnumerable<MathNode> items)
        : base(Snapshot(items, out var list))
    {
        Items = list;
    }

    /// <summary>The parts.</summary>
    public IReadOnlyList<MathNode> Items { get; }

    private static IReadOnlyList<MathNode> Snapshot(IEnumerable<MathNode> items, out IReadOnlyList<MathNode> list)
    {
        list = ModelCollections.Snapshot(items, nameof(items));
        return list.Count == 0 ? throw new ArgumentException("A row needs at least one part.", nameof(items)) : list;
    }
}

/// <summary>A fraction.</summary>
public sealed class MathFraction : MathNode
{
    /// <summary>Creates a fraction.</summary>
    public MathFraction(MathNode numerator, MathNode denominator)
        : base([Need(numerator, nameof(numerator)), Need(denominator, nameof(denominator))])
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>The part above the bar.</summary>
    public MathNode Numerator { get; }

    /// <summary>The part below the bar.</summary>
    public MathNode Denominator { get; }
}

/// <summary>A base with a superscript, a subscript or both.</summary>
public sealed class MathScript : MathNode
{
    /// <summary>Creates a script.</summary>
    /// <exception cref="ArgumentException">Neither a superscript nor a subscript is given.</exception>
    public MathScript(MathNode baseNode, MathNode? superscript = null, MathNode? subscript = null)
        : base([Need(baseNode, nameof(baseNode)), superscript, subscript])
    {
        if (superscript is null && subscript is null)
        {
            throw new ArgumentException("A script needs a superscript or a subscript.");
        }

        Base = baseNode;
        Superscript = superscript;
        Subscript = subscript;
    }

    /// <summary>The part the scripts belong to.</summary>
    public MathNode Base { get; }

    /// <summary>The raised part; null for none.</summary>
    public MathNode? Superscript { get; }

    /// <summary>The lowered part; null for none.</summary>
    public MathNode? Subscript { get; }
}

/// <summary>A root: a square root, or a root with a degree.</summary>
public sealed class MathRadical : MathNode
{
    /// <summary>Creates a root.</summary>
    /// <param name="radicand">The part under the sign.</param>
    /// <param name="degree">The degree (a small 3 for a cube root); null for a square root.</param>
    public MathRadical(MathNode radicand, MathNode? degree = null)
        : base([Need(radicand, nameof(radicand)), degree])
    {
        Radicand = radicand;
        Degree = degree;
    }

    /// <summary>The part under the sign.</summary>
    public MathNode Radicand { get; }

    /// <summary>The degree; null for a square root.</summary>
    public MathNode? Degree { get; }
}

/// <summary>A big operator (sum, product, integral, union, intersection) with limits and a body.</summary>
public sealed class MathNary : MathNode
{
    /// <summary>The operators that are allowed.</summary>
    public const string Operators = "∑∏∫∬∭∮⋃⋂";

    /// <summary>Creates an operator.</summary>
    /// <param name="op">One of <see cref="Operators"/>.</param>
    /// <param name="body">What the operator applies to.</param>
    /// <param name="lower">The lower limit; null for none.</param>
    /// <param name="upper">The upper limit; null for none.</param>
    /// <exception cref="ArgumentException">The operator is not allowed.</exception>
    public MathNary(char op, MathNode body, MathNode? lower = null, MathNode? upper = null)
        : base([Need(body, nameof(body)), lower, upper])
    {
        if (!Operators.Contains(op, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The operator must be one of {Operators}.", nameof(op));
        }

        Operator = op;
        Body = body;
        Lower = lower;
        Upper = upper;
    }

    /// <summary>The operator character.</summary>
    public char Operator { get; }

    /// <summary>What the operator applies to.</summary>
    public MathNode Body { get; }

    /// <summary>The lower limit; null for none.</summary>
    public MathNode? Lower { get; }

    /// <summary>The upper limit; null for none.</summary>
    public MathNode? Upper { get; }

    /// <summary>Whether the limits stand above and below (sum, product, union, intersection) and not beside (integrals).</summary>
    public bool LimitsAbove => Operator is '∑' or '∏' or '⋃' or '⋂';
}

/// <summary>Brackets around one or more parts separated by commas.</summary>
public sealed class MathDelimiter : MathNode
{
    /// <summary>The characters that are allowed as opening or closing bracket.</summary>
    public const string Brackets = "()[]{}|‖⌊⌋⌈⌉⟨⟩";

    /// <summary>Creates a bracketed group.</summary>
    /// <param name="open">The opening bracket; null for none.</param>
    /// <param name="close">The closing bracket; null for none.</param>
    /// <param name="items">The parts; at least one.</param>
    /// <exception cref="ArgumentException">A bracket is not allowed or there is no part.</exception>
    public MathDelimiter(char? open, char? close, IEnumerable<MathNode> items)
        : base(Snapshot(items, out var list))
    {
        foreach (var bracket in new[] { open, close })
        {
            if (bracket is { } value && !Brackets.Contains(value, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The bracket must be one of {Brackets}.");
            }
        }

        Open = open;
        Close = close;
        Items = list;
    }

    /// <summary>The opening bracket; null for none.</summary>
    public char? Open { get; }

    /// <summary>The closing bracket; null for none.</summary>
    public char? Close { get; }

    /// <summary>The parts, set apart by commas.</summary>
    public IReadOnlyList<MathNode> Items { get; }

    private static IReadOnlyList<MathNode> Snapshot(IEnumerable<MathNode> items, out IReadOnlyList<MathNode> list)
    {
        list = ModelCollections.Snapshot(items, nameof(items));
        return list.Count == 0 ? throw new ArgumentException("A group needs at least one part.", nameof(items)) : list;
    }
}

/// <summary>
/// A display equation (ADR-0031): a formula in a paragraph of its own. DOCX and ODT store it as an editable formula
/// (OMML, MathML); the PDF draws it.
/// </summary>
public sealed class Equation : Block
{
    /// <summary>Creates an equation.</summary>
    /// <param name="root">The formula.</param>
    public Equation(MathNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
    }

    /// <summary>The formula.</summary>
    public MathNode Root { get; }

    /// <summary>Alignment of the formula; null means centred.</summary>
    public TextAlignment? Alignment { get; init; }

    /// <summary>Alternative text for accessibility.</summary>
    public string? AltText { get; init; }
}
