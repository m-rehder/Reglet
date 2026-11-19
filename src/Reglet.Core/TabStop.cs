namespace Reglet;

/// <summary>Alignment of a tab stop.</summary>
public enum TabStopAlignment
{
    /// <summary>Text begins at the tab stop.</summary>
    Left,

    /// <summary>Text is centered on the tab stop.</summary>
    Center,

    /// <summary>Text ends at the tab stop.</summary>
    Right,

    /// <summary>Decimal separator is placed at the tab stop.</summary>
    Decimal,
}

/// <summary>Leader before a tab stop.</summary>
public enum TabStopLeader
{
    /// <summary>No leader.</summary>
    None,

    /// <summary>Points.</summary>
    Dot,

    /// <summary>Bindestriche.</summary>
    Hyphen,

    /// <summary>Underline.</summary>
    Underscore,
}

/// <summary>A tab stop with position, alignment and optional leader.</summary>
public sealed record TabStop
{
    /// <summary>Creates a tab stop.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Position or alignment/leader is invalid.</exception>
    public TabStop(Length position, TabStopAlignment alignment = TabStopAlignment.Left, TabStopLeader leader = TabStopLeader.None)
    {
        if (position.Points < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Tab stop position must not be negative.");
        }

        if (!Enum.IsDefined(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Unknown tab alignment.");
        }

        if (!Enum.IsDefined(leader))
        {
            throw new ArgumentOutOfRangeException(nameof(leader), leader, "Unknown padding character.");
        }

        Position = position;
        Alignment = alignment;
        Leader = leader;
    }

    /// <summary>Position ab dem linken Rand.</summary>
    public Length Position { get; }

    /// <summary>Alignment.</summary>
    public TabStopAlignment Alignment { get; }

    /// <summary>Leader.</summary>
    public TabStopLeader Leader { get; }
}
