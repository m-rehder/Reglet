namespace Reglet;

/// <summary>
/// edits a document at a bookmark: replaces the content of a bookmark range or inserts blocks after
/// the block that holds the bookmark. The model is immutable, so each call returns a new document.
/// The body, list items and table cells are searched; headers, footers and notes are not.
/// Content taken from another document keeps the styles it names; the styles of the other
/// document are not merged.
/// </summary>
public static class DocumentBookmarks
{
    /// <summary>
    /// replaces what lies between a <see cref="Bookmark"/> and its <see cref="BookmarkEnd"/> with
    /// <paramref name="content"/>; a bookmark without an end gets the content right after it.
    /// </summary>
    /// <exception cref="ArgumentException">No paragraph or heading holds the bookmark.</exception>
    /// <exception cref="InvalidOperationException">The bookmark range ends in another paragraph.</exception>
    public static PortableDocument ReplaceContent(PortableDocument document, string name, IEnumerable<Inline> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var replacement = content.ToList();
        var isRange = BookmarkScan.Collect(document).RangeNames.Contains(name);
        return Edit(document, name, blocks => EditList(blocks, name, (block, inlines) =>
        {
            var start = IndexOfStart(inlines, name);
            if (start < 0)
            {
                return null;
            }

            var end = inlines.ToList().FindIndex(start + 1, inline => inline is BookmarkEnd close && close.Name == name);
            if (end < 0 && isRange)
            {
                throw new InvalidOperationException($"The bookmark '{name}' ends in another paragraph or in a hyperlink; its content cannot be replaced.");
            }

            var result = inlines.Take(start + 1).Concat(replacement);
            return (end < 0 ? result.Concat(inlines.Skip(start + 1)) : result.Concat(inlines.Skip(end))).ToList();
        }));
    }

    /// <summary>Inserts <paramref name="blocks"/> right after the block that holds the bookmark.</summary>
    /// <exception cref="ArgumentException">No block holds the bookmark.</exception>
    public static PortableDocument InsertBlocksAfter(PortableDocument document, string name, IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var inserted = blocks.ToList();
        return Edit(document, name, list =>
        {
            var index = list.ToList().FindIndex(block => Inlines(block) is { } inlines && IndexOfStart(inlines, name) >= 0);
            return index < 0 ? list : list.Take(index + 1).Concat(inserted).Concat(list.Skip(index + 1)).ToList();
        });
    }

    private static PortableDocument Edit(PortableDocument document, string name, Func<IReadOnlyList<Block>, IReadOnlyList<Block>> edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var changed = false;
        var sections = new List<Section>(document.Sections.Count);
        foreach (var section in document.Sections)
        {
            var blocks = MapLists(section.Blocks, edit);
            changed |= !ReferenceEquals(blocks, section.Blocks);
            sections.Add(ReferenceEquals(blocks, section.Blocks) ? section : section with { Blocks = blocks });
        }

        return changed ? document with { Sections = sections } : throw new ArgumentException($"No block holds the bookmark '{name}'.", nameof(name));
    }

    /// <summary>The paragraphs of a list rewritten by <paramref name="rewrite"/> (null: not this one).</summary>
    private static IReadOnlyList<Block> EditList(
        IReadOnlyList<Block> blocks,
        string name,
        Func<Block, IReadOnlyList<Inline>, IReadOnlyList<Inline>?> rewrite)
    {
        var result = new List<Block>(blocks.Count);
        var changed = false;
        foreach (var block in blocks)
        {
            var inlines = Inlines(block);
            var rewritten = inlines is null ? null : rewrite(block, inlines);
            if (rewritten is null)
            {
                result.Add(block);
                continue;
            }

            changed = true;
            result.Add(block switch
            {
                Paragraph paragraph => new Paragraph(rewritten) { Style = paragraph.Style, SourceStyleBase = paragraph.SourceStyleBase },
                Heading heading => new Heading(heading.Level, rewritten) { Style = heading.Style },
                _ => block,
            });
        }

        return changed ? result : blocks;
    }

    private static IReadOnlyList<Inline>? Inlines(Block block) => block switch
    {
        Paragraph paragraph => paragraph.Inlines,
        Heading heading => heading.Inlines,
        _ => null,
    };

    private static int IndexOfStart(IReadOnlyList<Inline> inlines, string name)
    {
        for (var index = 0; index < inlines.Count; index++)
        {
            if (inlines[index] is Bookmark bookmark && bookmark.Name == name)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Applies <paramref name="edit"/> to this list and to the lists inside list items and table cells.</summary>
    private static IReadOnlyList<Block> MapLists(IReadOnlyList<Block> blocks, Func<IReadOnlyList<Block>, IReadOnlyList<Block>> edit)
    {
        var inner = new List<Block>(blocks.Count);
        var changed = false;
        foreach (var block in blocks)
        {
            var mapped = MapInside(block, edit);
            changed |= !ReferenceEquals(mapped, block);
            inner.Add(mapped);
        }

        var current = changed ? inner : blocks;
        return edit(current);
    }

    private static Block MapInside(Block block, Func<IReadOnlyList<Block>, IReadOnlyList<Block>> edit)
    {
        switch (block)
        {
            case ListBlock list:
            {
                var changed = false;
                var items = new List<ListItem>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    var blocks = MapLists(item.Blocks, edit);
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
                        var blocks = MapLists(cell.Blocks, edit);
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
                    rows.Add(rowChanged ? new TableRow(cells) { IsHeader = row.IsHeader, Height = row.Height, HeightRule = row.HeightRule } : row);
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
}
