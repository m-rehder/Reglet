namespace Reglet;

/// <summary>
/// editing protection of a DOCX document (phase AD, AD03), stored in <c>settings.xml</c> as
/// <c>w:documentProtection</c>. This is protection without encryption; the user can remove it.
/// </summary>
public enum DocxProtection
{
    /// <summary>Leave any existing protection unchanged; add none to a new document.</summary>
    None,

    /// <summary>Remove an existing protection.</summary>
    Remove,

    /// <summary>Read-only: editing is blocked.</summary>
    ReadOnly,

    /// <summary>Only comments may be added.</summary>
    Comments,

    /// <summary>Only tracked changes are allowed.</summary>
    TrackedChanges,

    /// <summary>Only form fields may be filled.</summary>
    Forms,
}
