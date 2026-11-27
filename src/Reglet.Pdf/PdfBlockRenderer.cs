using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// renders the block elements of a section into the page content streams: paragraphs with
/// breaks and alignment, headings, lists, tables and page breaks.
/// </summary>
internal sealed class PdfBlockRenderer
{
    /// <summary>Text indent per list level; the marker hangs <see cref="ListHanging"/> before the text (Word: w:ind left 720, hanging 360).</summary>
    private const double ListIndent = 36;

    private const double ListHanging = 18;

    /// <summary>Baseline shift superscript, relative to the font size.</summary>
    private const double SuperscriptShiftFactor = 0.33;

    /// <summary>Baseline shift subscript, relative to the font size.</summary>
    private const double SubscriptShiftFactor = 0.15;

    /// <summary>Font size for lowercase letters in SmallCaps (relative).</summary>
    private const double SmallCapsSizeFactor = 0.8;

    /// <summary>Height of the strikethrough line above the baseline, relative to the font size.</summary>
    private const double StrikethroughPositionFactor = 0.3;

    /// <summary>
    /// distance of the underline below the baseline, relative to the font size
    /// (PostScript-AFM: UnderlinePosition −100/1000).
    /// </summary>
    private const double UnderlinePositionFactor = 0.1;

    /// <summary>
    /// stroke width of the underline relative to the font size
    /// (PostScript-AFM: UnderlineThickness 50/1000).
    /// </summary>
    private const double UnderlineThicknessFactor = 0.05;

    private readonly PdfPageRenderer _page;
    private readonly PdfImageManager _images;
    private readonly PdfOutline _outline;
    private readonly IReadOnlyList<(int Level, string Text)> _headings;
    private readonly IReadOnlyList<int>? _tocPages;
    private readonly List<int>? _collectedPages;
    private readonly PdfFieldContext _fields;
    private readonly HyphenationPatterns? _hyphenation;
    private readonly bool _band;
    private readonly bool _applyDefaults;
    private int _headingIndex;
    private readonly SortedDictionary<int, (int Level, string Title, int Page, double Y)> _outlineHeadings = [];
    private bool _repeatingTableHeader;
    private readonly UnsupportedFeatureHandler _handler;
    private readonly ParagraphSpacing _spacing = new();
    private DocumentDefaults _defaults = DocumentDefaults.Default;
    private readonly NumberingLookup _numbering;
    private IReadOnlyList<CharacterStyle> _characterStyles = [];
    private IReadOnlyList<ParagraphStyle> _paragraphStyles = [];
    private IReadOnlyList<ParagraphStyle> _sourceStyles = [];
    private int _footnoteNumber;
    private int _endnoteNumber;
    private readonly List<(int Number, string Text, IReadOnlyList<string> Bookmarks)> _endnotes = [];
    private readonly Dictionary<int, IReadOnlyList<string>> _noteBookmarks = [];
    private readonly List<FloatZone> _floatZones = [];

    /// <summary>
    /// the last number written per numbering definition and level: lists that share a definition
    /// continue each other (OF-B02), also across pages and sections.
    /// </summary>
    private readonly Dictionary<(string Name, int Level), int> _listNumbers = [];

    /// <summary>
    /// An exclusion zone of a floating image. A <see cref="Contour"/> limits it to the visible part of
    /// the image, so a through wrap keeps the text clear of the shape only (OF-B06).
    /// </summary>
    private sealed record FloatZone(
        double Left,
        double Right,
        double Top,
        double Bottom,
        bool LeftSide,
        int Page,
        ImageContour? Contour = null)
    {
        /// <summary>The occupied extents in the vertical band [<paramref name="top"/>, <paramref name="bottom"/>].</summary>
        public (double Left, double Right) Extents(double top, double bottom)
        {
            if (Contour is not { } contour)
            {
                return (Left, Right);
            }

            // The page grows downwards: Top is above Bottom.
            var height = Top - Bottom;
            if (height <= 0)
            {
                return (Left, Right);
            }

            var (left, right) = contour.Extents((Top - top) / height, (Top - bottom) / height);
            if (right <= left)
            {
                return (double.PositiveInfinity, double.NegativeInfinity); // no visible image here
            }

            return (Left + (left * (Right - Left)), Left + (right * (Right - Left)));
        }
    }

    /// <summary>The space after of the last rendered paragraph (not part of a band's height).</summary>
    private double _trailingSpace;

    /// <summary>Space after the paragraph just drawn; the next paragraph takes the larger of it and its own space before (Word and LibreOffice).</summary>
    private double _lastAfter;

    private double _carriedAfter;

    private static readonly ParagraphBorder DefaultTableBorder =
        new(BorderStyle.Single, Typography.TableBorderWidth, Color.Black);

    public PdfBlockRenderer(
        PdfPageRenderer page,
        PdfImageManager images,
        UnsupportedFeatureHandler handler,
        PdfOutline outline,
        IReadOnlyList<(int Level, string Text)> headings,
        NumberingLookup numbering,
        IReadOnlyList<int>? tocPages = null,
        List<int>? collectedPages = null,
        PdfFieldContext? fields = null,
        HyphenationPatterns? hyphenation = null,
        bool band = false,
        bool applyDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(numbering);
        _page = page;
        _numbering = numbering;
        _images = images;
        _handler = handler;
        _outline = outline;
        _headings = headings;
        _tocPages = tocPages;
        _collectedPages = collectedPages;
        _fields = fields ?? new PdfFieldContext(new DocumentMetadata(), null, null);
        _hyphenation = hyphenation;
        _band = band;
        _applyDefaults = applyDefaults;
        page.NoteBookmark = (names, baseline) =>
        {
            foreach (var name in names)
            {
                _outline.AddBookmark(name, _page.CurrentPageObject, baseline);
                _fields.RecordBookmark(name, _page.DisplayPageNumber);
            }
        };
    }

    public void Render(
        IReadOnlyList<Block> blocks,
        DocumentDefaults defaults,
        IReadOnlyList<CharacterStyle> characterStyles,
        IReadOnlyList<ParagraphStyle> paragraphStyles,
        IReadOnlyList<ParagraphStyle> sourceStyles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(defaults);

        _defaults = defaults;
        _characterStyles = characterStyles;
        _paragraphStyles = paragraphStyles;
        _sourceStyles = sourceStyles;
        for (var index = 0; index < blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _carriedAfter = _lastAfter;
            _lastAfter = 0;
            RenderBlock(blocks[index], indent: 0, listLevel: 0, index + 1 < blocks.Count ? blocks[index + 1] : null);
            if (blocks[index] is not (Paragraph or Heading))
            {
                _lastAfter = 0;
            }
        }
    }

    /// <summary>
    /// Renders the blocks of a header or footer at the cursor (the page renderer is in band mode)
    /// and returns the height they occupy, without the space after the last paragraph.
    /// </summary>
    public double RenderBand(
        IReadOnlyList<Block> blocks,
        DocumentDefaults defaults,
        IReadOnlyList<ParagraphStyle> paragraphStyles,
        IReadOnlyList<ParagraphStyle> sourceStyles)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(defaults);

        _defaults = defaults;
        _paragraphStyles = paragraphStyles;
        _sourceStyles = sourceStyles;
        _floatZones.Clear();
        var top = _page.CursorY;
        for (var index = 0; index < blocks.Count; index++)
        {
            // The band is clipped there: the blocks below it would be laid out on every page for nothing.
            if (top - _page.CursorY > _page.BandMaxHeight)
            {
                break;
            }

            _trailingSpace = 0;
            _carriedAfter = _lastAfter;
            _lastAfter = 0;
            RenderBlock(blocks[index], indent: 0, listLevel: 0, index + 1 < blocks.Count ? blocks[index + 1] : null);
            if (blocks[index] is not (Paragraph or Heading))
            {
                _lastAfter = 0;
            }
        }

        // A floating image (a logo beside the address) can reach below the last text line.
        var bottom = _page.CursorY + _trailingSpace;
        if (_floatZones.Count > 0)
        {
            bottom = Math.Min(bottom, _floatZones.Min(zone => zone.Bottom));
            _floatZones.Clear();
        }

