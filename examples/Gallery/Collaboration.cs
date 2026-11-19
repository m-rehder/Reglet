using Reglet;

namespace Gallery;

/// <summary>Working together: comments with replies and tracked changes.</summary>
public static class Collaboration
{
    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 9, 30, 0, TimeSpan.FromHours(1));

    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("collaboration", "comments", "Comments on a word and a sentence, a reply and a resolved comment.")
        {
            Document = Comments,
        };
        yield return new Example("collaboration", "tracked-changes", "Insertions and deletions by two authors that Word and LibreOffice can accept or reject.")
        {
            Document = TrackedChanges,
        };
    }

    private static PortableDocument Comments()
    {
        var question = new Comment(1, "Anna Berg", "Is this date confirmed?") { Initials = "AB", Date = Monday, Replies = [new Comment(2, "Tom Weber", "Yes, the venue confirmed it today.") { Initials = "TW", Date = Monday.AddHours(2) }] };
        var done = new Comment(3, "Tom Weber", "Fixed the spelling.") { Initials = "TW", Date = Monday.AddHours(3), Resolved = true };
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Team meeting")),
            new Paragraph(new Inline[] { new TextRun("The next meeting is on "), question, new TextRun("12 March"), new CommentEnd(1), new TextRun(" in room 4.") }),
            new Paragraph(new Inline[] { done, new TextRun("Please bring the quarterly figures."), new CommentEnd(3) }),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Comments" }, new Section(blocks));
    }

    private static PortableDocument TrackedChanges()
    {
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Contract draft")),
            new Paragraph(new Inline[]
            {
                new TextRun("The supplier delivers within "),
                new TrackedChange(1, TrackedChangeKind.Deletion, "Anna Berg") { Date = Monday }, new TextRun("30"), new TrackedChangeEnd(1),
                new TrackedChange(2, TrackedChangeKind.Insertion, "Anna Berg") { Date = Monday }, new TextRun("14"), new TrackedChangeEnd(2),
                new TextRun(" days of the order."),
            }),
            new Paragraph(new Inline[]
            {
                new TextRun("Payment is due "),
                new TrackedChange(3, TrackedChangeKind.Insertion, "Tom Weber") { Date = Monday.AddDays(1) }, new TextRun("30 days "), new TrackedChangeEnd(3),
                new TextRun("after delivery."),
            }),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Tracked changes" }, new Section(blocks));
    }
}
