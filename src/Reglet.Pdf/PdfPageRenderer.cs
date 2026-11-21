using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// manages pages, the vertical cursor, page changes and reserved headers/footers.
/// writes text lines into the content stream of the current page.
/// </summary>
internal sealed class PdfPageRenderer
{
    private const double FootnoteFontSize = 8;

    /// <summary>A header or footer may take at most this share of the page height.</summary>
    private const double MaxBandShare = 1.0 / 3.0;

    /// <summary>Slant of synthetic italic (tangent of 10°).</summary>
    private const double SyntheticItalicSlant = 0.1763;

    /// <summary>Stroke width of synthetic bold relative to the font size.</summary>
    private const double SyntheticBoldStrokeFactor = 0.04;

    private readonly PdfFileWriter _file;
    private readonly PdfResourceManager _resources;
    private readonly PdfFontCatalog _fonts;
    private readonly PdfStructureTree _structure;
    private readonly int _pagesObject;
    private readonly int _resourcesObject;
    private readonly PdfPageDecoration? _decoration;
    private readonly List<int> _pageObjects = [];
    private readonly List<(string Target, double X1, double Y1, double X2, double Y2)> _annotations = [];
    private readonly List<(FormField Field, double X, double Y, double Width, double Height)> _widgets = [];
    private readonly List<PdfAnnotation> _pageComments = [];
    private readonly Dictionary<string, string> _alphaStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _svgShadings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _svgPatterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _svgForms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _svgMasks = new(StringComparer.Ordinal);

    private Section _section = null!;
    private PageBorders? _pageBorders;
    private HeaderFooter? _header;
    private HeaderFooter? _footer;
    private HeaderFooter? _firstHeader;
    private HeaderFooter? _firstFooter;
    private HeaderFooter? _evenHeader;
    private HeaderFooter? _evenFooter;
    private int? _pageNumberStart;
    private int _sectionStartPage;
    private PdfContentWriter _content = new();
    private int _pageObject;
    private int _structParents;
    private double _pageWidth;
    private double _pageHeight;
    private double _contentLeft;
    private double _contentWidth;
    private double _fullContentLeft;
    private double _fullContentWidth;
    private double _columnWidth;
    private double _columnGap;
    private int _columnCount = 1;
    private int _columnIndex;
    private double _contentTop;

    /// <summary>The lowest point any column of the current band reached on this page (a new column layout starts below it).</summary>
    private double _bandLowest = double.MaxValue;
    private double _contentBottom;
    private double _contentBottomBase;
    private double _footnoteHeight;
    private readonly List<(int Number, IReadOnlyList<string> Lines, bool Continuation, IReadOnlyList<string> Bookmarks)> _pageFootnotes = [];
    private readonly List<Action> _deferredDraws = [];
    private readonly LinkedList<(int Number, IReadOnlyList<string> Lines, int Offset, IReadOnlyList<string> Bookmarks)> _pendingFootnotes = [];
    private double _cursorY;
    private int _pageNumber;
    private LineNumbering? _lineNumbering;
    private int _lineCounter;
    private bool _pageStarted;
    private bool _pageStartedByBreak;
    private bool _inBand;
    private bool _suppressAnnotations;
    private double _bandMaxHeight;

    /// <summary>The page limit of the document (<see cref="ResourceLimits.MaxPages"/>).</summary>
    public int MaxPages { get; init; } = ResourceLimits.Default.MaxPages;

    /// <summary>Checked for every new page, so a long layout can be cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The height a header or footer being drawn is clipped to.</summary>
    internal double BandMaxHeight => _bandMaxHeight;

    public PdfPageRenderer(
        PdfFileWriter file,
        PdfResourceManager resources,
        PdfFontCatalog fonts,
        PdfStructureTree structure,
        int pagesObject,
        int resourcesObject,
        PdfPageDecoration? decoration = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(structure);

        _file = file;
        _resources = resources;
        _fonts = fonts;
        _structure = structure;
        _pagesObject = pagesObject;
        _resourcesObject = resourcesObject;
        _decoration = decoration;
    }

    public IReadOnlyList<int> PageObjects => _pageObjects;

    public PdfFontCatalog Fonts => _fonts;

    public double ContentLeft => _contentLeft;

    /// <summary>The object of the signature field widget, listed in the first page's Annots.</summary>
    public int? SignatureWidget { get; init; }

    /// <summary>the non-link annotations of the document (OF-F01), placed by their page.</summary>
    public IReadOnlyList<PdfAnnotation> Annotations { get; init; } = [];

    /// <summary>Writes the form fields (ADR-0024); null in a renderer that only measures.</summary>
    public PdfFormWriter? Forms { get; init; }

    /// <summary>Annotations carry an appearance of their own (PDF/A requires one).</summary>
    public bool WriteAnnotationAppearances { get; init; }

    public double ContentWidth => _contentWidth;

    public double ContentHeight => _inBand ? _bandMaxHeight : _contentTop - _contentBottom;

    public double CursorY => _cursorY;

    public double ContentBottom => _contentBottom;

    /// <summary>Whether the cursor is at the very top of the page.</summary>
    public bool IsAtPageTop => _cursorY == _contentTop;

    public int PageNumber => _pageNumber;

    /// <summary>The total page count from a previous layout pass (NUMPAGES); null when unknown.</summary>
    public int? TotalPages { get; init; }

    /// <summary>
    /// Draws the blocks of a header or footer from the cursor downward and returns the height
    /// they occupy. Set by the document builder; while it runs the renderer is in band mode:
    /// page and column breaks and tagging do not apply there, the whole band is one artifact.
    /// </summary>
    public Func<IReadOnlyList<Block>, double>? BandRenderer { get; set; }

    /// <summary>
    /// Called with the bookmarks of a footnote and the baseline of its first line, so the writer
    /// can record a named destination on the page where the note is drawn (OF-C03).
    /// </summary>
    public Action<IReadOnlyList<string>, double>? NoteBookmark { get; set; }

    /// <summary>Displayed page number of the section (with <c>PageNumberStart</c>).</summary>
    public int DisplayPageNumber => (_pageNumberStart ?? _sectionStartPage) + (_pageNumber - _sectionStartPage);

    /// <summary>Object number of the page currently being written.</summary>
    public int CurrentPageObject => _pageObject;

    /// <summary>Clips to a rectangle until <see cref="EndClip"/> (for cropped images).</summary>
    public void BeginClip(double x, double y, double width, double height)
    {
        _content.SaveState();
        _content.Rectangle(x, y, width, height);
        _content.Clip();
    }

    /// <summary>Ends the clipping started by <see cref="BeginClip"/>.</summary>
    public void EndClip() => _content.RestoreState();

    private int _sectionCount;

    /// <summary>
    /// whether the cursor is at the very top of a page that was created by a section break
    /// has begun (not the document start, not a break/overflow). There
    /// Word/LibreOffice the space before of the first paragraph.
    /// </summary>
    public bool IsAtTopOfSectionStart => !_inBand && !_pageStartedByBreak && _sectionCount > 1 && _cursorY == _contentTop;

    /// <summary>
    /// whether the cursor is at the very top of a page that was created by a break or
    /// overflow has begun (not by a section start). As in Word, the
    /// there the space before a paragraph.
    /// </summary>
    public bool IsAtTopOfContinuedPage => !_inBand && _pageStartedByBreak && _cursorY == _contentTop;

