using System.Security.Cryptography;

namespace Reglet;

/// <summary>A font of a <see cref="PdfCanvas"/>: a family of the font collection and its style.</summary>
/// <param name="Family">The family name, as in <see cref="PdfWriteOptions.FontCollection"/>; without a matching font, a standard font is used.</param>
/// <param name="Bold">Bold style.</param>
/// <param name="Italic">Italic style.</param>
public sealed record PdfCanvasFont(string Family, bool Bold = false, bool Italic = false);

/// <summary>
/// Which point of the text <see cref="PdfCanvasPage.DrawText"/> places at the given position. The line box
/// runs from the ascender above the baseline to the descender below it (hhea of the font file; for a
/// standard font the values of Arial, Times New Roman and Courier New).
/// </summary>
public enum TextAnchor
{
    /// <summary>Left end of the baseline.</summary>
    BaselineLeft,

    /// <summary>Centre of the baseline.</summary>
    BaselineCenter,

    /// <summary>Right end of the baseline.</summary>
    BaselineRight,

    /// <summary>Top left of the line box (the baseline lies one ascender lower).</summary>
    TopLeft,

    /// <summary>Top centre of the line box.</summary>
    TopCenter,

    /// <summary>Top right of the line box.</summary>
    TopRight,

    /// <summary>Left end of the line box's vertical centre (half way between ascender and descender).</summary>
    CenterLeft,

    /// <summary>Centre of the line box.</summary>
    Center,

    /// <summary>Right end of the line box's vertical centre.</summary>
    CenterRight,
}

/// <summary>
/// A PDF drawn page by page at fixed coordinates, without the flow layout: text, lines, rectangles and
/// images. Units are points, the origin is the top left of the page and y points down. The file is
/// written with the same <see cref="PdfWriteOptions"/> as a flow document (fonts embedded and subset,
/// PDF/A, encryption, viewer settings), and its text is extracted by <see cref="PdfDocumentReader"/>.
/// </summary>
public sealed class PdfCanvas
{
    private readonly List<PdfCanvasPage> _pages = [];
    private readonly Dictionary<string, ImageBlock> _images = new(StringComparer.Ordinal);
    private readonly PdfWriteOptions _options;
    private readonly PdfFontCatalog _measureFonts;

    /// <summary>Creates an empty canvas document.</summary>
    /// <param name="options">The writer options; the fonts of <see cref="PdfWriteOptions.FontCollection"/> are drawn and measured.</param>
    public PdfCanvas(PdfWriteOptions? options = null)
    {
        _options = options ?? PdfWriteOptions.Default;

        // Measuring goes through the same catalog the writer builds, so width and drawing agree.
        var file = new PdfFileWriter(Stream.Null);
        var resources = new PdfResourceManager();
        _measureFonts = new PdfFontCatalog(
            new PdfStandardFontManager(file, resources),
            new PdfTrueTypeFontManager(file, resources),
            PdfDocumentBuilder.ResolveFonts(_options, new UnsupportedFeatureHandler(UnsupportedContentBehavior.Warn, [])),
            requireEmbeddedFonts: _options.PdfA != PdfAConformance.None);
    }

    /// <summary>Title, author, creator and the other metadata of the file.</summary>
    public DocumentMetadata Metadata { get; set; } = new();

    /// <summary>The pages added so far.</summary>
    public IReadOnlyList<PdfCanvasPage> Pages => _pages;

    /// <summary>Adds a page.</summary>
    /// <param name="size">The paper size, e.g. <see cref="PageSize.A3"/> or a custom <see cref="PageSize"/>.</param>
    /// <param name="orientation">Landscape swaps width and height.</param>
    public PdfCanvasPage AddPage(PageSize size, PageOrientation orientation = PageOrientation.Portrait)
    {
        ArgumentNullException.ThrowIfNull(size);
        var landscape = orientation == PageOrientation.Landscape;
        var page = new PdfCanvasPage(
            this,
            size,
            orientation,
            landscape ? size.Height.Points : size.Width.Points,
            landscape ? size.Width.Points : size.Height.Points);
        _pages.Add(page);
        return page;
    }