        return top - bottom;
    }

    /// <summary>
    /// DOCX and ODT carry a floating object in a paragraph of its own, which Word and LibreOffice lay out as an empty
    /// line; the PDF takes the same room, so the text after it lines up with theirs.
    /// </summary>
    private void RenderAnchorParagraph(ImageWrap wrap, double indent, int listLevel, Block? next)
    {
        if (wrap != ImageWrap.None)
        {
            RenderBlock(new Paragraph(Array.Empty<Inline>()), indent, listLevel, next);
        }
    }

    private void RenderBlock(Block block, double indent, int listLevel, Block? next = null)
    {
        if (_band && block is PageBreak or ColumnBreak or TableOfContents or TableOfFigures or AlphabeticalIndex)
        {
            _handler.Report($"PDF: {block.GetType().Name} in a header or footer is not rendered.");
            return;
        }

        switch (block)
        {
            case Paragraph paragraph:
                RenderParagraph(
                    StyleOf(paragraph), paragraph.Inlines, headingSize: null, indent,
                    compact: _spacing.IsCompact(paragraph),
                    keepWithNext: StyleOf(paragraph).KeepWithNext == true ? FirstLineHeight(next) : 0);
                break;
            case Heading heading:
                RenderHeading(heading, indent, keepWithNext: FirstLineHeight(next));
                break;
            case ListBlock list:
                RenderList(list, listLevel);
                break;
            case TableBlock table:
                RenderTable(table);
                break;
            case PageBreak:
                _page.NewPageByBreak();
                break;
            case ColumnBreak:
                _page.NextColumnByBreak();
                break;
            case ImageBlock image:
                RenderImage(image, indent);
                if (image.TakesAnchorLine)
                {
                    RenderAnchorParagraph(image.Wrap, indent, listLevel, next);
                }

                break;
            case CanvasBlock { Drawing: PdfCanvasPage canvasPage }:
                canvasPage.Render(_page, _images);
                break;
            case EmbeddedObject embedded:
                if (PreviewImage(embedded) is { } previewImage)
                {
                    RenderImage(previewImage, indent);
                }

                break;
            case TableOfContents toc:
                RenderTableOfContents(toc);
                break;
            case TableOfFigures figures:
                RenderTableOfFigures(figures);
                break;
            case AlphabeticalIndex index:
                RenderAlphabeticalIndex(index);
                break;
            case DrawingShape shape:
                RenderShape(shape, indent);
                RenderAnchorParagraph(shape.Wrap, indent, listLevel, next);
                break;
            case FormField field:
                RenderFormField(field, indent);
                break;
            case Chart chart:
                RenderChart(chart, indent);
                break;
            case Diagram diagram:
                RenderDiagram(diagram, indent);
                break;
            case Equation equation:
                RenderEquation(equation, indent);
                break;
        }
    }

    /// <summary>A display equation (ADR-0031): laid out with the document font, scaled down to fit, aligned (centred by default).</summary>
    private void RenderEquation(Equation equation, double indent)
    {
        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var box = _page.MathLayoutFor().Layout(equation.Root, _defaults.FontSize.Points);
        var available = Math.Max(1, _page.ContentWidth - indent);
        var scale = Math.Min(1, Math.Min(available / Math.Max(1, box.Width), Math.Max(1, _page.ContentHeight - spacing) / Math.Max(1, box.Height)));
        var width = box.Width * scale;
        var x = _page.ContentLeft + indent + equation.Alignment switch
        {
            TextAlignment.Left => 0,
            TextAlignment.Right => available - width,
            _ => (available - width) / 2,
        };
        ReportMissingMathGlyphs(box);

        _page.EnsureSpace(box.Height * scale + spacing);
        _page.BeginStructure("Formula", equation.AltText);
        _page.DrawMath(box, x, _page.CursorY - box.Ascent * scale, scale);
        _page.EndStructure();
        _page.Advance(box.Height * scale + spacing);
    }

    /// <summary>
    /// A diagram (ADR-0030) in the text flow: its shapes at their positions in a reserved area, scaled down
    /// to fit the page, text included.
    /// </summary>
    private void RenderDiagram(Diagram diagram, double indent)
    {
        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(Math.Max(1, _page.ContentWidth - indent) / diagram.Width.Points,
            Math.Max(1, _page.ContentHeight - spacing) / diagram.Height.Points));
        var height = diagram.Height.Points * scale;
        _page.EnsureSpace(height + spacing);
        var left = _page.ContentLeft + indent;
        var top = _page.CursorY;
        _page.BeginStructure("Figure", diagram.AltText);
        DrawDiagramShapes(diagram, scale, left, top);
        _page.EndStructure();
        _page.Advance(height + spacing);
    }

    /// <summary>The shapes of a diagram at their positions below the top-left corner, scaled (text included).</summary>
    private void DrawDiagramShapes(Diagram diagram, double scale, double left, double top)
    {
        foreach (var placed in DiagramLayout.Scale(diagram.Shapes, scale))
        {
            var width = placed.Shape.Width.Points;
            var shapeHeight = placed.Shape.Height.Points;
            var x = left + placed.X.Points;
            var y = top - placed.Y.Points - shapeHeight;
            _page.DrawShape(placed.Shape, width, shapeHeight, x, y);
            if (placed.Shape.Content is { } content)
            {
                DrawTextBoxContent(content, width, shapeHeight, x, y, 4 * scale);
            }
        }
    }

    private void ReportMissingMathGlyphs(MathBox box)
    {
        foreach (var glyphs in box.Items.OfType<MathGlyphs>())
        {
            if (!_page.Fonts.GetFont(null, bold: false, glyphs.Italic).CanEncode(glyphs.Text))
            {
                _handler.Report($"PDF: the equation text '{glyphs.Text}' has characters the document font does not have; embed a font that has them (PdfWriteOptions.Fonts).");
            }
        }
    }

    /// <summary>The preview of an embedded object as an image of the object's size; null (reported) without a preview.</summary>
    private ImageBlock? PreviewImage(EmbeddedObject embedded)
    {
        if (embedded.Preview is not { } preview)
        {
            _handler.Report("PDF: an embedded object without a preview is not drawn.");
            return null;
        }

        return new ImageBlock(preview.RawData, preview.MediaType)
        {
            Width = embedded.Width,
            Height = embedded.Height,
            AltText = embedded.ProgId.Length > 0 ? embedded.ProgId : null,
        };
    }

    /// <summary>Renders the collected endnotes at the end of the document (phase Z, Z08).</summary>
    public void RenderEndnotes()
    {
        if (_endnotes.Count == 0)
        {
            return;
        }

        _page.NewPage();
        RenderParagraph(ParagraphStyle.Default, new Inline[] { new TextRun("Endnotes") }, null, indent: 0);
        foreach (var (number, text, bookmarks) in _endnotes)
        {
            var parts = text.Split('\n');
            for (var index = 0; index < parts.Length; index++)
            {
                var label = index == 0
                    ? number.ToString(CultureInfo.InvariantCulture) + ". " + parts[index]
                    : parts[index];
                var inlines = new List<Inline>();
                if (index == 0)
                {
                    foreach (var bookmark in bookmarks)
                    {
                        inlines.Add(new Bookmark(bookmark));
                    }
                }

                inlines.Add(new TextRun(label));
                RenderParagraph(
                    ParagraphStyle.Default,
                    inlines,
                    null,
                    indent: 0,
                    compact: true);
            }
        }
    }

    /// <summary>Places a floating image beside the text (Square wrap) and records the exclusion zone.</summary>
    private void RenderFloatingImage(ImageBlock image, double imageWidth, double imageHeight, Action<double, double, double, double> draw)
        => RenderFloating(
            image.Wrap, FloatPlacement.Of(image), image.AltText, image.Crop,
            () => image.Wrap == ImageWrap.Through ? ImageContour.From(image.RawData.ToArray(), image.MediaType) : null,
            imageWidth, imageHeight, draw);

    private void RenderFloating(
        ImageWrap wrap, FloatPlacement placement, string? altText, ImageCrop? crop, Func<ImageContour?> contourOf,
        double imageWidth, double imageHeight, Action<double, double, double, double> draw,
        string tag = "Figure")
    {
        var spacing = 6.0;
        var scale = Math.Min(1, Math.Min(_page.ContentWidth / imageWidth, Math.Max(1, _page.ContentHeight - spacing) / imageHeight));
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var offsetY = placement.OffsetY?.Points ?? 0;
        var box = _page.PageBox;

        // R5: a page- or margin-relative position does not depend on the paragraph that carries it.
        if (!placement.IsPageRelative)
        {
            _page.EnsureSpace(Math.Min(height + Math.Max(0, offsetY), _page.ContentHeight));
        }

        var (left, right) = placement.HorizontalRelativeTo switch
        {
            HorizontalAnchor.Page => (0.0, box.Width),
            HorizontalAnchor.Margin => (box.MarginLeft, box.MarginRight),
            _ => (_page.ContentLeft, _page.ContentLeft + _page.ContentWidth),
        };
        var x = placement.HorizontalAlignment switch
        {
            HorizontalFloatAlignment.Left => left,
            HorizontalFloatAlignment.Center => (left + right - width) / 2,
            HorizontalFloatAlignment.Right => right - width,
            _ => left + (placement.OffsetX?.Points ?? 0),
        };
        var (upper, lower) = placement.VerticalRelativeTo switch
        {
            VerticalAnchor.Page => (box.Height, 0.0),
            VerticalAnchor.Margin => (box.MarginTop, box.MarginBottom),
            _ => (_page.CursorY, _page.CursorY - height),
        };
        var top = placement.VerticalAlignment switch
        {
            VerticalFloatAlignment.Top => upper,
            VerticalFloatAlignment.Center => (upper + lower + height) / 2,
            VerticalFloatAlignment.Bottom => lower + height,
            _ => upper - offsetY,
        };
        var y = top - height;

        void Draw()
        {
            _page.BeginStructure(tag, altText);
            DrawCropped(crop, width, height, x, y, draw);
            _page.EndStructure();
        }

        // OF-B06: an image in front of the text is drawn at the end of the page, one behind the text
        // right here; the text is not moved for through, behind and in front.
        if (wrap == ImageWrap.InFront)
        {
            _page.DrawDeferred(Draw);
        }
        else
        {
            Draw();
        }

        switch (wrap)
        {
            case ImageWrap.Square:
            case ImageWrap.Through:
            {
                // A through wrap reserves only the visible shape; the image itself is unchanged.
                var contour = contourOf();
                var leftSpace = x - _page.ContentLeft;
                var rightSpace = _page.ContentLeft + _page.ContentWidth - x - width;
                _floatZones.Add(new FloatZone(x, x + width, top, y, rightSpace >= leftSpace, _page.PageNumber, contour));
                break;
            }

            // A page-relative object moves the text only when it covers the cursor. The anchor paragraph's empty
            // line follows right below the object (RenderAnchorParagraph), as in Word and LibreOffice.
            case ImageWrap.TopAndBottom when !placement.IsPageRelative || (_page.CursorY <= top && _page.CursorY > y):
                _page.Advance(Math.Max(0, _page.CursorY - y));
                break;
        }
    }

    /// <summary>
    /// A shape or text box (ADR-0022), inline in the text flow or placed like a floating image. The
    /// text of a text box is laid out like a table cell of the box's size and clipped to it.
    /// </summary>
    private void RenderShape(DrawingShape shape, double indent)
    {
        string? tag = shape.Kind == ShapeKind.TextBox ? "Div" : null;
        void Draw(double width, double height, double x, double y)
        {
            if (shape.Rotation != 0)
            {
                _page.BeginRotation(x + width / 2, y + height / 2, shape.Rotation);
            }

            _page.DrawShape(shape, width, height, x, y);
            if (shape.Content is { } content)
            {
                DrawTextBoxContent(content, width, height, x, y);
            }

            if (shape.Rotation != 0)
            {
                _page.EndRotation();
            }
        }

        if (shape.Wrap != ImageWrap.None)
        {
            RenderFloating(
                shape.Wrap, FloatPlacement.Of(shape), shape.AltText, null, () => null,
                shape.Width.Points, Math.Max(shape.Height.Points, 1), Draw, tag ?? "Figure");
            return;
        }

        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(Math.Max(1, _page.ContentWidth - indent) / Math.Max(1, shape.Width.Points),
            Math.Max(1, _page.ContentHeight - spacing) / Math.Max(1, shape.Height.Points)));
        var boxWidth = shape.Width.Points * scale;
        var boxHeight = shape.Height.Points * scale;
        _page.EnsureSpace(boxHeight + spacing);
        var y = _page.CursorY - boxHeight;
        _page.BeginStructure(tag ?? "Figure", shape.AltText);
        Draw(boxWidth, boxHeight, _page.ContentLeft + indent, y);
        _page.EndStructure();
        _page.Advance(boxHeight + spacing);
    }

    /// <summary>A chart (ADR-0025) in the text flow, scaled down to fit the page like an inline shape.</summary>
    private void RenderChart(Chart chart, double indent)
    {
        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(Math.Max(1, _page.ContentWidth - indent) / chart.Width.Points,
            Math.Max(1, _page.ContentHeight - spacing) / chart.Height.Points));
        var width = chart.Width.Points * scale;
        var height = chart.Height.Points * scale;
        _page.EnsureSpace(height + spacing);
        var y = _page.CursorY - height;
        _page.BeginStructure("Figure", chart.AltText ?? chart.Title);
        _page.DrawChart(chart, width, height, _page.ContentLeft + indent, y, _handler.Report);
        _page.EndStructure();
        _page.Advance(height + spacing);
    }

    /// <summary>A form field (ADR-0024) in the text flow: a widget in a box of its own size.</summary>
    private void RenderFormField(FormField field, double indent)
    {
        const double Spacing = 4;
        var width = Math.Min(field.Width.Points, Math.Max(1, _page.ContentWidth - indent));
        var height = Math.Min(field.Height.Points, Math.Max(1, _page.ContentHeight - Spacing));
        _page.EnsureSpace(height + Spacing);
        _page.AddFormWidget(field, _page.ContentLeft + indent, _page.CursorY - height, width, height);
        _page.Advance(height + Spacing);
    }

    /// <summary>
    /// Lays out the blocks of a text box as a one-cell table of the box's width and draws it with the
    /// frame's top left corner at x/y + height. Headings keep their look but are not part of the
    /// outline or the table of contents.
    /// </summary>
    private void DrawTextBoxContent(IReadOnlyList<Block> content, double width, double height, double x, double y, double padding = 4)
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new TableCell(content) { Padding = Length.FromPoints(padding) })
            {
                Height = Length.FromPoints(Math.Max(1, height)),
                HeightRule = TableRowHeightRule.Exactly,
            },
        })
        {
            ColumnWidths = [Length.FromPoints(width)],
            Border = new ParagraphBorder(BorderStyle.None),
        };
        var headingIndex = _headingIndex;
        var rows = LayoutTable(table, width, depth: 0);
        _headingIndex = headingIndex;
        if (rows.Count == 0)
        {
            return;
        }

        var cell = rows[0].Cells[0];
        for (var index = 0; index < cell.Lines.Count; index++)
        {
            cell.Lines[index] = cell.Lines[index] with { Heading = null };
        }

        _page.BeginContainer("Div");
        _page.BeginClip(x, y, width, height);
        DrawRowSlice(rows[0], x, y + height, height, [0], [cell.Lines.Count], atPageTop: false, tagged: true, "Div", default);
        _page.EndClip();
        _page.EndContainer();
    }

    /// <summary>Draws an image into a frame; a crop scales the kept region up to the frame and clips the rest.</summary>
    private void DrawCropped(ImageCrop? crop, double width, double height, double x, double y, Action<double, double, double, double> draw)
    {
        if (crop is not { IsEmpty: false } kept)
        {
            draw(width, height, x, y);
            return;
        }

        var scaledWidth = width / (1 - kept.Left - kept.Right);
        var scaledHeight = height / (1 - kept.Top - kept.Bottom);
        _page.BeginClip(x, y, width, height);
        draw(scaledWidth, scaledHeight, x - kept.Left * scaledWidth, y - kept.Bottom * scaledHeight);
        _page.EndClip();
    }

    /// <summary>The width to reserve beside an active floating image on the current line.</summary>
    private (double Left, double Right) WrapMargins()
    {
        // A zone ends where the image ends: only the cursor decides that, never a measured line.
        _floatZones.RemoveAll(zone => zone.Page != _page.PageNumber || _page.CursorY <= zone.Bottom);
        return WrapMarginsAt(_page.CursorY, _defaults.FontSize.Points * Typography.LineHeightFactor);
    }

    private (double Left, double Right) WrapMarginsAt(double top, double height)
    {
        const double spacing = 6;
        double left = 0, right = 0;
        foreach (var zone in _floatZones)
        {
            if (zone.Right <= _page.ContentLeft || zone.Left >= _page.ContentLeft + _page.ContentWidth
                || top <= zone.Bottom || top - height >= zone.Top)
            {
                continue;
            }

            // LeftSide means the text flows to the right of the zone: the insets count from the near edge.
            var (extentLeft, extentRight) = zone.Extents(top, top - Math.Max(0, height));
            if (zone.LeftSide) left = Math.Max(left, extentRight - _page.ContentLeft + spacing);
            else right = Math.Max(right, _page.ContentLeft + _page.ContentWidth - extentLeft + spacing);
        }
        return (left, right);
    }

    private void RenderTableOfContents(TableOfContents toc)
    {
        if (!string.IsNullOrEmpty(toc.Title))
        {
            RenderParagraph(ParagraphStyle.Default, new Inline[] { new TextRun(toc.Title!) }, null, indent: 0);
        }

        for (var index = 0; index < _headings.Count; index++)
        {
            var (level, text) = _headings[index];
            if (level > toc.MaxLevel)
            {
                continue;
            }

            var page = _tocPages is not null && index < _tocPages.Count
                ? _tocPages[index].ToString(CultureInfo.InvariantCulture)
                : "0";

            // Entry text, dot leader and the page number right-aligned at the content edge
            // (as Word and LibreOffice lay out a table of contents).
            var entryIndent = (level - 1) * 18.0;
            RenderParagraph(
                ParagraphStyle.Default with
                {
                    LeftIndent = Length.FromPoints(entryIndent),
                    TabStops = [new TabStop(Length.FromPoints(_page.ContentWidth), TabStopAlignment.Right, TabStopLeader.Dot)],
                },
                new Inline[] { new Hyperlink("#" + TocAnchor(index), [new TextRun(text + "\t" + page)]) },
                null,
                indent: 0,
                compact: true);
        }
    }

    /// <summary>
    /// A table of figures: the captions of the label with a dot leader and the page number of the
    /// previous layout pass, each linked to its caption.
    /// </summary>
    private void RenderTableOfFigures(TableOfFigures figures)
    {
        if (!string.IsNullOrEmpty(figures.Title))
        {
            RenderParagraph(ParagraphStyle.Default, new Inline[] { new TextRun(figures.Title!) }, null, indent: 0);
        }

        foreach (var (id, label, text) in _fields.Lists?.Captions ?? [])
        {
            if (!label.Equals(figures.Label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var anchor = PdfListMarks.CaptionAnchor(id);
            var page = _fields.BookmarkPage(anchor) is { } known ? known.ToString(CultureInfo.InvariantCulture) : "0";
            RenderParagraph(
                ParagraphStyle.Default with
                {
                    TabStops = [new TabStop(Length.FromPoints(_page.ContentWidth), TabStopAlignment.Right, TabStopLeader.Dot)],
                },
                new Inline[] { new Hyperlink("#" + anchor, [new TextRun(text + "\t" + page)]) },
                null,
                indent: 0,
                compact: true);
        }
    }

    /// <summary>
    /// The alphabetical index: a letter heading, then the entries sorted without regard to case and
    /// accents; a sub entry is indented below its main entry; each line lists the pages of the marks
    /// from the previous layout pass.
    /// </summary>
    private void RenderAlphabeticalIndex(AlphabeticalIndex index)
    {
        if (!string.IsNullOrEmpty(index.Title))
        {
            RenderParagraph(ParagraphStyle.Default, new Inline[] { new TextRun(index.Title!) }, null, indent: 0);
        }

        var marks = _fields.Lists?.Marks ?? [];
        string? group = null;
        string? previousMain = null;
        foreach (var (text, subText) in marks.Select(mark => (mark.Entry.Text, mark.Entry.SubText)).Distinct()
            .OrderBy(entry => FigureIndexScan.SortKey(entry.Text), StringComparer.Ordinal)
            .ThenBy(entry => entry.SubText is null ? string.Empty : FigureIndexScan.SortKey(entry.SubText), StringComparer.Ordinal)
            .ThenBy(entry => entry.Text, StringComparer.Ordinal)
            .ThenBy(entry => entry.SubText, StringComparer.Ordinal))
        {
            var letter = FigureIndexScan.GroupOf(text);
            if (letter != group)
            {
                group = letter;
                RenderParagraph(
                    ParagraphStyle.Default with { KeepWithNext = true },
                    new Inline[] { new TextRun(letter) { Bold = true } },
                    null,
                    indent: 0,
                    compact: true);
            }

            // The pages of the marks with exactly this entry, in ascending order.
            var own = marks.Where(mark => mark.Entry.Text == text && mark.Entry.SubText == subText).ToList();
            var pages = own
                .Select(mark => _fields.BookmarkPage(PdfListMarks.MarkAnchor(mark.Id)))
                .Select(page => page ?? 0)
                .Distinct()
                .Order()
                .Select(page => page.ToString(CultureInfo.InvariantCulture));
            var anchor = PdfListMarks.MarkAnchor(own[0].Id);

            if (subText is null || text != previousMain)
            {
                if (subText is not null)
                {
                    // A main entry that only has sub entries stands alone as their heading.
                    RenderParagraph(ParagraphStyle.Default, new Inline[] { new TextRun(text) }, null, indent: 0, compact: true);
                }
            }

            previousMain = text;
            var line = (subText ?? text) + ", " + string.Join(", ", pages);
            RenderParagraph(
                ParagraphStyle.Default with { LeftIndent = Length.FromPoints(subText is null ? 0 : 18) },
                new Inline[] { new Hyperlink("#" + anchor, [new TextRun(line)]) },
                null,
                indent: 0,
                compact: true);
        }
    }

    private static string TocAnchor(int index) => "_Toc" + index.ToString(CultureInfo.InvariantCulture);

    private void RenderImage(ImageBlock image, double indent = 0)
    {
        if (image.MediaType == ImageMediaType.Svg)
        {
            RenderVectorImage(image, indent);
            return;
        }

        var handle = _images.GetImage(image);
        if (image.Wrap != ImageWrap.None)
        {
            RenderFloatingImage(image, handle.Width, handle.Height,
                (width, height, x, y) => _page.DrawImage(handle.ResourceName, width, height, x, y));
            return;
        }

        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(Math.Max(1, _page.ContentWidth - indent) / handle.Width, (_page.ContentHeight - spacing) / handle.Height));
        var width = handle.Width * scale;
        var height = handle.Height * scale;
        _page.EnsureSpace(height + spacing);
        var y = _page.CursorY - height;
        _page.BeginStructure("Figure", image.AltText);
        DrawCropped(image.Crop, width, height, _page.ContentLeft + indent, y,
            (drawWidth, drawHeight, drawX, drawY) => _page.DrawImage(handle.ResourceName, drawWidth, drawHeight, drawX, drawY));
        _page.EndStructure();

        _page.Advance(height + spacing);
    }

    /// <summary>
    /// Draws an SVG image as vector graphics (phase AA, AA03). The document subset of the
    /// converter is used; unsupported elements are reported.
    /// </summary>
    private void RenderVectorImage(ImageBlock image, double indent = 0)
    {
        _images.Check(image);
        var drawing = SvgConverter.TryConvert(image.RawData, _page.AlphaState, out var warnings, _page.RenderSvgText, _page.RegisterSvgGradient, _page.RegisterSvgPattern, (bytes, type) => _page.RegisterSvgImage(bytes, type) is { } image ? new PdfImageHandle(image.ResourceName, image.Width, image.Height) : null,
            _page.RegisterForm,
            _page.RegisterMask,
            _page.RegisterAlphaMask);
        foreach (var warning in warnings)
        {
            _handler.Report(warning);
        }

        if (drawing is null)
        {
            _handler.Report("PDF: the SVG image could not be converted.");
            return;
        }

        var (width, height) = ImageLayout.Resolve(image);
        if (width.Points <= 0 || height.Points <= 0)
        {
            return;
        }

        if (image.Wrap != ImageWrap.None)
        {
            RenderFloatingImage(image, width.Points, height.Points,
                (drawWidth, drawHeight, x, y) => _page.DrawVector(drawing, drawWidth, drawHeight, x, y));
            return;
        }

        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(Math.Max(1, _page.ContentWidth - indent) / width.Points, (_page.ContentHeight - spacing) / height.Points));
        var boxWidth = width.Points * scale;
        var boxHeight = height.Points * scale;
        _page.EnsureSpace(boxHeight + spacing);
        var y = _page.CursorY - boxHeight;
        _page.BeginStructure("Figure", image.AltText);
        DrawCropped(image.Crop, boxWidth, boxHeight, _page.ContentLeft + indent, y,
            (drawWidth, drawHeight, drawX, drawY) => _page.DrawVector(drawing, drawWidth, drawHeight, drawX, drawY));
        _page.EndStructure();
        _page.Advance(boxHeight + spacing);
    }

    /// <summary>
    /// The effective style of a paragraph (ADR-0011): a named style edited after reading is
    /// resolved against its source, direct formatting wins.
    /// </summary>
    private ParagraphStyle StyleOf(Paragraph paragraph)
    {
        if (ParagraphStyleResolver.TryResolve(
                paragraph.Style,
                paragraph.SourceStyleBase,
                _paragraphStyles,
                _sourceStyles,
                out var effective,
                out _))
        {
            return effective;
        }

        // A paragraph without a kept named style inherits the document paragraph defaults. For a
        // document built from the model this is always applied; for a read document only when the
        // paragraph has no style reference (otherwise the reader already resolved the style and
        // the tri-state flags stay null to inherit).
        return _applyDefaults || paragraph.Style.StyleName is null
            ? ParagraphStyleResolver.ApplyDefaults(paragraph.Style, _defaults.ParagraphStyle)
            : paragraph.Style;
    }

    /// <summary>
    /// height that must be free after a heading so that it stays with the first line of the
    /// following block on one page (like keepNext in DOCX).
    /// </summary>
    private double FirstLineHeight(Block? next)
    {
        var textLine = DefaultLineHeight(_defaults.FontSize.Points);
        return next switch
        {
            Paragraph paragraph => (StyleOf(paragraph).SpaceBefore ?? Length.Zero).Points + textLine,
            Heading heading => (HeadingStyle(heading).SpaceBefore ?? Length.Zero).Points
                + DefaultLineHeight(Typography.HeadingSize(heading.Level).Points),
            ListBlock or TableBlock => textLine + 2 * Typography.TableCellPaddingVertical.Points,
            ImageBlock image => ImageHeight(image),
            _ => 0,
        };
    }

    /// <summary>Line height of the default font for a font size.</summary>
    private double DefaultLineHeight(double fontSize)
        => _page.Fonts.GetFont(null, bold: false, italic: false).Metrics.LineHeightEm * fontSize;

    private double ImageHeight(ImageBlock image)
    {
        var (width, height) = ImageLayout.Resolve(image);
        var spacing = Math.Min(6, _page.ContentHeight / 10);
        var scale = Math.Min(1, Math.Min(_page.ContentWidth / width.Points, (_page.ContentHeight - spacing) / height.Points));
        return height.Points * scale + spacing;
    }

    private ParagraphStyle HeadingStyle(Heading heading)
    {
        var style = heading.Style.SpaceBefore is null && heading.Style.SpaceAfter is null
            ? heading.Style with
            {
                SpaceBefore = Typography.HeadingSpaceBefore,
                SpaceAfter = Typography.HeadingSpaceAfter,
            }
            : heading.Style;
        return _applyDefaults ? ParagraphStyleResolver.ApplyDefaults(style, _defaults.ParagraphStyle) : style;
    }

    private void RenderHeading(Heading heading, double indent, ListMarker? marker = null, double keepWithNext = 0)
    {
        // In a document with a table of contents every heading gets a named destination that
        // its entry links to (the index follows the heading scan of the table of contents,
        // so the text is determined the same way).
        var text = HeadingScan.PlainText(heading.Inlines);
        string? anchor = null;
        int? headingIndex = null;
        if (!_band && text.Length > 0)
        {
            headingIndex = _headingIndex++;
            if (_tocPages is not null || _collectedPages is not null)
            {
                anchor = TocAnchor(headingIndex.Value);
            }
        }

        RenderParagraph(
            HeadingStyle(heading), heading.Inlines, Typography.HeadingSize(heading.Level).Points, indent, marker,
            keepWithNext: keepWithNext, role: "H" + ((int)heading.Level).ToString(CultureInfo.InvariantCulture),
            outlineHeading: headingIndex is { } outlineIndex ? ((int)heading.Level, text, outlineIndex) : null,
            anchor: anchor);

    }

    public void WriteOutlineEntries()
    {
        foreach (var (_, heading) in _outlineHeadings)
        {
            _outline.AddHeading(heading.Title, heading.Level, heading.Page, heading.Y);
        }
    }

    private void RecordHeadingPage(int index)
    {
        if (_collectedPages is null)
        {
            return;
        }

        while (_collectedPages.Count <= index)
        {
            _collectedPages.Add(0);
        }

        _collectedPages[index] = _page.PageNumber;
    }

    /// <summary>The bookmark targets declared by the blocks of a note, in document order.</summary>
    private static IReadOnlyList<string> NoteBookmarkNames(IReadOnlyList<Block> blocks)
    {
        var names = new List<string>();
        Blocks(blocks);
        return names;

        void Blocks(IReadOnlyList<Block> content)
        {
            foreach (var block in content)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        Inlines(paragraph.Inlines);
                        break;
                    case Heading heading:
                        Inlines(heading.Inlines);
                        break;
                    case ListBlock list:
                        foreach (var item in list.Items)
                        {
                            Blocks(item.Blocks);
                        }

                        break;
                    case TableBlock table:
                        foreach (var row in table.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                Blocks(cell.Blocks);
                            }
                        }

                        break;
                }
            }
        }

        void Inlines(IReadOnlyList<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Bookmark bookmark when !names.Contains(bookmark.Name):
                        names.Add(bookmark.Name);
                        break;
                    case Hyperlink hyperlink:
                        Inlines(hyperlink.Inlines);
                        break;
                    case Footnote note:
                        Blocks(note.Blocks);
                        break;
                }
            }
        }
    }

    /// <summary>Plain text of block content (for the footnote text).</summary>
    /// <summary>
    /// The text of blocks as lines (used where only text can be drawn: footnotes, nested tables
    /// drawn as text). Lists and tables are included recursively so that no text is lost.
    /// </summary>
    private string PlainText(IReadOnlyList<Block> blocks)
    {
        ReaderGuard.EnsureStack();
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    parts.Add(InlineText(paragraph.Inlines));
                    break;
                case Heading heading:
                    parts.Add(InlineText(heading.Inlines));
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        parts.Add("\u2022 " + PlainText(item.Blocks));
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        parts.Add(string.Join(" | ", row.Cells.Select(cell => PlainText(cell.Blocks).Replace('\n', ' '))));
                    }

                    break;
            }
        }

        return string.Join('\n', parts);

        string InlineText(IReadOnlyList<Inline> inlines)
        {
            var builder = new StringBuilder();
            AppendInlines(builder, inlines);
            return builder.ToString().Trim();
        }
    }

    private void AppendInlines(StringBuilder builder, IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run when RunFormatResolver.Resolve(run, null, RunFormatResolver.Find(_characterStyles, run.CharacterStyleName)).Hidden != true:
                    builder.Append(run.Text);
                    break;
                case Hyperlink hyperlink:
                    AppendInlines(builder, hyperlink.Inlines);
                    break;
                case DateField dateField:
                    builder.Append((dateField.Value ?? DateTimeOffset.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    private void RenderParagraph(
        ParagraphStyle style,
        IReadOnlyList<Inline> inlines,
        double? headingSize,
        double indent,
        ListMarker? marker = null,
        bool compact = false,
        double keepWithNext = 0,
        string role = "P",
        (int Level, string Title, int Index)? outlineHeading = null,
        string? anchor = null)
    {
        if (style.PageBreakBefore == true && !_page.IsAtPageTop)
        {
            _page.NewPageByBreak();
        }

        var atoms = BuildAtoms(inlines, headingSize, style);

        // An empty paragraph occupies a blank line as in Word, in the font of its paragraph mark.
        var (defaultFont, emptySize) = MarkFormat(headingSize, style);
        var fontSize = Measured(atoms) is { Count: > 0 } visible ? visible.Max(atom => atom.FontSize) : emptySize;

        // left, right and first-line indent limit the wrap width and shift
        // the start position (N06). The list indent is added from the nesting level.
        var leftIndent = style.LeftIndent?.Points ?? 0;
        var rightIndent = style.RightIndent?.Points ?? 0;
        var firstLineIndent = style.FirstLineIndent?.Points ?? 0;
        var (wrapLeft, wrapRight) = WrapMargins();
        if (wrapLeft + wrapRight > 0 && wrapLeft + wrapRight + indent + leftIndent + rightIndent >= _page.ContentWidth
            && _floatZones.Count > 0)
        {
            var bottom = _floatZones.Where(zone => zone.Right > _page.ContentLeft && zone.Left < _page.ContentLeft + _page.ContentWidth)
                .Min(zone => zone.Bottom);
            _page.Advance(Math.Max(0, _page.CursorY - bottom + 6));
            _floatZones.Clear();
            wrapLeft = 0;
            wrapRight = 0;
        }

        // Right to left the paragraph is laid out from the start edge and mirrored at the content
        // area (indents count from the right as for w:bidi); floats stay on their physical side.
        double? mirrorAxis = style.RightToLeft == true ? (2 * _page.ContentLeft) + _page.ContentWidth : null;
        if (mirrorAxis is not null)
        {
            (wrapLeft, wrapRight) = (wrapRight, wrapLeft);
        }

        double available = 0;
        double leftEdge = 0;
        double firstLineOffset = 0;
        double markerStart = 0;
        List<List<TextAtom>> lines = [];
        double[] lineHeights = [];
        List<(double Left, double Right)>? lineMargins = null;
        double Mirror(double left, double width) => mirrorAxis is { } axis ? axis - left - width : left;

        // AA01: lays the paragraph out at the position of the cursor. Every line takes the width
        // that the floating images of its own vertical band leave, so paragraphs with notes, keep
        // constraints, borders or a list marker wrap like plain ones. The call runs once for the
        // measurement and once at the position the paragraph is really drawn at.
        void LayoutAtPosition()
        {
            (wrapLeft, wrapRight) = WrapMargins();
            if (mirrorAxis is not null)
            {
                (wrapLeft, wrapRight) = (wrapRight, wrapLeft);
            }

            available = Math.Max(1, _page.ContentWidth - indent - leftIndent - rightIndent - wrapLeft - wrapRight);
            leftEdge = _page.ContentLeft + indent + leftIndent + wrapLeft;
            // A floating image on the first line's band also moves the list marker aside.
            markerStart = (marker?.X ?? 0) + wrapLeft;

            // The first line starts at its own indent and wraps at the width left from there. A
            // list marker wider than the hanging indent pushes it to the next default tab stop,
            // as the list tab does in Word and LibreOffice.
            firstLineOffset = firstLineIndent;
            if (marker is { } hanging)
            {
                var hangingFont = _page.Fonts.GetFont(null, bold: hanging.Bold, italic: false);
                var markerEnd = markerStart + hangingFont.Measure(hanging.Text, _defaults.FontSize.Points);
                var minGap = _defaults.FontSize.Points * MinTabGapEm;
                if (markerEnd + minGap > leftEdge + firstLineOffset)
                {
                    firstLineOffset = NextTab(null, _page.ContentLeft, markerEnd, minGap).X - leftEdge;
                }
            }

            var firstLineWidth = Math.Max(1, available - firstLineOffset);
            if (_floatZones.Count == 0)
            {
                lineMargins = null;
                lines = WrapAtoms(atoms, available, style.TabStops, firstLineWidth, tabOffset: indent + leftIndent, firstTabOffset: indent + leftIndent + firstLineOffset);
            }
            else
            {
                var plannedTop = _page.CursorY;
                var limit = _page.ContentBottom;
                var maximumHeight = atoms.Count == 0 ? ScaledLineHeight(Array.Empty<TextAtom>()) : ScaledLineHeight(atoms);
                lineMargins = [];
                lines = WrapAtoms(atoms, available, style.TabStops, firstLineWidth, WidthForLine, indent + leftIndent, indent + leftIndent + firstLineOffset);

                double WidthForLine(IReadOnlyList<List<TextAtom>> completed)
                {
                    while (lineMargins!.Count <= completed.Count)
                    {
                        if (lineMargins.Count > 0) plannedTop -= ScaledLineHeight(completed[lineMargins.Count - 1]);
                        var margins = plannedTop > limit ? WrapMarginsAt(plannedTop, maximumHeight) : (0.0, 0.0);
                        if (mirrorAxis is not null) margins = (margins.Item2, margins.Item1);
                        lineMargins.Add(margins);
                    }

                    var current = lineMargins[completed.Count];
                    var lineWidth = _page.ContentWidth - indent - leftIndent - rightIndent - current.Left - current.Right;
                    return Math.Max(1, lineWidth - (completed.Count == 0 ? firstLineOffset : 0));
                }
            }

            lineHeights = lines.Select(ScaledLineHeight).ToArray();
        }

        LayoutAtPosition();

        var carriedAfter = _carriedAfter;
        _carriedAfter = 0;
        var spaceAfter = ParagraphSpacing.SpaceAfter(style, compact);

        // With keepWithNext the whole paragraph stays with the first following line, provided
        // both even fit on one page.
        // Word (compatibility mode 15) keeps the space before at the top of a page that a section break or a hard page
        // break began, and drops it only at the top of a page the flow began.
        var spaceBefore = (style.SpaceBefore ?? Length.Zero).Points;
        // The gap between two paragraphs is the larger of the space after and the space before, not their sum.
        if (!_page.IsAtTopAfterBreak && !_page.IsAtTopOfContinuedPage)
        {
            spaceBefore = Math.Max(0, spaceBefore - carriedAfter);
        }
        var together = spaceBefore + lineHeights.Sum() + spaceAfter + keepWithNext;
        var keepBlockTogether = (keepWithNext > 0 || style.KeepTogether == true) && together <= _page.ContentHeight;
        var firstNoteCount = lines[0].Count(atom => atom.Note is not null);
        var firstNoteHeight = lines[0].Sum(atom => atom.Note is { } note ? _page.FootnoteHeight(note.Number, note.Text, firstNoteCount) : 0);
        if (firstNoteHeight > 0)
        {
            while (_page.HasPendingFootnotes) _page.NewPage();
        }
        _page.EnsureSpace((keepBlockTogether ? together : spaceBefore + lineHeights[0]) + firstNoteHeight);
        // A paragraph that starts at the top of a page does not move on for widow control: the space before it does not count.
        var startsAtPageTop = _page.IsAtPageTop;
        if (!_page.IsAtTopOfContinuedPage)
        {
            _page.Advance(spaceBefore);
        }

        // The final layout happens where the paragraph stands: a page or column change removed
        // the floating images of the previous page, and per-line widths may have changed.
        LayoutAtPosition();
        if (keepBlockTogether
            && lineHeights.Sum() + spaceAfter + keepWithNext > _page.CursorY - _page.ContentBottom)
        {
            _page.NewPage();
            LayoutAtPosition();
        }

        var boxTop = _page.CursorY;

        if (outlineHeading is { } heading && heading.Title.Length > 0 && !_band)
        {
            _outlineHeadings.Add(heading.Index, (heading.Level, heading.Title, _page.CurrentPageObject, boxTop));
            RecordHeadingPage(heading.Index);
        }

        if (anchor is not null)
        {
            _outline.AddBookmark(anchor, _page.CurrentPageObject, boxTop);
        }

        var firstLine = true;
        var structurePage = -1;
        var flowPage = _page.PageNumber;
        // The border box spans every line of the paragraph, also the lines that the floating
        // images made narrower (as in Word, where the box ignores the image and runs over it).
        var borderLeft = leftEdge;
        var borderRight = leftEdge + available;

        // Marked content and the border box end with the page or column: what follows starts afresh there.
        var segmentTop = boxTop;
        var segmentHeight = 0.0;
        void CloseSegment()
        {
            if (structurePage >= 0)
            {
                _page.EndStructure();
                structurePage = -1;
            }

            if (segmentHeight > 0 && style.Borders is { } borders)
            {
                var borderWidth = borderRight - borderLeft;
                DrawParagraphBorders(borders, Mirror(borderLeft, borderWidth), borderWidth, segmentTop, segmentHeight);
            }

            segmentHeight = 0;
        }

        // Widow and orphan control (ADR-0033, as Word and LibreOffice do by default): a paragraph that has to
        // split leaves at least two lines on each page; a short one moves as a whole.
        int PlanBreak(int start)
        {
            if (_band || lines.Count < 2 || lines.Skip(start).Any(row => row.Any(atom => atom.Note is not null)))
            {
                return -1;
            }

            var remaining = lines.Count - start;
            var used = 0.0;
            var fit = 0;
            for (var index = start; index < lines.Count; index++)
            {
                used += lineHeights[index];
                if (!_page.HasSpaceFor(used))
                {
                    break;
                }

                fit++;
            }

            if (fit >= remaining)
            {
                return -1;
            }

            var split = remaining - fit < 2 ? remaining - 2 : fit;
            if (start == 0 && split < 2)
            {
                split = 0;
            }

            if (split <= 0 && (start > 0 || _page.IsAtPageTop || startsAtPageTop))
            {
                return -1;
            }

            return start + split;
        }

        var breakAt = PlanBreak(0);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (lineIndex == breakAt)
            {
                CloseSegment();
                _page.NextColumn();
                breakAt = PlanBreak(lineIndex);
            }

            var line = lines[lineIndex];
            var lineHeight = lineHeights[lineIndex];
            var noteCount = line.Count(atom => atom.Note is not null);
            var noteHeight = line.Sum(atom => atom.Note is { } note ? _page.FootnoteHeight(note.Number, note.Text, noteCount) : 0);
            if ((noteHeight > 0 && _page.HasPendingFootnotes) || !_page.HasSpaceFor(lineHeight + noteHeight))
            {
                CloseSegment();
            }

            if (noteHeight > 0)
            {
                while (_page.HasPendingFootnotes) _page.NewPage();
            }
            _page.EnsureSpace(lineHeight + noteHeight);
            if (segmentHeight == 0)
            {
                segmentTop = _page.CursorY;
            }

            foreach (var atom in line)
            {
                if (atom.Note is { } note)
                {
                    _page.AddFootnote(note.Number, note.Text, noteCount, _noteBookmarks.GetValueOrDefault(note.Number));
                }
            }

            // tagged paragraph: on a page change the structure element is continued on the
            // new page (marked content must not span a page boundary).
            if (structurePage < 0)
            {
                _page.BeginStructure(role);
                structurePage = _page.PageNumber;
            }

            // A page change in the middle of a paragraph leaves its floating images on their
            // page: the remaining lines flow over the full width again.
            if (flowPage != _page.PageNumber && lineMargins is not null)
            {
                flowPage = _page.PageNumber;
                for (var rest = lineIndex; rest < lineMargins.Count; rest++)
                {
                    lineMargins[rest] = (0, 0);
                }

                // A line without a planned margin falls back to the paragraph's start margins, which belong to the old page.
                (wrapLeft, wrapRight) = (0, 0);
            }

            // The baseline lies at the ascender below the line start so that the
            // glyphs fill the line upward as in Word.
            var baseline = _page.CursorY - LineAscentOf(line);
            if (line.Count > 0)
            {
                // Line numbering (phase D): the number is drawn in the left margin.
                _page.WriteLineNumber(baseline, _defaults.FontSize.Points);
            }
            if (firstLine && marker is { } listMarker)
            {
                // AJ04: the number or the bullet may have its own color and weight.
                var markerFont = _page.Fonts.GetFont(null, bold: listMarker.Bold, italic: false);
                // Right to left the marker reads in the paragraph direction: "1." is shown as ".1" (as in Word/LibreOffice).
                var markerText = mirrorAxis is null ? listMarker.Text : VisualText(listMarker.Text, rightToLeft: true);
                var markerX = Mirror(markerStart, markerFont.Measure(markerText, _defaults.FontSize.Points));
                _page.WriteLine(markerFont, markerText, _defaults.FontSize.Points, listMarker.Color ?? _defaults.TextColor, markerX, baseline);
            }

            firstLine = false;
            var margins = lineMargins is not null && lineIndex < lineMargins.Count ? lineMargins[lineIndex] : (wrapLeft, wrapRight);
            // Computed from the current column: a paragraph may continue in the next text column (ADR-0033 finding).
            var currentLeft = _page.ContentLeft + indent + leftIndent + margins.Item1;
            var currentWidth = Math.Max(1, _page.ContentWidth - indent - leftIndent - rightIndent - margins.Item1 - margins.Item2);
            var lineStart = currentLeft + (lineIndex == 0 ? firstLineOffset : 0);
            var lineAvailable = Math.Max(1, currentWidth - (lineIndex == 0 ? firstLineOffset : 0));
            borderLeft = Math.Min(borderLeft, currentLeft);
            borderRight = Math.Max(borderRight, currentLeft + currentWidth);
            var currentMirrorAxis = lineMargins is not null && style.RightToLeft == true ? (2 * _page.ContentLeft) + _page.ContentWidth : mirrorAxis;
            if (style.Background is { } lineBackground)
            {
                // Line by line, so it stays on the page of its line; the small overlap avoids hairlines between lines.
                var overlap = lineIndex < lines.Count - 1 ? 0.25 : 0;
                var backgroundLeft = currentMirrorAxis is { } axis ? axis - currentLeft - currentWidth : currentLeft;
                _page.FillRectangle(backgroundLeft, _page.CursorY - lineHeight - overlap, currentWidth, lineHeight + overlap, lineBackground);
            }
            var lineWidth = LineWidth(line);

            // justification (N07): all lines except the last fill the wrap width by
            // the extra width is distributed evenly across the word gaps.
            if (style.Alignment == TextAlignment.Justify && lineIndex < lines.Count - 1 && line.Count > 0)
            {
                var gaps = CountSpaceGaps(line);
                var extra = gaps > 0 ? Math.Max(0, lineAvailable - lineWidth) / gaps : 0;
                WriteAtoms(line, lineStart, baseline, extra, style.TabStops, currentMirrorAxis, _page.ContentLeft);
            }
            else
            {
                var x = lineStart + AlignmentOffset(style.Alignment ?? TextAlignment.Left, lineAvailable, lineWidth);
                WriteAtoms(line, x, baseline, 0, style.TabStops, currentMirrorAxis, _page.ContentLeft);
            }

            _page.Advance(lineHeight);
            segmentHeight += lineHeight;
        }

        CloseSegment();
        _page.Advance(spaceAfter);
        _trailingSpace = spaceAfter;
        _lastAfter = spaceAfter;

        // height of the line according to the metrics of its fonts; empty lines use the
        // default font with the paragraph's font size.
        // As in Word, a line with text takes its height from its runs; the paragraph mark sizes only an empty paragraph.
        double LineHeightOf(IReadOnlyList<TextAtom> line)
            => Measured(line) is { Count: > 0 } measured
                ? measured.Max(AtomLineHeight)
                : defaultFont.Metrics.LineHeightEm * fontSize;

        double ScaledLineHeight(IReadOnlyList<TextAtom> line)
        {
            var height = LineHeightOf(line);
            return style.LineSpacing is not { } spacing ? height : spacing.Rule switch
            {
                LineSpacingRule.Multiple => height * spacing.Factor,
                LineSpacingRule.AtLeast => Math.Max(height, spacing.Height.Points),
                _ => spacing.Height.Points,
            };
        }

        // distance of the baseline from the line start: the largest ascender of the line,
        // how Word sets the baseline within the line box; in a line of exact height at a fixed share of it.
        double LineAscentOf(IReadOnlyList<TextAtom> line)
            => style.LineSpacing is { Rule: LineSpacingRule.Exactly } exact
                ? ExactLineAscent(exact)
                : Measured(line) is { Count: > 0 } measured
                    ? measured.Max(AtomAscent)
                    : defaultFont.Metrics.AscentEm * fontSize;
    }

    /// <summary>
    /// The atoms that take up room in a line; a bookmark marks a position and has no size, and an empty run has none
    /// either (Word sizes such a line by its paragraph mark).
    /// </summary>
    private static List<TextAtom> Measured(IReadOnlyList<TextAtom> line)
        => line.Where(atom => atom.Bookmark is null && atom.Comment is null
            && (atom.Text.Length > 0 || atom.Image is not null || atom.IsHardBreak || atom.IsTab)).ToList();

    /// <summary>
    /// Word sets the baseline of a line of exact height at four fifths of it, whatever the size of its text: lines of
    /// 21 pt with 6 pt and 10 pt Arial both have their baseline 16.8 pt below the top of the line.
    /// </summary>
    private static double ExactLineAscent(LineSpacing exact) => 0.8 * exact.Height.Points;

    private static double AlignmentOffset(TextAlignment alignment, double available, double lineWidth) => alignment switch
    {
        TextAlignment.Center => (available - lineWidth) / 2,
        TextAlignment.Right => available - lineWidth,
        _ => 0,
    };

    private void DrawParagraphBorders(ParagraphBorders borders, double x, double width, double top, double height)
    {
        var bottom = top - height;
        DrawBorderLine(borders.Top, x, top, x + width, top);
        DrawBorderLine(borders.Bottom, x, bottom, x + width, bottom);
        DrawBorderLine(borders.Left, x, top, x, bottom);
        DrawBorderLine(borders.Right, x + width, top, x + width, bottom);
    }

    private void DrawBorderLine(ParagraphBorder? border, double x1, double y1, double x2, double y2)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            return;
        }

        var style = border.Style switch
        {
            BorderStyle.Dotted => TextUnderlineStyle.Dotted,
            BorderStyle.Dashed => TextUnderlineStyle.Dashed,
            _ => TextUnderlineStyle.Single,
        };

        _page.DrawLine(x1, y1, x2, y2, border.Color, border.Width.Points, style);

        if (border.Style != BorderStyle.Double)
        {
            return;
        }

        // Offset the second line perpendicular to the first (not lengthwise).
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0)
        {
            return;
        }

        var offset = border.Width.Points * 2.5;
        var nx = -dy / length * offset;
        var ny = dx / length * offset;
        _page.DrawLine(x1 + nx, y1 + ny, x2 + nx, y2 + ny, border.Color, border.Width.Points, TextUnderlineStyle.Single);
    }

    private void RenderList(ListBlock list, int parentLevel)
    {
        // The level follows the actual nesting; NestingLevel can increase it.
        var level = Math.Max(list.NestingLevel, parentLevel + 1);
        var markerX = _page.ContentLeft + (level * ListIndent) - ListHanging;
        var contentIndent = level * ListIndent;
        using var spacing = _spacing.EnterList(list);

        var spec = _numbering.Level(list, level);

        // OF-B02: a list continues the numbering of the same definition and level (the model
        // shares one definition per list style); a fresh definition starts at its level start.
        var counter = (list.NumberingName, level);
        var number = spec.Kind == ListKind.Number
            ? _listNumbers.TryGetValue(counter, out var previous) ? previous : spec.Start - 1
            : 0;
        foreach (var item in list.Items)
        {
            number++;

            // A new item restarts the deeper levels, as Word and LibreOffice count them.
            for (var deeper = level + 1; deeper <= NumberingDefinition.MaxLevels; deeper++)
            {
                _listNumbers.Remove((list.NumberingName, deeper));
            }

            ListMarker? marker = new ListMarker(
                spec.Kind == ListKind.Number ? FormatListNumber(number, spec.Format) + "." : Bullet(level),
                markerX,
                spec.NumberColor,
                spec.NumberBold);

            for (var index = 0; index < item.Blocks.Count; index++)
            {
                var block = item.Blocks[index];
                var next = index + 1 < item.Blocks.Count ? item.Blocks[index + 1] : null;
                switch (block)
                {
                    case Paragraph paragraph:
                        RenderParagraph(
                            StyleOf(paragraph), paragraph.Inlines, headingSize: null, contentIndent, marker,
                            compact: _spacing.IsCompact(paragraph));
                        break;
                    case Heading heading:
                        RenderHeading(heading, contentIndent, marker, FirstLineHeight(next));
                        break;
                    default:
                        if (marker is not null)
                        {
                            // entry does not begin with text: marker on its own line.
                            RenderParagraph(ParagraphStyle.Default, [], headingSize: null, contentIndent, marker, compact: true);
                        }

                        RenderBlock(block, contentIndent, level, next);
                        break;
                }

                marker = null;
            }

            if (marker is not null)
            {
                RenderParagraph(ParagraphStyle.Default, [], headingSize: null, contentIndent, marker, compact: true);
            }
        }

        // The counter moves on, so a later list with the same definition continues here.
        if (spec.Kind == ListKind.Number)
        {
            _listNumbers[counter] = number;
        }
    }

    private static string FormatListNumber(int number, ListNumberFormat format) => format switch
    {
        ListNumberFormat.LowerRoman => ToRoman(number).ToLowerInvariant(),
        ListNumberFormat.UpperRoman => ToRoman(number),
        ListNumberFormat.LowerLetter => ToLetters(number).ToLowerInvariant(),
        ListNumberFormat.UpperLetter => ToLetters(number),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    private static string ToRoman(int number)
    {
        if (number is < 1 or > 3999)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<(int Value, string Text)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];

        var builder = new StringBuilder();
        foreach (var (value, text) in numerals)
        {
            while (number >= value)
            {
                builder.Append(text);
                number -= value;
            }
        }

        return builder.ToString();
    }

    private static string ToLetters(int number)
    {
        if (number < 1)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        while (number > 0)
        {
            number--;
            builder.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }

        return builder.ToString();
    }

    /// <summary>The smallest gap a tab leaves before the following text, in ems.</summary>
    private const double MinTabGapEm = 0.25;

    /// <summary>Maximum nesting of tables inside table cells; deeper tables are drawn as text.</summary>
    private const int MaxNestedTableDepth = 4;

    /// <summary>The indent of the table being drawn (a header table drawn on a page break keeps its own).</summary>
    private double _tableIndent;

    private void RenderTable(TableBlock table)
    {
        var outerIndent = _tableIndent;
        _tableIndent = table.LeftIndent?.Points ?? 0;
        try
        {
            RenderTableRows(table);
        }
        finally
        {
            _tableIndent = outerIndent;
        }
    }

    private void RenderTableRows(TableBlock table)
    {
        var rows = LayoutTable(table, _page.ContentWidth, depth: 0);
        if (rows.Count == 0)
        {
            return;
        }

        // Leading header rows are repeated at the top of every continuation page (as Word does
        // for w:tblHeader), provided they are self-contained and leave room for the content.
        var headerCount = 0;
        while (headerCount < rows.Count && rows[headerCount].IsHeader)
        {
            headerCount++;
        }

        var header = rows.Take(headerCount).ToList();
        var repeatHeader = headerCount > 0
            && headerCount < rows.Count
            && header.All(row => row.Cells.All(cell => !cell.Merged && !cell.Continuation))
            && header.Sum(row => row.NaturalHeight) <= _page.ContentHeight / 2;

        // OF-B04: only the outer corners of the table round, so the first row carries the top radii
        // and the last row the bottom radii.
        var radius = table.CornerRadius?.Points ?? 0;

        _page.BeginContainer("Table");
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].KeepHeight > _page.CursorY - _page.ContentBottom && !_page.IsAtPageTop)
            {
                NewTablePage(repeatHeader && index >= headerCount ? header : null);
            }

            var corners = new TableCorners(
                Top: index == 0 ? radius : 0,
                Bottom: index == rows.Count - 1 ? radius : 0);
            RenderTableRow(rows[index], repeatHeader && index >= headerCount ? header : null, corners);
        }

        _page.Advance(Math.Min(TableBottomBand(rows), Math.Max(0, _page.CursorY - _page.ContentBottom)));
        _page.EndContainer();
    }

    /// <summary>
    /// Lays out the rows of a table for the given width: column widths, row spans (continuation
    /// cells), conditional backgrounds and the lines of every cell (text lines, images and
    /// nested tables). The layout does not depend on the page position.
    /// </summary>
    private List<RowLayout> LayoutTable(TableBlock table, double availableWidth, int depth)
    {
        var result = new List<RowLayout>();
        var logicalColumns = TableLayout.LogicalColumns(table);
        if (logicalColumns == 0)
        {
            return result;
        }

        // Outside a fixed layout Word widens a column to its longest word rather than breaking inside it.
        var widths = TableLayout.UsesAutoFit(table)
            ? AutoFitColumns(table, logicalColumns, availableWidth)
            : table.FixedColumnWidths
                ? TableLayout.ResolveColumnWidthsWithPercent(table, logicalColumns, availableWidth)
                : TableLayout.WidenToMinimums(
                    TableLayout.ResolveColumnWidthsWithPercent(table, logicalColumns, availableWidth),
                    ColumnContentWidths(table, logicalColumns, withMaximum: false).Minimum);

        var style = table.Style;
        var styleBorder = table.Border ?? style?.Border;
        var tablePadY = TableLayout.CellPadding(table, null).Vertical.Points;
        var size = _defaults.FontSize.Points;
        var defaultMetrics = _page.Fonts.GetFont(null, bold: false, italic: false).Metrics;

        var rowIndex = 0;
        var remaining = new int[logicalColumns];
        var coveredCell = new TableCell?[logicalColumns];
        var coveredBackground = new Color?[logicalColumns];
        var coveredAtStart = new bool[logicalColumns];
        var coveredAtEnd = new bool[logicalColumns];
        foreach (var row in table.Rows)
        {
            var conditional = row.IsHeader ? style?.HeaderBackground : TableConditionalStyle.IsBanded(table, rowIndex) ? style?.BandBackground : null;
            if (rowIndex == table.Rows.Count - 1 && !row.IsHeader && style?.LastRowBackground is { } lastRowBackground)
            {
                conditional = lastRowBackground;
            }

            var cells = new List<CellLayout>();
            var cellIndex = 0;
            var columnIndex = 0;
            while (columnIndex < logicalColumns)
            {
                if (remaining[columnIndex] > 0)
                {
                    remaining[columnIndex]--;
                    var spanCell = coveredCell[columnIndex]!;
                    var (continuationPadX, continuationPadY) = TableLayout.CellPadding(table, spanCell);
                    cells.Add(new CellLayout(
                        spanCell,
                        widths[Math.Min(columnIndex, widths.Length - 1)],
                        continuationPadX.Points,
                        continuationPadY.Points,
                        [],
                        coveredBackground[columnIndex],
                        Continuation: true,
                        Merged: false,
                        LastContinuation: remaining[columnIndex] == 0,
                        AtSpanStart: coveredAtStart[columnIndex],
                        AtSpanEnd: coveredAtEnd[columnIndex]));
                    columnIndex++;
                    continue;
                }

                if (cellIndex >= row.Cells.Count)
                {
                    columnIndex++;
                    continue;
                }

                var cell = row.Cells[cellIndex++];
                var span = Math.Clamp(cell.ColumnSpan, 1, Math.Max(1, logicalColumns - columnIndex));
                var cellWidth = 0.0;
                for (var offset = 0; offset < span && columnIndex + offset < widths.Length; offset++)
                {
                    cellWidth += widths[columnIndex + offset];
                }

                var (padding, verticalPadding) = TableLayout.CellPadding(table, cell);
                var padX = padding.Points;
                var padY = verticalPadding.Points;
                var available = Math.Max(1, cellWidth - 2 * padX);
                var area = TableConditionalStyle.AreaOf(table, style, row, rowIndex, columnIndex, logicalColumns);

                // Boxes (images, nested tables) must fit on an empty page together with the padding.
                var maxBoxHeight = Math.Max(1, _page.ContentHeight - (2 * padY) - 1);
                var lines = BuildCellLines(cell, available, maxBoxHeight, depth, area);
                var styleBackground = conditional;
                if (!row.IsHeader && columnIndex == 0 && style?.FirstColumnBackground is { } firstColumn)
                {
                    styleBackground = firstColumn;
                }
                else if (!row.IsHeader && columnIndex + span - 1 == logicalColumns - 1 && style?.LastColumnBackground is { } lastColumn)
                {
                    styleBackground = lastColumn;
                }

                var background = cell.Background ?? styleBackground;
                var merged = cell.RowSpan > 1;
                if (merged)
                {
                    for (var offset = 0; offset < span && columnIndex + offset < logicalColumns; offset++)
                    {
                        remaining[columnIndex + offset] = cell.RowSpan - 1;
                        coveredCell[columnIndex + offset] = cell;
                        coveredBackground[columnIndex + offset] = background;
                        coveredAtStart[columnIndex + offset] = offset == 0;
                        coveredAtEnd[columnIndex + offset] = offset == span - 1;
                    }
                }

                cells.Add(new CellLayout(cell, cellWidth, padX, padY, lines, background, Continuation: false, Merged: merged, LastContinuation: false, Area: area));
                columnIndex += span;
            }

            // The line height comes from the text of the row's cells (an empty line from its paragraph mark, which, as in
            // Word, does not raise a line with text); the document's size only for a row without any text line.
            var lineHeight = 0.0;
            var ascent = 0.0;
            foreach (var line in cells.SelectMany(cell => cell.Lines).Where(line => line.Atoms is not null))
            {
                var measured = Measured(line.Atoms!);
                if (measured.Count == 0)
                {
                    lineHeight = Math.Max(lineHeight, line.Empty.Height);
                    ascent = Math.Max(ascent, line.Empty.Ascent);
                }

                // An inline image raises only its own line (RowLayout.ImageRise), not every line of the row.
                foreach (var atom in measured.Where(atom => atom.Image is null))
                {
                    lineHeight = Math.Max(lineHeight, atom.Font.Metrics.LineHeightEm * atom.FontSize);
                    ascent = Math.Max(ascent, atom.Font.Metrics.AscentEm * atom.FontSize);
                }
            }

            if (lineHeight == 0)
            {
                lineHeight = defaultMetrics.LineHeightEm * size;
                ascent = defaultMetrics.AscentEm * size;
            }

            if (lineHeight + 2 * tablePadY > _page.ContentHeight)
            {
                throw new UnsupportedFeatureException("PDF: a table row is taller than the available page content.");
            }

            // Row height (Y02): at least the minimum or exactly the fixed height. As in Word, a minimum is for the
            // content: the cells' top and bottom padding and the table's grid above the row come on top of it; an exact
            // height includes both.
            var requestedHeight = row.Height?.Points ?? 0;
            var minimumPadding = requestedHeight > 0 ? 2 * cells.Select(cell => cell.PadY).DefaultIfEmpty(tablePadY).Max() : 0;
            result.Add(new RowLayout(
                row.IsHeader,
                cells,
                lineHeight,
                ascent,
                row.HeightRule != TableRowHeightRule.Exactly ? requestedHeight + minimumPadding : 0,
                row.HeightRule == TableRowHeightRule.Exactly ? requestedHeight : 0,
                tablePadY,
                styleBorder));
            rowIndex++;
        }

        // As Word does, a horizontal border takes its width in the row below it: the text starts below the border.
        var previousBottom = 0.0;
        for (var index = 0; index < result.Count; index++)
        {
            var row = result[index];
            var top = Math.Max(previousBottom, row.Cells.Where(cell => !cell.Continuation).Select(cell => BorderWidth(cell, row.Border, top: true)).DefaultIfEmpty(0).Max());
            previousBottom = row.Cells.Where(cell => !cell.Merged).Select(cell => BorderWidth(cell, row.Border, top: false)).DefaultIfEmpty(0).Max();
            // Only the table's grid takes its room in the row; Word draws a cell's own border inside the row, whatever its height rule.
            var grid = row.Cells.Where(cell => !cell.Continuation)
                .Select(cell => GridWidth(cell.Area?.Border ?? row.Border ?? DefaultTableBorder)).DefaultIfEmpty(0).Max();
            if (row.ExactHeight == 0)
            {
                result[index] = row with { BorderTop = Math.Min(grid, top) };
            }
        }

        SpreadRowSpans(result);
        for (var index = 0; index < result.Count; index++)
        {
            // A row kept together moves whole to the next page, if a page can hold it.
            if (table.Rows[index].KeepTogether && result[index].NaturalHeight <= _page.ContentHeight)
            {
                result[index] = result[index] with { KeepHeight = Math.Max(result[index].KeepHeight, result[index].NaturalHeight) };
            }
        }

        return result;
    }

    /// <summary>The width of a table's grid line (none is 0).</summary>
    private static double GridWidth(ParagraphBorder border) => border.Style == BorderStyle.None ? 0 : border.Width.Points;

    /// <summary>The width of a cell's top or bottom border, resolved as <see cref="DrawCellBorders"/> draws it.</summary>
    private static double BorderWidth(CellLayout cell, ParagraphBorder? tableBorder, bool top)
    {
        var border = (top ? cell.Cell.Borders?.Top : cell.Cell.Borders?.Bottom) ?? cell.Area?.Border ?? tableBorder ?? DefaultTableBorder;
        return border.Style == BorderStyle.None ? 0 : border.Width.Points;
    }

    /// <summary>The bottom border below the last row of a table, which Word adds to the table's height.</summary>
    private static double TableBottomBand(List<RowLayout> rows)
        => rows.Count == 0 || rows[^1].ExactHeight > 0
            ? 0
            : rows[^1].Cells.Where(cell => !cell.Merged).Select(cell => BorderWidth(cell, rows[^1].Border, top: false)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// A row-spanning cell shares its height among its rows: only what they lack together goes to
    /// the last one. The rows then stay on one page, because the text is drawn from the first row;
    /// a span that cannot fit on a page keeps the cell in its first row, where it can be split.
    /// </summary>
    private void SpreadRowSpans(List<RowLayout> rows)
    {
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var index = 0; index < rows[rowIndex].Cells.Count; index++)
            {
                var cell = rows[rowIndex].Cells[index];
                var last = Math.Min(rowIndex + cell.Cell.RowSpan - 1, rows.Count - 1);
                if (!cell.Merged || last == rowIndex)
                {
                    continue;
                }

                rows[rowIndex].Cells[index] = cell with { Spanned = true };
                var height = rows.Skip(rowIndex).Take(last - rowIndex + 1).Sum(row => row.NaturalHeight);
                var content = rows[rowIndex].BorderTop + cell.Lines.Sum(rows[rowIndex].HeightOf) + (2 * cell.PadY);
                var total = Math.Max(height, content);
                if (total > _page.ContentHeight || (content > height && rows[last].ExactHeight > 0))
                {
                    rows[rowIndex].Cells[index] = cell;
                    continue;
                }

                if (content > height)
                {
                    rows[last] = rows[last] with { MinHeight = rows[last].NaturalHeight - rows[last].BorderTop + content - height };
                }

                rows[rowIndex] = rows[rowIndex] with { KeepHeight = Math.Max(rows[rowIndex].KeepHeight, total) };
            }
        }
    }

    /// <summary>
    /// The lines of a cell: wrapped text (lists become marked text lines), images scaled to the
    /// cell width and nested tables as one box each.
    /// </summary>
    private List<CellLine> BuildCellLines(TableCell cell, double available, double maxBoxHeight, int depth, TableStyleArea? area = null)
    {
        var lines = new List<CellLine>();
        var textBlocks = new List<Block>();

        void FlushText()
        {
            if (textBlocks.Count == 0)
            {
                return;
            }

            // Per paragraph: its left, right and first-line indent apply inside the cell as in
            // Word and LibreOffice (kept within the cell, a hanging line does not enter the padding).
            foreach (var block in textBlocks)
            {
                var style = block is Heading heading ? heading.Style : StyleOf((Paragraph)block);
                var title = block is Heading cellHeading ? HeadingScan.PlainText(cellHeading.Inlines) : string.Empty;
                int? headingIndex = !_band && title.Length > 0
                    ? _headingIndex++ : null;
                var left = Math.Max(0, style.LeftIndent?.Points ?? 0);
                var width = Math.Max(1, available - left - Math.Max(0, style.RightIndent?.Points ?? 0));
                var firstIndent = Math.Max(-left, style.FirstLineIndent?.Points ?? 0);
                var spaceBefore = style.SpaceBefore?.Points ?? 0;
                var spaceAfter = ParagraphSpacing.SpaceAfter(style, compact: true);
                var atoms = ExtractAtoms([block], cell, area);
                var (emptyFont, emptySize) = MarkFormat(
                    block is Heading sizedHeading ? Typography.HeadingSize(sizedHeading.Level).Points : null,
                    TableLayout.CellParagraphStyle(style, cell));
                var segmentStart = 0;
                var firstSegment = true;
                for (var index = 0; index <= atoms.Count; index++)
                {
                    if (index < atoms.Count && !atoms[index].IsHardBreak)
                    {
                        continue;
                    }

                    var firstWidth = firstSegment ? Math.Max(1, width - firstIndent) : width;
                    var wrapped = WrapAtoms(atoms.GetRange(segmentStart, index - segmentStart), width, style.TabStops, firstWidth, null, left, left + (firstSegment ? firstIndent : 0));
                    for (var line = 0; line < wrapped.Count; line++)
                    {
                        var first = firstSegment && line == 0;
                        lines.Add(CellLine.Text(
                            wrapped[line],
                            line == wrapped.Count - 1,
                            indent: left + (first ? firstIndent : 0),
                            available: first ? firstWidth : width,
                            tabStops: style.TabStops) with
                        {
                            Heading = first && block is Heading h2 && title.Length > 0 ? ((int)h2.Level, title, headingIndex) : null,
                            Before = first ? spaceBefore : 0,
                            After = index == atoms.Count && line == wrapped.Count - 1 ? spaceAfter : 0,
                            Spacing = style.LineSpacing,
                            Empty = (emptyFont.Metrics.LineHeightEm * emptySize, emptyFont.Metrics.AscentEm * emptySize),
                        });
                    }

                    segmentStart = index + 1;
                    firstSegment = false;
                }
            }

            textBlocks.Clear();
        }

        foreach (var cellBlock in FlattenCellLists(cell.Blocks, parentLevel: 0, _numbering, Bullet))
        {
            // An embedded object is its preview picture (ADR-0029).
            var block = cellBlock is EmbeddedObject embeddedObject ? PreviewImage(embeddedObject) : cellBlock;
            if (block is null)
            {
                continue;
            }

            switch (block)
            {
                case Paragraph or Heading:
                    textBlocks.Add(block);
                    break;
                case ImageBlock { MediaType: ImageMediaType.Svg } image:
                {
                    // Vector graphics inside a cell are drawn like an image.
                    FlushText();
                    _images.Check(image);
                    var drawing = SvgConverter.TryConvert(image.RawData, _page.AlphaState, out var warnings, _page.RenderSvgText, _page.RegisterSvgGradient, _page.RegisterSvgPattern, (bytes, type) => _page.RegisterSvgImage(bytes, type) is { } image ? new PdfImageHandle(image.ResourceName, image.Width, image.Height) : null,
            _page.RegisterForm,
            _page.RegisterMask,
            _page.RegisterAlphaMask);
                    foreach (var warning in warnings)
                    {
                        _handler.Report(warning);
                    }

                    if (drawing is null)
                    {
                        break;
                    }

                    var (svgWidth, svgHeight) = ImageLayout.Resolve(image);
                    var svgScale = Math.Min(1, Math.Min(available / svgWidth.Points, maxBoxHeight / svgHeight.Points));
                    var boxWidth = svgWidth.Points * svgScale;
                    var boxHeight = svgHeight.Points * svgScale;
                    lines.Add(new CellLine(null, boxWidth, boxHeight, (x, top) => DrawCropped(image.Crop, boxWidth, boxHeight, x, top - boxHeight,
                        (drawWidth, drawHeight, drawX, drawY) => _page.DrawVector(drawing, drawWidth, drawHeight, drawX, drawY))) { Figure = true, AltText = image.AltText });
                    break;
                }
                case ImageBlock image:
                {
                    FlushText();
                    var handle = _images.GetImage(image);
                    var scale = Math.Min(1, Math.Min(available / handle.Width, maxBoxHeight / handle.Height));
                    var width = handle.Width * scale;
                    var height = handle.Height * scale;
                    lines.Add(new CellLine(null, width, height, (x, top) => DrawCropped(image.Crop, width, height, x, top - height,
                        (drawWidth, drawHeight, drawX, drawY) => _page.DrawImage(handle.ResourceName, drawWidth, drawHeight, drawX, drawY))) { Figure = true, AltText = image.AltText });
                    break;
                }

                case Chart chart:
                {
                    FlushText();
                    var chartScale = Math.Min(1, Math.Min(available / chart.Width.Points, maxBoxHeight / chart.Height.Points));
                    var chartWidth = chart.Width.Points * chartScale;
                    var chartHeight = chart.Height.Points * chartScale;
                    lines.Add(new CellLine(null, chartWidth, chartHeight, (x, top) => _page.DrawChart(chart, chartWidth, chartHeight, x, top - chartHeight, _handler.Report))
                    {
                        Figure = true,
                        AltText = chart.AltText ?? chart.Title,
                    });
                    break;
                }

                case Equation equation:
                {
                    // A formula in a cell: laid out like a display equation, scaled down to the cell, left-aligned
                    // unless it asks for centre or right (the cell is the measure).
                    FlushText();
                    var mathBox = _page.MathLayoutFor().Layout(equation.Root, _defaults.FontSize.Points);
                    ReportMissingMathGlyphs(mathBox);
                    var mathScale = Math.Min(1, Math.Min(available / Math.Max(1, mathBox.Width), maxBoxHeight / Math.Max(1, mathBox.Height)));
                    var mathWidth = mathBox.Width * mathScale;
                    var mathHeight = mathBox.Height * mathScale;
                    var shift = equation.Alignment switch
                    {
                        TextAlignment.Center => (available - mathWidth) / 2,
                        TextAlignment.Right => available - mathWidth,
                        _ => 0,
                    };
                    lines.Add(new CellLine(null, mathWidth, mathHeight, (x, top) =>
                    {
                        _page.BeginStructure("Formula", equation.AltText);
                        _page.DrawMath(mathBox, x + shift, top - mathBox.Ascent * mathScale, mathScale);
                        _page.EndStructure();
                    }));
                    break;
                }

                case Diagram diagram:
                {
                    FlushText();
                    var diagramScale = Math.Min(1, Math.Min(available / diagram.Width.Points, maxBoxHeight / diagram.Height.Points));
                    var diagramWidth = diagram.Width.Points * diagramScale;
                    var diagramHeight = diagram.Height.Points * diagramScale;
                    lines.Add(new CellLine(null, diagramWidth, diagramHeight, (x, top) => DrawDiagramShapes(diagram, diagramScale, x, top))
                    {
                        Figure = true,
                        AltText = diagram.AltText,
                    });
                    break;
                }

                case FormField field:
                {
                    FlushText();
                    var fieldWidth = Math.Min(field.Width.Points, available);
                    var fieldHeight = Math.Min(field.Height.Points, maxBoxHeight);
                    lines.Add(new CellLine(null, fieldWidth, fieldHeight, (x, top) => _page.AddFormWidget(field, x, top - fieldHeight, fieldWidth, fieldHeight)));
                    break;
                }

                case TableBlock nested:
                    FlushText();
                    var previousHeadingIndex = _headingIndex;
                    var previousFootnoteNumber = _footnoteNumber;
                    var previousEndnoteNumber = _endnoteNumber;
                    var previousEndnoteCount = _endnotes.Count;
                    if (depth < MaxNestedTableDepth && LayoutNestedTable(nested, available, maxBoxHeight, depth + 1) is { } box)
                    {
                        if (box.Height > 0)
                        {
                            lines.Add(box);
                        }
                    }
                    else
                    {
                        _headingIndex = previousHeadingIndex;
                        _footnoteNumber = previousFootnoteNumber;
                        _endnoteNumber = previousEndnoteNumber;
                        _endnotes.RemoveRange(previousEndnoteCount, _endnotes.Count - previousEndnoteCount);
                        _handler.Report("PDF: a nested table that is too deep or taller than a page is drawn as text.");
                        textBlocks.AddRange(FlattenNestedTable(nested));
                    }

                    break;
                default:
                    _handler.Report($"PDF: {block.GetType().Name} in table cells is not rendered.");
                    break;
            }
        }

        FlushText();
        if (lines.Count == 0)
        {
            // A cell without blocks holds an empty paragraph in the document's font, as in Word.
            var (emptyFont, emptySize) = MarkFormat(null, null);
            lines.Add(CellLine.Text([], lastOfParagraph: true) with { Empty = (emptyFont.Metrics.LineHeightEm * emptySize, emptyFont.Metrics.AscentEm * emptySize) });
        }

        return lines;
    }

    private IEnumerable<Block> FlattenNestedTable(TableBlock table)
    {
        ReaderGuard.EnsureStack();
        foreach (var cell in table.Rows.SelectMany(row => row.Cells))
        {
            foreach (var block in FlattenCellLists(cell.Blocks, 0, _numbering, Bullet))
            {
                if (block is Paragraph or Heading)
                {
                    yield return block;
                }
                else if (block is TableBlock nested)
                {
                    foreach (var inner in FlattenNestedTable(nested))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    /// <summary>A nested table as one box; null if it does not fit on a page.</summary>
    private CellLine? LayoutNestedTable(TableBlock table, double width, double maxHeight, int depth)
    {
        var rows = LayoutTable(table, width, depth);
        if (rows.Count == 0)
        {
            return new CellLine(null, 0, 0, (_, _) => { });
        }

        var height = rows.Sum(row => row.NaturalHeight) + TableBottomBand(rows);
        if (height > maxHeight)
        {
            return null;
        }

        var tableWidth = rows.Max(row => row.Cells.Sum(cell => cell.Width));
        var indent = table.LeftIndent?.Points ?? 0;
        return new CellLine(null, tableWidth, height, (left, top) =>
        {
            var x = left + indent;
            var y = top;
            foreach (var row in rows)
            {
                var from = new int[row.Cells.Count];
                var to = row.Cells.Select(cell => cell.Lines.Count).ToArray();
                DrawRowSlice(row, x, y, row.NaturalHeight, from, to, atPageTop: false, tagged: false, "TD", TableCorners.None, row.BorderTop);
                y -= row.NaturalHeight;
            }
        })
        {
            Notes = rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Lines).SelectMany(CellNotes).ToArray(),
        };
    }

    /// <summary>
    /// Renders one row; it is split across pages between cell lines. On every page change the
    /// repeated header rows are drawn first (as artifacts, they are repetitions).
    /// </summary>
    private void RenderTableRow(RowLayout row, IReadOnlyList<RowLayout>? header, TableCorners corners)
    {
        var offsets = new int[row.Cells.Count];
        var first = true;
        var freshPage = false;
        var reservedNoteHeight = 0.0;
        var role = row.IsHeader ? "TH" : "TD";
        _page.BeginContainer("TR");
        while (true)
        {
            var band = first ? row.BorderTop : 0;
            var room = _page.CursorY - _page.ContentBottom - reservedNoteHeight - band;
            var targets = new int[row.Cells.Count];
            double consumed;
            double sliceHeight;

            if (first && row.ExactHeight > 0)
            {
                // A row with a fixed height is not split; its content is clipped to the height.
                for (var index = 0; index < row.Cells.Count; index++)
                {
                    targets[index] = row.Cells[index].Lines.Count;
                }

                sliceHeight = row.ExactHeight;
                consumed = 0;
            }
            else
            {
                consumed = 0;
                var blocked = false;
                for (var index = 0; index < row.Cells.Count; index++)
                {
                    var cell = row.Cells[index];
                    if (cell.Spanned)
                    {
                        targets[index] = cell.Lines.Count;
                        continue;
                    }

                    var available = room - (2 * cell.PadY);
                    var height = 0.0;
                    var target = offsets[index];
                    while (target < cell.Lines.Count && height + row.HeightOf(cell.Lines[target]) <= available + 1e-6)
                    {
                        height += row.HeightOf(cell.Lines[target]);
                        target++;
                    }

                    if (target == offsets[index] && target < cell.Lines.Count)
                    {
                        if (freshPage || _page.IsAtPageTop)
                        {
                            // Even an empty page cannot hold the next line (e.g. below a repeated
                            // header): place it anyway instead of looping forever.
                            height = row.HeightOf(cell.Lines[target]);
                            target++;
                        }
                        else
                        {
                            blocked = true;
                        }
                    }

                    targets[index] = target;
                    if (height > 0)
                    {
                        consumed = Math.Max(consumed, height + (2 * cell.PadY));
                    }
                }

                // The start of a row stays together: if a cell cannot place its first line, the
                // whole row moves to the next page.
                if (blocked && (first || targets.SequenceEqual(offsets)))
                {
                    NewTablePage(header);
                    freshPage = true;
                    reservedNoteHeight = 0;
                    continue;
                }

                if (consumed == 0)
                {
                    consumed = row.LineHeight + (2 * row.PadY);
                }

                consumed += band;
                sliceHeight = first ? Math.Max(consumed, row.MinHeight + row.BorderTop) : consumed;
            }

            // A required height counts when the row is placed; only an empty page can cut it down. As in Word, a row
            // that ends here also needs room for its bottom border, which is drawn below it.
            var pageRoom = _page.CursorY - _page.ContentBottom;
            var endsHere = row.ExactHeight > 0 || targets.Select((target, index) => target >= row.Cells[index].Lines.Count).All(done => done);
            var bottomBorder = endsHere
                ? row.Cells.Where(cell => !cell.Merged).Select(cell => BorderWidth(cell, row.Border, top: false)).DefaultIfEmpty(0).Max()
                : 0;
            if (first && sliceHeight + bottomBorder > pageRoom + 1e-6)
            {
                if (!freshPage && !_page.IsAtPageTop)
                {
                    NewTablePage(header);
                    freshPage = true;
                    reservedNoteHeight = 0;
                    continue;
                }

                if (row.ExactHeight > pageRoom)
                {
                    _handler.Report("PDF: a table row with an exact height taller than the page is cut off at the page end.");
                }

                sliceHeight = Math.Max(consumed, Math.Min(sliceHeight, pageRoom));
            }

            var notes = row.Cells.SelectMany((cell, index) => cell.Lines.Skip(offsets[index]).Take(targets[index] - offsets[index]))
                .SelectMany(CellNotes)
                .ToArray();
            var noteHeight = notes.Sum(note => _page.FootnoteHeight(note.Number, note.Text, notes.Length));
            if (noteHeight > 0 && _page.HasPendingFootnotes)
            {
                NewTablePage(header);
                freshPage = true;
                reservedNoteHeight = 0;
                continue;
            }
            if (reservedNoteHeight == 0 && noteHeight > 0 && sliceHeight + noteHeight > _page.CursorY - _page.ContentBottom)
            {
                if (!freshPage && !_page.IsAtPageTop)
                {
                    NewTablePage(header);
                    freshPage = true;
                }
                else
                {
                    reservedNoteHeight = noteHeight;
                }

                continue;
            }

            foreach (var note in notes)
            {
                _page.AddFootnote(note.Number, note.Text, notes.Length, _noteBookmarks.GetValueOrDefault(note.Number));
            }

            var rowDone = row.ExactHeight > 0
                || targets.Select((target, index) => target >= row.Cells[index].Lines.Count).All(done => done);

            // The top radii belong to the first slice of the first row, the bottom radii to the last
            // slice of the last row; a row that continues on the next page is drawn square there.
            var sliceCorners = new TableCorners(first ? corners.Top : 0, rowDone ? corners.Bottom : 0);
            DrawRowSlice(row, _page.ContentLeft + _tableIndent, _page.CursorY, sliceHeight, offsets, targets, _page.IsAtPageTop, tagged: true, role, sliceCorners, band);
            _page.Advance(sliceHeight);
            offsets = targets;
            first = false;
            reservedNoteHeight = 0;

            if (rowDone)
            {
                break;
            }

            NewTablePage(header);
            freshPage = true;
        }

        _page.EndContainer();
    }

    /// <summary>
    /// Sets <see cref="ClosingParagraph"/>: it needs its room as Word's paragraph does, so a table that fills the page to
    /// its end leaves it on the next page (in Word an empty last page).
    /// </summary>
    public void RenderClosingParagraph()
    {
        _page.EnsureSpace(ClosingParagraph.Height);
        _page.Advance(ClosingParagraph.Height);
    }

    /// <summary>Starts a new page within a table and repeats the header rows on it.</summary>
    private void NewTablePage(IReadOnlyList<RowLayout>? header)
    {
        _page.NewPage();
        if (header is null)
        {
            return;
        }

        _page.BeginArtifact();
        _repeatingTableHeader = true;
        foreach (var row in header)
        {
            var from = new int[row.Cells.Count];
            var to = row.Cells.Select(cell => cell.Lines.Count).ToArray();
            DrawRowSlice(row, _page.ContentLeft + _tableIndent, _page.CursorY, row.NaturalHeight, from, to, _page.IsAtPageTop, tagged: false, "TD", TableCorners.None, row.BorderTop);
            _page.Advance(row.NaturalHeight);
        }

        _repeatingTableHeader = false;
        _page.EndArtifact();
    }

    /// <summary>Draws the backgrounds, borders and the lines [from, to) of every cell of a row slice.</summary>
    private void DrawRowSlice(
        RowLayout row,
        double left,
        double top,
        double sliceHeight,
        int[] from,
        int[] to,
        bool atPageTop,
        bool tagged,
        string role,
        TableCorners corners,
        double band = 0)
    {
        // The border band lies above the text; the cell outline runs through the middle of it, and the
        // bottom edge through the middle of the band below (the next row's, or the table's bottom border).
        var outlineTop = top - (band / 2);

        // backgrounds and borders per cell (also for each part of a row across pages).
        var cellX = left;
        for (var index = 0; index < row.Cells.Count; index++)
        {
            var cell = row.Cells[index];

            // OF-B04: only the outermost cells of the first and last row take part in the rounded
            // outline of the table.
            var limit = Math.Max(0, Math.Min(cell.Width, sliceHeight) / 2);
            var topRadius = Math.Clamp(corners.Top, 0, limit);
            var bottomRadius = Math.Clamp(corners.Bottom, 0, limit);
            var radii = new CellCorners(
                TopLeft: index == 0 ? topRadius : 0,
                TopRight: index == row.Cells.Count - 1 ? topRadius : 0,
                BottomRight: index == row.Cells.Count - 1 ? bottomRadius : 0,
                BottomLeft: index == 0 ? bottomRadius : 0);

            if (cell.Background is { } background)
            {
                _page.FillRoundedRectangle(
                    cellX, outlineTop - sliceHeight, cell.Width, sliceHeight,
                    radii.TopLeft, radii.TopRight, radii.BottomRight, radii.BottomLeft,
                    background);
            }

            DrawCellBorders(cell, row.Border, cellX, outlineTop - sliceHeight, cell.Width, sliceHeight, atPageTop, radii);
            cellX += cell.Width;
        }

        var needed = 0.0;
        for (var index = 0; index < row.Cells.Count; index++)
        {
            if (!row.Cells[index].Spanned)
            {
                needed = Math.Max(needed, band + row.Cells[index].Lines.Skip(from[index]).Take(to[index] - from[index]).Sum(row.HeightOf) + (2 * row.Cells[index].PadY));
            }
        }

        var rowWidth = row.Cells.Sum(cell => cell.Width);
        var clip = row.ExactHeight > 0 && sliceHeight < needed;
        if (clip)
        {
            _page.BeginClip(left, top - sliceHeight, rowWidth, sliceHeight);
        }

        cellX = left;
        for (var index = 0; index < row.Cells.Count; index++)
        {
            var cell = row.Cells[index];
            if (tagged)
            {
                _page.BeginContainer(role);
                _page.BeginStructure("P");
            }

            // vertical alignment (Y02) within the row slice.
            var contentHeight = cell.Lines.Count == 0 ? row.LineHeight : cell.Lines.Sum(row.HeightOf);
            var slack = Math.Max(0, (cell.Spanned ? Math.Max(sliceHeight, row.KeepHeight) : sliceHeight) - band - 2 * cell.PadY - contentHeight);
            var verticalOffset = cell.Cell.VerticalAlignment switch
            {
                TableCellVerticalAlignment.Center => slack / 2,
                TableCellVerticalAlignment.Bottom => slack,
                _ => 0,
            };

            var y = top - band - cell.PadY - verticalOffset;
            var innerWidth = cell.Width - 2 * cell.PadX;
            for (var lineIndex = from[index]; lineIndex < to[index]; lineIndex++)
            {
                var line = cell.Lines[lineIndex];
                if (!_repeatingTableHeader && !_band && line.Heading is { } heading)
                {
                    if (heading.Index is { } headingIndex)
                    {
                        _outlineHeadings.Add(headingIndex, (heading.Level, heading.Title, _page.CurrentPageObject, y));
                        if (_tocPages is not null || _collectedPages is not null)
                        {
                            _outline.AddBookmark(TocAnchor(headingIndex), _page.CurrentPageObject, y);
                        }

                        RecordHeadingPage(headingIndex);
                    }
                }

                if (line.Atoms is { } atoms)
                {
                    var alignment = atoms.Count > 0 ? atoms[0].Alignment : TextAlignment.Left;
                    var lineWidth = LineWidth(atoms);
                    var lineAvailable = line.Available ?? innerWidth;
                    var lineX = cellX + cell.PadX + line.Indent + Math.Max(0, AlignmentOffset(alignment, lineAvailable, lineWidth));
                    var justifyExtra = 0.0;
                    if (alignment == TextAlignment.Justify && !line.LastOfParagraph)
                    {
                        var gaps = CountSpaceGaps(atoms);
                        justifyExtra = gaps > 0 ? Math.Max(0, lineAvailable - lineWidth) / gaps : 0;
                    }

                    // Right to left the line is mirrored inside the cell's inner box.
                    var cellMirror = atoms.Count > 0 && atoms[0].RightToLeft ? (2 * (cellX + cell.PadX)) + innerWidth : (double?)null;
                    WriteAtoms(atoms, lineX, y - line.Before - (line.Spacing is { Rule: LineSpacingRule.Exactly } exact ? ExactLineAscent(exact) : row.Ascent) - row.ImageRise(line), justifyExtra, line.TabStops, cellMirror, cellX + cell.PadX);
                    y -= row.HeightOf(line);
                }
                else
                {
                    var boxX = cellX + cell.PadX + Math.Max(0, AlignmentOffset(cell.Cell.Alignment, innerWidth, line.Width));

                    // An image is its own structure element, inside the paragraph of the cell.
                    if (tagged && line.Figure)
                    {
                        _page.EndStructure();
                        _page.BeginStructure("Figure", line.AltText);
                        line.Draw!(boxX, y);
                        _page.EndStructure();
                        _page.BeginStructure("P");
                    }
                    else
                    {
                        line.Draw!(boxX, y);
                    }

                    y -= line.Height;
                }
            }

            if (tagged)
            {
                _page.EndStructure();
                _page.EndContainer();
            }

            cellX += cell.Width;
        }

        if (clip)
        {
            _page.EndClip();
        }
    }

    /// <summary>Draws the cell borders; a plain cell uses one rectangle (as before).</summary>
    private void DrawCellBorders(
        CellLayout cell,
        ParagraphBorder? tableBorder,
        double x,
        double y,
        double width,
        double height,
        bool atPageTop,
        CellCorners radii)
    {
        var borders = cell.Cell.Borders;
        if (borders is null && tableBorder is null && cell.Area?.Border is null && !cell.Merged && !cell.Continuation && !radii.IsRounded)
        {
            _page.StrokeRectangle(x, y, width, height, Typography.TableBorderWidth.Points, Color.Black);
            return;
        }

        // The border of the cell wins, then the conditional border of the area, then the border
        // of the table.
        var areaBorder = cell.Area?.Border;
        var top = borders?.Top ?? areaBorder ?? tableBorder ?? DefaultTableBorder;
        var bottom = borders?.Bottom ?? areaBorder ?? tableBorder ?? DefaultTableBorder;
        var left = borders?.Left ?? areaBorder ?? tableBorder ?? DefaultTableBorder;
        var right = borders?.Right ?? areaBorder ?? tableBorder ?? DefaultTableBorder;

        if (!cell.Continuation || atPageTop)
        {
            DrawCellBorderSide(top, x + radii.TopLeft, y + height, x + width - radii.TopRight, y + height);
        }

        if (!cell.Merged && (!cell.Continuation || cell.LastContinuation))
        {
            DrawCellBorderSide(bottom, x + radii.BottomLeft, y, x + width - radii.BottomRight, y);
        }

        if (!cell.Continuation || cell.AtSpanStart)
        {
            DrawCellBorderSide(left, x, y + height - radii.TopLeft, x, y + radii.BottomLeft);
        }

        if (!cell.Continuation || cell.AtSpanEnd)
        {
            DrawCellBorderSide(right, x + width, y + height - radii.TopRight, x + width, y + radii.BottomRight);
        }

        // The corner arcs belong to the horizontal border of their corner (top or bottom), so a
        // rounded corner never invents a border colour that the cell does not have.
        DrawCornerArc(top, x + radii.TopLeft, y + height, radii.TopLeft, Corner.TopLeft);
        DrawCornerArc(top, x + width - radii.TopRight, y + height, radii.TopRight, Corner.TopRight);
        DrawCornerArc(bottom, x + width - radii.BottomRight, y, radii.BottomRight, Corner.BottomRight);
        DrawCornerArc(bottom, x + radii.BottomLeft, y, radii.BottomLeft, Corner.BottomLeft);
    }

    private enum Corner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft,
    }

    /// <summary>Draws a quarter circle at a corner of the cell box (OF-B04).</summary>
    private void DrawCornerArc(ParagraphBorder? border, double x, double y, double radius, Corner corner)
    {
        if (border is null || border.Style == BorderStyle.None || radius <= 0)
        {
            return;
        }

        // The quarter circle of radius r around (x, y): (x ± r, y) and (x, y ± r) are the tangents,
        // the two control points sit at kappa × r from them.
        const double Kappa = 0.5522847498307933;
        var k = radius * Kappa;
        var (x1, y1, cx1, cy1, cx2, cy2, x2, y2) = corner switch
        {
            Corner.TopLeft => (x, y - radius, x, y - radius + k, x - radius + k, y, x - radius, y),
            Corner.TopRight => (x, y - radius, x, y - radius + k, x + radius - k, y, x + radius, y),
            Corner.BottomRight => (x, y + radius, x, y + radius - k, x + radius - k, y, x + radius, y),
            _ => (x, y + radius, x, y + radius - k, x - radius + k, y, x - radius, y),
        };

        _page.StrokeCurve(x1, y1, cx1, cy1, cx2, cy2, x2, y2, border.Width.Points, border.Color);
    }

    private void DrawCellBorderSide(ParagraphBorder? border, double x1, double y1, double x2, double y2)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            return;
        }

        if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) < 1e-6)
        {
            // Both corners of that side are fully rounded: the arcs form the outline.
            return;
        }

        DrawBorderLine(border, x1, y1, x2, y2);
    }

    /// <summary>
    /// the radii of the outer corners of a table for one row slice (OF-B04); 0 means square.
    /// </summary>
    private readonly record struct TableCorners(double Top, double Bottom)
    {
        public static TableCorners None => default;
    }

    /// <summary>the radii of the four corners of one cell in the rounded outline of a table (OF-B04).</summary>
    private readonly record struct CellCorners(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
    {
        public bool IsRounded => TopLeft > 0 || TopRight > 0 || BottomRight > 0 || BottomLeft > 0;
    }

    private sealed record CellLayout(TableCell Cell, double Width, double PadX, double PadY, List<CellLine> Lines, Color? Background, bool Continuation, bool Merged, bool LastContinuation, bool AtSpanStart = true, bool AtSpanEnd = true, TableStyleArea? Area = null, bool Spanned = false);

    /// <summary>
    /// A line of a cell: a text line (<see cref="Atoms"/>, with the row's line height) or a box
    /// (image, nested table) with its own size, drawn at its top-left corner.
    /// </summary>
    private sealed record CellLine(
        List<TextAtom>? Atoms,
        double Width,
        double Height,
        Action<double, double>? Draw,
        bool LastOfParagraph = false,
        double Indent = 0,
        double? Available = null,
        IReadOnlyList<TabStop>? TabStops = null)
    {
        public (int Level, string Title, int? Index)? Heading { get; init; }

        public IReadOnlyList<(int Number, string Text)>? Notes { get; init; }

        /// <summary>The space before and after the line (the first and last line of a paragraph).</summary>
        public double Before { get; init; }

        public double After { get; init; }

        public LineSpacing? Spacing { get; init; }

        /// <summary>The line height and ascent of the paragraph mark: the line's height without visible text.</summary>
        public (double Height, double Ascent) Empty { get; init; }

        /// <summary>An image, tagged as a figure with its alternative text.</summary>
        public bool Figure { get; init; }

        public string? AltText { get; init; }

        /// <summary>A text line; indent, width and tab stops come from its paragraph.</summary>
        public static CellLine Text(List<TextAtom> atoms, bool lastOfParagraph = false, double indent = 0, double? available = null, IReadOnlyList<TabStop>? tabStops = null)
            => new(atoms, 0, 0, null, lastOfParagraph, indent, available, tabStops);
    }

    private static IEnumerable<(int Number, string Text)> CellNotes(CellLine line)
        => line.Notes is { } notes ? notes : (line.Atoms ?? []).Where(atom => atom.Note is not null).Select(atom => atom.Note!.Value);

    /// <summary>A laid-out table row; text lines share the row's line height.</summary>
    private sealed record RowLayout(
        bool IsHeader,
        List<CellLayout> Cells,
        double LineHeight,
        double Ascent,
        double MinHeight,
        double ExactHeight,
        double PadY,
        ParagraphBorder? Border,
        double KeepHeight = 0,
        double BorderTop = 0)
    {
        public double HeightOf(CellLine line)
        {
            if (line.Atoms is null)
            {
                return line.Height;
            }

            var height = line.Spacing is not { } spacing ? LineHeight : spacing.Rule switch
            {
                LineSpacingRule.Multiple => LineHeight * spacing.Factor,
                LineSpacingRule.AtLeast => Math.Max(LineHeight, spacing.Height.Points),
                _ => spacing.Height.Points,
            };
            return height + ImageExtra(line, AtomLineHeight, LineHeight) + line.Before + line.After;
        }

        /// <summary>How far an inline image in the line moves its baseline down, below the row's ascent.</summary>
        public double ImageRise(CellLine line) => ImageExtra(line, AtomAscent, Ascent);

        // As in a paragraph, an image line is as high as its image needs and its baseline as low.
        private static double ImageExtra(CellLine line, Func<TextAtom, double> measure, double rowValue)
            => line.Atoms is { } atoms && atoms.Any(atom => atom.Image is not null)
                ? Math.Max(0, atoms.Where(atom => atom.Image is not null).Max(measure) - rowValue)
                : 0;

        /// <summary>The height of the row when it is not split.</summary>
        public double NaturalHeight => ExactHeight > 0
            ? ExactHeight
            : BorderTop + Math.Max(
                Math.Max(MinHeight, LineHeight + (2 * PadY)),
                Cells.Where(cell => !cell.Spanned && cell.Lines.Count > 0).Select(cell => cell.Lines.Sum(HeightOf) + (2 * cell.PadY)).DefaultIfEmpty(0).Max());
    }


    private List<TextAtom> BuildAtoms(IReadOnlyList<Inline> inlines, double? headingSize, ParagraphStyle? paragraphStyle = null)
    {
        var atoms = new List<TextAtom>();

        foreach (var inline in inlines)
        {
            if (inline is Bookmark bookmark)
            {
                atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                    headingSize ?? _defaults.FontSize.Points, _defaults.TextColor, Bookmark: bookmark));
                continue;
            }

            if (inline is Comment comment)
            {
                atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                    headingSize ?? _defaults.FontSize.Points, _defaults.TextColor, Comment: comment));
                continue;
            }

            if (inline is Hyperlink hyperlink)
            {
                foreach (var atom in BuildAtoms(hyperlink.Inlines, headingSize, paragraphStyle))
                {
                    atoms.Add(atom with { Hyperlink = hyperlink.Target });
                }

                continue;
            }

            if (inline is PageNumberField)
            {
                // The page number follows the section's numbering (PageNumberStart), as in the
                // header/footer; the font follows the paragraph values.
                atoms.Add(FieldAtom(_page.DisplayPageNumber.ToString(CultureInfo.InvariantCulture), headingSize, paragraphStyle));
                continue;
            }

            if (inline is PageCountField)
            {
                if (_page.TotalPages is { } total)
                {
                    atoms.Add(FieldAtom(total.ToString(CultureInfo.InvariantCulture), headingSize, paragraphStyle));
                }
                else
                {
                    _handler.Report("PDF: the total page count (NUMPAGES) is not resolved.");
                }

                continue;
            }

            if (inline is DateField dateField)
            {
                atoms.Add(FieldAtom(
                    (dateField.Value ?? DateTimeOffset.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    headingSize,
                    paragraphStyle));
                continue;
            }

            if (inline is AlignmentTab)
            {
                // Left in a table cell or a text box (see AlignmentTabs): a plain tab there.
                atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                    headingSize ?? _defaults.FontSize.Points, _defaults.TextColor, IsTab: true));
                continue;
            }

            if (inline is IndexEntry indexEntry)
            {
                // A zero-width target; the next pass reads its page for the index.
                if (_fields.Lists?.MarkAnchor(indexEntry) is { } markAnchor)
                {
                    atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                        headingSize ?? _defaults.FontSize.Points, _defaults.TextColor, Bookmark: new Bookmark(markAnchor)));
                }

                continue;
            }

            if (inline is Field caption && _fields.Lists?.CaptionAnchor(caption) is { } captionAnchor)
            {
                atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                    headingSize ?? _defaults.FontSize.Points, _defaults.TextColor, Bookmark: new Bookmark(captionAnchor)));
            }

            if (inline is PageRefField or DocPropertyField or DocVariableField or Field)
            {
                if (ResolveField(inline) is { } value)
                {
                    atoms.Add(FieldAtom(value, headingSize, paragraphStyle));
                }

                continue;
            }

            if (inline is Footnote bandNote && _band)
            {
                // A band carries no note (Word shows none there, LibreOffice drops it), so the text
                // stays visible at the reference. The page handler reports it once per document.
                _handler.Report("PDF: a note in a header or footer is written as text.");
                if (NoteScan.Text(bandNote) is { Length: > 0 } bandText)
                {
                    atoms.Add(new TextAtom(
                        bandText,
                        _page.Fonts.GetFont(null, bold: false, italic: false),
                        headingSize ?? _defaults.FontSize.Points,
                        _defaults.TextColor));
                }

                continue;
            }

            if (inline is Footnote footnote)
            {
                // The reference marker is a superscript number (phase Z, Z08).
                var isEndnote = footnote.Kind == NoteKind.Endnote;
                var number = isEndnote ? ++_endnoteNumber : ++_footnoteNumber;
                var noteText = PlainText(footnote.Blocks);
                var noteBookmarks = NoteBookmarkNames(footnote.Blocks);
                if (isEndnote)
                {
                    _endnotes.Add((number, noteText, noteBookmarks));
                }
                else
                {
                    _noteBookmarks[number] = noteBookmarks;
                }

                var markerSize = (headingSize ?? _defaults.FontSize.Points) * 0.7;
                atoms.Add(new TextAtom(
                    number.ToString(CultureInfo.InvariantCulture),
                    _page.Fonts.GetFont(null, bold: false, italic: false),
                    markerSize,
                    _defaults.TextColor,
                    BaselineShift: (headingSize ?? _defaults.FontSize.Points) * 0.33,
                    Note: !isEndnote ? (number, noteText) : null));
                continue;
            }

            if (inline is InlineImage inlineImage)
            {
                var image = inlineImage.Image.MediaType == ImageMediaType.Svg ? inlineImage.Image.RasterFallback : inlineImage.Image;
                if (image is null)
                {
                    _handler.Report("PDF: an inline SVG image without a raster fallback is left out.");
                    continue;
                }

                // As a block image does, a picture larger than the page is scaled down to fit.
                var handle = _images.GetImage(image);
                var fit = Math.Min(1, Math.Min(_page.ContentWidth / handle.Width, _page.ContentHeight * 0.9 / handle.Height));
                var (imageFont, imageSize, imageColor) = PlainRunFormat(null, headingSize, paragraphStyle);
                atoms.Add(new TextAtom("\uFFFC", imageFont, imageSize, imageColor,
                    Image: new InlineImageBox(handle, handle.Width * fit, handle.Height * fit, image.Crop, inlineImage.Image.AltText)));
                continue;
            }

            if (inline is not TextRun rawRun)
            {
                continue;
            }

            // ADR-0010: the paragraph style provides the base, the character style overrides it and
            // the formatting of the run wins.
            var run = RunFormatResolver.Resolve(
                rawRun,
                paragraphStyle,
                RunFormatResolver.Find(_characterStyles, rawRun.CharacterStyleName));

            if (run.Hidden == true)
            {
                continue;
            }

            var font = _page.Fonts.GetFont(run.FontFamily, run.Bold == true || headingSize is not null, run.Italic == true);
            var size = run.FontSize?.Points ?? headingSize ?? _defaults.FontSize.Points;
            var color = run.Color ?? _defaults.TextColor;

            // Word's w:kern: kerning only from the threshold size on.
            if (run.Kerning is { } kerningThreshold && size < kerningThreshold.Points)
            {
                font = font.WithoutKerning();
            }

            var renderedText = run.Text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            if (!font.CanEncode(run.AllCaps == true || run.SmallCaps == true ? renderedText.ToUpperInvariant() : renderedText))
            {
                _handler.Report(font.IsEmbedded
                    ? "PDF: the embedded font does not contain all required glyphs."
                    : "PDF: characters outside WinAnsi are replaced with '?'.");
            }

            var underlineStyle = run.UnderlineStyle ?? TextUnderlineStyle.None;
            var baselineShift = run.Position?.Points
                ?? (run.Baseline == TextBaseline.Superscript ? size * SuperscriptShiftFactor
                    : run.Baseline == TextBaseline.Subscript ? -size * SubscriptShiftFactor
                    : 0);
            var background = run.Highlight ?? run.Background;
            var characterSpacing = run.CharacterSpacing?.Points ?? 0;
            var horizontalScaling = run.HorizontalScaling ?? 100;

            void AddText(string text)
            {
                // BJ01: the hyphenation patterns insert the soft hyphens (the break
                // opportunities) into the text.
                if (_hyphenation is { } patterns)
                {
                    text = patterns.ApplyToText(text);
                }

                var parts = text.Split('\t');
                for (var partIndex = 0; partIndex < parts.Length; partIndex++)
                {
                    // A soft hyphen is a break opportunity: it is invisible, unless the line
                    // ends there.
                    var words = parts[partIndex].Split('\u00AD');
                    for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
                    {
                        foreach (var (segment, sizeFactor) in TransformCaps(words[wordIndex], run.AllCaps == true, run.SmallCaps == true))
                        {
                            if (segment.Length == 0)
                            {
                                continue;
                            }

                            atoms.Add(new TextAtom(
                                segment,
                                font,
                                size * sizeFactor,
                                color,
                                UnderlineStyle: underlineStyle,
                                UnderlineColor: run.UnderlineColor,
                                Strikethrough: run.Strikethrough ?? TextStrikethrough.None,
                                BaselineShift: baselineShift,
                                Background: background,
                                CharacterSpacing: characterSpacing,
                                HorizontalScaling: horizontalScaling,
                                Border: run.Border));
                        }

                        if (wordIndex < words.Length - 1)
                        {
                            atoms.Add(new TextAtom(string.Empty, font, size, color, IsSoftHyphen: true));
                        }
                    }

                    if (partIndex < parts.Length - 1)
                    {
                        atoms.Add(new TextAtom(string.Empty, font, size, color, IsTab: true));
                    }
                }
            }

            var start = 0;
            for (var index = 0; index < run.Text.Length; index++)
            {
                var character = run.Text[index];
                if (character is not ('\r' or '\n' or '\u2028' or '\u2029'))
                {
                    continue;
                }

                if (index > start)
                {
                    AddText(run.Text[start..index]);
                }

                atoms.Add(new TextAtom(string.Empty, font, size, color, IsHardBreak: true));
                if (character == '\r' && index + 1 < run.Text.Length && run.Text[index + 1] == '\n')
                {
                    index++;
                }

                start = index + 1;
            }

            if (start < run.Text.Length)
            {
                AddText(run.Text[start..]);
            }
        }

        return atoms;
    }

    /// <summary>A field result in the default font (bold in headings) at the paragraph's size.</summary>
    private TextAtom FieldAtom(string text, double? headingSize, ParagraphStyle? paragraphStyle)
    {
        var (font, size, color) = PlainRunFormat(null, headingSize, paragraphStyle);
        return new(text, font, size, color);
    }

    /// <summary>
    /// The font, size and colour of <paramref name="raw"/> (or of a run without formatting of its own) in a
    /// paragraph: fields, and the line of an empty paragraph, take them as a text run does.
    /// </summary>
    private (PdfFontHandle Font, double Size, Color Color) PlainRunFormat(TextRun? raw, double? headingSize, ParagraphStyle? paragraphStyle)
    {
        var run = RunFormatResolver.Resolve(
            raw ?? new TextRun(string.Empty),
            paragraphStyle,
            raw is null ? null : RunFormatResolver.Find(_characterStyles, raw.CharacterStyleName));
        return (
            _page.Fonts.GetFont(run.FontFamily, run.Bold == true || headingSize is not null, run.Italic == true),
            run.FontSize?.Points ?? headingSize ?? _defaults.FontSize.Points,
            run.Color ?? _defaults.TextColor);
    }

    /// <summary>The font and size of the paragraph mark: its own, else the paragraph's, else the document's.</summary>
    private (PdfFontHandle Font, double Size) MarkFormat(double? headingSize, ParagraphStyle? paragraphStyle)
    {
        var mark = new TextRun(string.Empty) { FontFamily = paragraphStyle?.MarkFontFamily, FontSize = paragraphStyle?.MarkFontSize };
        var (font, size, _) = PlainRunFormat(mark, headingSize, paragraphStyle);
        return (font, size);
    }

    /// <summary>
    /// The result of a page reference, document property or document variable field; null (and
    /// a report) when it cannot be resolved, as Word shows an error text instead.
    /// </summary>
    private string? ResolveField(Inline field)
    {
        switch (field)
        {
            case PageRefField pageRef:
                if (_fields.BookmarkPage(pageRef.BookmarkName) is { } page)
                {
                    return page.ToString(CultureInfo.InvariantCulture);
                }

                if (!_fields.HasBookmarkPages)
                {
                    // First layout pass: a placeholder of typical width until the pages are known.
                    return "0";
                }

                _handler.Report(_fields.IsBandBookmark(pageRef.BookmarkName)
                    ? $"PDF: the page reference to the bookmark '{pageRef.BookmarkName}' is not resolved (the bookmark is in a header or a footer)."
                    : $"PDF: the page reference to the bookmark '{pageRef.BookmarkName}' is not resolved (bookmark not found).");
                return null;
            case DocPropertyField property:
                if (_fields.DocumentProperty(property.Name) is { } propertyValue)
                {
                    return propertyValue;
                }

                _handler.Report($"PDF: the document property '{property.Name}' is not set and is not rendered.");
                return null;
            case DocVariableField variable:
                if (_fields.DocumentVariable(variable.Name) is { } variableValue)
                {
                    return variableValue;
                }

                _handler.Report($"PDF: the document variable '{variable.Name}' is not set and is not rendered.");
                return null;
            case Field generalField:
                if (generalField.Result is { } result)
                {
                    // The PDF draws the cached result.
                    return result;
                }

                // AJ02: a field without a cached result is calculated (IF, MERGEFIELD, SEQ, ...).
                var context = _fields.Context.ForPage(_page.DisplayPageNumber, _page.TotalPages);
                if (FieldEvaluator.Evaluate(generalField, context) is { } calculated)
                {
                    return calculated;
                }

                // OF-C03: a REF field whose bookmark is missing is reported once by the document
                // (ReferenceScan), not per occurrence.
                if (ReferenceScan.RefTarget(generalField.Instruction) is not null)
                {
                    return null;
                }

                _handler.Report($"PDF: the field '{generalField.Instruction}' has no result and is not drawn.");
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Applies casing: <c>AllCaps</c> turns everything into uppercase,
    /// <c>SmallCaps</c> turns lowercase letters into reduced uppercase letters (factor 0.8).
    /// </summary>
    private static IEnumerable<(string Text, double SizeFactor)> TransformCaps(string text, bool allCaps, bool smallCaps)
    {
        if (allCaps)
        {
            yield return (text.ToUpperInvariant(), 1.0);
            yield break;
        }

        if (!smallCaps)
        {
            yield return (text, 1.0);
            yield break;
        }

        var builder = new StringBuilder();
        var previousIsLower = false;
        var hasPrevious = false;
        foreach (var character in text)
        {
            var isLower = char.IsLower(character);
            if (hasPrevious && isLower != previousIsLower)
            {
                yield return (builder.ToString(), previousIsLower ? SmallCapsSizeFactor : 1.0);
                builder.Clear();
            }

            previousIsLower = isLower;
            hasPrevious = true;
            builder.Append(isLower ? char.ToUpperInvariant(character) : character);
        }

        if (builder.Length > 0)
        {
            yield return (builder.ToString(), previousIsLower ? SmallCapsSizeFactor : 1.0);
        }
    }

    /// <summary>
    /// Orders the words of a line visually (UAX #9, L2) within each tab segment; the shaper
    /// reorders the characters within a word. Right to left, the atoms are returned in the order
    /// they are placed from the right edge (the caller mirrors the positions).
    /// </summary>
    private static IReadOnlyList<TextAtom> VisualOrder(IReadOnlyList<TextAtom> atoms, bool rightToLeft)
    {
        var codePoints = new List<int>();
        var starts = new int[atoms.Count];
        for (var i = 0; i < atoms.Count; i++)
        {
            starts[i] = codePoints.Count;
            foreach (var rune in atoms[i].Text.EnumerateRunes())
            {
                codePoints.Add(rune.Value);
            }
        }

        var paragraphLevel = rightToLeft ? 1 : 0;
        var resolved = BidiAlgorithm.Resolve(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(codePoints), paragraphLevel);
        var levels = new byte[atoms.Count];
        for (var i = 0; i < atoms.Count; i++)
        {
            // An atom without text (tab, soft hyphen) stays next to its predecessor.
            levels[i] = atoms[i].Text.Length > 0 ? resolved.Levels[starts[i]] : i > 0 ? levels[i - 1] : (byte)paragraphLevel;
        }

        if (!rightToLeft && levels.All(level => level == 0))
        {
            return atoms;
        }

        // Tab stops are positions from the start edge: only the words between two tabs move.
        var result = new List<TextAtom>(atoms.Count);
        var segmentStart = 0;
        for (var i = 0; i <= atoms.Count; i++)
        {
            if (i < atoms.Count && !atoms[i].IsTab)
            {
                continue;
            }

            var order = BidiAlgorithm.ReorderVisual(levels.AsSpan(segmentStart, i - segmentStart));
            var placement = rightToLeft ? order.Reverse() : order;
            result.AddRange(placement.Select(index => WithLevels(segmentStart + index)));
            if (i < atoms.Count)
            {
                result.Add(atoms[i]);
            }

            segmentStart = i + 1;
        }

        return result;

        // The shaper orders and mirrors the characters of a word by the levels of the whole line.
        TextAtom WithLevels(int index)
        {
            var length = atoms[index].Text.EnumerateRunes().Count();
            return length == 0 ? atoms[index] : atoms[index] with { BidiLevels = resolved.Levels[starts[index]..(starts[index] + length)] };
        }
    }

    /// <summary>A short unshaped text (list marker) in visual order for the given base direction.</summary>
    internal static string VisualText(string text, bool rightToLeft)
    {
        var codePoints = text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var levels = BidiAlgorithm.Resolve(codePoints, rightToLeft ? 1 : 0).Levels;
        return string.Concat(BidiAlgorithm.ReorderVisual(levels).Select(index =>
            char.ConvertFromUtf32((levels[index] & 1) == 1 && BidiData.TryGetMirror(codePoints[index], out var mirror) ? mirror : codePoints[index])));
    }

    /// <summary>Wraps atoms into lines; the first line may have its own width (first-line indent, wide list marker).</summary>
    private static List<List<TextAtom>> WrapAtoms(List<TextAtom> atoms, double maxWidth, IReadOnlyList<TabStop>? tabStops = null,
        double? firstLineWidth = null, Func<IReadOnlyList<List<TextAtom>>, double>? widthForLine = null,
        double tabOffset = 0, double firstTabOffset = 0)
    {
        var lines = new List<List<TextAtom>>();
        var current = new List<TextAtom>();
        var pendingBookmarks = new List<TextAtom>();
        var width = 0.0;
        var softWrapped = false;

        // The width a part must fit: the current line while it is empty, otherwise the next one.
        double SplitWidth() => current.Count > 0
            ? maxWidth
            : widthForLine?.Invoke(lines) ?? (lines.Count == 0 ? firstLineWidth ?? maxWidth : maxWidth);

        foreach (var atom in atoms)
        {
            if (atom.Bookmark is not null || atom.Comment is not null)
            {
                pendingBookmarks.Add(atom);
                continue;
            }

            if (atom.IsHardBreak)
            {
                current.AddRange(pendingBookmarks);
                pendingBookmarks.Clear();
                lines.Add(current);
                current = [];
                width = 0;
                softWrapped = false;
                continue;
            }

            if (atom.IsSoftHyphen)
            {
                // A break opportunity: it has no width and is invisible, unless the line
                // ends here (then the hyphen is drawn).
                current.Add(atom);
                continue;
            }

            // A tab has no text of its own; while wrapping it keeps at least a small gap so that
            // text and a right-aligned tab target (e.g. a page number) do not run together.
            var segments = atom.IsTab
                ? [atom with { }]
                : SplitAtSpaces(atom).SelectMany(part => SplitToFit(part, SplitWidth));
            foreach (var segment in segments)
            {
                var isSpace = !segment.IsTab && IsWhiteSpace(segment.Text);
                var segmentWidth = segment.IsTab ? TabAdvance(tabStops, (lines.Count == 0 ? firstTabOffset : tabOffset) + width, segment.FontSize) : MeasureAtom(segment);
                var lineMax = widthForLine?.Invoke(lines) ?? (lines.Count == 0 ? firstLineWidth ?? maxWidth : maxWidth);
                if (current.Count > 0 && width + segmentWidth > lineMax)
                {
                    // BJ01: a break at a soft hyphen of the last word is preferred: the word is split
                    // and the hyphen is drawn. The last soft hyphen may leave no room for the hyphen
                    // itself, then the one before it is tried (a word is never split without a hyphen).
                    foreach (var hyphenIndex in isSpace ? [] : SoftHyphensOfLastWord(current))
                    {
                        var hyphen = current[hyphenIndex] with { Text = "-", IsSoftHyphen = false };
                        var tail = current.Skip(hyphenIndex + 1).ToList();
                        var tailWidth = tail.Sum(MeasureAtom);
                        var headWidth = width - tailWidth;
                        if (headWidth + MeasureAtom(hyphen) > lineMax)
                        {
                            continue;
                        }

                        var head = current.Take(hyphenIndex).ToList();
                        head.Add(hyphen);
                        lines.Add(head);
                        current = tail;
                        width = tailWidth;
                        softWrapped = true;
                        lineMax = widthForLine?.Invoke(lines) ?? maxWidth;
                        break;
                    }

                    // automatic wrap: whitespace at the end of the line otherwise shifts the
                    // alignment, whitespace at the start of the following line indents it.
                    if (current.Count > 0 && width + segmentWidth > lineMax)
                    {
                        while (current.Count > 0 && !current[^1].IsTab && !current[^1].IsSoftHyphen && IsWhiteSpace(current[^1].Text))
                        {
                            current.RemoveAt(current.Count - 1);
                        }

                        lines.Add(current);
                        current = [];
                        width = 0;
                        softWrapped = true;
                    }
                }

                if (isSpace && softWrapped && current.Count == 0)
                {
                    continue;
                }

                if (!isSpace || segment.IsTab)
                {
                    current.AddRange(pendingBookmarks);
                    pendingBookmarks.Clear();
                }

                current.Add(segment);
                width += segmentWidth;
            }
        }

        current.AddRange(pendingBookmarks);
        // The last line may consist of a word placed right after a wrap, which never asked for its width: plan its
        // margins too, or it falls back to those of the paragraph's start (beside a picture that has already ended).
        widthForLine?.Invoke(lines);
        lines.Add(current);

        return lines.Select(JoinHyphenParts).ToList();
    }

    /// <summary>
    /// The parts of a word split at its hyphens for wrapping (<see cref="IsBreakAfterHyphen"/>) are one piece of text
    /// again where they share a line: "2026-09-25" stays one text run in the page.
    /// </summary>
    private static List<TextAtom> JoinHyphenParts(List<TextAtom> line)
    {
        var joined = new List<TextAtom>(line.Count);
        foreach (var atom in line)
        {
            if (atom.JoinsPrevious && joined.Count > 0 && joined[^1] is { Text: [.., '-' or '\u2010'] } previous)
            {
                joined[^1] = previous with { Text = previous.Text + atom.Text };
            }
            else
            {
                joined.Add(atom);
            }
        }

        return joined;
    }

    /// <summary>The indexes of the soft hyphens in the last word of the line, the last one first (a space or tab ends the word).</summary>
    private static IEnumerable<int> SoftHyphensOfLastWord(List<TextAtom> atoms)
    {
        for (var index = atoms.Count - 1; index >= 0; index--)
        {
            if (atoms[index].IsSoftHyphen)
            {
                yield return index;
            }
            else if (atoms[index].IsTab || (atoms[index].Text.Length > 0 && IsWhiteSpace(atoms[index].Text)))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// The width a tab occupies while wrapping: up to a left tab stop (or the next default stop);
    /// right and centered stops place the following text before the stop, so they count as a gap.
    /// </summary>
    private static double TabAdvance(IReadOnlyList<TabStop>? tabStops, double x, double fontSize)
    {
        var (stopX, _, alignment) = NextTab(tabStops, 0, x, fontSize * MinTabGapEm);
        return alignment == TabStopAlignment.Left ? Math.Max(stopX - x, 0) : fontSize;
    }

    private static bool IsWhiteSpace(string text) => text.All(IsBreakingSpace);

    /// <summary>White space a line may break at; the no-break spaces keep their neighbours together.</summary>
    private static bool IsBreakingSpace(char character)
        => char.IsWhiteSpace(character) && character is not ('\u00A0' or '\u2007' or '\u202F');

    /// <summary>
    /// A line may break before <paramref name="index"/> after a hyphen inside a word, as Word does: "UP-|Kombination",
    /// "A4-|Blatt", and unlike UAX #14 (HY × NU) also before a digit: "9170-|2", "12-12-|2022". Never after a hyphen
    /// that starts a word ("-5") or in "--".
    /// </summary>
    private static bool IsBreakAfterHyphen(string text, int index)
        => index >= 2
            && char.IsLetterOrDigit(text[index])
            && (text[index - 1] == '\u2010' || (text[index - 1] == '-' && char.IsLetterOrDigit(text[index - 2])));

    private static IEnumerable<TextAtom> SplitAtSpaces(TextAtom atom)
    {
        var start = 0;
        var joins = atom.JoinsPrevious;
        for (var index = 1; index < atom.Text.Length; index++)
        {
            var hyphen = IsBreakAfterHyphen(atom.Text, index);
            if (IsBreakingSpace(atom.Text[index]) != IsBreakingSpace(atom.Text[start]) || hyphen)
            {
                yield return atom with { Text = atom.Text[start..index], JoinsPrevious = joins };
                start = index;
                joins = hyphen;
            }
        }

        if (start < atom.Text.Length)
        {
            yield return atom with { Text = atom.Text[start..], JoinsPrevious = joins };
        }
    }

    private static IEnumerable<TextAtom> SplitToFit(TextAtom atom, Func<double> maxWidthNow)
    {
        var maxWidth = maxWidthNow();
        if (atom.Note is not null || MeasureAtom(atom) <= maxWidth)
        {
            yield return atom;
            yield break;
        }

        var part = new StringBuilder();
        var width = 0.0;
        foreach (var rune in atom.Text.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeWidth = atom.Font.Measure(runeText, atom.FontSize) * (atom.HorizontalScaling / 100.0);
            var spacing = part.Length > 0 ? atom.CharacterSpacing : 0;
            if (part.Length > 0 && width + spacing + runeWidth > maxWidth)
            {
                yield return atom with { Text = part.ToString() };
                maxWidth = maxWidthNow();
                part.Clear();
                width = 0;
                spacing = 0;
            }

            part.Append(runeText);
            width += spacing + runeWidth;
        }

        if (part.Length > 0)
        {
            yield return atom with { Text = part.ToString() };
        }
    }

    /// <summary>Width of an atom including horizontal scaling and character spacing.</summary>
    /// <summary>
    /// The next tab stop after <paramref name="x"/>. A stop closer than <paramref name="minGap"/>
    /// is skipped, so that a tab always leaves a visible gap (text ending just before a stop
    /// would otherwise touch the following text).
    /// </summary>
    private static (double X, TabStopLeader Leader, TabStopAlignment Alignment) NextTab(IReadOnlyList<TabStop>? tabStops, double lineStart, double x, double minGap = 0)
    {
        const double DefaultTabInterval = 36.0;
        var from = x + Math.Max(0.01, minGap);
        if (tabStops is not null)
        {
            foreach (var stop in tabStops.OrderBy(item => item.Position.Points))
            {
                var absolute = lineStart + stop.Position.Points;
                if (absolute > from)
                {
                    return (absolute, stop.Leader, stop.Alignment);
                }
            }
        }

        return (lineStart + (Math.Floor((from - lineStart) / DefaultTabInterval) + 1) * DefaultTabInterval, TabStopLeader.None, TabStopAlignment.Left);
    }

    private static double MeasureAtom(TextAtom atom)
    {
        if (atom.IsTab)
        {
            return 0;
        }

        if (atom.Image is { } image)
        {
            return image.Width;
        }

        var width = atom.Font.Measure(atom.Text, atom.FontSize) * (atom.HorizontalScaling / 100.0);
        if (Math.Abs(atom.CharacterSpacing) > 1e-9 && atom.Text.Length > 0)
        {
            var glyphs = atom.Text.EnumerateRunes().Count();
            if (glyphs > 1)
            {
                width += atom.CharacterSpacing * (glyphs - 1);
            }
        }

        return width;
    }

    /// <summary>The width of the text after a tab, up to its last decimal separator for a decimal tab.</summary>
    private static double TrailingWidth(IReadOnlyList<TextAtom> atoms, int tabIndex, bool toDecimal = false)
    {
        var width = 0.0;
        double? beforeSeparator = null;
        for (var index = tabIndex + 1; index < atoms.Count; index++)
        {
            if (atoms[index].IsTab)
            {
                break;
            }

            // shortcut: both separators count, whichever comes last; take the language when a text after the number needs it.
            if (toDecimal && atoms[index].Text.LastIndexOfAny(['.', ',']) is >= 0 and var separator)
            {
                beforeSeparator = width + MeasureAtom(atoms[index] with { Text = atoms[index].Text[..separator] });
            }

            width += MeasureAtom(atoms[index]);
        }

        return beforeSeparator ?? width;
    }

    /// <summary>
    /// The width a line takes for its alignment: as in Word, the spaces at its end hang over the edge, so "Date: " right
    /// aligned ends with the colon at the edge.
    /// </summary>
    private static double LineWidth(List<TextAtom> line)
        => line.Take(TextEnd(line)).Sum(MeasureAtom);

    /// <summary>The number of word gaps of a line: its spaces before the trailing ones.</summary>
    private static int CountSpaceGaps(IReadOnlyList<TextAtom> line)
        => line.Take(TextEnd(line)).Count(atom => atom.Text.Length > 0 && IsWhiteSpace(atom.Text));

    /// <summary>The index after the last atom of a line that is not a trailing space.</summary>
    private static int TextEnd(IReadOnlyList<TextAtom> line)
    {
        var end = line.Count;
        while (end > 0 && line[end - 1] is { IsTab: false, Image: null, Note: null } atom && IsWhiteSpace(atom.Text))
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// The logical text of a line that mixes right-to-left and Latin letters, or null otherwise.
    /// The writer emits it as <c>/ActualText</c> so readers recover the logical order despite the
    /// visual glyph order.
    /// </summary>
    private static string? MixedDirectionText(IReadOnlyList<TextAtom> atoms)
    {
        var builder = new StringBuilder();
        var rightToLeft = false;
        var latin = false;
        foreach (var atom in atoms)
        {
            if (atom.IsTab || atom.IsSoftHyphen || atom.Bookmark is not null || atom.Comment is not null || atom.Text.Length == 0)
            {
                continue;
            }

            builder.Append(atom.Text);
            foreach (var rune in atom.Text.EnumerateRunes())
            {
                switch (BidiData.GetBidiClass(rune.Value))
                {
                    case BidiClass.R:
                    case BidiClass.AL:
                        rightToLeft = true;
                        break;
                    case BidiClass.L:
                        latin = true;
                        break;
                }
            }
        }

        return rightToLeft && latin ? builder.ToString() : null;
    }

    /// <summary>
    /// writes the atoms of a line from x on the baseline. Underlines are
    /// not drawn per atom, but as one line per contiguous section
    /// joined (same font size and color); this matches Word and keeps the
    /// content stream small. For justification, <paramref name="justifyExtra"/> is added after each
    /// word gap is added so that the line fills the wrap width. <c>mirrorAxis</c> (right-to-left
    /// paragraph) is the sum of the left and right edge the line is mirrored at: the line is laid
    /// out from the start edge as usual and every position is mirrored, so alignment, indents and
    /// tab stops count from the right.
    /// </summary>
    private void WriteAtoms(IReadOnlyList<TextAtom> atoms, double x, double baseline, double justifyExtra = 0, IReadOnlyList<TabStop>? tabStops = null, double? mirrorAxis = null, double? tabOrigin = null)
    {
        var actualText = MixedDirectionText(atoms);
        if (actualText is not null)
        {
            _page.BeginActualText(actualText);
        }

        atoms = VisualOrder(atoms, rightToLeft: mirrorAxis is not null);
        double X(double left, double width) => mirrorAxis is { } axis ? axis - left - width : left;
        var lineStart = tabOrigin ?? x;
        var underlineStart = 0.0;
        var underlineEnd = 0.0;
        var underlineThickness = 0.0;
        var underlinePosition = 0.0;
        var underlineColor = Color.Black;
        var underlineStyle = TextUnderlineStyle.None;
        var inUnderline = false;

        var strikeStart = 0.0;
        var strikeEnd = 0.0;
        var strikeThickness = 0.0;
        var strikePosition = 0.0;
        var strikeColor = Color.Black;
        var strikeStyle = TextUnderlineStyle.None;
        var inStrike = false;

        for (var atomIndex = 0; atomIndex < atoms.Count; atomIndex++)
        {
            var atom = atoms[atomIndex];
            if (atom.Comment is { } comment)
            {
                // A band repeats on every page and a repeated table header repeats its rows: no note there.
                if (_page.WriteComments && !_band && !_repeatingTableHeader)
                {
                    _page.AddComment(comment, baseline + atom.Font.Metrics.AscentEm * atom.FontSize);
                }

                continue;
            }

            if (atom.Bookmark is { } bookmark)
            {
                // A band is drawn on every page; a band-only bookmark points at the first page.
                // A name that also exists in the body is recorded there, not from the band.
                var record = !_repeatingTableHeader
                    && (!_band || (_fields.IsBandBookmark(bookmark.Name) && !_outline.ContainsBookmark(bookmark.Name)));
                if (record)
                {
                    _outline.AddBookmark(bookmark.Name, _page.CurrentPageObject, baseline + atom.Font.Metrics.AscentEm * atom.FontSize);
                    _fields.RecordBookmark(bookmark.Name, _page.DisplayPageNumber);
                }

                continue;
            }

            if (atom.IsTab)
            {
                var (stopX, leader, alignment) = NextTab(tabStops, lineStart, x, atom.FontSize * MinTabGapEm);
                var textX = alignment switch
                {
                    TabStopAlignment.Right => stopX - TrailingWidth(atoms, atomIndex),
                    TabStopAlignment.Decimal => stopX - TrailingWidth(atoms, atomIndex, toDecimal: true),
                    TabStopAlignment.Center => stopX - (TrailingWidth(atoms, atomIndex) / 2),
                    _ => stopX,
                };
                // The leader fills the gap up to the text after the tab; for a right or center
                // tab that text starts before the stop, so the leader must end there.
                var leaderEnd = Math.Min(stopX, textX) - (alignment == TabStopAlignment.Left ? 0 : 2);
                if (leader != TabStopLeader.None && leaderEnd > x + 2)
                {
                    var leaderStyle = leader switch
                    {
                        TabStopLeader.Hyphen => TextUnderlineStyle.Dashed,
                        TabStopLeader.Underscore => TextUnderlineStyle.Single,
                        _ => TextUnderlineStyle.Dotted,
                    };
                    _page.DrawLine(X(x + 2, 0), baseline, X(leaderEnd, 0), baseline, Color.Black, 0.5, leaderStyle);
                }

                x = Math.Max(x, textX);
                continue;
            }

            if (atom.Image is { } image)
            {
                FlushUnderline();
                FlushStrike();
                var imageLeft = X(x, image.Width);
                if (image.AltText is { Length: > 0 } alt)
                {
                    _page.BeginAlt(alt);
                }

                DrawCropped(image.Crop, image.Width, image.Height, imageLeft, baseline,
                    (drawWidth, drawHeight, drawX, drawY) => _page.DrawImage(image.Handle.ResourceName, drawWidth, drawHeight, drawX, drawY));
                if (image.AltText is { Length: > 0 })
                {
                    _page.EndMarkedContent();
                }

                if (atom.Hyperlink is { } imageLink)
                {
                    _page.AddLinkAnnotation(imageLink, imageLeft, imageLeft + image.Width, baseline, image.Height / 0.8);
                }

                x += image.Width;
                continue;
            }

            var width = MeasureAtom(atom);
            var atomBaseline = baseline + atom.BaselineShift;

            if (atom.Background is { } background && atom.Text.Length > 0)
            {
                var ascent = atom.Font.Metrics.AscentEm * atom.FontSize;
                var lineBox = atom.Font.Metrics.LineHeightEm * atom.FontSize;
                _page.FillRectangle(X(x, width), atomBaseline + ascent - lineBox, width, lineBox, background);
            }

            if (atom.Border is { } border && atom.Text.Length > 0 && border.Style != BorderStyle.None)
            {
                var borderAscent = atom.Font.Metrics.AscentEm * atom.FontSize;
                var borderBox = atom.Font.Metrics.LineHeightEm * atom.FontSize;
                _page.StrokeRectangle(X(x, width), atomBaseline + borderAscent - borderBox, width, borderBox, border.Width.Points, border.Color);
            }

            if (atom.UnderlineStyle != TextUnderlineStyle.None && atom.Text.Length > 0)
            {
                var thickness = atom.FontSize * UnderlineThicknessFactor;
                var position = atomBaseline - atom.FontSize * UnderlinePositionFactor;
                var color = atom.UnderlineColor ?? atom.Color;
                if (inUnderline && Math.Abs(thickness - underlineThickness) < 1e-9
                    && Math.Abs(position - underlinePosition) < 1e-9
                    && underlineColor == color && underlineStyle == atom.UnderlineStyle)
                {
                    underlineEnd = x + width;
                }
                else
                {
                    FlushUnderline();
                    underlineStart = x;
                    underlineEnd = x + width;
                    underlineThickness = thickness;
                    underlinePosition = position;
                    underlineColor = color;
                    underlineStyle = atom.UnderlineStyle;
                    inUnderline = true;
                }
            }
            else
            {
                FlushUnderline();
            }

            if (atom.Strikethrough != TextStrikethrough.None && atom.Text.Length > 0)
            {
                var style = atom.Strikethrough == TextStrikethrough.Double
                    ? TextUnderlineStyle.Double
                    : TextUnderlineStyle.Single;
                var thickness = atom.FontSize * UnderlineThicknessFactor;
                var position = atomBaseline + atom.FontSize * StrikethroughPositionFactor;
                if (inStrike && Math.Abs(thickness - strikeThickness) < 1e-9
                    && Math.Abs(position - strikePosition) < 1e-9
                    && strikeColor == atom.Color && strikeStyle == style)
                {
                    strikeEnd = x + width;
                }
                else
                {
                    FlushStrike();
                    strikeStart = x;
                    strikeEnd = x + width;
                    strikeThickness = thickness;
                    strikePosition = position;
                    strikeColor = atom.Color;
                    strikeStyle = style;
                    inStrike = true;
                }
            }
            else
            {
                FlushStrike();
            }

            if (atom.Hyperlink is { } linkTarget && atom.Text.Length > 0)
            {
                _page.AddLinkAnnotation(linkTarget, X(x, width), X(x, width) + width, atomBaseline, atom.FontSize);
            }

            _page.WriteLine(
                atom.Font, atom.Text, atom.FontSize, atom.Color, X(x, width), atomBaseline,
                atom.CharacterSpacing, atom.HorizontalScaling, atom.BidiLevels);
            x += width;
            if (justifyExtra > 0 && IsWhiteSpace(atom.Text))
            {
                x += justifyExtra;
            }
        }

        FlushUnderline();
        FlushStrike();
        if (actualText is not null)
        {
            _page.EndMarkedContent();
        }

        void FlushUnderline()
        {
            if (!inUnderline)
            {
                return;
            }

            _page.DrawRule(underlineColor, X(underlineStart, underlineEnd - underlineStart), X(underlineStart, underlineEnd - underlineStart) + underlineEnd - underlineStart, underlinePosition, underlineThickness, underlineStyle);
            inUnderline = false;
        }

        void FlushStrike()
        {
            if (!inStrike)
            {
                return;
            }

            _page.DrawRule(strikeColor, X(strikeStart, strikeEnd - strikeStart), X(strikeStart, strikeEnd - strikeStart) + strikeEnd - strikeStart, strikePosition, strikeThickness, strikeStyle);
            inStrike = false;
        }
    }

    /// <summary>
    /// applies the conditional text formatting of a table area to the runs (the direct
    /// formatting of the run wins).
    /// </summary>
    private static IReadOnlyList<Inline> ApplyArea(IReadOnlyList<Inline> inlines, TableStyleArea area)
    {
        if (area.Bold is not true && area.Italic is not true && area.TextColor is null)
        {
            return inlines;
        }

        var result = new List<Inline>(inlines.Count);
        foreach (var inline in inlines)
        {
            if (inline is not TextRun run)
            {
                result.Add(inline);
                continue;
            }

            result.Add(new TextRun(run.Text)
            {
                Bold = run.Bold ?? (area.Bold == true ? true : null),
                Italic = run.Italic ?? (area.Italic == true ? true : null),
                Underline = run.Underline,
                UnderlineStyle = run.UnderlineStyle,
                UnderlineColor = run.UnderlineColor,
                Strikethrough = run.Strikethrough,
                Baseline = run.Baseline,
                AllCaps = run.AllCaps,
                SmallCaps = run.SmallCaps,
                Effects = run.Effects,
                Background = run.Background,
                Highlight = run.Highlight,
                Kerning = run.Kerning,
                CharacterSpacing = run.CharacterSpacing,
                HorizontalScaling = run.HorizontalScaling,
                Position = run.Position,
                FontFamily = run.FontFamily,
                FontSize = run.FontSize,
                Hidden = run.Hidden,
                Border = run.Border,
                Color = run.Color ?? area.TextColor,
                CharacterStyleName = run.CharacterStyleName,
            });
        }

        return result;
    }

    /// <summary>
    /// The column widths of an AutoFit table from its cells' text: the longest word and the unbroken text of every cell
    /// that spans one column, plus the cell padding (<see cref="TableLayout.AutoFitWidths"/>).
    /// </summary>
    private double[] AutoFitColumns(TableBlock table, int columns, double availableWidth)
    {
        var (minimum, maximum) = ColumnContentWidths(table, columns, withMaximum: true);
        var target = availableWidth * (table.WidthPercent ?? 100) / 100;
        return TableLayout.AutoFitWidths(minimum, maximum, target, fill: table.WidthPercent is not null);
    }

    /// <summary>
    /// Per column, the longest word and (with <paramref name="withMaximum"/>) the unbroken text of its single-column
    /// cells, each plus the padding.
    /// </summary>
    private (double[] Minimum, double[] Maximum) ColumnContentWidths(TableBlock table, int columns, bool withMaximum)
    {
        var minimum = new double[columns];
        var maximum = new double[columns];
        var covered = new int[columns];
        foreach (var row in table.Rows)
        {
            var column = 0;
            foreach (var cell in row.Cells)
            {
                while (column < columns && covered[column] > 0)
                {
                    covered[column++]--;
                }

                if (column >= columns)
                {
                    break;
                }

                var span = Math.Clamp(cell.ColumnSpan, 1, columns - column);
                for (var offset = 0; offset < span && cell.RowSpan > 1; offset++)
                {
                    covered[column + offset] = cell.RowSpan - 1;
                }

                // shortcut: cells spanning several columns, images and nested tables do not size the columns; measure them when a table needs it.
                if (span == 1)
                {
                    var padding = 2 * TableLayout.CellPadding(table, cell).Horizontal.Points;
                    var atoms = ExtractAtoms([.. cell.Blocks.Where(block => block is Paragraph or Heading)], cell)
                        .Where(atom => atom.Bookmark is null && atom.Comment is null).ToList();
                    var longestWord = atoms.Where(atom => !atom.IsTab && !atom.IsHardBreak)
                        .SelectMany(SplitAtSpaces).Select(MeasureAtom).DefaultIfEmpty(0).Max();
                    var longestLine = withMaximum ? WrapAtoms(atoms, double.MaxValue).Select(LineWidth).DefaultIfEmpty(0).Max() : 0;
                    minimum[column] = Math.Max(minimum[column], longestWord + padding);
                    maximum[column] = Math.Max(maximum[column], Math.Max(longestWord, longestLine) + padding);
                }

                column += span;
            }

            for (; column < columns; column++)
            {
                if (covered[column] > 0)
                {
                    covered[column]--;
                }
            }
        }

        return (minimum, maximum);
    }

    private List<TextAtom> ExtractAtoms(IReadOnlyList<Block> blocks, TableCell cell, TableStyleArea? area = null)
    {
        var atoms = new List<TextAtom>();
        var firstBlock = true;
        foreach (var block in blocks)
        {
            var inlines = block is Heading heading ? heading.Inlines : ((Paragraph)block).Inlines;
            if (!firstBlock)
            {
                atoms.Add(new TextAtom(string.Empty, _page.Fonts.GetFont(null, false, false),
                    _defaults.FontSize.Points, _defaults.TextColor, IsHardBreak: true));
            }

            var headingSize = block is Heading headingBlock ? Typography.HeadingSize(headingBlock.Level).Points : (double?)null;
            var style = block is Heading styledHeading ? styledHeading.Style : StyleOf((Paragraph)block);
            var cellStyle = TableLayout.CellParagraphStyle(style, cell);
            var alignment = cellStyle.Alignment ?? TextAlignment.Left;
            var effective = area is null ? inlines : ApplyArea(inlines, area);
            atoms.AddRange(BuildAtoms(effective, headingSize, cellStyle).Select(atom => atom with { Alignment = alignment, RightToLeft = style.RightToLeft == true }));
            firstBlock = false;
        }

        return atoms;
    }

    /// <summary>
    /// The bullet of a list level as in DOCX and ODT (•, ◦, ▪); a font that cannot encode it (the
    /// WinAnsi standard fonts lack ◦ and ▪) keeps •.
    /// </summary>
    private string Bullet(int level)
    {
        var symbol = ((level - 1) % 3) switch
        {
            0 => "\u2022",
            1 => "\u25E6",
            _ => "\u25AA",
        };
        return _page.Fonts.GetFont(null, bold: false, italic: false).CanEncode(symbol) ? symbol : "\u2022";
    }

    /// <summary>
    /// Lists in a table cell become paragraphs with a hanging indent: the marker ("•", "1.") hangs
    /// <see cref="ListHanging"/> before the text, a tab leads to the text and the text is indented
    /// <see cref="ListIndent"/> per level, as in the page body; wrapped lines align with the text.
    /// </summary>
    private IEnumerable<Block> FlattenCellLists(IEnumerable<Block> blocks, int parentLevel, NumberingLookup numbering, Func<int, string> bullet)
    {
        foreach (var block in blocks)
        {
            if (block is not ListBlock list)
            {
                // Further content of a list item is indented to the item's text.
                if (parentLevel == 0 || block is not Paragraph paragraph)
                {
                    yield return block;
                }
                else
                {
                    var style = StyleOf(paragraph);
                    yield return new Paragraph(paragraph.Inlines)
                    {
                        Style = style with
                        {
                            LeftIndent = Length.FromPoints((style.LeftIndent?.Points ?? 0) + (parentLevel * ListIndent)),
                        },
                    };
                }

                continue;
            }

            // The level follows the actual nesting; NestingLevel can increase it (as in RenderList).
            var level = Math.Max(list.NestingLevel, parentLevel + 1);
            var spec = numbering.Level(list, level);
            var number = spec.Start;
            foreach (var item in list.Items)
            {
                var marker = new TextRun((spec.Kind == ListKind.Number ? FormatListNumber(number, spec.Format) + "." : bullet(level)) + "\t")
                {
                    Bold = spec.NumberBold,
                    Color = spec.NumberColor,
                };
                number++;

                // The marker goes onto the item's first paragraph; an item starting with something
                // else (a nested list, an image) gets the marker on a line of its own.
                var inner = FlattenCellLists(item.Blocks, level, numbering, bullet).ToList();
                if (item.Blocks.FirstOrDefault() is Paragraph && inner.Count > 0 && inner[0] is Paragraph first)
                {
                    inner[0] = Hanging(first, marker);
                }
                else
                {
                    var itemStyle = new ParagraphStyle { LeftIndent = Length.FromPoints(level * ListIndent) };
                    inner.Insert(0, Hanging(new Paragraph([]) { Style = itemStyle }, marker));
                }

                foreach (var flattened in inner)
                {
                    yield return flattened;
                }
            }
        }

        Paragraph Hanging(Paragraph paragraph, TextRun marker)
        {
            var style = StyleOf(paragraph);
            return new Paragraph([marker, .. paragraph.Inlines])
            {
                Style = style with
                {
                    FirstLineIndent = Length.FromPoints(-ListHanging),
                    TabStops = [new TabStop(style.LeftIndent ?? Length.Zero), .. style.TabStops ?? []],
                },
            };
        }
    }

    private sealed record TextAtom(
        string Text,
        PdfFontHandle Font,
        double FontSize,
        Color Color,
        bool IsHardBreak = false,
        TextAlignment Alignment = TextAlignment.Left,
        bool RightToLeft = false,
        byte[]? BidiLevels = null,
        TextUnderlineStyle UnderlineStyle = TextUnderlineStyle.None,
        Color? UnderlineColor = null,
        TextStrikethrough Strikethrough = TextStrikethrough.None,
        double BaselineShift = 0,
        Color? Background = null,
        double CharacterSpacing = 0,
        double HorizontalScaling = 100,
        ParagraphBorder? Border = null,
        bool IsTab = false,
        bool IsSoftHyphen = false,
        string? Hyperlink = null,
        (int Number, string Text)? Note = null,
        Bookmark? Bookmark = null,
        Comment? Comment = null,
        InlineImageBox? Image = null,
        bool JoinsPrevious = false);

    /// <summary>An image in a line: drawn on the baseline at its size.</summary>
    private sealed record InlineImageBox(PdfImageHandle Handle, double Width, double Height, ImageCrop? Crop, string? AltText);

    /// <summary>Height above the baseline: the ascender, or for an image its height and the font's line gap above it.</summary>
    private static double AtomAscent(TextAtom atom)
        => atom.Image is { } image
            ? image.Height + ((atom.Font.Metrics.LineHeightEm - atom.Font.Metrics.AscentEm - atom.Font.Metrics.DescentEm) * atom.FontSize)
            : atom.Font.Metrics.AscentEm * atom.FontSize;

    /// <summary>The line height an atom needs: line gap, image and descent for an image (as MigraDoc and Word).</summary>
    private static double AtomLineHeight(TextAtom atom)
        => atom.Image is { } image
            ? image.Height + ((atom.Font.Metrics.LineHeightEm - atom.Font.Metrics.AscentEm) * atom.FontSize)
            : atom.Font.Metrics.LineHeightEm * atom.FontSize;

    private sealed record ListMarker(string Text, double X, Color? Color = null, bool Bold = false);
}
