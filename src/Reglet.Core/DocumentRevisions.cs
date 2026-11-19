namespace Reglet;

/// <summary>
/// Accepts and rejects tracked changes (ADR-0027). The model is immutable, so every call returns a new
/// document; the markers of the changes touched are gone, the content they marked is kept or removed.
/// Only the text of the body is changed (the writers write tracked changes there only). A paragraph
/// left empty by a deletion across paragraphs is not merged with its neighbour.
/// </summary>
public static class DocumentRevisions
{
    /// <summary>Accepts every tracked change: insertions stay, deleted text is removed.</summary>
    public static PortableDocument AcceptAll(PortableDocument document) => Apply(document, accept: true, ids: null);

    /// <summary>Rejects every tracked change: insertions are removed, deleted text stays.</summary>
    public static PortableDocument RejectAll(PortableDocument document) => Apply(document, accept: false, ids: null);

    /// <summary>Accepts the tracked change with this id; the document is returned unchanged if there is none.</summary>
    public static PortableDocument Accept(PortableDocument document, int id) => Apply(document, accept: true, new HashSet<int> { id });

    /// <summary>Rejects the tracked change with this id; the document is returned unchanged if there is none.</summary>
    public static PortableDocument Reject(PortableDocument document, int id) => Apply(document, accept: false, new HashSet<int> { id });

    /// <summary>
    /// Marks every tracked change in the text of the body: <paramref name="restyle"/> gets each run inside a change
    /// together with the innermost change that covers it; the markers are removed. The PDF writer uses this for
    /// the markup mode of the PDF (ADR-0027).
    /// </summary>
    internal static PortableDocument Markup(PortableDocument document, Func<TextRun, TrackedChange, TextRun> restyle)
        => Apply(document, accept: true, ids: null, restyle);

    private static PortableDocument Apply(PortableDocument document, bool accept, ISet<int>? ids, Func<TextRun, TrackedChange, TextRun>? restyle = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var walker = new Walker(accept, ids, restyle);
        var sections = new List<Section>(document.Sections.Count);
        var changed = false;
        foreach (var section in document.Sections)
        {
            var blocks = walker.Blocks(section.Blocks);
            changed |= !ReferenceEquals(blocks, section.Blocks);
            sections.Add(ReferenceEquals(blocks, section.Blocks) ? section : section with { Blocks = blocks });
        }

        return changed ? document with { Sections = sections } : document;
    }

    /// <summary>Walks the body in document order; a range can start in one paragraph and end in another.</summary>
    private sealed class Walker(bool accept, ISet<int>? ids, Func<TextRun, TrackedChange, TextRun>? restyle)
    {
        // Markup mode: the open changes, innermost last.
        private readonly List<TrackedChange> _marking = [];

        // Open ranges of selected changes whose content is removed.
        private readonly HashSet<int> _removing = [];

        // Open ranges of selected changes whose content stays.
        private readonly HashSet<int> _keeping = [];

        // Markers inside removed content: their counterpart is dropped too.
        private readonly HashSet<int> _droppedChanges = [];
        private readonly HashSet<int> _droppedComments = [];

        public IReadOnlyList<Block> Blocks(IReadOnlyList<Block> blocks)
        {
            var result = new List<Block>(blocks.Count);
            var changed = false;
            foreach (var block in blocks)
            {
                var mapped = Block(block);
                changed |= !ReferenceEquals(mapped, block);
                result.Add(mapped);
            }

            return changed ? result : blocks;
        }

