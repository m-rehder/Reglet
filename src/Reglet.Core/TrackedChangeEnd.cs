namespace Reglet;

/// <summary>The end of a tracked change; the <see cref="TrackedChange"/> with the same id marks the start (ADR-0027).</summary>
public sealed class TrackedChangeEnd : Inline
{
    /// <summary>Creates the end of a tracked change.</summary>
    /// <param name="id">The <see cref="TrackedChange.Id"/> of the change that starts the range.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative.</exception>
    public TrackedChangeEnd(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        Id = id;
    }

    /// <summary>The id of the change that starts the range.</summary>
    public int Id { get; }
}