    public void BeginSection(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var landscape = section.Orientation == PageOrientation.Landscape;
        var pageWidth = landscape ? section.PageSize.Height.Points : section.PageSize.Width.Points;
        var pageHeight = landscape ? section.PageSize.Width.Points : section.PageSize.Height.Points;
        if (pageWidth <= section.Margins.Left.Points + section.Margins.Right.Points
            || pageHeight <= section.Margins.Top.Points + section.Margins.Bottom.Points)
        {
            throw new DocumentValidationException("Page margins leave no content area.", nameof(section));
        }

        // A continuous section continues on the same page; that is only possible when the page
        // layout does not change.
        var continuous = _pageStarted && section.StartType == SectionStartType.Continuous && SameLayout(section);

        if (_pageStarted && !continuous)
        {
            FinishPage();
            FinishPendingFootnotes();

            // Odd or even page: a blank page is inserted when the parity does not match.
            if (section.StartType is SectionStartType.OddPage or SectionStartType.EvenPage)
            {
                var nextIndex = _pageObjects.Count + 1;
                var wanted = section.StartType == SectionStartType.OddPage ? 1 : 0;
                if (nextIndex % 2 != wanted)
                {
                    StartPage();
                    FinishPage();
                }
            }
        }

        _header = section.Header ?? _decoration?.Template?.Header;
        _footer = section.Footer ?? _decoration?.Template?.Footer;
        _firstHeader = section.FirstHeader;
        _firstFooter = section.FirstFooter;
        _evenHeader = section.EvenHeader;
        _evenFooter = section.EvenFooter;
        _pageNumberStart = section.PageNumberStart;
        _sectionStartPage = _pageNumber + 1;
        _lineNumbering = section.LineNumbering;
        if (section.LineNumbering is { Restart: LineNumberRestart.NewSection })
        {
            _lineCounter = 0;
        }

        _sectionCount++;
        _section = section;
        _pageBorders = section.PageBorders;
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _contentLeft = section.Margins.Left.Points;
        _contentWidth = _pageWidth - section.Margins.Left.Points - section.Margins.Right.Points;
        _fullContentLeft = section.Margins.Left.Points;
        _fullContentWidth = _contentWidth;
        var previousColumns = _columnCount;
        _columnCount = Math.Clamp(section.TextColumns ?? 1, 1, 12);
        _columnGap = _columnCount > 1 ? 18.0 : 0.0;
        _columnWidth = _columnCount > 1
            ? (_fullContentWidth - (_columnCount - 1) * _columnGap) / _columnCount
            : _fullContentWidth;

        if (continuous && previousColumns != _columnCount)
        {
            // A new column layout on the same page starts below everything written so far, in its first column.
            var top = Math.Min(_cursorY, _bandLowest);
            _contentTop = top;
            _cursorY = top;
            _columnIndex = 0;
            _bandLowest = top;
        }

        if (continuous)
        {
            _columnIndex = Math.Min(_columnIndex, _columnCount - 1);
            _contentLeft = _fullContentLeft + _columnIndex * (_columnWidth + _columnGap);
            _contentWidth = _columnWidth;
        }

        if (!continuous || !_pageStarted)
        {
            _contentTop = _pageHeight - section.Margins.Top.Points;
            _contentBottomBase = section.Margins.Bottom.Points;
            _contentBottom = _contentBottomBase;
            StartPage();
            _pageStartedByBreak = false;
        }
    }

    /// <summary>Whether <paramref name="next"/> would begin on a new page after the current section.</summary>
    public bool StartsNewPage(Section next)
        => next.StartType != SectionStartType.Continuous || !SameLayout(next);

    /// <summary>Whether the section has the same page layout as the current one.</summary>
    private bool SameLayout(Section section)
    {
        var landscape = section.Orientation == PageOrientation.Landscape;
        var width = landscape ? section.PageSize.Height.Points : section.PageSize.Width.Points;
        var height = landscape ? section.PageSize.Width.Points : section.PageSize.Height.Points;
        return Math.Abs(width - _pageWidth) < 0.001
            && Math.Abs(height - _pageHeight) < 0.001
            && Math.Abs(section.Margins.Left.Points - _fullContentLeft) < 0.001
            && Math.Abs(section.Margins.Right.Points - (_pageWidth - _fullContentLeft - _fullContentWidth)) < 0.001
            && Math.Abs(section.Margins.Top.Points - _section.Margins.Top.Points) < 0.001
            && Math.Abs(section.Margins.Bottom.Points - _section.Margins.Bottom.Points) < 0.001;
    }

    /// <summary>Starts a new page if needed so that <paramref name="height"/> fits.</summary>
    /// <summary>Whether <paramref name="height"/> still fits in the current column.</summary>
    public bool HasSpaceFor(double height) => _inBand || _cursorY - height >= _contentBottom;

    public void EnsureSpace(double height)
    {
        if (_cursorY - height < _contentBottom)
        {
            NextColumn();
        }
    }

    /// <summary>Advances to the next column; after the last column a new page starts.</summary>
    public void NextColumn()
    {
        if (_inBand)
        {
            return;
        }

        if (_columnIndex + 1 < _columnCount)
        {
            _bandLowest = Math.Min(_bandLowest, _cursorY);
            _columnIndex++;
            _contentLeft = _fullContentLeft + _columnIndex * (_columnWidth + _columnGap);
            _cursorY = _contentTop;
            _pageStartedByBreak = true;
        }
        else
        {
            NewPage();
        }
    }

    public void Advance(double height) => _cursorY -= height;

    public void NewPage()
    {
        if (_inBand)
        {
            return;
        }

        FinishPage();
        StartPage();
        _pageStartedByBreak = true;
    }

    public void WriteLine(PdfFontHandle font, string text, double fontSize, Color color, double x)
        => WriteLine(font, text, fontSize, color, x, _cursorY);

    /// <summary>
    /// Counts a text line and draws its line number in the left margin (phase D). The number is
    /// drawn right-aligned at the text edge minus the configured distance.
    /// </summary>
    public void WriteLineNumber(double baseline, double defaultFontSize)
    {
        if (_lineNumbering is not { } numbering || _inBand)
        {
            return;
        }

        _lineCounter++;
        var number = numbering.Start + _lineCounter - 1;
        if (numbering.CountBy > 1 && number % numbering.CountBy != 0)
        {
            return;
        }

        var text = number.ToString(CultureInfo.InvariantCulture);
        var font = _fonts.GetFont(null, bold: false, italic: false);
        var fontSize = Math.Min(10, defaultFontSize);
        var width = font.Measure(text, fontSize);
        var x = _contentLeft - numbering.Distance.Points - width;
        if (x < 0)
        {
            return;
        }

        _content.BeginArtifact();
        WriteLine(font, text, fontSize, new Color(128, 128, 128), x, baseline);
        _content.EndMarkedContent();
    }

    /// <summary>Rotates everything drawn until <see cref="EndRotation"/> clockwise around a point.</summary>
    public void BeginRotation(double centerX, double centerY, double degrees)
    {
        var radians = -degrees * Math.PI / 180.0;
        var (sin, cos) = Math.SinCos(radians);
        _content.SaveState();
        _content.ConcatMatrix(1, 0, 0, 1, centerX, centerY);
        _content.ConcatMatrix(cos, sin, -sin, cos, 0, 0);
        _content.ConcatMatrix(1, 0, 0, 1, -centerX, -centerY);
    }

    /// <summary>Ends the rotation started by <see cref="BeginRotation"/>.</summary>
    public void EndRotation() => _content.RestoreState();

    /// <summary>Draws a chart (ADR-0025) into the frame whose bottom-left corner is at x/y.</summary>
    public void DrawChart(Chart chart, double width, double height, double x, double y, Action<string> report)
        => PdfChartPainter.Paint(
            _content, chart, _fonts.GetFont(null, bold: false, italic: false), _fonts.GetFont(null, bold: true, italic: false), report,
            width, height, x, y);

    /// <summary>
    /// Draws the geometry of a shape (ADR-0022) into the frame whose bottom-left corner is at
    /// x/y: fill, outline and dash; the text of a text box is drawn by the caller.
    /// </summary>
    public void DrawShape(DrawingShape shape, double width, double height, double x, double y)
    {
        const double Kappa = 0.5522847498;
        var top = y + height;
        _content.SaveState();
        if (shape.Fill is { } fill)
        {
            _content.SetFillColor(fill);
        }

        if (shape.Outline is { } outline)
        {
            // A shape that was scaled down keeps its proportions, so the outline scales with it.
            var scale = shape.Width.Points > 0 ? width / shape.Width.Points : 1;
            var lineWidth = shape.OutlineWidth.Points * (scale > 0 ? scale : 1);
            _content.SetStrokeColor(outline).SetLineWidth(lineWidth);
            if (shape.Dash != ShapeDash.Solid)
            {
                _content.SetDash(shape.Dash == ShapeDash.Dash ? [4 * lineWidth, 3 * lineWidth] : [lineWidth, 2 * lineWidth]);
            }
        }

        switch (shape.Kind)
        {
            case ShapeKind.Line:
                _content.MoveTo(x, top).LineTo(x + width, y);
                break;
            case ShapeKind.Ellipse:
            {
                double rx = width / 2, ry = height / 2, cx = x + rx, cy = y + ry;
                _content.MoveTo(cx + rx, cy)
                    .CurveTo(cx + rx, cy + Kappa * ry, cx + Kappa * rx, cy + ry, cx, cy + ry)
                    .CurveTo(cx - Kappa * rx, cy + ry, cx - rx, cy + Kappa * ry, cx - rx, cy)
                    .CurveTo(cx - rx, cy - Kappa * ry, cx - Kappa * rx, cy - ry, cx, cy - ry)
                    .CurveTo(cx + Kappa * rx, cy - ry, cx + rx, cy - Kappa * ry, cx + rx, cy)
                    .ClosePath();
                break;
            }

            case ShapeKind.RoundRectangle:
                _content.RoundedRectangle(x, y, width, height, Math.Min(width, height) / 6);
                break;
            case ShapeKind.Freeform:
            {
                var scaleX = shape.Width.Points > 0 ? width / shape.Width.Points : 1;
                var scaleY = shape.Height.Points > 0 ? height / shape.Height.Points : 1;
                double PX(Length value) => x + value.Points * scaleX;
                double PY(Length value) => top - value.Points * scaleY;
                foreach (var step in shape.Path!)
                {
                    switch (step.Command)
                    {
                        case ShapePathCommand.MoveTo:
                            _content.MoveTo(PX(step.X), PY(step.Y));
                            break;
                        case ShapePathCommand.LineTo:
                            _content.LineTo(PX(step.X), PY(step.Y));
                            break;
                        case ShapePathCommand.CubicTo:
                            _content.CurveTo(PX(step.X1), PY(step.Y1), PX(step.X2), PY(step.Y2), PX(step.X), PY(step.Y));
                            break;
                        default:
                            _content.ClosePath();
                            break;
                    }
                }

                break;
            }

            default:
                _content.Rectangle(x, y, width, height);
                break;
        }

        // A line has no inside: only its outline is painted.
        var fills = shape.Fill is not null && shape.Kind != ShapeKind.Line;
        if (fills && shape.Outline is not null)
        {
            _content.FillAndStroke();
        }
        else if (fills)
        {
            _content.Fill();
        }
        else if (shape.Outline is not null)
        {
            _content.Stroke();
        }
        else
        {
            _content.EndPath();
        }

        _content.RestoreState();
    }

