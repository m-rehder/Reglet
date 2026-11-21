namespace Reglet;

/// <summary>
/// calculates the general fields of a document and returns a new document with the results
/// (phase AJ, AJ02). The model is immutable, so the input document stays unchanged.
/// A field whose value cannot be calculated keeps its cached result.
/// </summary>
public static class FieldResolver
{
    /// <summary>
    /// calculates the fields of the document (body, headers and footers).
    /// </summary>
    /// <param name="document">The document whose fields are calculated.</param>
    /// <param name="context">The values of the calculation.</param>
    /// <exception cref="ArgumentNullException">Document or context is null.</exception>
    public static PortableDocument Resolve(PortableDocument document, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        var sections = new List<Section>(document.Sections.Count);
        var changed = false;
        // A source package may contain unmodelled band XML. Resolving band fields would
        // regenerate that XML, so bands are left alone for read documents.
        var includeBands = document.SourcePackage is null;
        foreach (var section in document.Sections)
        {
            var blocks = ResolveBlocks(section.Blocks, context);
            var header = includeBands ? ResolveBand(section.Header, context) : section.Header;
            var footer = includeBands ? ResolveBand(section.Footer, context) : section.Footer;
            var firstHeader = includeBands ? ResolveBand(section.FirstHeader, context) : section.FirstHeader;
            var firstFooter = includeBands ? ResolveBand(section.FirstFooter, context) : section.FirstFooter;
            var evenHeader = includeBands ? ResolveBand(section.EvenHeader, context) : section.EvenHeader;
            var evenFooter = includeBands ? ResolveBand(section.EvenFooter, context) : section.EvenFooter;

            var replaced = ReferenceEquals(blocks, section.Blocks)
                && ReferenceEquals(header, section.Header)
                && ReferenceEquals(footer, section.Footer)
                && ReferenceEquals(firstHeader, section.FirstHeader)
                && ReferenceEquals(firstFooter, section.FirstFooter)
                && ReferenceEquals(evenHeader, section.EvenHeader)
                && ReferenceEquals(evenFooter, section.EvenFooter)
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
                    };
            changed |= !ReferenceEquals(replaced, section);
            sections.Add(replaced);
        }

        return changed ? document with { Sections = sections } : document;
    }

    /// <summary>calculates the fields of a header or a footer.</summary>
    private static HeaderFooter? ResolveBand(HeaderFooter? band, FieldContext context)
    {
        if (band is null)
        {
            return null;
        }

        var blocks = ResolveBlocks(band.Blocks, context);
        return ReferenceEquals(blocks, band.Blocks) ? band : new HeaderFooter(blocks);
    }

    /// <summary>calculates the fields of a block list.</summary>
    private static IReadOnlyList<Block> ResolveBlocks(IReadOnlyList<Block> blocks, FieldContext context)
    {
        var result = new List<Block>(blocks.Count);
        var changed = false;
        foreach (var block in blocks)
        {
            var replaced = ResolveBlock(block, context);
            changed |= !ReferenceEquals(replaced, block);
            result.Add(replaced);
        }

        return changed ? result : blocks;
    }

    /// <summary>calculates the fields of a block.</summary>
    private static Block ResolveBlock(Block block, FieldContext context) => block switch
    {
        Paragraph paragraph => ResolveParagraph(paragraph, context),
        Heading heading => ResolveHeading(heading, context),
        ListBlock list => ResolveList(list, context),
        TableBlock table => ResolveTable(table, context),
        _ => block,
    };

    private static Paragraph ResolveParagraph(Paragraph paragraph, FieldContext context)
    {
        var inlines = ResolveInlines(paragraph.Inlines, context);
        return ReferenceEquals(inlines, paragraph.Inlines)
            ? paragraph
            : new Paragraph(inlines) { Style = paragraph.Style, SourceStyleBase = paragraph.SourceStyleBase };
    }

    private static Heading ResolveHeading(Heading heading, FieldContext context)
    {
        var inlines = ResolveInlines(heading.Inlines, context);
        return ReferenceEquals(inlines, heading.Inlines)
            ? heading
            : new Heading(heading.Level, inlines) { Style = heading.Style };
    }

    private static ListBlock ResolveList(ListBlock list, FieldContext context)
    {
        var items = new List<ListItem>(list.Items.Count);
        var changed = false;
        foreach (var item in list.Items)
        {
            var blocks = ResolveBlocks(item.Blocks, context);
            var replaced = ReferenceEquals(blocks, item.Blocks) ? item : new ListItem(blocks);
            changed |= !ReferenceEquals(replaced, item);
            items.Add(replaced);
        }

        return changed
            ? new ListBlock(list.NumberingName, items) { NestingLevel = list.NestingLevel }
            : list;
    }

    private static TableBlock ResolveTable(TableBlock table, FieldContext context)
    {
        var rows = new List<TableRow>(table.Rows.Count);
        var changed = false;
        foreach (var row in table.Rows)
        {
            var cells = new List<TableCell>(row.Cells.Count);
            var rowChanged = false;
            foreach (var cell in row.Cells)
            {
                var blocks = ResolveBlocks(cell.Blocks, context);
                var replaced = ReferenceEquals(blocks, cell.Blocks)
                    ? cell
                    : cell.WithBlocks(blocks);
                rowChanged |= !ReferenceEquals(replaced, cell);
                cells.Add(replaced);
            }

            var replacedRow = rowChanged
                ? row.WithCells(cells)
                : row;
            changed |= !ReferenceEquals(replacedRow, row);
            rows.Add(replacedRow);
        }

        return changed
            ? table.WithRows(rows)
            : table;
    }

    /// <summary>calculates the fields of an inline list.</summary>
    private static IReadOnlyList<Inline> ResolveInlines(IReadOnlyList<Inline> inlines, FieldContext context)
    {
        var result = new List<Inline>(inlines.Count);
        var changed = false;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Field field:
                {
                    var resolved = ResolveField(field, context);
                    changed |= !ReferenceEquals(resolved, field);
                    result.Add(resolved);
                    break;
                }

                case Hyperlink hyperlink:
                {
                    var inner = ResolveInlines(hyperlink.Inlines, context);
                    changed |= !ReferenceEquals(inner, hyperlink.Inlines);
                    result.Add(ReferenceEquals(inner, hyperlink.Inlines)
                        ? hyperlink
                        : new Hyperlink(hyperlink.Target, inner));
                    break;
                }

                default:
                    result.Add(inline);
                    break;
            }
        }

        return changed ? result : inlines;
    }

    /// <summary>
    /// calculates a field. A field with a cached result keeps it; a field without a result gets
    /// the calculated value, if the calculation succeeds.
    /// </summary>
    private static Field ResolveField(Field field, FieldContext context)
    {
        if (field.Result is not null)
        {
            return field;
        }

        var value = FieldEvaluator.Evaluate(field, context);
        return value is null ? field : new Field(field.Instruction, value);
    }
}
