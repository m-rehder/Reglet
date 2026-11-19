namespace Reglet;

/// <summary>
/// the kind of a PDF annotation (OF-F01). Only the four kinds below are written and read;
/// a link is not an annotation of this kind (hyperlinks are written from the model).
/// </summary>
public enum PdfAnnotationKind
{
    /// <summary>A sticky note at a position of the page (<c>/Text</c>).</summary>
    Text,

    /// <summary>A highlighted area of the page (<c>/Highlight</c>).</summary>
    Highlight,

    /// <summary>A stamp over an area of the page (<c>/Stamp</c>).</summary>
    Stamp,

    /// <summary>A text box drawn on the page (<c>/FreeText</c>).</summary>
    FreeText,
}

/// <summary>
/// a non-link annotation on a page of the PDF (OF-F01): a sticky note, a highlight, a stamp or a
/// free text box. The position is given in points from the lower left corner of the page.
/// </summary>
public sealed record PdfAnnotation
{
    private double _width = 18;
    private double _height = 18;

    /// <summary>The kind of the annotation.</summary>
    public PdfAnnotationKind Kind { get; init; } = PdfAnnotationKind.Text;

    /// <summary>The 1-based page the annotation is placed on.</summary>
    public int Page { get; init; } = 1;

    /// <summary>The horizontal position of the left edge in points.</summary>
    public double X { get; init; }

    /// <summary>The vertical position of the bottom edge in points.</summary>
    public double Y { get; init; }

    /// <summary>The width of the annotation area in points. Default is 18.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public double Width
    {
        get => _width;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Width), value, "The width must be positive.");
            }

            _width = value;
        }
    }

    /// <summary>The height of the annotation area in points. Default is 18.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    public double Height
    {
        get => _height;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Height), value, "The height must be positive.");
            }

            _height = value;
        }
    }

    /// <summary>The text of the annotation (<c>/Contents</c>); the text of a free text box is drawn on the page.</summary>
    public string? Content { get; init; }

    /// <summary>The author of the annotation (<c>/T</c>).</summary>
    public string? Author { get; init; }

    /// <summary>The subject of the annotation (<c>/Subj</c>).</summary>
    public string? Subject { get; init; }

    /// <summary>The colour of the annotation (<c>/C</c>); null means the viewer default.</summary>
    public Color? Color { get; init; }

    /// <summary>
    /// The icon of a text annotation (<c>/Name</c>: <c>Note</c>, <c>Comment</c>, <c>Help</c>,
    /// <c>NewParagraph</c>, <c>Paragraph</c>, <c>Insert</c>) or the stamp name of a stamp
    /// (<c>Approved</c>, <c>Draft</c>, <c>Confidential</c>, …); ignored for the other kinds.
    /// </summary>
    public string? Icon { get; init; }
}
