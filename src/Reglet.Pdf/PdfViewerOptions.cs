namespace Reglet;

/// <summary>
/// the window flags of the PDF viewer (OF-F02, ISO 32000-1 12.3.3).
/// </summary>
[Flags]
public enum PdfViewerPreferences
{
    /// <summary>no flag is written.</summary>
    None = 0,

    /// <summary>the toolbar of the viewer is hidden.</summary>
    HideToolbar = 1,

    /// <summary>the menu bar of the viewer is hidden.</summary>
    HideMenuBar = 2,

    /// <summary>the user interface of the viewer is hidden (only the document is shown).</summary>
    HideWindowUi = 4,

    /// <summary>the page is scaled to the window of the viewer.</summary>
    FitWindow = 8,

    /// <summary>the page is centered in the window of the viewer.</summary>
    CenterWindow = 16,

    /// <summary>the document is opened in full screen mode.</summary>
    FullScreen = 32,
}

/// <summary>
/// the page layout of the PDF viewer (OF-F02, ISO 32000-1 12.3.2).
/// </summary>
public enum PdfPageLayout
{
    /// <summary>the viewer decides (the default, nothing is written).</summary>
    Default = 0,

    /// <summary>one page at a time.</summary>
    SinglePage,

    /// <summary>one column at a time.</summary>
    OneColumn,

    /// <summary>two columns, the odd pages on the left.</summary>
    TwoColumnLeft,

    /// <summary>two columns, the odd pages on the right.</summary>
    TwoColumnRight,

    /// <summary>two pages side by side, the odd pages on the left (PDF 1.5).</summary>
    TwoPageLeft,

    /// <summary>two pages side by side, the odd pages on the right (PDF 1.5).</summary>
    TwoPageRight,
}

/// <summary>
/// the mode the viewer opens the document in (OF-F02, ISO 32000-1 12.3.3).
/// </summary>
public enum PdfPageMode
{
    /// <summary>no mode is requested (the default, nothing is written).</summary>
    Default = 0,

    /// <summary>the outline (bookmarks) of the document is shown.</summary>
    UseOutlines,

    /// <summary>the page thumbnails are shown.</summary>
    UseThumbnails,

    /// <summary>the document is opened in full screen mode.</summary>
    FullScreen,

    /// <summary>the optional content groups of the document are shown.</summary>
    UseOptionalContent,

    /// <summary>the attachments of the document are shown.</summary>
    UseAttachments,
}