    /// <summary>The layout of a formula (ADR-0031) with the metrics of the document font.</summary>
    public MathLayout MathLayoutFor()
        => new((text, italic, size) => _fonts.GetFont(null, bold: false, italic).Measure(text, size));

    /// <summary>Draws a laid-out formula with its baseline start at x/y, scaled by <paramref name="scale"/>.</summary>
    public void DrawMath(MathBox box, double x, double y, double scale)
    {
        foreach (var item in box.Items)
        {
            switch (item)
            {
                case MathGlyphs glyphs:
                    WriteLineTo(
                        _content, _fonts.GetFont(null, bold: false, glyphs.Italic), glyphs.Text, glyphs.Size * scale, Color.Black,
                        x + glyphs.X * scale, y + glyphs.Y * scale, 0, 100, null);
                    break;
                case MathRule rule:
                    _content.SaveState().SetFillColor(Color.Black)
                        .Rectangle(x + rule.X * scale, y + rule.Y * scale, rule.Width * scale, rule.Thickness * scale).Fill().RestoreState();
                    break;
                case MathPath path:
                    _content.SaveState().SetStrokeColor(Color.Black).SetLineWidth(path.Thickness * scale);
                    for (var i = 0; i < path.Points.Count; i++)
                    {
                        var (px, py) = path.Points[i];
                        if (i == 0)
                        {
                            _content.MoveTo(x + px * scale, y + py * scale);
                        }
                        else
                        {
                            _content.LineTo(x + px * scale, y + py * scale);
                        }
                    }

                    _content.Stroke().RestoreState();
                    break;
            }
        }
    }

    /// <summary>Draws the outline of a rectangle (x/y = bottom-left corner).</summary>
    public void StrokeRectangle(double x, double y, double width, double height, double lineWidth, Color color)
        => _content.SaveState()
            .SetStrokeColor(color)
            .SetLineWidth(lineWidth)
            .Rectangle(x, y, width, height)
            .Stroke()
            .RestoreState();

    /// <summary>
    /// Draws the outline of a rectangle with rounded corners (OF-B04); the radius is clamped to
    /// half of the shorter side.
    /// </summary>
    public void StrokeRoundedRectangle(double x, double y, double width, double height, double radius, double lineWidth, Color color)
    {
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        if (r <= 0)
        {
            StrokeRectangle(x, y, width, height, lineWidth, color);
            return;
        }

        _content.SaveState()
            .SetStrokeColor(color)
            .SetLineWidth(lineWidth)
            .RoundedRectangle(x, y, width, height, r)
            .Stroke()
            .RestoreState();
    }

    /// <summary>Fills a rectangle with rounded corners (OF-B04).</summary>
    public void FillRoundedRectangle(double x, double y, double width, double height, double radius, Color color)
        => FillRoundedRectangle(x, y, width, height, radius, radius, radius, radius, color);

    /// <summary>
    /// fills a rectangle whose corners round with the given radii (OF-B04); a radius of 0 keeps
    /// that corner square.
    /// </summary>
    public void FillRoundedRectangle(
        double x,
        double y,
        double width,
        double height,
        double topLeft,
        double topRight,
        double bottomRight,
        double bottomLeft,
        Color color)
    {
        if (topLeft <= 0 && topRight <= 0 && bottomRight <= 0 && bottomLeft <= 0)
        {
            FillRectangle(x, y, width, height, color);
            return;
        }

        _content.SaveState()
            .SetFillColor(color)
            .RoundedRectangle(x, y, width, height, topLeft, topRight, bottomRight, bottomLeft)
            .Fill()
            .RestoreState();
    }

    /// <summary>
    /// strokes a cubic Bézier segment (OF-B04): the rounded corners of a table are drawn as
    /// quarter arcs of four Bézier control points.
    /// </summary>
    public void StrokeCurve(
        double x1,
        double y1,
        double control1X,
        double control1Y,
        double control2X,
        double control2Y,
        double x2,
        double y2,
        double lineWidth,
        Color color)
        => _content.SaveState()
            .SetStrokeColor(color)
            .SetLineWidth(lineWidth)
            .MoveTo(x1, y1)
            .CurveTo(control1X, control1Y, control2X, control2Y, x2, y2)
            .Stroke()
            .RestoreState();

    /// <summary>Draws an image XObject at the specified position.</summary>
    public void DrawImage(string resourceName, double width, double height, double x, double y)
        => _content.DrawImage(resourceName, width, height, x, y);

    /// <summary>
    /// Draws a vector graphic (an SVG converted to PDF operators) at the specified position.
    /// The drawing is scaled into the box; the SVG coordinates have y growing downwards.
    /// </summary>
    public void DrawVector(SvgDrawing drawing, double width, double height, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        if (drawing.UserWidth <= 0 || drawing.UserHeight <= 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var scaleX = width / drawing.UserWidth;
        var scaleY = height / drawing.UserHeight;
        _content.SaveState();
        _content.ConcatMatrix(scaleX, 0, 0, -scaleY, x, y + height);
        if (drawing.UsesPatterns)
        {
            // A pattern's matrix is relative to the enclosing content stream, so a drawing with
            // patterns is a form XObject: the patterns then follow the placement above.
            _content.AppendOperators("/" + RegisterSvgForm(drawing.Content, 0, 0, drawing.UserWidth, drawing.UserHeight) + " Do\n");
        }
        else
        {
            _content.AppendOperators(drawing.Content);
        }

        _content.RestoreState();
    }

    /// <summary>
    /// Returns the name of an ExtGState resource for an opacity value (phase AA, AA03). The
    /// key is for example <c>"fill:0.5"</c>; the resource is created once per key.
    /// </summary>
    public string AlphaState(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_alphaStates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var parts = key.Split(':');
        var pair = parts.Length == 3 && parts[0] == "pair"; // pair:fill:stroke
        var alpha = double.Parse(parts[pair ? 1 : ^1], CultureInfo.InvariantCulture);
        var strokeAlpha = pair ? double.Parse(parts[2], CultureInfo.InvariantCulture) : alpha;
        var both = parts.Length >= 2 && parts[0] == "both";
        var isFill = parts.Length < 2 || parts[0] == "fill";
        var objectNumber = _file.ReserveObject();
        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(state =>
        {
            state.WriteName("Type");
            state.WriteName("ExtGState");
            state.WriteName("ca");
            state.WriteNumber(both || isFill || pair ? alpha : 1);
            state.WriteName("CA");
            state.WriteNumber(pair ? strokeAlpha : both ? alpha : isFill ? 1 : alpha);
        }));
        var name = _resources.AddExtGState(key, objectNumber);
        _alphaStates[key] = name;
        return name;
    }

    /// <summary>Draws an underline/strikethrough line in the text color.</summary>
    public void DrawRule(Color color, double x1, double x2, double y, double thickness, TextUnderlineStyle style)
        => _content.Rule(color, x1, x2, y, thickness, style);

    /// <summary>Fills a rectangle (character background) in the specified color.</summary>
    public void FillRectangle(double x, double y, double width, double height, Color color)
        => _content.FillRectangle(x, y, width, height, color);

    /// <summary>Draws a line with an optional dash pattern.</summary>
    public void DrawLine(double x1, double y1, double x2, double y2, Color color, double width, TextUnderlineStyle style)
        => _content.StrokeLine(x1, y1, x2, y2, color, width, style);

    /// <summary>Starts marked content with alternative text (/Span) around an object.</summary>
    public void BeginMarkedContentWithAlt(string alt)
        => _content.BeginMarkedContentWithAlt("Span", alt);

    /// <summary>Starts a span whose <c>/ActualText</c> replaces the drawn glyphs.</summary>
    public void BeginActualText(string actualText)
        => _content.BeginMarkedContentWithActualText(actualText);

