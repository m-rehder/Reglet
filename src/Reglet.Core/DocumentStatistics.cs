namespace Reglet;

/// <summary>
/// content statistics of a document (phase AE, AE02): counts of the model elements and of the
/// text.
/// </summary>
/// <param name="Sections">Number of sections.</param>
/// <param name="Blocks">Number of top-level blocks (including the bands).</param>
/// <param name="Paragraphs">Number of paragraphs.</param>
/// <param name="Headings">Number of headings.</param>
/// <param name="Lists">Number of lists.</param>
/// <param name="ListItems">Number of list items.</param>
/// <param name="Tables">Number of tables.</param>
/// <param name="TableRows">Number of table rows.</param>
/// <param name="TableCells">Number of table cells.</param>
/// <param name="Images">Number of images.</param>
/// <param name="Words">Number of words (whitespace separated).</param>
/// <param name="Characters">Number of characters.</param>
public sealed record DocumentStatistics(
    int Sections,
    int Blocks,
    int Paragraphs,
    int Headings,
    int Lists,
    int ListItems,
    int Tables,
    int TableRows,
    int TableCells,
    int Images,
    int Words,
    int Characters);
