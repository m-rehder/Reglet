namespace Reglet;

/// <summary>
/// the end of a commented range. A <see cref="Comment"/> marks the start; the text between the two is
/// what the comment refers to. Without an end the comment is a point in the text.
/// </summary>
public sealed class CommentEnd : Inline
{
    /// <summary>Creates the end of a commented range.</summary>
    /// <param name="id">The <see cref="Comment.Id"/> of the comment that starts the range.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative.</exception>
    public CommentEnd(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        Id = id;
    }

    /// <summary>The id of the comment that starts the range.</summary>
    public int Id { get; }
}
