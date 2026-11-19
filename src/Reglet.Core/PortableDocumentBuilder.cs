using System.Globalization;

namespace Reglet;

/// <summary>
/// fluent builder for a <see cref="PortableDocument"/>. It collects metadata,
/// page settings and blocks and produces in <see cref="Build"/> a validated,
/// immutable document.
/// </summary>
public sealed class PortableDocumentBuilder
{
    private readonly List<Block> _blocks = [];
    private DocumentMetadata _metadata = new();
    private DocumentDefaults _defaults = DocumentDefaults.Default;
    private PageSize _pageSize = PageSize.A4;
    private PageMargins _margins = PageMargins.Normal;
    private PageOrientation _orientation = PageOrientation.Portrait;
    private LineNumbering? _lineNumbering;
    private readonly List<NumberingDefinition> _numbering = [];
    private int _listCount;

    internal PortableDocumentBuilder()
    {
    }

    /// <summary>Sets document metadata; unspecified values remain.</summary>
    public PortableDocumentBuilder Metadata(
        string? title = null,
        string? author = null,
        string? subject = null,
        IEnumerable<string>? keywords = null,
        string? language = null,
        DateTimeOffset? created = null)
    {
        _metadata = new DocumentMetadata
        {
            Title = title ?? _metadata.Title,
            Author = author ?? _metadata.Author,
            Subject = subject ?? _metadata.Subject,
            Keywords = keywords is null ? _metadata.Keywords : keywords.ToArray(),
            Language = language ?? _metadata.Language,
            Created = created ?? _metadata.Created,
        };

        return this;
    }

    /// <summary>Sets the document-wide default values (default style).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is null.</exception>
    public PortableDocumentBuilder Defaults(DocumentDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
        return this;
    }

    /// <summary>Sets paper size, margins and orientation.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="size"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The alignment is unknown.</exception>
    public PortableDocumentBuilder Page(
        PageSize size,
        PageMargins? margins = null,
        PageOrientation orientation = PageOrientation.Portrait,
        LineNumbering? lineNumbering = null)
    {
        ArgumentNullException.ThrowIfNull(size);

        if (!Enum.IsDefined(orientation))
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unknown page orientation.");
        }

        var selectedMargins = margins ?? _margins;
        var pageWidth = orientation == PageOrientation.Landscape ? size.Height.Points : size.Width.Points;
        var pageHeight = orientation == PageOrientation.Landscape ? size.Width.Points : size.Height.Points;
        if (selectedMargins.Left.Points + selectedMargins.Right.Points >= pageWidth
            || selectedMargins.Top.Points + selectedMargins.Bottom.Points >= pageHeight)
        {
            throw new ArgumentException("The page margins leave no content area.", nameof(margins));
        }

        _pageSize = size;
        _margins = selectedMargins;
        _orientation = orientation;
        _lineNumbering = lineNumbering;
        return this;
    }

    /// <summary>
    /// adds numbering definitions that lists can reference by name. <see cref="BulletList"/> and
    /// <see cref="NumberedList"/> create one definition per call, so they restart their numbering;
    /// a definition added here lets several lists continue each other.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is null.</exception>
    public PortableDocumentBuilder Numbering(params NumberingDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _numbering.AddRange(definitions);
        return this;
    }

    /// <summary>Creates the validated, immutable document.</summary>
    public PortableDocument Build()
    {
        var section = new Section(_blocks)
        {
            PageSize = _pageSize,
            Orientation = _orientation,
            Margins = _margins,
            LineNumbering = _lineNumbering,
        };

        return new PortableDocument(_metadata, section) { Defaults = _defaults, Numbering = _numbering };
    }

    /// <summary>Adds a heading of level 1–6.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1–6.</exception>
    public PortableDocumentBuilder Heading(string text, int level = 1)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (level is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Heading level must be between 1 and 6.");
        }

        _blocks.Add(new Heading((HeadingLevel)level, new TextRun(text)));
        return this;
    }

    /// <summary>Adds a paragraph from plain text.</summary>
    public PortableDocumentBuilder Paragraph(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _blocks.Add(new Paragraph(new TextRun(text)));
        return this;
    }

    /// <summary>Adds a paragraph via the paragraph builder.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public PortableDocumentBuilder Paragraph(Action<ParagraphBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ParagraphBuilder();
        configure(builder);
        _blocks.Add(builder.Build());
        return this;
    }

    /// <summary>Adds a bullet list.</summary>
    public PortableDocumentBuilder BulletList(params string[] items) => AddList(ListKind.Bullet, items);

    /// <summary>Adds a numbered list.</summary>
    public PortableDocumentBuilder NumberedList(params string[] items) => AddList(ListKind.Number, items);

    /// <summary>Adds a table via the table builder.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public PortableDocumentBuilder Table(Action<TableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new TableBuilder();
        configure(builder);
        _blocks.Add(builder.Build());
        return this;
    }

    /// <summary>Adds an image.</summary>
    public PortableDocumentBuilder Image(
        ReadOnlyMemory<byte> data,
        ImageMediaType mediaType,
        string? altText = null,
        Length? width = null,
        Length? height = null,
        ImageScaleMode scaleMode = ImageScaleMode.Original)
    {
        var image = new ImageBlock(data, mediaType)
        {
            AltText = altText,
            Width = width,
            Height = height,
            ScaleMode = scaleMode,
        };

        var metadata = ImageMetadataReader.Read(data.Span, mediaType);
        if (metadata.PixelWidth <= 0 || metadata.PixelHeight <= 0)
        {
            throw new ImageException("Image width and height must be positive.");
        }

        _blocks.Add(image);

        return this;
    }

    /// <summary>
    /// adds a list that uses a numbering definition added via <see cref="Numbering"/>. Several
    /// such lists continue each other.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="numberingName"/> or <paramref name="items"/> is null.</exception>
    public PortableDocumentBuilder List(string numberingName, params string[] items)
    {
        ArgumentNullException.ThrowIfNull(numberingName);
        ArgumentNullException.ThrowIfNull(items);
        var listItems = items
            .Select(item => new ListItem(new Paragraph(new TextRun(item))))
            .ToList();
        _blocks.Add(new ListBlock(numberingName, listItems));
        return this;
    }

    /// <summary>Adds a manual page break.</summary>
    public PortableDocumentBuilder PageBreak()
    {
        _blocks.Add(new PageBreak());
        return this;
    }

    private PortableDocumentBuilder AddList(ListKind kind, IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var listItems = items
            .Select(item => new ListItem(new Paragraph(new TextRun(item))))
            .ToList();
        var name = string.Create(CultureInfo.InvariantCulture, $"Numbering{++_listCount}");
        _numbering.Add(new NumberingDefinition(name, [new NumberingLevel { Kind = kind }]));
        _blocks.Add(new ListBlock(name, listItems));
        return this;
    }
}
