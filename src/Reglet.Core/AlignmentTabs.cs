namespace Reglet;

/// <summary>
/// turns the <see cref="AlignmentTab"/> of a document into normal tabs with matching tab stops, for
/// the formats that have no alignment tab (ODT, PDF). Paragraphs in the body, headers, footers and
/// lists are changed; the tabs in table cells and text boxes become plain tabs because their width is
/// not known here. The input document stays unchanged; without alignment tabs it is returned itself.
/// </summary>
internal static class AlignmentTabs
{
    public static PortableDocument Expand(PortableDocument document)
    {
        var sections = new List<Section>(document.Sections.Count);
        var changed = false;
        foreach (var section in document.Sections)
        {
            var width = Math.Max(1, section.PageSize.Width.Points - section.Margins.Left.Points - section.Margins.Right.Points);
            if (section.Orientation == PageOrientation.Landscape && section.PageSize.Width.Points < section.PageSize.Height.Points)
            {
                width = Math.Max(1, section.PageSize.Height.Points - section.Margins.Left.Points - section.Margins.Right.Points);
            }

            var blocks = ExpandBlocks(section.Blocks, width);
            var header = ExpandBand(section.Header, width);
            var footer = ExpandBand(section.Footer, width);
            var firstHeader = ExpandBand(section.FirstHeader, width);
            var firstFooter = ExpandBand(section.FirstFooter, width);
            var evenHeader = ExpandBand(section.EvenHeader, width);
            var evenFooter = ExpandBand(section.EvenFooter, width);
            var same = ReferenceEquals(blocks, section.Blocks) && ReferenceEquals(header, section.Header)
                && ReferenceEquals(footer, section.Footer) && ReferenceEquals(firstHeader, section.FirstHeader)
                && ReferenceEquals(firstFooter, section.FirstFooter) && ReferenceEquals(evenHeader, section.EvenHeader)
                && ReferenceEquals(evenFooter, section.EvenFooter);
            changed |= !same;
            sections.Add(same
                ? section
                : section with
                {
                    Blocks = blocks,
                    Header = header,
                    Footer = footer,
                    FirstHeader = firstHeader,
                    FirstFooter = firstFooter,
                    EvenHeader = evenHeader,
                    EvenFooter = evenFooter,
                });
        }

        return changed ? document with { Sections = sections } : document;
    }

    private static HeaderFooter? ExpandBand(HeaderFooter? band, double width)
    {
        if (band is null)
        {
            return null;
        }

        var blocks = ExpandBlocks(band.Blocks, width);
        return ReferenceEquals(blocks, band.Blocks) ? band : new HeaderFooter(blocks);
    }

    private static IReadOnlyList<Block> ExpandBlocks(IReadOnlyList<Block> blocks, double width)
    {
        List<Block>? result = null;
        for (var index = 0; index < blocks.Count; index++)
        {
            var replaced = ExpandBlock(blocks[index], width);
            if (!ReferenceEquals(replaced, blocks[index]) && result is null)
            {
                result = new List<Block>(blocks.Take(index));
            }

            result?.Add(replaced);
        }

        return (IReadOnlyList<Block>?)result ?? blocks;
    }

    private static Block ExpandBlock(Block block, double width)
    {
        switch (block)
        {
            case Paragraph paragraph when HasTab(paragraph.Inlines):
            {
                var (inlines, style) = ExpandInlines(paragraph.Inlines, paragraph.Style, width);
                return new Paragraph(inlines) { Style = style, SourceStyleBase = paragraph.SourceStyleBase };
            }

            case Heading heading when HasTab(heading.Inlines):
            {
                var (inlines, style) = ExpandInlines(heading.Inlines, heading.Style, width);
                return new Heading(heading.Level, inlines) { Style = style };
            }

            case ListBlock list:
            {
                var items = new List<ListItem>(list.Items.Count);
                var changed = false;
                foreach (var item in list.Items)
                {
                    // The text of a list item starts at its own indent; the margin-relative stops stay valid.
                    var blocks = ExpandBlocks(item.Blocks, width);
                    changed |= !ReferenceEquals(blocks, item.Blocks);
                    items.Add(ReferenceEquals(blocks, item.Blocks) ? item : new ListItem(blocks));
                }

                return changed ? new ListBlock(list.NumberingName, items) { NestingLevel = list.NestingLevel } : list;
            }

            default:
                return block;
        }
    }

    private static bool HasTab(IReadOnlyList<Inline> inlines)
        => inlines.Any(inline => inline is AlignmentTab || inline is Hyperlink { } link && HasTab(link.Inlines));

    private static (List<Inline> Inlines, ParagraphStyle Style) ExpandInlines(IReadOnlyList<Inline> inlines, ParagraphStyle style, double width)
    {
        var stops = style.TabStops?.ToList() ?? [];
        var left = style.LeftIndent?.Points ?? 0;
        var right = style.RightIndent?.Points ?? 0;
        var added = false;

        List<Inline> Walk(IReadOnlyList<Inline> source)
        {
            var result = new List<Inline>(source.Count);
            foreach (var inline in source)
            {
                switch (inline)
                {
                    case AlignmentTab tab:
                    {
                        var (start, end) = tab.RelativeTo == AlignmentTabRelativeTo.Indent ? (left, width - right) : (0, width);
                        var position = tab.Alignment switch
                        {
                            TabStopAlignment.Right => end,
                            TabStopAlignment.Center => start + ((end - start) / 2),
                            _ => start,
                        };
                        var stop = new TabStop(Length.FromPoints(Math.Max(0, position)), tab.Alignment, tab.Leader);
                        if (!stops.Contains(stop))
                        {
                            stops.Add(stop);
                            added = true;
                        }

                        result.Add(new TextRun("\t"));
                        break;
                    }

                    case Hyperlink link when HasTab(link.Inlines):
                        result.Add(new Hyperlink(link.Target, Walk(link.Inlines)));
                        break;

                    default:
                        result.Add(inline);
                        break;
                }
            }

            return result;
        }

        var walked = Walk(inlines);
        return (walked, added ? style with { TabStops = stops } : style);
    }
}
