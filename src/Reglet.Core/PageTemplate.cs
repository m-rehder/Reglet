namespace Reglet;

/// <summary>
/// a page template: design elements applied to every page. Unlike
/// <see cref="DocumentTemplate"/> it is not file-based, but is built from the model
/// built up (phase S).
/// </summary>
/// <remarks>
/// The page template extends the PDF writer's options with headers/footers, a
/// watermark and a page frame. Individual specifications remain optional; missing
/// elements are not drawn.
/// </remarks>
public sealed record PageTemplate
{
    /// <summary>Optional header for all pages.</summary>
    public HeaderFooter? Header { get; init; }

    /// <summary>Optional footer for all pages.</summary>
    public HeaderFooter? Footer { get; init; }

    /// <summary>Optional watermark behind the content.</summary>
    public Watermark? Watermark { get; init; }

    /// <summary>Optional picture watermark behind the content (OF-B09).</summary>
    public PictureWatermark? PictureWatermark { get; init; }

    /// <summary>Optional page frame around the type area.</summary>
    public PageFrame? PageFrame { get; init; }
}