    /// <summary>The width of a text in points, exactly as <see cref="PdfCanvasPage.DrawText"/> draws it (shaping, kerning, same font).</summary>
    public double MeasureText(string text, PdfCanvasFont font, double size)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        return _measureFonts.GetFont(font.Family, font.Bold, font.Italic).Measure(text, size);
    }

    /// <summary>Writes the pages as PDF.</summary>
    /// <exception cref="InvalidOperationException">No page was added.</exception>
    public Task<WriteResult> WriteAsync(Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var (document, options) = ToDocument();
        return new PdfDocumentWriter().WriteAsync(document, output, options, cancellationToken);
    }

    /// <summary>
    /// Writes the pages as paged HTML (ADR-0039): one element per page, every drawing at the coordinates of the PDF, with the
    /// same fonts (embedded as <c>@font-face</c>), anchors and measurement; an image drawn several times is embedded once.
    /// </summary>
    /// <exception cref="InvalidOperationException">No page was added.</exception>
    public Task<WriteResult> WriteHtmlAsync(Stream output, PdfHtmlOptions? html = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var (document, options) = ToDocument();
        return PdfHtmlWriter.WriteAsync(document, output, options, html ?? PdfHtmlOptions.Default, cancellationToken);
    }

    /// <summary>The pages as a document of margin-less sections, and the options to write it with.</summary>
    /// <exception cref="InvalidOperationException">No page was added.</exception>
    internal (PortableDocument Document, PdfWriteOptions Options) ToDocument()
    {
        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("The canvas has no page; call AddPage first.");
        }

        var sections = _pages.Select(page => new Section(new CanvasBlock(page))
        {
            PageSize = page.Size,
            Orientation = page.Orientation,
            Margins = new PageMargins(Length.Zero),
        });
        return (new PortableDocument(Metadata, sections), _options);
    }

    /// <summary>One image block per distinct content, so the same bytes are embedded once.</summary>
    internal ImageBlock Image(ReadOnlySpan<byte> data)
    {
        var key = Convert.ToHexString(SHA256.HashData(data));
        if (!_images.TryGetValue(key, out var image))
        {
            image = new ImageBlock(data.ToArray(), ImageInfo.Read(data).MediaType);
            _images[key] = image;
        }

        return image;
    }
}

/// <summary>
/// A page of a <see cref="PdfCanvas"/>. Coordinates are points from the top left of the page, y pointing
/// down, shifted by <see cref="Translate"/>.
/// </summary>
public sealed class PdfCanvasPage
{
    private readonly PdfCanvas _canvas;
    private readonly List<Action<PdfPageRenderer, PdfImageManager>> _operations = [];
    private readonly Stack<(double X, double Y)> _states = new();
    private double _offsetX;
    private double _offsetY;

    internal PdfCanvasPage(PdfCanvas canvas, PageSize size, PageOrientation orientation, double width, double height)
    {
        _canvas = canvas;
        Size = size;
        Orientation = orientation;
        Width = width;
        Height = height;
    }

    /// <summary>The paper size.</summary>
    public PageSize Size { get; }

    /// <summary>The orientation.</summary>
    public PageOrientation Orientation { get; }

    /// <summary>The width of the page in points (after the orientation).</summary>
    public double Width { get; }

    /// <summary>The height of the page in points (after the orientation).</summary>
    public double Height { get; }

    /// <summary>Moves the origin of the following drawing.</summary>
    public void Translate(double dx, double dy)
    {
        _offsetX += dx;
        _offsetY += dy;
    }

    /// <summary>Saves the current origin.</summary>
    public void SaveState() => _states.Push((_offsetX, _offsetY));

    /// <summary>Restores the origin saved by the matching <see cref="SaveState"/>.</summary>
    /// <exception cref="InvalidOperationException">There is no saved state.</exception>
    public void RestoreState()
    {
        if (!_states.TryPop(out var state))
        {
            throw new InvalidOperationException("RestoreState without a matching SaveState.");
        }

        (_offsetX, _offsetY) = state;
    }

    /// <inheritdoc cref="PdfCanvas.MeasureText"/>
    public double MeasureText(string text, PdfCanvasFont font, double size) => _canvas.MeasureText(text, font, size);

