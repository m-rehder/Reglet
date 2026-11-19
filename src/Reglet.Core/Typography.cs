namespace Reglet;

/// <summary>
/// central typography baseline used by all writers. Changes here affect
/// the same across formats.
/// </summary>
public static class Typography
{
    /// <summary>
    /// line height relative to the font size for the PDF default fonts (WinAnsi).
    /// default fonts have no font file with layout metrics and therefore use the
    /// usual factor 1.2. Embedded TrueType fonts compute with the metrics of the
    /// font file itself (ascender + descender + line gap from `hhea`); the
    /// matches Word and LibreOffice.
    /// </summary>
    public const double LineHeightFactor = 1.2;

    /// <summary>
    /// default spacing after a paragraph when <see cref="ParagraphStyle.SpaceAfter"/> is
    /// <c>null</c> (not set). List items (except the last paragraph of a list), table cells and
    /// headers/footers are compact and receive 0.
    /// </summary>
    public static readonly Length ParagraphSpaceAfter = Length.FromPoints(6);

    /// <summary>
    /// amount by which Word and LibreOffice reduce the space before of the first paragraph after a
    /// section break (new page). Measured with LibreOffice from our DOCX
    /// and observed in Word ("~5 pt" at a 12-pt heading): effectively
    /// <c>max(0, SpaceBefore − this amount)</c>. After a manual page break
    /// the space is dropped entirely (0). DOCX leaves the computation to Word/LibreOffice;
    /// ODT and PDF reproduce the reduction.
    /// </summary>
    public static readonly Length SpaceBeforeReductionAtSectionStart = Length.FromPoints(6);

    /// <summary>Padding of a table cell left and right (Word default 108 twips).</summary>
    public static readonly Length TableCellPaddingHorizontal = Length.FromPoints(5.4);

    /// <summary>Line width of the table borders (black, outer and inner).</summary>
    public static readonly Length TableBorderWidth = Length.FromPoints(0.5);

    /// <summary>Padding of a table cell top and bottom.</summary>
    public static readonly Length TableCellPaddingVertical = Length.Zero;

    /// <summary>
    /// distance of the header/footer from the sheet edge (Word default 0.5 inch). The area lies
    /// within the page margin; the body still begins at the page margin.
    /// </summary>
    public static readonly Length HeaderFooterDistance = Length.FromInches(0.5);

    /// <summary>Space before a heading.</summary>
    public static readonly Length HeadingSpaceBefore = Length.FromPoints(12);

    /// <summary>Space after a heading.</summary>
    public static readonly Length HeadingSpaceAfter = Length.FromPoints(6);

    /// <summary>Font sizes of headings 1–6 in points.</summary>
    private static readonly Length[] Sizes =
    [
        Length.FromPoints(16),
        Length.FromPoints(14),
        Length.FromPoints(12),
        Length.FromPoints(11),
        Length.FromPoints(11),
        Length.FromPoints(11),
    ];

    /// <summary>Font size of a heading of the specified level (1–6).</summary>
    public static Length HeadingSize(HeadingLevel level) => Sizes[(int)level - 1];
}
