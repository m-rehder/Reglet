namespace Reglet;

/// <summary>Which state of the tracked changes (ADR-0027) a PDF shows.</summary>
public enum PdfRevisionMode
{
    /// <summary>All changes accepted: insertions are plain text, deleted text is left out (default, Word's "Final" view).</summary>
    Final,

    /// <summary>All changes rejected: inserted text is left out, deleted text is shown.</summary>
    Original,

    /// <summary>
    /// Insertions are underlined and deletions struck through, both in one color per author (taken from a fixed
    /// palette in the order the authors first appear).
    /// </summary>
    Markup,
}