    /// <summary>Ends the previously started marked content (EMC).</summary>
    public void EndMarkedContent()
        => _content.EndMarkedContent();

    /// <summary>Starts a tagged content element (paragraph, heading, figure) in the structure tree.</summary>
    public void BeginStructure(string role, string? alt = null)
    {
        if (_inBand)
        {
            return;
        }

        var mcid = _structure.BeginContent(role, alt, _pageObject, _structParents);
        _content.BeginStructure(role, mcid);
    }

    /// <summary>Ends the most recently started content element.</summary>
    public void EndStructure()
    {
        if (_inBand)
        {
            return;
        }

        _content.EndMarkedContent();
        _structure.End();
    }

    /// <summary>Starts a container element (table, row, cell).</summary>
    public void BeginContainer(string role)
    {
        if (!_inBand)
        {
            _structure.BeginContainer(role, _pageObject);
        }
    }

    /// <summary>Ends the most recently started container element.</summary>
    public void EndContainer()
    {
        if (!_inBand)
        {
            _structure.End();
        }
    }

    /// <summary>Starts an artifact section (header/footer).</summary>
    public void BeginArtifact() => _content.BeginArtifact();

    /// <summary>Ends an artifact section.</summary>
    public void EndArtifact() => _content.EndMarkedContent();

    public void WriteLine(
        PdfFontHandle font,
        string text,
        double fontSize,
        Color color,
        double x,
        double y,
        double characterSpacing = 0,
        double horizontalScaling = 100,
        IReadOnlyList<byte>? bidiLevels = null)
    {
        WriteLineTo(_content, font, text, fontSize, color, x, y, characterSpacing, horizontalScaling, bidiLevels);
    }

    internal static void WriteLineTo(
        PdfContentWriter content, PdfFontHandle font, string text, double fontSize, Color color,
        double x, double y, double characterSpacing, double horizontalScaling, IReadOnlyList<byte>? bidiLevels)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (text.Length == 0)
        {
            return;
        }

        if (font.SyntheticBold || font.SyntheticItalic)
        {
            // Synthetic variants (N11): bold via render mode 2 (fill + stroke),
            // Italic via slanting around the baseline. The text state is encapsulated.
            content.SaveState();
            if (font.SyntheticItalic)
            {
                content.ConcatMatrix(1, 0, 0, 1, x, y);
                content.ConcatMatrix(1, SyntheticItalicSlant, 0, 1, 0, 0);
                WriteTextAt(content, font, text, fontSize, color, 0, 0, characterSpacing, horizontalScaling, bidiLevels);
            }
            else
            {
                WriteTextAt(content, font, text, fontSize, color, x, y, characterSpacing, horizontalScaling, bidiLevels);
            }

            content.RestoreState();
            return;
        }

