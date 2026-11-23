namespace Reglet;

/// <summary>Key of an automatic paragraph style: style, compact spacing and break before.</summary>
internal readonly record struct OdtParagraphKey(
    ParagraphStyle Style,
    bool Compact,
    OdtBreak? Break = null,
    bool ReferenceOnly = false,
    bool ForceStyle = false);

/// <summary>
/// automatic ODT paragraph styles. The same is used when collecting and when writing
/// <see cref="ParagraphSpacing"/> state is carried along so that both passes use the same
/// recognize compact paragraphs (lists, table cells).
/// </summary>
internal sealed class OdtParagraphStyles
{
    private readonly Dictionary<OdtParagraphKey, string> _names;

    private OdtParagraphStyles(
        Dictionary<OdtParagraphKey, string> names,
        OdtBreakPlan breaks,
        OdtTableCellStyles cellStyles,
        IReadOnlyList<(int Level, string Text)> headings,
        double defaultFontSize)
    {
        _names = names;
        Breaks = breaks;
        CellStyles = cellStyles;
        Headings = headings;
        DefaultFontSize = defaultFontSize;
    }

    /// <summary>
    /// The document font size used when a style states a baseline shift but no font size of its own.
    /// ODF needs a percentage, so the shift cannot be written without a size.
    /// </summary>
    public double DefaultFontSize { get; }

    /// <summary>Planned page and section breaks.</summary>
    public OdtBreakPlan Breaks { get; }

    /// <summary>Automatic cell, row and table styles (table features Y01–Y04).</summary>
    public OdtTableCellStyles CellStyles { get; }

    /// <summary>The headings of the document (for a table of contents).</summary>
    public IReadOnlyList<(int Level, string Text)> Headings { get; }

    /// <summary>The block groups of the document, for the captions and index entries of a list block.</summary>
    public IReadOnlyList<IEnumerable<Block>> Groups { get; init; } = [];

    /// <summary>The named paragraph styles of the document (ADR-0011).</summary>
    public IReadOnlyList<ParagraphStyle> Styles { get; set; } = [];

    /// <summary>The named paragraph styles as they were read (ADR-0011).</summary>
    public IReadOnlyList<ParagraphStyle> SourceStyles { get; set; } = [];

    /// <summary>State for the write pass.</summary>
    public ParagraphSpacing Spacing { get; } = new();

    /// <summary>
    /// in headers/footers, page breaks cannot be represented; they are reported in advance
    /// and skipped when writing.
    /// </summary>
    public bool SuppressPageBreaks { get; private init; }

    /// <summary>
    /// Whether the styles belong to headers and footers. A band carries no note: LibreOffice drops
    /// a <c>text:note</c> there, so its text is written instead (see <c>NoteScan.Text</c>).
    /// </summary>
    public bool InBand { get; private init; }

    /// <summary>
    /// the named character styles of the document (ADR-0010); the writer resolves the style a run
    /// references when it writes the run.
    /// </summary>
    public IReadOnlyList<CharacterStyle> CharacterStyles { get; set; } = [];