        private Block Block(Block block)
        {
            switch (block)
            {
                case Paragraph paragraph:
                {
                    var inlines = Inlines(paragraph.Inlines);
                    return ReferenceEquals(inlines, paragraph.Inlines)
                        ? paragraph
                        : new Paragraph(inlines) { Style = paragraph.Style, SourceStyleBase = paragraph.SourceStyleBase };
                }

                case Heading heading:
                {
                    var inlines = Inlines(heading.Inlines);
                    return ReferenceEquals(inlines, heading.Inlines)
                        ? heading
                        : new Heading(heading.Level, inlines) { Style = heading.Style };
                }

                case ListBlock list:
                {
                    var changed = false;
                    var items = new List<ListItem>(list.Items.Count);
                    foreach (var item in list.Items)
                    {
                        var blocks = Blocks(item.Blocks);
                        changed |= !ReferenceEquals(blocks, item.Blocks);
                        items.Add(ReferenceEquals(blocks, item.Blocks) ? item : new ListItem(blocks));
                    }

                    return changed ? new ListBlock(list.NumberingName, items) { NestingLevel = list.NestingLevel } : list;
                }

                case TableBlock table:
                {
                    var changed = false;
                    var rows = new List<TableRow>(table.Rows.Count);
                    foreach (var row in table.Rows)
                    {
                        var rowChanged = false;
                        var cells = new List<TableCell>(row.Cells.Count);
                        foreach (var cell in row.Cells)
                        {
                            var blocks = Blocks(cell.Blocks);
                            rowChanged |= !ReferenceEquals(blocks, cell.Blocks);
                            cells.Add(ReferenceEquals(blocks, cell.Blocks)
                                ? cell
                                : new TableCell(blocks)
                                {
                                    Alignment = cell.Alignment,
                                    VerticalAlignment = cell.VerticalAlignment,
                                    Background = cell.Background,
                                    Borders = cell.Borders,
                                    ColumnSpan = cell.ColumnSpan,
                                    RowSpan = cell.RowSpan,
                                    Padding = cell.Padding,
                                });
                        }

                        changed |= rowChanged;
                        rows.Add(rowChanged
                            ? new TableRow(cells) { IsHeader = row.IsHeader, Height = row.Height, HeightRule = row.HeightRule }
                            : row);
                    }

                    return changed
                        ? new TableBlock(rows)
                        {
                            ColumnWidths = table.ColumnWidths,
                            Border = table.Border,
                            CellPadding = table.CellPadding,
                            WidthPercent = table.WidthPercent,
                            Style = table.Style,
                        }
                        : table;
                }

                default:
                    return block;
            }
        }

        private IReadOnlyList<Inline> Inlines(IReadOnlyList<Inline> inlines)
        {
            var result = new List<Inline>(inlines.Count);
            var changed = false;
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TrackedChange start when restyle is not null:
                        _marking.Add(start);
                        changed = true;
                        break;
                    case TrackedChangeEnd end when restyle is not null:
                        changed = true;
                        _marking.RemoveAll(open => open.Id == end.Id);
                        break;
                    case TextRun run when restyle is not null && _marking.Count > 0:
                        result.Add(restyle(run, _marking[^1]));
                        changed = true;
                        break;
                    case TrackedChange start when ids is null || ids.Contains(start.Id):
                        // Whether the content goes depends on the kind: accepting a deletion or rejecting an insertion removes it.
                        (((start.Kind == TrackedChangeKind.Deletion) == accept) ? _removing : _keeping).Add(start.Id);
                        changed = true;
                        break;
                    case TrackedChangeEnd end when _removing.Remove(end.Id) || _keeping.Remove(end.Id):
                        changed = true;
                        break;
                    case TrackedChange start when _removing.Count > 0:
                        _droppedChanges.Add(start.Id);
                        changed = true;
                        break;
                    case TrackedChangeEnd end when _droppedChanges.Remove(end.Id):
                        changed = true;
                        break;
                    case Comment comment when _removing.Count > 0:
                        _droppedComments.Add(comment.Id);
                        changed = true;
                        break;
                    case CommentEnd end when _droppedComments.Remove(end.Id):
                        changed = true;
                        break;
                    case Hyperlink hyperlink when _removing.Count == 0:
                    {
                        var inner = Inlines(hyperlink.Inlines);
                        changed |= !ReferenceEquals(inner, hyperlink.Inlines);
                        result.Add(ReferenceEquals(inner, hyperlink.Inlines) ? hyperlink : new Hyperlink(hyperlink.Target, inner));
                        break;
                    }

                    default:
                        if (_removing.Count > 0)
                        {
                            changed = true;
                        }
                        else
                        {
                            result.Add(inline);
                        }

                        break;
                }
            }

            return changed ? result : inlines;
        }
    }
}
