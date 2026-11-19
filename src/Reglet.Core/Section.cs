namespace Reglet;

/// <summary>A document section with page settings and blocks.</summary>
public sealed record Section
{
    private static readonly PageSize DefaultPageSize = PageSize.A4;
    private PageSize _pageSize = DefaultPageSize;
    private PageMargins _margins = PageMargins.Normal;
    private PageOrientation _orientation = PageOrientation.Portrait;
    private int? _textColumns;
    private int? _pageNumberStart;
    private SectionStartType _startType;
    private IReadOnlyList<Block> _blocks = [];

    /// <summary>Creates a section with exactly one block.</summary>
    public Section(Block block)
        : this(new[] { block })
    {
    }

    /// <summary>Creates a section with several blocks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public Section(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        SetBlocks(blocks);
    }

    /// <summary>Paper size.</summary>
    public PageSize PageSize
    {
        get => _pageSize;
        init => _pageSize = value ?? throw new ArgumentNullException(nameof(PageSize));
    }

    /// <summary>Orientation (portrait or landscape).</summary>
    public PageOrientation Orientation
    {
        get => _orientation;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(Orientation), value, "Unknown page orientation.");
            }

            _orientation = value;
        }
    }

    /// <summary>Page margins.</summary>
    public PageMargins Margins
    {
        get => _margins;
        init => _margins = value ?? throw new ArgumentNullException(nameof(Margins));
    }

    /// <summary>Optional header.</summary>
    public HeaderFooter? Header { get; init; }

    /// <summary>Optional footer.</summary>
    public HeaderFooter? Footer { get; init; }

    /// <summary>Optional header of the first page.</summary>
    public HeaderFooter? FirstHeader { get; init; }

    /// <summary>Optional footer of the first page.</summary>
    public HeaderFooter? FirstFooter { get; init; }

    /// <summary>Optional header of even pages.</summary>
    public HeaderFooter? EvenHeader { get; init; }

    /// <summary>Optional footer of even pages.</summary>
    public HeaderFooter? EvenFooter { get; init; }

    /// <summary>First page number of the section; null means continuous.</summary>
    public int? PageNumberStart
    {
        get => _pageNumberStart;
        init
        {
            if (value is { } start && start < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(PageNumberStart), start, "Starting page number must not be negative.");
            }

            _pageNumberStart = value;
        }
    }

    /// <summary>
    /// the line numbering of the section; null means no line numbers (phase D).
    /// </summary>
    public LineNumbering? LineNumbering { get; init; }

    /// <summary>
    /// How the section starts (the break before it); <see cref="SectionStartType.NextPage"/> is the
    /// default.
    /// </summary>
    public SectionStartType StartType
    {
        get => _startType;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(StartType), value, "Unknown section start type.");
            }

            _startType = value;
        }
    }

    /// <summary>Number of text columns (1 = single-column); null means single-column.</summary>
    public int? TextColumns
    {
        get => _textColumns;
        init
        {
            if (value is { } columns && columns is < 1 or > 12)
            {
                throw new ArgumentOutOfRangeException(nameof(TextColumns), columns, "Column count must be between 1 and 12.");
            }

            _textColumns = value;
        }
    }

    /// <summary>The blocks of the section. Settable via <c>with</c>.</summary>
    public IReadOnlyList<Block> Blocks
    {
        get => _blocks;
        init => SetBlocks(value);
    }

    /// <summary>
    /// the border around the text area of every page of the section (phase AJ, AJ05); null
    /// means no page border. DOCX writes <c>w:pgBorders</c>, the PDF draws the border, ODT
    /// has no portable page border and reports it.
    /// </summary>
    public PageBorders? PageBorders { get; init; }

    private void SetBlocks(IEnumerable<Block> blocks)
        => _blocks = ModelCollections.Snapshot(blocks, nameof(Blocks));
}