    /// <summary>
    /// the names of the bookmarks that have an explicit end (a range, OF-C03); a bookmark of the
    /// other kind is a zero-width target and is written as <c>text:bookmark</c>.
    /// </summary>
    public IReadOnlySet<string> BookmarkRanges { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The comments of the body (ADR-0026) with their annotation names.</summary>
    public Dictionary<Comment, string> CommentNames { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The model ids of the comments that have an explicit <see cref="CommentEnd"/>; the others are points.</summary>
    public IReadOnlySet<int> CommentEnded { get; set; } = new HashSet<int>();

    /// <summary>The comments whose range is open: model id → comment.</summary>
    public Dictionary<int, Comment> OpenComments { get; } = [];

    /// <summary>The plan for the tracked changes of the body (ADR-0027); null in a band, where there are none.</summary>
    public OdtRevisions.Plan? Revisions { get; set; }

    /// <summary>The registered images, for the inline images a paragraph writes (InlineImage).</summary>
    public IReadOnlyDictionary<ImageBlock, OdtImageResource>? Images { get; set; }

    /// <summary>Returns the next footnote citation number.</summary>
    public int NextFootnoteIndex() => ++_footnoteIndex;

    /// <summary>Returns the next endnote citation number.</summary>
    public int NextEndnoteIndex() => ++_endnoteIndex;

    private int _footnoteIndex;
    private int _endnoteIndex;

    public IEnumerable<KeyValuePair<OdtParagraphKey, string>> Entries => _names;

    public bool TryGetName(ParagraphStyle style, bool compact, OdtBreak? pageBreak, out string name)
        => TryGetName(style, compact, pageBreak, referenceOnly: false, forceStyle: false, out name);

    public bool TryGetName(
        ParagraphStyle style,
        bool compact,
        OdtBreak? pageBreak,
        bool referenceOnly,
        bool forceStyle,
        out string name)
    {
        name = string.Empty;
        return !referenceOnly
            && _names.TryGetValue(new OdtParagraphKey(style, compact, pageBreak, ForceStyle: forceStyle), out name!);
    }

    /// <summary>
    /// The style a paragraph is written with and whether it only references its named style
    /// (ADR-0011). A reference-only paragraph keeps the named style; a direct formatting change
    /// gets an automatic style built from the resolved formatting.
    /// </summary>
    public static (ParagraphStyle Style, bool ReferenceOnly, bool ForceStyle) Resolve(
        Paragraph paragraph,
        IReadOnlyList<ParagraphStyle>? styles,
        IReadOnlyList<ParagraphStyle>? sourceStyles)
    {
        if (!ParagraphStyleResolver.TryResolve(
                paragraph.Style,
                paragraph.SourceStyleBase,
                styles,
                sourceStyles,
                out var effective,
                out var inheritedBase))
        {
            return (paragraph.Style, false, false);
        }

        var referenceOnly = !ParagraphStyleResolver.HasDirectFormatting(effective, inheritedBase!);
        return (effective, referenceOnly, !referenceOnly);
    }

    public static OdtParagraphStyles Collect(
        PortableDocument document,
        int nameStart = 0,
        int cellStart = 0,
        int rowStart = 0,
        int tableStart = 0,
        int tableBreakStart = 0)
        => Collect(
            document.Sections.Select(section => section.Blocks),
            "P",
            bands: false,
            OdtBreakPlan.Create(document, tableBreakStart),
            nameStart,
            cellStart,
            rowStart,
            tableStart,
            document.Defaults.FontSize.Points,
            document.Styles,
            document.SourceStyles);

    /// <summary>
    /// collects the paragraph styles of several block groups. Headers/footers (<paramref name="bands"/>)
    /// are compact and suppress page breaks.
    /// </summary>
    public static OdtParagraphStyles Collect(
        IEnumerable<IEnumerable<Block>> groups,
        string prefix,
        bool bands,
        OdtBreakPlan? breaks = null,
        int nameStart = 0,
        int cellStart = 0,
        int rowStart = 0,
        int tableStart = 0,
        double defaultFontSize = 11.0,
        IReadOnlyList<ParagraphStyle>? styles = null,
        IReadOnlyList<ParagraphStyle>? sourceStyles = null)
    {
        breaks ??= OdtBreakPlan.Empty;
        var names = new Dictionary<OdtParagraphKey, string>();
        var spacing = new ParagraphSpacing();
        foreach (var blocks in groups)
        {
            using var compact = bands ? spacing.EnterCompact() : default;
            foreach (var key in Enumerate(blocks, spacing, breaks, styles, sourceStyles))
            {
                if (!key.ReferenceOnly
                    && (key.ForceStyle || HasDirectFormatting(key))
                    && !names.ContainsKey(key))
                {
                    names[key] = prefix + (nameStart + names.Count + 1);
                }
            }
        }

        var cellStyles = OdtTableCellStyles.Collect(groups, breaks, cellStart, rowStart, tableStart);
        var headings = HeadingScan.Collect(groups, 6);
        return new OdtParagraphStyles(names, breaks, cellStyles, headings, defaultFontSize)
        {
            SuppressPageBreaks = bands,
            InBand = bands,
            Groups = [.. groups],
            Styles = styles ?? [],
            SourceStyles = sourceStyles ?? [],
        };
    }

    private static bool HasDirectFormatting(OdtParagraphKey key)
        => !key.ReferenceOnly
            && (key.Style.Alignment is not null
            || key.Style.SpaceBefore is not null
            || key.Style.SpaceAfter is not null
            || key.Style.LeftIndent is not null
            || key.Style.RightIndent is not null
            || key.Style.FirstLineIndent is not null
            || key.Style.LineSpacing is not null
            || key.Style.Borders is not null
            || key.Style.Background is not null
            || key.Style.KeepWithNext is not null
            || key.Style.KeepTogether is not null
            || key.Style.PageBreakBefore is not null
            || key.Style.TabStops is not null
            || key.Style.RightToLeft is not null
            || key.Compact
            || key.Break is not null);

    private static IEnumerable<OdtParagraphKey> Enumerate(
        IEnumerable<Block> blocks,
        ParagraphSpacing spacing,
        OdtBreakPlan breaks,
        IReadOnlyList<ParagraphStyle>? styles,
        IReadOnlyList<ParagraphStyle>? sourceStyles)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                {
                    var (style, referenceOnly, forceStyle) = Resolve(paragraph, styles, sourceStyles);
                    yield return new OdtParagraphKey(
                        style, spacing.IsCompact(paragraph), breaks.BreakBefore(paragraph), referenceOnly, forceStyle);
                    break;
                }

                case Heading heading:
                    yield return new OdtParagraphKey(heading.Style, false, breaks.BreakBefore(heading));
                    break;
                case ImageBlock image when breaks.BreakBefore(image) is { } imageBreak:
                    yield return new OdtParagraphKey(ParagraphStyle.Default, false, imageBreak);
                    break;
                case ListBlock list:
                    using (spacing.EnterList(list))
                    {
                        foreach (var item in list.Items)
                        {
                            foreach (var key in Enumerate(item.Blocks, spacing, breaks, styles, sourceStyles))
                            {
                                yield return key;
                            }
                        }
                    }

                    break;
                case DrawingShape { Content: { } content }:
                    using (spacing.EnterCompact())
                    {
                        foreach (var key in Enumerate(content, spacing, breaks, styles, sourceStyles))
                        {
                            yield return key;
                        }
                    }

                    break;
                case Diagram diagram:
                    using (spacing.EnterCompact())
                    {
                        foreach (var key in Enumerate(OdtDiagrams.Shapes(diagram).SelectMany(placed => placed.Shape.Content ?? []).ToList(), spacing, breaks, styles, sourceStyles))
                        {
                            yield return key;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            using (spacing.EnterCompact())
                            {
                                foreach (var cellBlock in cell.Blocks)
                                {
                                    switch (cellBlock)
                                    {
                                        // As in the writer: the cell alignment applies to direct paragraphs.
                                        case Paragraph paragraph:
                                        {
                                            var (style, referenceOnly, forceStyle) = Resolve(paragraph, styles, sourceStyles);
                                            var cellStyle = TableLayout.CellParagraphStyle(style, cell);
                                            yield return new OdtParagraphKey(
                                                cellStyle,
                                                true,
                                                breaks.BreakBefore(paragraph),
                                                referenceOnly && Equals(cellStyle, style),
                                                forceStyle || !Equals(cellStyle, style));
                                            break;
                                        }

                                        case Heading heading:
                                            yield return new OdtParagraphKey(
                                                TableLayout.CellParagraphStyle(heading.Style, cell), false, breaks.BreakBefore(heading));
                                            break;
                                        default:
                                            foreach (var key in Enumerate([cellBlock], spacing, breaks, styles, sourceStyles))
                                            {
                                                yield return key;
                                            }

                                            break;
                                    }
                                }
                            }
                        }
                    }

                    break;
            }
        }
    }
}