        WriteTextAt(content, font, text, fontSize, color, x, y, characterSpacing, horizontalScaling, bidiLevels);
    }

    public (string Content, double Width)? RenderSvgText(string text, string? family, bool bold, bool italic, double size, Color color)
    {
        var font = _fonts.GetFont(family, bold, italic);
        if (!font.CanEncode(text)) return null;
        var content = new PdfContentWriter();
        WriteLineTo(content, font, text, size, color, 0, 0, 0, 100, null);
        return (System.Text.Encoding.ASCII.GetString(content.ToArray()), font.Measure(text, size));
    }

    /// <summary>
    /// Registers the raster image embedded in an SVG image (OF-B07) and returns its resource.
    /// </summary>
    public (string ResourceName, double Width, double Height)? RegisterSvgImage(byte[] bytes, ImageMediaType mediaType)
        => RegisterImage?.Invoke(bytes, mediaType);

    /// <summary>Registers an image for an SVG image; set by the document builder.</summary>
    public Func<byte[], ImageMediaType, (string ResourceName, double Width, double Height)?>? RegisterImage { get; set; }

    /// <summary>Registers a transparency group for an SVG image; set by the document builder.</summary>
    public Func<string, double, double, double, double, string>? RegisterForm { get; set; }

    /// <summary>Registers a mask for an SVG image; set by the document builder.</summary>
    public Func<string, double, double, double, double, string>? RegisterMask { get; set; }

    /// <summary>Registers a silhouette as an alpha mask for an SVG image; set by the document builder.</summary>
    public Func<string, double, double, double, double, string>? RegisterAlphaMask { get; set; }

    /// <summary>
    /// Registers a transparency group of an SVG image (OF-B07) as a form XObject with a
    /// transparency group dictionary and returns its resource name.
    /// </summary>
    public string RegisterSvgForm(string content, double minX, double minY, double maxX, double maxY)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_svgForms.TryGetValue(content, out var existing))
        {
            return existing;
        }

        static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        var objectNumber = _file.ReserveObject();
        _file.WriteStreamObject(
            objectNumber,
            System.Text.Encoding.ASCII.GetBytes(content),
            "/Type /XObject /Subtype /Form /FormType 1 "
            + $"/BBox [{Number(minX)} {Number(minY)} {Number(maxX)} {Number(maxY)}] "
            + "/Group << /S /Transparency /CS /DeviceRGB /I false /K false >> "
            + $"/Resources {_resourcesObject} 0 R");
        var name = _resources.AddForm($"svgform:{_svgForms.Count + 1}", objectNumber);
        _svgForms.Add(content, name);
        return name;
    }

    /// <summary>
    /// Registers the mask of an SVG image (OF-B07) as a luminance soft mask and returns the name of
    /// the graphics state that applies it.
    /// </summary>
    public string RegisterSvgMask(string content, double minX, double minY, double maxX, double maxY)
        => RegisterSvgSoftMask(content, minX, minY, maxX, maxY, alpha: false);

    /// <summary>
    /// Registers a silhouette as an alpha soft mask (OF-B07, a stroke painted with a paint server) and
    /// returns the name of the graphics state that applies it.
    /// </summary>
    public string RegisterSvgAlphaMask(string content, double minX, double minY, double maxX, double maxY)
        => RegisterSvgSoftMask(content, minX, minY, maxX, maxY, alpha: true);

    private string RegisterSvgSoftMask(string content, double minX, double minY, double maxX, double maxY, bool alpha)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = (alpha ? "alpha:" : "luminosity:") + content;
        if (_svgMasks.TryGetValue(key, out var existing))
        {
            return existing;
        }

        static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        var groupObject = _file.ReserveObject();
        _file.WriteStreamObject(
            groupObject,
            System.Text.Encoding.ASCII.GetBytes(content),
            "/Type /XObject /Subtype /Form /FormType 1 "
            + $"/BBox [{Number(minX)} {Number(minY)} {Number(maxX)} {Number(maxY)}] "
            + (alpha
                ? "/Group << /S /Transparency /CS /DeviceGray >> "
                : "/Group << /S /Luminosity /CS /DeviceGray >> ")
            + $"/Resources {_resourcesObject} 0 R");
        var softMaskObject = _file.ReserveObject();
        _file.WriteObject(softMaskObject, tokens => tokens.WriteDictionary(mask =>
        {
            mask.WriteName("Type");
            mask.WriteName("Mask");
            mask.WriteName("S");
            mask.WriteName(alpha ? "Alpha" : "Luminosity");
            mask.WriteName("G");
            mask.WriteReference(groupObject);
        }));
        var stateObject = _file.ReserveObject();
        _file.WriteObject(stateObject, tokens => tokens.WriteDictionary(state =>
        {
            state.WriteName("Type");
            state.WriteName("ExtGState");
            state.WriteName("SMask");
            state.WriteReference(softMaskObject);
        }));
        var name = _resources.AddExtGState($"{key}:{_svgMasks.Count + 1}", stateObject);
        _svgMasks.Add(key, name);
        return name;
    }

    public string RegisterSvgGradient(SvgGradient gradient)
    {
        var key = (gradient.Radial ? "radial:" : "linear:")
            + string.Join(",", gradient.Coordinates.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))
            + ":" + string.Join(",", gradient.Stops.Select(stop => stop.Offset.ToString("R", CultureInfo.InvariantCulture) + ":" + stop.Color))
            + (gradient.Extend ? string.Empty : ":clip")
            + (gradient.Samples is { } sampled ? ":s" + SamplesHash(sampled) : string.Empty);
        if (_svgShadings.TryGetValue(key, out var existing)) return existing;
        var objectNumber = _file.ReserveObject();
        int? functionObject = null;
        if (gradient.Samples is { } samples)
        {
            functionObject = _file.ReserveObject();
            var bytes = new byte[samples.Count * 3];
            for (var index = 0; index < samples.Count; index++)
            {
                bytes[index * 3] = samples[index].Red;
                bytes[index * 3 + 1] = samples[index].Green;
                bytes[index * 3 + 2] = samples[index].Blue;
            }

            _file.WriteStreamObject(
                functionObject.Value,
                PdfStreamCompressor.Compress(bytes),
                $"/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [{samples.Count}] "
                + $"/BitsPerSample 8 /Order 1 /Encode [0 {samples.Count - 1}] "
                + "/Decode [0 1 0 1 0 1] /Filter /FlateDecode");
        }

        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(shading =>
        {
            shading.WriteName("ShadingType"); shading.WriteInteger(gradient.Radial ? 3 : 2);
            shading.WriteName("ColorSpace"); shading.WriteName("DeviceRGB");
            shading.WriteName("Coords"); shading.WriteArray(array => { foreach (var value in gradient.Coordinates) array.WriteNumber(value); });
            shading.WriteName("Function");
            if (functionObject is { } function)
            {
                shading.WriteReference(function);
            }
            else
            {
                shading.WriteDictionary(stitched =>
                {
                    stitched.WriteName("FunctionType"); stitched.WriteInteger(3);
                    stitched.WriteName("Domain"); stitched.WriteArray(array => { array.WriteInteger(0); array.WriteInteger(1); });
                    stitched.WriteName("Functions"); stitched.WriteArray(array =>
                    {
                        for (var index = 0; index < gradient.Stops.Count - 1; index++)
                        {
                            var start = gradient.Stops[index].Color;
                            var end = gradient.Stops[index + 1].Color;
                            array.WriteDictionary(segment =>
                            {
                                segment.WriteName("FunctionType"); segment.WriteInteger(2);
                                segment.WriteName("Domain"); segment.WriteArray(domain => { domain.WriteInteger(0); domain.WriteInteger(1); });
                                segment.WriteName("C0"); segment.WriteArray(color => WriteGradientColor(color, start));
                                segment.WriteName("C1"); segment.WriteArray(color => WriteGradientColor(color, end));
                                segment.WriteName("N"); segment.WriteInteger(1);
                            });
                        }
                    });
                    stitched.WriteName("Bounds"); stitched.WriteArray(array =>
                    {
                        foreach (var stop in gradient.Stops.Skip(1).SkipLast(1)) array.WriteNumber(stop.Offset);
                    });
                    stitched.WriteName("Encode"); stitched.WriteArray(array =>
                    {
                        for (var index = 0; index < gradient.Stops.Count - 1; index++) { array.WriteInteger(0); array.WriteInteger(1); }
                    });
                });
            }
            shading.WriteName("Extend");
            shading.WriteArray(array =>
            {
                array.WriteBoolean(gradient.Extend);
                array.WriteBoolean(gradient.Extend);
            });
        }));
        var name = _resources.AddShading(key, objectNumber);
        _svgShadings.Add(key, name);
        return name;
    }

    private static void WriteGradientColor(PdfTokenWriter writer, Color color)
    {
        writer.WriteNumber(color.Red / 255.0);
        writer.WriteNumber(color.Green / 255.0);
        writer.WriteNumber(color.Blue / 255.0);
    }

    /// <summary>A stable hash of the sampled colour function, so equal gradients share one shading.</summary>
    private static string SamplesHash(IReadOnlyList<Color> samples)
    {
        var hash = 14695981039346656037UL;
        foreach (var sample in samples)
        {
            hash = Mix(hash, sample.Red);
            hash = Mix(hash, sample.Green);
            hash = Mix(hash, sample.Blue);
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);

        static ulong Mix(ulong value, byte next) => (value ^ next) * 1099511628211UL;
    }

    public string RegisterSvgPattern(SvgPattern pattern)
    {
        var numbers = new[] { pattern.X, pattern.Y, pattern.Width, pattern.Height }.Concat(pattern.Matrix)
            .Select(value => value.ToString("R", CultureInfo.InvariantCulture)).ToArray();
        var key = string.Join(",", numbers) + ":" + pattern.Content;
        if (_svgPatterns.TryGetValue(key, out var existing)) return existing;
        static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
        var bbox = $"{Number(pattern.X)} {Number(pattern.Y)} {Number(pattern.X + pattern.Width)} {Number(pattern.Y + pattern.Height)}";
        var matrix = string.Join(" ", pattern.Matrix.Select(Number));
        var objectNumber = _file.ReserveObject();
        _file.WriteStreamObject(objectNumber, System.Text.Encoding.ASCII.GetBytes(pattern.Content),
            $"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [{bbox}] "
            + $"/XStep {Number(pattern.Width)} /YStep {Number(pattern.Height)} /Matrix [{matrix}] "
            + $"/Resources {_resourcesObject} 0 R");
        var name = _resources.AddPattern(key, objectNumber);
        _svgPatterns.Add(key, name);
        return name;
    }

    /// <summary>Writes a text piece; for synthetic bold it is additionally stroked.</summary>
    private static void WriteTextAt(
        PdfContentWriter content,
        PdfFontHandle font,
        string text,
        double fontSize,
        Color color,
        double x,
        double y,
        double characterSpacing,
        double horizontalScaling,
        IReadOnlyList<byte>? bidiLevels)
    {
        var shaped = font.Shape(text, bidiLevels);
        content.BeginText();
        content.SetFillColor(color);
        content.SetFont(font.ResourceName, fontSize);
        if (Math.Abs(characterSpacing) > 1e-9)
        {
            content.SetCharacterSpacing(characterSpacing);
        }

        if (Math.Abs(horizontalScaling - 100) > 1e-9)
        {
            content.SetHorizontalScaling(horizontalScaling);
        }

        if (font.SyntheticBold)
        {
            content.SetTextRenderMode(2);
            content.SetStrokeColor(color);
            content.SetLineWidth(fontSize * SyntheticBoldStrokeFactor);
        }

        content.MoveText(x, y);
        if (shaped is not null)
        {
            font.RecordShaped(text, shaped);
            content.ShowShapedGlyphs(shaped);
        }
        else if (font.IsEmbedded)
        {
            content.ShowHexText(font.Encode(text));
        }
        else
        {
            content.ShowText(font.Encode(text));
        }

        content.EndText();
    }

    /// <summary>
    /// Defers a drawing to the end of the page, so it lies in front of the text that follows it
    /// (OF-B06, an image in front of the text).
    /// </summary>
    public void DrawDeferred(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        _deferredDraws.Add(draw);
    }

    /// <summary>Records a link annotation for a drawn hyperlink (phase AC).</summary>
    /// <summary>
    /// Places the widget of a form field (ADR-0024) on the current page; the frame's bottom left
    /// corner is at x/y. Nothing happens while the layout is only measured.
    /// </summary>
    public void AddFormWidget(FormField field, double x, double y, double width, double height)
    {
        if (!_suppressAnnotations)
        {
            _widgets.Add((field, x, y, width, height));
        }
    }

    /// <summary>Whether the comments of the document are written as sticky notes (ADR-0026).</summary>
    public bool WriteComments { get; init; }

    /// <summary>
    /// Places the sticky note of a comment in the right margin of the current page, level with its anchor (top is
    /// the top of the anchor's line); notes of the same line stand side by side. The replies follow the text of the
    /// comment in the contents of the note.
    /// </summary>
    public void AddComment(Comment comment, double top)
    {
        const double Size = 18;
        var sameLine = _pageComments.Count(note => Math.Abs(note.Y - (top - Size)) < Size);
        var x = _pageWidth - Size - 4 - (sameLine * Size);
        var lines = comment.Paragraphs.Concat(comment.Replies.SelectMany(reply =>
            reply.Paragraphs.Select((paragraph, index) => index == 0 ? $"{reply.Author}: {paragraph}" : paragraph)));
        _pageComments.Add(new PdfAnnotation
        {
            Kind = PdfAnnotationKind.Text,
            X = Math.Clamp(x, 0, Math.Max(0, _pageWidth - Size)),
            Y = Math.Clamp(top - Size, 0, Math.Max(0, _pageHeight - Size)),
            Width = Size,
            Height = Size,
            Author = comment.Author,
            Content = string.Join('\n', lines),
            Icon = "Comment",
        });
    }

    public void AddLinkAnnotation(string target, double x1, double x2, double baseline, double fontSize)
    {
        if (_suppressAnnotations)
        {
            return;
        }

        var ascent = fontSize * 0.8;
        var descent = fontSize * 0.25;
        _annotations.Add((target, x1, baseline - descent, x2, baseline + ascent));
    }

    public IReadOnlyList<int> Finish()
    {
        if (_pageStarted)
        {
            FinishPage();
            FinishPendingFootnotes();
        }

        return _pageObjects;
    }

    /// <summary>Reserves a line at the page bottom and records the note text (phase Z, Z08).</summary>
    public void AddFootnote(int number, string text, int notesOnLine = 1, IReadOnlyList<string>? bookmarks = null)
    {
        var font = _fonts.GetFont(null, bold: false, italic: false);
        var lines = WrapFootnote(number, text, font, _contentWidth);
        var count = Math.Min(FootnoteLinesPerNote(notesOnLine), lines.Count);
        var names = bookmarks ?? [];
        AddFootnoteLines(number, lines.Take(count).ToArray(), continuation: false, names);
        if (count < lines.Count) _pendingFootnotes.AddLast((number, lines, count, names));
    }

    public bool HasPendingFootnotes => _pendingFootnotes.Count > 0;

    public double FootnoteHeight(int number, string text, int notesOnLine = 1)
        => Math.Min(FootnoteLinesPerNote(notesOnLine), WrapFootnote(number, text, _fonts.GetFont(null, bold: false, italic: false), _contentWidth).Count) * FootnoteLineHeight;

    // shortcut: footnotes reserve at most half a page per batch; use joint pagination for tighter packing.
    private int MaxFootnoteLines => Math.Max(1, (int)((_contentTop - _contentBottomBase) / (2 * FootnoteLineHeight)));
    private int FootnoteLinesPerNote(int count) => Math.Max(1, MaxFootnoteLines / count);

    private void ReservePendingFootnotes()
    {
        var remaining = Math.Max(0, MaxFootnoteLines - (int)Math.Round(_footnoteHeight / FootnoteLineHeight));
        while (remaining > 0 && _pendingFootnotes.First is { } first)
        {
            var note = first.Value;
            _pendingFootnotes.RemoveFirst();
            var count = Math.Min(remaining, note.Lines.Count - note.Offset);
            AddFootnoteLines(note.Number, note.Lines.Skip(note.Offset).Take(count).ToArray(), note.Offset > 0, note.Bookmarks);
            remaining -= count;
            if (note.Offset + count < note.Lines.Count)
            {
                _pendingFootnotes.AddFirst((note.Number, note.Lines, note.Offset + count, note.Bookmarks));
            }
        }
    }

    private void AddFootnoteLines(int number, IReadOnlyList<string> lines, bool continuation, IReadOnlyList<string> bookmarks)
    {
        _pageFootnotes.Add((number, lines, continuation, bookmarks));
        var height = lines.Count * FootnoteLineHeight;
        _footnoteHeight += height;
        _contentBottom += height;
    }

    private void FinishPendingFootnotes()
    {
        while (_pendingFootnotes.Count > 0)
        {
            StartPage();
            FinishPage();
        }
    }

    /// <summary>Wraps the footnote text to the content width, indenting continuation lines.</summary>
    private static IReadOnlyList<string> WrapFootnote(int number, string text, PdfFontHandle font, double available)
    {
        var indent = number.ToString(CultureInfo.InvariantCulture) + ". ";
        var indentWidth = font.Measure(indent, FootnoteFontSize);
        var limit = Math.Max(1, available - indentWidth);
        var spaceWidth = font.Measure(" ", FootnoteFontSize);
        var lines = new List<string>();
        var paragraphs = text.Split('\n');

        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            var paragraphLines = new List<string>();
            var current = new StringBuilder();
            var currentWidth = 0.0;

            foreach (var word in paragraphs[paragraphIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = font.Measure(word, FootnoteFontSize);
                if (current.Length > 0 && currentWidth + spaceWidth + wordWidth > limit)
                {
                    paragraphLines.Add(current.ToString());
                    current.Clear();
                    currentWidth = 0;
                }

                if (wordWidth > limit)
                {
                    var elements = StringInfo.GetTextElementEnumerator(word);
                    while (elements.MoveNext())
                    {
                        var element = elements.GetTextElement();
                        var elementWidth = font.Measure(element, FootnoteFontSize);
                        if (current.Length > 0 && currentWidth + elementWidth > limit)
                        {
                            paragraphLines.Add(current.ToString());
                            current.Clear();
                            currentWidth = 0;
                        }

                        current.Append(element);
                        currentWidth += elementWidth;
                    }

                    continue;
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                    currentWidth += spaceWidth;
                }

                current.Append(word);
                currentWidth += wordWidth;
            }

            paragraphLines.Add(current.ToString());
            if (paragraphIndex == 0 && paragraphLines.Count > 0)
            {
                paragraphLines[0] = indent + paragraphLines[0];
            }

            lines.AddRange(paragraphLines);
        }

        return lines;
    }

    private double FootnoteLineHeight
        => _fonts.GetFont(null, bold: false, italic: false).Metrics.LineHeightEm * FootnoteFontSize;

    private void DrawFootnotes()
    {
        if (_pageFootnotes.Count == 0)
        {
            return;
        }

        var font = _fonts.GetFont(null, bold: false, italic: false);
        var lineHeight = FootnoteLineHeight;
        var top = _contentBottom;

        _content.BeginArtifact();
        DrawLine(_contentLeft, top + 4, _contentLeft + _contentWidth / 3, top + 4, Color.Black, 0.5, TextUnderlineStyle.Single);

        var baseline = top - font.Metrics.AscentEm * FootnoteFontSize;
        foreach (var (number, lines, continuation, bookmarks) in _pageFootnotes)
        {
            if (!continuation && bookmarks.Count > 0)
            {
                NoteBookmark?.Invoke(bookmarks, baseline);
            }

            var continuationIndent = font.Measure(number.ToString(CultureInfo.InvariantCulture) + ". ", FootnoteFontSize);
            for (var index = 0; index < lines.Count; index++)
            {
                WriteLine(
                    font,
                    lines[index],
                    FootnoteFontSize,
                    Color.Black,
                    _contentLeft + (index == 0 && !continuation ? 0 : continuationIndent),
                    baseline);
                baseline -= lineHeight;
            }
        }

        _content.EndMarkedContent();
    }

    private void StartPage()
    {
        _content = new PdfContentWriter();
        _columnIndex = 0;
        _contentLeft = _fullContentLeft;
        _contentWidth = _columnWidth;
        _footnoteHeight = 0;
        _pageFootnotes.Clear();
        _deferredDraws.Clear();
        _annotations.Clear();
        Cancellation.ThrowIfCancellationRequested();
        if (_pageObjects.Count >= MaxPages)
        {
            throw new DocumentValidationException($"PDF: the document needs more than {MaxPages} pages (ResourceLimits.MaxPages).");
        }

        _pageNumber++;
        if (_lineNumbering is { Restart: LineNumberRestart.NewPage })
        {
            _lineCounter = 0;
        }

        _pageStarted = true;
        _pageObject = _file.ReserveObject();
        _structParents = _structure.BeginPage();
        DrawWatermark();
        DrawPictureWatermark();
        DrawPageFrame();
        DrawPageBorders();

        // The bands decide where the body starts and ends on this page (first/even variants
        // may differ in height).
        var (top, bottom) = DrawHeaderFooter();
        _contentTop = top;
        _contentBottomBase = bottom;
        _contentBottom = bottom;
        _cursorY = _contentTop;
        _bandLowest = _contentTop;
        ReservePendingFootnotes();
    }

    private void FinishPage()
    {
        _pageStarted = false;

        // Images in front of the text (OF-B06) are drawn last, so they cover the body text.
        foreach (var draw in _deferredDraws)
        {
            draw();
        }

        _deferredDraws.Clear();
        DrawFootnotes();
        var contentBytes = _content.ToArray();

        var contentObject = _file.ReserveObject();
        if (contentBytes.Length == 0)
        {
            _file.WriteStreamObject(contentObject, []);
        }
        else
        {
            _file.WriteStreamObject(contentObject, PdfStreamCompressor.Compress(contentBytes), "/Filter /FlateDecode");
        }

        var pageObject = _pageObject;

        // AC: link annotations for the hyperlinks drawn on this page; the first page also holds
        // the widget of the signature field (a widget belongs to the Annots of its page).
        var annotationObjects = new List<int>(_annotations.Count + 1);
        if (_pageObjects.Count == 0 && SignatureWidget is { } widget)
        {
            annotationObjects.Add(widget);
        }

        foreach (var (target, x1, y1, x2, y2) in _annotations)
        {
            var annotationObject = _file.ReserveObject();
            _file.WriteObject(annotationObject, tokens => tokens.WriteDictionary(annotation =>
            {
                annotation.WriteName("Type");
                annotation.WriteName("Annot");
                annotation.WriteName("Subtype");
                annotation.WriteName("Link");
                annotation.WriteName("Rect");
                annotation.WriteArray(rect =>
                {
                    rect.WriteNumber(x1);
                    rect.WriteNumber(y1);
                    rect.WriteNumber(x2);
                    rect.WriteNumber(y2);
                });
                annotation.WriteName("Border");
                annotation.WriteArray(border =>
                {
                    border.WriteInteger(0);
                    border.WriteInteger(0);
                    border.WriteInteger(0);
                });

                // Print flag: required for PDF/A and makes the link part of printed output.
                annotation.WriteName("F");
                annotation.WriteInteger(4);
                if (target.StartsWith('#') && target.Length > 1)
                {
                    // An internal target is a bookmark: a named destination of this document.
                    annotation.WriteName("Dest");
                    annotation.WriteLiteralString(target[1..]);
                }
                else
                {
                    annotation.WriteName("A");
                    annotation.WriteDictionary(action =>
                    {
                        action.WriteName("S");
                        action.WriteName("URI");
                        action.WriteName("URI");
                        action.WriteLiteralString(Encoding.ASCII.GetBytes(PdfTextString.ToAsciiUri(target)));
                    });
                }
            }));
            annotationObjects.Add(annotationObject);
        }

        // OF-F01: sticky notes, highlights, stamps and free text boxes of this page.
        foreach (var annotation in Annotations)
        {
            if (annotation.Page == _pageNumber)
            {
                annotationObjects.Add(WriteAnnotation(annotation));
            }
        }

        foreach (var comment in _pageComments)
        {
            annotationObjects.Add(WriteAnnotation(comment with { Page = _pageNumber }));
        }

        _pageComments.Clear();

        // ADR-0024: the widgets of the form fields of this page.
        foreach (var (field, x, y, width, height) in _widgets)
        {
            if (Forms?.WriteWidget(field, x, y, width, height, pageObject) is { } formWidget)
            {
                annotationObjects.Add(formWidget);
            }
        }

        _widgets.Clear();
        _file.WriteObject(pageObject, tokens => tokens.WriteDictionary(page =>
        {
            page.WriteName("Type");
            page.WriteName("Page");
            page.WriteName("Parent");
            page.WriteReference(_pagesObject);
            page.WriteName("Resources");
            page.WriteReference(_resourcesObject);
            page.WriteName("MediaBox");
            page.WriteArray(mediaBox =>
            {
                mediaBox.WriteInteger(0);
                mediaBox.WriteInteger(0);
                mediaBox.WriteNumber(_pageWidth);
                mediaBox.WriteNumber(_pageHeight);
            });
            page.WriteName("StructParents");
            page.WriteInteger(_structParents);
            page.WriteName("Contents");
            page.WriteReference(contentObject);

            if (annotationObjects.Count > 0)
            {
                page.WriteName("Annots");
                page.WriteArray(annots =>
                {
                    foreach (var annotation in annotationObjects)
                    {
                        annots.WriteReference(annotation);
                    }
                });
            }
        }));

        _pageObjects.Add(pageObject);
    }

    /// <summary>Writes one non-link annotation object and returns its object number (OF-F01).</summary>
    private int WriteAnnotation(PdfAnnotation annotation)
    {
        var objectNumber = _file.ReserveObject();
        var appearance = WriteAnnotationAppearances ? WriteAnnotationAppearance(annotation) : (int?)null;
        var subtype = annotation.Kind switch
        {
            PdfAnnotationKind.Highlight => "Highlight",
            PdfAnnotationKind.Stamp => "Stamp",
            PdfAnnotationKind.FreeText => "FreeText",
            _ => "Text",
        };

        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(annot =>
        {
            annot.WriteName("Type");
            annot.WriteName("Annot");
            annot.WriteName("Subtype");
            annot.WriteName(subtype);
            annot.WriteName("Rect");
            annot.WriteArray(rect =>
            {
                rect.WriteNumber(annotation.X);
                rect.WriteNumber(annotation.Y);
                rect.WriteNumber(annotation.X + annotation.Width);
                rect.WriteNumber(annotation.Y + annotation.Height);
            });

            // Print flag: the annotation belongs to the printed output (required for PDF/A).
            annot.WriteName("F");
            annot.WriteInteger(4);

            if (annotation.Color is { } color)
            {
                annot.WriteName("C");
                annot.WriteArray(components =>
                {
                    components.WriteNumber(color.Red / 255.0);
                    components.WriteNumber(color.Green / 255.0);
                    components.WriteNumber(color.Blue / 255.0);
                });
            }

            // Required by the annotation types: without them a viewer draws nothing.
            if (annotation.Kind == PdfAnnotationKind.Highlight)
            {
                var (left, bottom, right, top) = (annotation.X, annotation.Y, annotation.X + annotation.Width, annotation.Y + annotation.Height);
                annot.WriteName("QuadPoints");
                annot.WriteArray(points =>
                {
                    foreach (var value in new[] { left, top, right, top, left, bottom, right, bottom })
                    {
                        points.WriteNumber(value);
                    }
                });
            }
            else if (annotation.Kind == PdfAnnotationKind.FreeText)
            {
                annot.WriteName("DA");
                annot.WriteLiteralString("0 g /Helv 10 Tf");
            }

            if (appearance is { } normal)
            {
                annot.WriteName("AP");
                annot.WriteDictionary(entries =>
                {
                    entries.WriteName("N");
                    entries.WriteReference(normal);
                });
            }

            WriteAnnotationText(annot, "T", annotation.Author);
            WriteAnnotationText(annot, "Subj", annotation.Subject);
            WriteAnnotationText(annot, "Contents", annotation.Content);

            if (annotation.Kind is PdfAnnotationKind.Text or PdfAnnotationKind.Stamp
                && annotation.Icon is { Length: > 0 } icon)
            {
                annot.WriteName("Name");
                annot.WriteName(PdfAnnotationName(icon));
            }
        }));

        return objectNumber;
    }

    /// <summary>
    /// The appearance of an annotation as a form: a marked area, or a framed text in the font of the
    /// document. The bounding box clips what does not fit.
    /// </summary>
    private int WriteAnnotationAppearance(PdfAnnotation annotation)
    {
        var (width, height) = (annotation.Width, annotation.Height);
        var color = annotation.Color ?? new Color(255, 255, 0);
        var content = new PdfContentWriter();
        switch (annotation.Kind)
        {
            case PdfAnnotationKind.Highlight:
            case PdfAnnotationKind.Text:
                content.FillRectangle(0, 0, width, height, color);
                break;
            default:
                const double FontSize = 10;
                if (annotation.Kind == PdfAnnotationKind.Stamp)
                {
                    content.StrokeRectangle(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1), annotation.Color ?? Color.Black, 1);
                }

                // shortcut: only explicit line breaks, no wrapping; wrap with the block renderer when long texts are needed.
                var font = _fonts.GetFont(null, bold: false, italic: false);
                var y = height - (font.Metrics.AscentEm * FontSize) - 2;
                foreach (var line in (annotation.Content ?? annotation.Icon ?? string.Empty).Split('\n'))
                {
                    WriteLineTo(content, font, line.TrimEnd('\r'), FontSize, Color.Black, 3, y, 0, 100, null);
                    y -= font.Metrics.LineHeightEm * FontSize;
                }

                break;
        }

        // Multiply keeps the marked text readable.
        var multiply = annotation.Kind == PdfAnnotationKind.Highlight;
        var form = _file.ReserveObject();
        var box = string.Create(CultureInfo.InvariantCulture, $"/Type /XObject /Subtype /Form /BBox [0 0 {width:0.###} {height:0.###}] ");
        _file.WriteStreamObject(
            form,
            multiply ? [.. "/M gs\n"u8, .. content.ToArray()] : content.ToArray(),
            box + (multiply
                ? "/Resources << /ExtGState << /M << /Type /ExtGState /BM /Multiply >> >> >>"
                : string.Create(CultureInfo.InvariantCulture, $"/Resources {_resourcesObject} 0 R")));
        return form;
    }

    /// <summary>Writes a text entry of an annotation dictionary as a UTF-16BE string.</summary>
    private static void WriteAnnotationText(PdfTokenWriter tokens, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        tokens.WriteName(key);
        tokens.WriteHexString(PdfTextString.ToUtf16Be(value));
    }

    /// <summary>
    /// maps an icon or stamp name to a PDF name: names without blanks are written as they are,
    /// others as <c>#20</c> escaped names (for example <c>Draft#20Copy</c>).
    /// </summary>
    internal static string PdfAnnotationName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '+')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('#').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Draws the header and footer of the current page and returns the top and bottom of the
    /// body. As in Word, a band sits at <see cref="Typography.HeaderFooterDistance"/> from the
    /// sheet edge (at most the page margin) and the body begins at max(page margin, distance +
    /// band height); a band that grows beyond the margin pushes the body back.
    /// </summary>
    private (double Top, double Bottom) DrawHeaderFooter()
    {
        var marginTop = _section.Margins.Top.Points;
        var marginBottom = _section.Margins.Bottom.Points;
        var top = _pageHeight - marginTop;
        var bottom = marginBottom;
        var isFirst = _pageNumber == _sectionStartPage;
        var isEven = DisplayPageNumber % 2 == 0;

        var header = SelectBand(_header, _firstHeader, _evenHeader, isFirst, isEven);
        var footer = SelectBand(_footer, _firstFooter, _evenFooter, isFirst, isEven);
        var maxHeight = _pageHeight * MaxBandShare;

        if (header is { Blocks.Count: > 0 } headerBand)
        {
            var distance = Math.Min(Typography.HeaderFooterDistance.Points, marginTop);
            var bandTop = _pageHeight - distance;
            var height = RenderBand(headerBand, bandTop, maxHeight, measureOnly: false);
            top = Math.Min(top, bandTop - height);
        }

        if (footer is { Blocks.Count: > 0 } footerBand)
        {
            // The footer grows upward from its distance: measure first, then draw.
            var distance = Math.Min(Typography.HeaderFooterDistance.Points, marginBottom);
            var height = RenderBand(footerBand, distance + maxHeight, maxHeight, measureOnly: true);
            RenderBand(footerBand, distance + height, maxHeight, measureOnly: false);
            bottom = Math.Max(bottom, distance + height);
        }

        return (top, bottom);
    }

    /// <summary>
    /// Draws a band from <paramref name="top"/> downward (as an artifact, clipped to the maximum
    /// band height) and returns its height. <paramref name="measureOnly"/> draws into a
    /// discarded content stream.
    /// </summary>
    private double RenderBand(HeaderFooter band, double top, double maxHeight, bool measureOnly)
    {
        if (BandRenderer is not { } render)
        {
            return 0;
        }

        var content = _content;
        var (cursor, left, width, contentTop, contentBottom) = (_cursorY, _contentLeft, _contentWidth, _contentTop, _contentBottom);
        if (measureOnly)
        {
            _content = new PdfContentWriter();
        }

        _inBand = true;
        _suppressAnnotations = measureOnly;
        _bandMaxHeight = maxHeight;
        _cursorY = top;
        _contentTop = top;
        _contentBottom = double.NegativeInfinity;
        _contentLeft = _fullContentLeft;
        _contentWidth = _fullContentWidth;
        double height;
        try
        {
            _content.BeginArtifact();
            BeginClip(0, top - maxHeight, _pageWidth, maxHeight);
            height = render(band.Blocks);
            EndClip();
            _content.EndMarkedContent();
        }
        finally
        {
            _inBand = false;
            _suppressAnnotations = false;
            _content = content;
            (_cursorY, _contentLeft, _contentWidth, _contentTop, _contentBottom) = (cursor, left, width, contentTop, contentBottom);
        }

        if (height > maxHeight + 1e-6 && !measureOnly)
        {
            _decoration?.Handler.Report("PDF: a header or footer is taller than a third of the page and is cut off.");
        }

        return Math.Min(Math.Max(0, height), maxHeight);
    }

    private static HeaderFooter? SelectBand(HeaderFooter? defaultBand, HeaderFooter? firstBand, HeaderFooter? evenBand, bool isFirst, bool isEven)
    {
        // "First Page" applies only if a First variant exists (like w:titlePg);
        // even pages only if an Even variant exists.
        if (isFirst && firstBand is not null)
        {
            return firstBand;
        }

        if (isEven && evenBand is not null)
        {
            return evenBand;
        }

        return defaultBand;
    }

    /// <summary>Draws the watermark (phase S, S04) centered and rotated behind the content.</summary>
    private void DrawWatermark()
    {
        if (_decoration?.Template?.Watermark is not { } watermark || _decoration.WatermarkExtGState is not { } extGState)
        {
            return;
        }

        var font = _fonts.GetFont(null, bold: false, italic: false);
        if (!font.CanEncode(watermark.Text))
        {
            _decoration.Handler.Report("PDF: characters outside WinAnsi in the watermark are replaced with '?'.");
        }

        var size = watermark.FontSize.Points;
        var width = font.Measure(watermark.Text, size);
        var radians = watermark.Rotation * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // Decorative: marked as an artifact so that screen readers do not read it (phase Q).
        _content.BeginArtifact();
        _content.SaveState();
        _content.SetExtGState(extGState);
        _content.SetFillColor(watermark.Color);
        _content.ConcatMatrix(cos, sin, -sin, cos, _pageWidth / 2, _pageHeight / 2);
        _content.BeginText();
        _content.SetFont(font.ResourceName, size);
        _content.MoveText(-width / 2, -size * 0.35);
        _content.ShowText(font.Encode(watermark.Text));
        _content.EndText();
        _content.RestoreState();
        _content.EndMarkedContent();
    }

    /// <summary>
    /// Draws the picture watermark (OF-B09) centered behind the content. The image keeps
    /// its aspect ratio; without a given size it takes half of the page width.
    /// </summary>
    private void DrawPictureWatermark()
    {
        if (_decoration?.PictureWatermark is not { } watermark
            || _decoration.PictureWatermarkExtGState is not { } extGState)
        {
            return;
        }

        var (width, height) = WatermarkSize(watermark);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = (_pageWidth - width) / 2 + watermark.OffsetX;
        var y = (_pageHeight - height) / 2 + watermark.OffsetY;

        // Decorative: marked as an artifact so that screen readers do not read it (phase Q).
        _content.BeginArtifact();
        _content.SaveState();
        _content.SetExtGState(extGState);
        _content.DrawImage(watermark.ResourceName, width, height, x, y);
        _content.RestoreState();
        _content.EndMarkedContent();
    }

    /// <summary>
    /// computes the display size of the picture watermark: the requested size wins, a single
    /// requested side keeps the aspect ratio of the image, without a size the watermark takes
    /// half of the page width.
    /// </summary>
    private (double Width, double Height) WatermarkSize(PdfPictureWatermark watermark)
    {
        var naturalWidth = watermark.NaturalWidth;
        var naturalHeight = watermark.NaturalHeight;
        if (naturalWidth <= 0 || naturalHeight <= 0)
        {
            return (0, 0);
        }

        var ratio = naturalHeight / naturalWidth;

        if (watermark.Width is { } width && watermark.Height is { } height)
        {
            // Both sides are given: the image is scaled into the box without distortion.
            var scale = Math.Min(width.Points / naturalWidth, height.Points / naturalHeight);
            var scaledWidth = naturalWidth * scale;
            var scaledHeight = naturalHeight * scale;
            return (scaledWidth, scaledHeight);
        }

        if (watermark.Width is { } onlyWidth)
        {
            var scaledWidth = onlyWidth.Points;
            return (scaledWidth, scaledWidth * ratio);
        }

        if (watermark.Height is { } onlyHeight)
        {
            var scaledHeight = onlyHeight.Points;
            return (scaledHeight / ratio, scaledHeight);
        }

        var half = _pageWidth / 2;
        return (half, half * ratio);
    }

    /// <summary>Draws the page frame (phase S, S04) at the paper edge.</summary>
    private void DrawPageFrame()
    {
        if (_decoration?.Template?.PageFrame is not { } frame)
        {
            return;
        }

        var inset = frame.Inset.Points;
        var width = _pageWidth - 2 * inset;
        var height = _pageHeight - 2 * inset;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _content.BeginArtifact();
        _content.StrokeRectangle(inset, inset, width, height, frame.Color, frame.Width.Points);
        _content.EndMarkedContent();
    }

    /// <summary>
    /// Draws the page border of the section (phase AJ, AJ05) around the text area. The border
    /// is offset from the text by the offset of the borders.
    /// </summary>
    private void DrawPageBorders()
    {
        if (_pageBorders is not { } borders || borders.IsEmpty)
        {
            return;
        }

        var offset = (borders.Offset ?? Length.FromPoints(24)).Points;
        var left = _section.Margins.Left.Points - offset;
        var right = _pageWidth - _section.Margins.Right.Points + offset;
        var bottom = _section.Margins.Bottom.Points - offset;
        var top = _pageHeight - _section.Margins.Top.Points + offset;
        if (right <= left || top <= bottom)
        {
            return;
        }

        _content.BeginArtifact();
        if (borders.Bottom is { } bottomBorder)
        {
            _content.StrokeLine(left, bottom, right, bottom, bottomBorder.Color, bottomBorder.Width.Points, UnderlineStyle(bottomBorder.Style));
        }

        if (borders.Top is { } topBorder)
        {
            _content.StrokeLine(left, top, right, top, topBorder.Color, topBorder.Width.Points, UnderlineStyle(topBorder.Style));
        }

        if (borders.Left is { } leftBorder)
        {
            _content.StrokeLine(left, bottom, left, top, leftBorder.Color, leftBorder.Width.Points, UnderlineStyle(leftBorder.Style));
        }

        if (borders.Right is { } rightBorder)
        {
            _content.StrokeLine(right, bottom, right, top, rightBorder.Color, rightBorder.Width.Points, UnderlineStyle(rightBorder.Style));
        }

        _content.EndMarkedContent();
    }

    /// <summary>The line style of a border.</summary>
    private static TextUnderlineStyle UnderlineStyle(BorderStyle style) => style switch
    {
        BorderStyle.None => TextUnderlineStyle.None,
        BorderStyle.Double => TextUnderlineStyle.Double,
        BorderStyle.Dotted => TextUnderlineStyle.Dotted,
        BorderStyle.Dashed => TextUnderlineStyle.Dashed,
        _ => TextUnderlineStyle.Single,
    };
}
