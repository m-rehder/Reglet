namespace Reglet;

/// <summary>
/// the start of a tracked change (ADR-0027). The inlines up to the <see cref="TrackedChangeEnd"/> with the
/// same <see cref="Id"/> were inserted or deleted by <see cref="Author"/>; the range may run across paragraphs.
/// </summary>
public sealed class TrackedChange : Inline
{
    /// <summary>Creates the start of a tracked change.</summary>
    /// <param name="id">The id that ties the start to its <see cref="TrackedChangeEnd"/>; unique among the tracked changes of the document.</param>
    /// <param name="kind">Whether the text was inserted or deleted.</param>
    /// <param name="author">The author's name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative or <paramref name="kind"/> is not defined.</exception>
    /// <exception cref="ArgumentException">The author is empty.</exception>
    public TrackedChange(int id, TrackedChangeKind kind, string author)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)kind, (int)TrackedChangeKind.Deletion);
        ArgumentOutOfRangeException.ThrowIfNegative((int)kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        Id = id;
        Kind = kind;
        Author = author;
    }

    /// <summary>The id that ties the start to its <see cref="TrackedChangeEnd"/>.</summary>
    public int Id { get; }

    /// <summary>Whether the text was inserted or deleted.</summary>
    public TrackedChangeKind Kind { get; }

    /// <summary>The author's name.</summary>
    public string Author { get; }

    /// <summary>When the change was made (default: unknown).</summary>
    public DateTimeOffset? Date { get; init; }
}
