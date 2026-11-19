namespace Reglet;

/// <summary>
/// plans page and section breaks for ODT. A break is represented as
/// <c>fo:break-before</c> (for sections additionally <c>style:master-page-name</c>) on the
/// next block. An own empty paragraph would otherwise stand as a blank line at the top of
/// the new page. Only if no suitable block follows (end, another break), a
/// the empty paragraph; then Word also produces an empty line or page.
/// </summary>
internal sealed class OdtBreakPlan
{
    private readonly Dictionary<Block, OdtBreak> _targets = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Block> _absorbed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _mergedSections = [];
    private readonly HashSet<int> _pageNumberStarts = [];
    private readonly Dictionary<Block, int> _occurrences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TableBlock, string> _tableStyles = new(ReferenceEqualityComparer.Instance);
    private readonly int _tableBreakStart;

    private OdtBreakPlan(int tableBreakStart = 0)
    {
        _tableBreakStart = tableBreakStart;
    }

    /// <summary>A plan without breaks (headers/footers).</summary>
    public static OdtBreakPlan Empty { get; } = new();

    public static OdtBreakPlan Create(PortableDocument document, int tableBreakStart = 0)
    {
        var plan = new OdtBreakPlan(tableBreakStart);
        foreach (var section in document.Sections)
        {
            plan.Count(section.Blocks);
        }

        for (var index = 0; index < document.Sections.Count; index++)
        {
            var blocks = document.Sections[index].Blocks;
            var number = index + 1;

            // The first section has no master-page switch. A page-number start still needs a
            // paragraph or table style that names the initial master page; the style then only
            // carries the start number and no page break.
            if (index == 0)
            {
                if (document.Sections[0].PageNumberStart is { } start
                    && blocks.Count > 0
                    && plan.TryTarget(blocks[0], new OdtBreak(
                        OdtSectionNames.MasterPage(1), start, PageNumberOnly: true)))
                {
                    plan._pageNumberStarts.Add(number);
                }

                plan.Walk(blocks);
                continue;
            }

            // A continuous section continues on the same page: no section break with a master
            // page is planned.
            if (document.Sections[index].StartType == SectionStartType.Continuous)
            {
                plan.Walk(blocks);
                continue;
            }

            if (blocks.Count > 0
                && plan.TryTarget(blocks[0], new OdtBreak(
                    OdtSectionNames.MasterPage(number), document.Sections[index].PageNumberStart)))
            {
                plan._mergedSections.Add(number);
            }

            plan.Walk(blocks);
        }

        return plan;
    }

    /// <summary>The break before this block, or null.</summary>
    public OdtBreak? BreakBefore(Block block) => _targets.TryGetValue(block, out var value) ? value : null;

    /// <summary>Whether this page break is taken over by the following block.</summary>
    public bool IsAbsorbed(PageBreak pageBreak) => _absorbed.Contains(pageBreak);

    /// <summary>Whether the section break (1-based) is taken over by the first block.</summary>
    public bool IsSectionMerged(int number) => _mergedSections.Contains(number);

    /// <summary>Whether the start page number of the section (1-based) is attached to its first block.</summary>
    public bool IsPageNumberStartApplied(int number) => _pageNumberStarts.Contains(number);

    /// <summary>Name of the table style that carries the break before a table, or null.</summary>
    public string? TableStyleName(TableBlock table) => _tableStyles.GetValueOrDefault(table);

    private void Walk(IReadOnlyList<Block> blocks)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            switch (blocks[index])
            {
                case PageBreak pageBreak when index + 1 < blocks.Count
                    && TryTarget(blocks[index + 1], new OdtBreak(null)):
                    _absorbed.Add(pageBreak);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Walk(item.Blocks);
                    }

                    break;
                case TableBlock table:
                    foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        Walk(cell.Blocks);
                    }

                    break;
            }
        }
    }

    private void Count(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            _occurrences[block] = _occurrences.GetValueOrDefault(block) + 1;
            switch (block)
            {
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Count(item.Blocks);
                    }

                    break;
                case TableBlock table:
                    foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        Count(cell.Blocks);
                    }

                    break;
            }
        }
    }

    private bool TryTarget(Block block, OdtBreak value)
    {
        Block? target = block switch
        {
            Paragraph or Heading or ImageBlock or TableBlock => block,
            ListBlock { Items.Count: > 0 } list when list.Items[0].Blocks is [Paragraph first, ..] => first,
            _ => null,
        };

        // If the same block occurs multiple times or already carries a break, it stays
        // for the empty paragraph so that the break does not land in the wrong place.
        if (target is null || _occurrences.GetValueOrDefault(target) != 1 || !_targets.TryAdd(target, value))
        {
            return false;
        }

        if (target is TableBlock table)
        {
            _tableStyles[table] = "TB" + (_tableBreakStart + _tableStyles.Count + 1);
        }

        return true;
    }
}

/// <summary>A break before a block; with <see cref="MasterPage"/> also a section break.</summary>
internal readonly record struct OdtBreak(string? MasterPage, int? PageNumberStart = null, bool PageNumberOnly = false);
