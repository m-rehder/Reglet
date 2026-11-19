namespace Reglet;

/// <summary>
/// a footnote or endnote reference in the text. The writers place the reference marker and
/// the note content (for example at the bottom of the page).
/// </summary>
/// <remarks>
/// A note inside a header or footer is written as its text at the reference and reported: Word
/// shows no notes in a band, LibreOffice drops a <c>text:note</c> there, and a note reference in a
/// header or footer part makes a DOCX unreadable for LibreOffice.
/// </remarks>
public sealed class Footnote : Inline
{
    /// <summary>Creates a footnote with exactly one block.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
    public Footnote(Block block)
        : this(new[] { block ?? throw new ArgumentNullException(nameof(block)) })
    {
    }

    /// <summary>Creates a footnote with several blocks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public Footnote(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = ModelCollections.Snapshot(blocks, nameof(blocks));
    }

    /// <summary>Footnote or endnote (default: footnote).</summary>
    public NoteKind Kind { get; init; } = NoteKind.Footnote;

    /// <summary>The blocks of the note content.</summary>
    public IReadOnlyList<Block> Blocks { get; }

    /// <summary>
    /// the note id in the source package when the note was read from DOCX. The writer keeps it when
    /// it is unique, so a preserved block that references the note stays valid (AF07).
    /// </summary>
    internal int? SourceId { get; init; }
}
