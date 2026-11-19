namespace Reglet;

/// <summary>What a tracked change did to the text between its markers (ADR-0027).</summary>
public enum TrackedChangeKind
{
    /// <summary>The text was inserted.</summary>
    Insertion,

    /// <summary>The text was deleted; it is still in the document until the change is accepted.</summary>
    Deletion,
}
