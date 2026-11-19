namespace Reglet;

/// <summary>
/// a review comment, anchored in the text. The comment starts here; a <see cref="CommentEnd"/> with the
/// same <see cref="Id"/> ends the commented range. Without an end the comment is a point. A reply is a
/// comment in <see cref="Replies"/>; it has no anchor of its own.
/// </summary>
/// <remarks>The body is plain text (ADR-0026); the writers drop character formatting and report it.</remarks>
public sealed class Comment : Inline
{
    private readonly IReadOnlyList<Comment> _replies = Array.Empty<Comment>();

    /// <summary>Creates a comment with a single paragraph of text.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative.</exception>
    /// <exception cref="ArgumentException">The author or the text is empty.</exception>
    public Comment(int id, string author, string text)
        : this(id, author, new[] { text })
    {
    }

    /// <summary>Creates a comment with several paragraphs of text.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is negative.</exception>
    /// <exception cref="ArgumentException">The author is empty, there is no paragraph, or a paragraph is null.</exception>
    public Comment(int id, string author, IEnumerable<string> paragraphs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentNullException.ThrowIfNull(paragraphs);

        var snapshot = ModelCollections.Snapshot(paragraphs, nameof(paragraphs));
        if (snapshot.Count == 0)
        {
            throw new ArgumentException("A comment needs at least one paragraph.", nameof(paragraphs));
        }

        Id = id;
        Author = author;
        Paragraphs = snapshot;
    }

    /// <summary>The id that ties the comment to its <see cref="CommentEnd"/>; unique within the document.</summary>
    public int Id { get; }

    /// <summary>The author's name.</summary>
    public string Author { get; }

    /// <summary>The author's initials (default: none).</summary>
    public string? Initials { get; init; }

    /// <summary>When the comment was written (default: unknown).</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The paragraphs of the comment text.</summary>
    public IReadOnlyList<string> Paragraphs { get; }

    /// <summary>Whether the comment is marked as done (resolved).</summary>
    public bool Resolved { get; init; }

    /// <summary>The replies, oldest first. A reply has no replies of its own, and the ids of all replies must differ.</summary>
    /// <exception cref="ArgumentException">A reply has replies, or an id is used twice.</exception>
    public IReadOnlyList<Comment> Replies
    {
        get => _replies;
        init
        {
            var replies = ModelCollections.Snapshot(value, nameof(Replies));
            if (replies.Any(reply => reply.Replies.Count > 0))
            {
                throw new ArgumentException("A reply cannot have replies.", nameof(Replies));
            }

            var ids = replies.Select(reply => reply.Id).Append(Id);
            if (ids.Distinct().Count() != replies.Count + 1)
            {
                throw new ArgumentException("Comment ids must be unique.", nameof(Replies));
            }

            _replies = replies;
        }
    }
}
