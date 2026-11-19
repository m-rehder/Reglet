namespace Reglet;

/// <summary>How the comments of a document (ADR-0026) are written into a PDF.</summary>
public enum PdfCommentMode
{
    /// <summary>The comments are not written (default, like a PDF exported from Word).</summary>
    None,

    /// <summary>
    /// Every comment becomes a sticky note (<c>/Text</c> annotation) at its anchor; the replies follow the text of
    /// the comment in its contents. Comments in a header, footer or note are reported and not written.
    /// </summary>
    Annotations,
}
