namespace Reglet;

/// <summary>Page margins.</summary>
public sealed record PageMargins
{
    /// <summary>Equal margin on all sides (default: 1 inch).</summary>
    public static PageMargins Normal { get; } = new(Length.FromInches(1));

    /// <summary>Creates uniform margins.</summary>
    public PageMargins(Length all)
        : this(all, all, all, all)
    {
    }

    /// <summary>Creates margins with individual values.</summary>
    /// <exception cref="ArgumentException">A margin is negative.</exception>
    public PageMargins(Length top, Length right, Length bottom, Length left)
    {
        if (top.Points < 0 || right.Points < 0 || bottom.Points < 0 || left.Points < 0)
        {
            throw new ArgumentException("Page margins must not be negative.");
        }

        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    /// <summary>Oberer Rand.</summary>
    public Length Top { get; }

    /// <summary>Rechter Rand.</summary>
    public Length Right { get; }

    /// <summary>Bottom margin.</summary>
    public Length Bottom { get; }

    /// <summary>Linker Rand.</summary>
    public Length Left { get; }
}
