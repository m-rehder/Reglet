namespace Reglet;

/// <summary>
/// the anchors of the captions and index marks of a document, for the tables of figures and the
/// index of the PDF: each caption (its <c>SEQ</c> field) and each index mark gets a stable number, a
/// layout pass puts a bookmark with that number at its place and the next pass reads the pages.
/// </summary>
internal sealed class PdfListMarks
{
    private readonly Dictionary<Field, int> _captionIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IndexEntry, int> _markIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<(int Id, string Label, string Text)> _captions = [];
    private readonly List<(int Id, IndexEntry Entry)> _marks = [];

    private PdfListMarks()
    {
    }

    /// <summary>The captions in document order, with their anchor numbers.</summary>
    public IReadOnlyList<(int Id, string Label, string Text)> Captions => _captions;

    /// <summary>The index marks in document order, with their anchor numbers.</summary>
    public IReadOnlyList<(int Id, IndexEntry Entry)> Marks => _marks;

    /// <summary>Collects the captions and marks of a document.</summary>
    public static PdfListMarks Collect(PortableDocument document)
    {
        var result = new PdfListMarks();
        var labels = document.Sections
            .SelectMany(section => FigureIndexScan.CaptionLabels(section.Blocks))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            foreach (var caption in FigureIndexScan.Captions(document, label))
            {
                if (result._captionIds.TryAdd(caption.Sequence, result._captions.Count))
                {
                    result._captions.Add((result._captions.Count, caption.Label, caption.Text));
                }
            }
        }

        // The captions of different labels are collected label by label; the order of the ids only has to be stable.
        foreach (var entry in FigureIndexScan.Marks(document))
        {
            if (result._markIds.TryAdd(entry, result._marks.Count))
            {
                result._marks.Add((result._marks.Count, entry));
            }
        }

        return result;
    }

    /// <summary>The anchor of a caption (the bookmark at its sequence field), or null.</summary>
    public string? CaptionAnchor(Field field)
        => _captionIds.TryGetValue(field, out var id) ? CaptionAnchor(id) : null;

    /// <summary>The anchor of an index mark, or null.</summary>
    public string? MarkAnchor(IndexEntry entry)
        => _markIds.TryGetValue(entry, out var id) ? MarkAnchor(id) : null;

    /// <summary>The bookmark name of a caption.</summary>
    public static string CaptionAnchor(int id) => "_PdCap" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The bookmark name of an index mark.</summary>
    public static string MarkAnchor(int id) => "_PdIdx" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
