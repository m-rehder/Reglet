namespace Reglet;

/// <summary>Style attributes of a paragraph.</summary>
public sealed record ParagraphStyle
{
    private TextAlignment? _alignment;
    private Length? _spaceBefore;
    private Length? _spaceAfter;
    private IReadOnlyList<TabStop>? _tabStops;
    private Length? _markFontSize;

    /// <summary>Default style without special attributes.</summary>
    public static ParagraphStyle Default { get; } = new();

    /// <summary>Optional name of the paragraph style (e.g. <c>Heading1</c>).</summary>
    public string? StyleName { get; init; }

    /// <summary>Optional name of the linked character style (DOCX <c>w:link</c>); null means none.</summary>
    public string? LinkedStyleName { get; init; }

    /// <summary>Horizontal alignment; <c>null</c> means "not set" and inherits.</summary>
    public TextAlignment? Alignment
    {
        get => _alignment;
        init
        {
            if (value is { } alignment && !Enum.IsDefined(alignment))
            {
                throw new ArgumentOutOfRangeException(nameof(Alignment), value, "Unknown text alignment.");
            }

            _alignment = value;
        }
    }

    /// <summary>Space before the paragraph; <c>null</c> means "not set" and inherits.</summary>
    public Length? SpaceBefore
    {
        get => _spaceBefore;
        init => _spaceBefore = value is not { } spaceBefore || spaceBefore.Points >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SpaceBefore), "Paragraph spacing must not be negative.");
    }

    /// <summary>
    /// Space after the paragraph; <c>null</c> means "not set" and inherits. The built-in default is
    /// <see cref="Typography.ParagraphSpaceAfter"/> for normal paragraphs and 0 in lists, table
    /// cells and headers/footers; <see cref="Length.Zero"/> is an explicit zero.
    /// </summary>
    public Length? SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = value is not { } spaceAfter || spaceAfter.Points >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SpaceAfter), "Paragraph spacing must not be negative.");
    }

    /// <summary>Left indent; <c>null</c> means "not set" and inherits.</summary>
    public Length? LeftIndent { get; init; }

    /// <summary>Right indent; <c>null</c> means "not set" and inherits.</summary>
    public Length? RightIndent { get; init; }

    /// <summary>First-line indent (negative = hanging indent); <c>null</c> means "not set" and inherits.</summary>
    public Length? FirstLineIndent { get; init; }

    /// <summary>Line spacing (multiple or fixed height); null means the font's default.</summary>
    public LineSpacing? LineSpacing { get; init; }

    /// <summary>
    /// Paragraph borders; <c>null</c> means "not set" and inherits, an empty
    /// <see cref="ParagraphBorders"/> (<see cref="ParagraphBorders.None"/>) means no borders.
    /// </summary>
    public ParagraphBorders? Borders { get; init; }

    /// <summary>Background color of the paragraph (shading); null means none.</summary>
    public Color? Background { get; init; }

    /// <summary>Keep a heading/paragraph together with the first line of the following block; <c>null</c> means "not set".</summary>
    public bool? KeepWithNext { get; init; }

    /// <summary>Keep the whole paragraph on one page (do not split across pages); <c>null</c> means "not set".</summary>
    public bool? KeepTogether { get; init; }

    /// <summary>
    /// Widow and orphan control: a paragraph split across pages keeps at least two lines on each, in the text and in a
    /// table row (the row moves whole when its paragraph may not split there). <c>null</c> means on, as in Word, where a
    /// missing <c>w:widowControl</c> is on; <c>false</c> splits a paragraph exactly where the room ends. DOCX writes it as
    /// <c>w:widowControl</c>, ODT as <c>fo:widows</c>/<c>fo:orphans</c>.
    /// </summary>
    public bool? WidowControl { get; init; }

    /// <summary>Start a new page before the paragraph; <c>null</c> means "not set".</summary>
    public bool? PageBreakBefore { get; init; }

    /// <summary>
    /// Tab stops; <c>null</c> means "not set" and inherits, an empty list means no tab stops.
    /// </summary>
    public IReadOnlyList<TabStop>? TabStops
    {
        get => _tabStops;
        init => _tabStops = value is null ? null : ModelCollections.Snapshot(value, nameof(TabStops));
    }

    /// <summary>
    /// Paragraph direction from right to left (base direction); <c>null</c> means "not set" and
    /// inherits, <c>false</c> is an explicit left-to-right.
    /// </summary>
    public bool? RightToLeft { get; init; }

    // Run formatting of the style (ADR-0010): what a run in this paragraph inherits. Every value is
    // tri-state - null means the style does not set it, false/None/Normal switch an inherited
    // property off. A character style and the formatting of the run itself take precedence.

    /// <summary>Bold for the runs of the paragraph; <c>null</c> means "not set".</summary>
    public bool? Bold { get; init; }

    /// <summary>Italic for the runs of the paragraph; <c>null</c> means "not set".</summary>
    public bool? Italic { get; init; }

    /// <summary>Single underline; <c>null</c> means "not set".</summary>
    public bool? Underline { get; init; }

    /// <summary>Underline style; <c>null</c> means "not set", None switches one off.</summary>
    public TextUnderlineStyle? UnderlineStyle { get; init; }

    /// <summary>Underline color; null means the text color.</summary>
    public Color? UnderlineColor { get; init; }

    /// <summary>Strikethrough; <c>null</c> means "not set", None switches one off.</summary>
    public TextStrikethrough? Strikethrough { get; init; }

    /// <summary>Vertical position; <c>null</c> means "not set", Normal switches one off.</summary>
    public TextBaseline? Baseline { get; init; }

    /// <summary>All capitals; <c>null</c> means "not set".</summary>
    public bool? AllCaps { get; init; }

    /// <summary>Small capitals; <c>null</c> means "not set".</summary>
    public bool? SmallCaps { get; init; }

    /// <summary>Text effects (outline, shadow, relief); <c>null</c> means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.</summary>
    public TextEffects? Effects { get; init; }

    /// <summary>Hidden text; <c>null</c> means "not set".</summary>
    public bool? Hidden { get; init; }

    /// <summary>
    /// text color of the runs; <c>null</c> means the document default. (<see cref="Background"/> is
    /// the shading of the paragraph box, not of the text.)
    /// </summary>
    public Color? TextColor { get; init; }

    /// <summary>Background of the text (not the paragraph shading); null means none.</summary>
    public Color? TextBackground { get; init; }

    /// <summary>Highlighting of the text; null means none.</summary>
    public Color? Highlight { get; init; }

    /// <summary>Font family of the runs; null means the document default.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font size of the runs; null means the document default.</summary>
    public Length? FontSize { get; init; }

    /// <summary>
    /// Font family of the paragraph mark (Word's <c>w:pPr/w:rPr</c>); null means <see cref="FontFamily"/>, else the document
    /// default. The mark sets the height of a paragraph without visible text; as in Word, it does not raise a line with text. DOCX and PDF
    /// honour it; ODT has no paragraph mark.
    /// </summary>
    public string? MarkFontFamily { get; init; }

    /// <summary>Font size of the paragraph mark; null means <see cref="FontSize"/>, else the document default. See <see cref="MarkFontFamily"/>.</summary>
    public Length? MarkFontSize
    {
        get => _markFontSize;
        init => _markFontSize = value is { Points: <= 0 } || (value is { } size && !double.IsFinite(size.Points))
            ? throw new ArgumentOutOfRangeException(nameof(MarkFontSize), "The mark font size must be positive.")
            : value;
    }

    /// <summary>Border around the text (<see cref="Borders"/> is the box of the paragraph).</summary>
    public ParagraphBorder? TextBorder { get; init; }

    /// <summary>Minimum font size for the kerning; null means no specification.</summary>
    public Length? Kerning { get; init; }

    /// <summary>Additional character spacing; null means the default.</summary>
    public Length? CharacterSpacing { get; init; }

    /// <summary>Horizontal scaling in percent (100 = unchanged); null means the default.</summary>
    public double? HorizontalScaling { get; init; }

    /// <summary>Baseline shift of the runs (positive = up); null means the default.</summary>
    public Length? Position { get; init; }
}
