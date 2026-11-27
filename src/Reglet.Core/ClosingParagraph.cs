namespace Reglet;

/// <summary>
/// The empty paragraph Word needs where a body, a section or a band does not end with one (after a table, for example).
/// Without it Word adds a paragraph in the default formatting when it opens the file, which can push a full page onto a
/// new one. The DOCX writer writes it as small as Word allows; the PDF sets the same height, so both count the same pages.
/// </summary>
internal static class ClosingParagraph
{
    /// <summary>The line height of the paragraph in twips (<c>w:line="1" w:lineRule="exact"</c>).</summary>
    public const long LineTwips = 1;

    /// <summary>The height of the paragraph in points.</summary>
    public const double Height = LineTwips / 20.0;

    /// <summary>
    /// Whether the last-written element of these blocks is a <c>w:p</c>. In the header/footer area, breaks, diagrams,
    /// equations, charts and embedded objects are not written.
    /// </summary>
    public static bool EndsWithParagraph(IReadOnlyList<Block> blocks, bool inBand)
    {
        if (blocks.Count == 0)
        {
            return false;
        }

        return blocks[^1] switch
        {
            Paragraph or Heading => true,
            ImageBlock or DrawingShape or FormField => true,
            // Written as a paragraph in the body only; in a band they are left out.
            Diagram or Equation or Chart or EmbeddedObject or PageBreak or ColumnBreak => !inBand,
            // An entry without a paragraph gets an empty numbered paragraph at the end.
            ListBlock { Items.Count: > 0 } list => !list.Items[^1].Blocks.Any(block => block is Paragraph)
                || EndsWithParagraph(list.Items[^1].Blocks, inBand),
            _ => false,
        };
    }
}