    /// <summary>Draws a single line of text.</summary>
    /// <param name="text">The text; line breaks are not interpreted.</param>
    /// <param name="font">Family and style.</param>
    /// <param name="size">Font size in points.</param>
    /// <param name="x">Horizontal position of the anchor.</param>
    /// <param name="y">Vertical position of the anchor (y pointing down).</param>
    /// <param name="anchor">Which point of the text lies at (x, y).</param>
    /// <param name="color">Text colour; black by default.</param>
    public void DrawText(string text, PdfCanvasFont font, double size, double x, double y, TextAnchor anchor = TextAnchor.BaselineLeft, Color? color = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var (left, top) = (x + _offsetX, y + _offsetY);
        var fill = color ?? Color.Black;
        _operations.Add((renderer, _) =>
        {
            var handle = renderer.Fonts.GetFont(font.Family, font.Bold, font.Italic);
            var width = handle.Measure(text, size);
            var startX = anchor switch
            {
                TextAnchor.BaselineCenter or TextAnchor.TopCenter or TextAnchor.Center => left - width / 2,
                TextAnchor.BaselineRight or TextAnchor.TopRight or TextAnchor.CenterRight => left - width,
                _ => left,
            };
            var ascent = handle.Metrics.AscentEm * size;
            var descent = handle.Metrics.DescentEm * size;
            var baseline = anchor switch
            {
                TextAnchor.TopLeft or TextAnchor.TopCenter or TextAnchor.TopRight => top + ascent,
                TextAnchor.CenterLeft or TextAnchor.Center or TextAnchor.CenterRight => top + (ascent - descent) / 2,
                _ => top,
            };
            renderer.BeginStructure("P");
            renderer.WriteLine(handle, text, size, fill, startX, Height - baseline);
            renderer.EndStructure();
        });
    }

    /// <summary>Draws a straight line.</summary>
    public void DrawLine(double x1, double y1, double x2, double y2, double width = 1, Color? color = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        var (ax, ay, bx, by) = (x1 + _offsetX, y1 + _offsetY, x2 + _offsetX, y2 + _offsetY);
        var stroke = color ?? Color.Black;
        Artifact(renderer => renderer.DrawLine(ax, Height - ay, bx, Height - by, stroke, width, TextUnderlineStyle.Single));
    }

    /// <summary>Draws a rectangle, filled and/or outlined; with neither colour nothing is drawn.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="fill">Fill colour, or null for no fill.</param>
    /// <param name="stroke">Outline colour, or null for no outline.</param>
    /// <param name="strokeWidth">Outline width in points.</param>
    public void DrawRectangle(double x, double y, double width, double height, Color? fill = null, Color? stroke = null, double strokeWidth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strokeWidth);
        var (left, bottom) = (x + _offsetX, Height - (y + _offsetY) - height);
        Artifact(renderer =>
        {
            if (fill is { } fillColor)
            {
                renderer.FillRectangle(left, bottom, width, height, fillColor);
            }

            if (stroke is { } strokeColor)
            {
                renderer.StrokeRectangle(left, bottom, width, height, strokeWidth, strokeColor);
            }
        });
    }

    /// <summary>Draws a PNG or JPEG image stretched into a rectangle. The same bytes drawn several times are embedded once.</summary>
    /// <exception cref="ImageException">The bytes are neither PNG nor JPEG.</exception>
    public void DrawImage(ReadOnlySpan<byte> data, double x, double y, double width, double height)
    {
        var image = _canvas.Image(data);
        var (left, bottom) = (x + _offsetX, Height - (y + _offsetY) - height);
        _operations.Add((renderer, images) =>
        {
            var handle = images.GetImage(image);
            renderer.BeginArtifact();
            renderer.DrawImage(handle.ResourceName, width, height, left, bottom);
            renderer.EndArtifact();
        });
    }

    /// <summary>Draws the recorded operations onto the current PDF page.</summary>
    internal void Render(PdfPageRenderer renderer, PdfImageManager images)
    {
        foreach (var operation in _operations)
        {
            operation(renderer, images);
        }
    }

    /// <summary>Graphics carry no content, so a tagged reader skips them.</summary>
    private void Artifact(Action<PdfPageRenderer> draw)
        => _operations.Add((renderer, _) =>
        {
            renderer.BeginArtifact();
            draw(renderer);
            renderer.EndArtifact();
        });
}
