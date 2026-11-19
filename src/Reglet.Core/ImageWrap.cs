namespace Reglet;

/// <summary>Wrapping of the text around a floating image.</summary>
public enum ImageWrap
{
    /// <summary>Inline (as character); the image is part of the text line.</summary>
    None,

    /// <summary>Floating; the text wraps on both sides of the image.</summary>
    Square,

    /// <summary>Floating; the text stops above and continues below the image.</summary>
    TopAndBottom,

    /// <summary>
    /// Floating; the text runs through the image (Word wraps tightly around its contour, the PDF
    /// and ODT wrap around the bounding box without reserving space).
    /// </summary>
    Through,

    /// <summary>Floating behind the text; the text is not moved.</summary>
    Behind,

    /// <summary>Floating in front of the text; the text is not moved.</summary>
    InFront,
}
