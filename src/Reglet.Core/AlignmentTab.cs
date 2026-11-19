namespace Reglet;

/// <summary>What an <see cref="AlignmentTab"/> is measured from.</summary>
public enum AlignmentTabRelativeTo
{
    /// <summary>The page margins: right is the right margin, center the middle of the text area.</summary>
    Margin,

    /// <summary>The paragraph indents: right is the right indent, center the middle between the indents.</summary>
    Indent,
}

/// <summary>
/// an alignment tab (<c>w:ptab</c>): the text after it is aligned left, centered or right in the
/// text area, whatever the tab stops of the paragraph are. DOCX writes and reads it as it is. ODT and
/// PDF have no such tab; they write a normal tab and a tab stop at the position it stands for. That
/// works for paragraphs in the body, in headers, footers and lists; in a table cell or a text box
/// it is a plain tab.
/// </summary>
public sealed class AlignmentTab : Inline
{
    /// <summary>Creates an alignment tab.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The alignment is not left, center or right, or a value is unknown.</exception>
    public AlignmentTab(
        TabStopAlignment alignment,
        TabStopLeader leader = TabStopLeader.None,
        AlignmentTabRelativeTo relativeTo = AlignmentTabRelativeTo.Margin)
    {
        if (alignment is not (TabStopAlignment.Left or TabStopAlignment.Center or TabStopAlignment.Right))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "An alignment tab is left, center or right.");
        }

        if (!Enum.IsDefined(leader))
        {
            throw new ArgumentOutOfRangeException(nameof(leader), leader, "Unknown tab leader.");
        }

        if (!Enum.IsDefined(relativeTo))
        {
            throw new ArgumentOutOfRangeException(nameof(relativeTo), relativeTo, "Unknown alignment tab reference.");
        }

        Alignment = alignment;
        Leader = leader;
        RelativeTo = relativeTo;
    }

    /// <summary>Where the text after the tab sits.</summary>
    public TabStopAlignment Alignment { get; }

    /// <summary>The leader before the text.</summary>
    public TabStopLeader Leader { get; }

    /// <summary>What the position is measured from.</summary>
    public AlignmentTabRelativeTo RelativeTo { get; }
}
