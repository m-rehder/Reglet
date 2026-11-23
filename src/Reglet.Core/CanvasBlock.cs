namespace Reglet;

/// <summary>
/// A page drawn at fixed coordinates (PdfCanvas). Internal: only the PDF writer knows the drawing;
/// the flow layout carries it as the single block of a margin-less section.
/// </summary>
internal sealed class CanvasBlock(object drawing) : Block
{
    public object Drawing { get; } = drawing;
}
