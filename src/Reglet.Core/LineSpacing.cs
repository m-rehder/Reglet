namespace Reglet;

/// <summary>Rule for the line spacing of a paragraph.</summary>
public enum LineSpacingRule
{
    /// <summary>Multiple of a single line (e.g. 1.5).</summary>
    Multiple,

    /// <summary>Minimum height of a row.</summary>
    AtLeast,

    /// <summary>Fixed line height.</summary>
    Exactly,
}

/// <summary>Line spacing of a paragraph: a multiple or a fixed minimum/line height.</summary>
public sealed record LineSpacing
{
    private LineSpacing(LineSpacingRule rule, double factor, Length height)
    {
        Rule = rule;
        Factor = factor;
        Height = height;
    }

    /// <summary>Rule of the line spacing.</summary>
    public LineSpacingRule Rule { get; }

    /// <summary>Multiple (only for <see cref="LineSpacingRule.Multiple"/>).</summary>
    public double Factor { get; }

    /// <summary>Height (only for <see cref="LineSpacingRule.AtLeast"/> or <see cref="LineSpacingRule.Exactly"/>).</summary>
    public Length Height { get; }

    /// <summary>Creates a line spacing as a multiple of a single line.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The multiple is not positive or finite.</exception>
    public static LineSpacing Multiple(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Multiple must be positive and finite.");
        }

        return new LineSpacing(LineSpacingRule.Multiple, factor, Length.Zero);
    }

    /// <summary>Creates a line spacing with a minimum height.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    public static LineSpacing AtLeast(Length height)
    {
        if (height.Points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        return new LineSpacing(LineSpacingRule.AtLeast, 1, height);
    }

    /// <summary>Creates a fixed line spacing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    public static LineSpacing Exactly(Length height)
    {
        if (height.Points <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        return new LineSpacing(LineSpacingRule.Exactly, 1, height);
    }
}
