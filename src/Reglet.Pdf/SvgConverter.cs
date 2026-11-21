using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// converts a documented subset of SVG into PDF content operators (phase AA, AA03).
///
/// <para>Supported: the elements <c>svg</c>, <c>g</c>, <c>path</c>, <c>rect</c>, <c>circle</c>,
/// <c>ellipse</c>, <c>line</c>, <c>polyline</c>, <c>polygon</c> and <c>use</c> with the presentation
/// attributes <c>fill</c>, <c>stroke</c>, <c>stroke-width</c>, <c>stroke-dasharray</c>,
/// <c>stroke-dashoffset</c>, <c>stroke-linecap</c>, <c>stroke-linejoin</c>,
/// <c>stroke-miterlimit</c>, <c>fill-rule</c>, <c>fill-opacity</c>, <c>stroke-opacity</c>,
/// <c>opacity</c> and <c>transform</c> (also as an inline <c>style</c> attribute), and the rules of
/// a <c>style</c> element whose selector names an element, a class or an id. The path
/// commands M, L, H, V, C, S, Q, T, A and Z are supported.</para>
///
/// <para>Not supported (and reported as a warning, never silently dropped): complex text, images,
/// unsupported gradients and patterns, complex clipping paths, filters, masks and markers, and style
/// rules with conditional groups or other selectors. A stroke
/// painted with a gradient or a pattern is not supported. <c>opacity</c> is applied to the fill and
/// the stroke of the element and its children; a group is not composited as one unit, so overlapping
/// children show their overlap.</para>
///
/// <para>The coordinates of the result are the user units of the SVG (y grows downwards); the
/// caller places them with a transformation matrix.</para>
/// </summary>
internal static class SvgConverter
{
    /// <summary>SVG pixels per inch (the CSS default of 96 dpi).</summary>
    private const double PixelsPerInch = 96.0;

    /// <summary>The namespace of the older <c>xlink:href</c> attribute of a <c>use</c> element.</summary>
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Magic constant for approximating a quarter circle with a cubic Bézier curve.</summary>
    private const double EllipseKappa = 0.5522847498307936;

    /// <summary>
    /// Converts an SVG file. Returns <c>null</c> when the file cannot be converted; the warnings
    /// then describe the reason.
    /// </summary>
    /// <param name="data">The SVG bytes.</param>
    /// <param name="alphaState">
    /// Maps a key of an opacity value (for example <c>"fill:0.5"</c>) to the name of an
    /// ExtGState resource; the caller creates the resource.
    /// </param>
    /// <param name="warnings">The collected warnings.</param>
    /// <param name="renderText">Renders supported text and returns its measured width.</param>
    /// <param name="registerGradient">Registers a supported gradient as a PDF shading.</param>
    /// <param name="registerPattern">Registers a supported pattern as a PDF tiling pattern.</param>
    /// <param name="registerImage">Registers an embedded raster image and returns its resource and size.</param>
    /// <param name="registerForm">
    /// Registers a transparency group (content and bounding box) as a form XObject and returns its
    /// resource name (OF-B07).
    /// </param>
    /// <param name="registerMask">
    /// Registers a mask (content and bounding box) as a luminance soft mask and returns the name of
    /// its graphics state (OF-B07).
    /// </param>
    /// <param name="registerAlphaMask">
    /// Registers a silhouette (content and bounding box) as an alpha soft mask and returns the name of
    /// its graphics state (a stroke painted with a paint server, OF-B07).
    /// </param>
    public static SvgDrawing? TryConvert(
        ReadOnlyMemory<byte> data,
        Func<string, string> alphaState,
        out IReadOnlyList<string> warnings,
        Func<string, string?, bool, bool, double, Color, (string Content, double Width)?>? renderText = null,
        Func<SvgGradient, string>? registerGradient = null,
        Func<SvgPattern, string>? registerPattern = null,
        Func<byte[], ImageMediaType, PdfImageHandle?>? registerImage = null,
        Func<string, double, double, double, double, string>? registerForm = null,
        Func<string, double, double, double, double, string>? registerMask = null,
        Func<string, double, double, double, double, string>? registerAlphaMask = null)
    {
        ArgumentNullException.ThrowIfNull(alphaState);
        var reports = new List<string>();

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(ReaderGuard.LimitXmlDepth(reader));
        }
        catch (XmlException exception)
        {
            reports.Add($"SVG: the file is not valid XML ({exception.Message}).");
            warnings = reports;
            return null;
        }

        if (document.Root is not { } root || !IsElement(root, "svg"))
        {
            reports.Add("SVG: the root element is not <svg>.");
            warnings = reports;
            return null;
        }

        var viewport = ReadViewport(root, reports);
        if (viewport is null)
        {
            warnings = reports;
            return null;
        }

        var content = new StringBuilder();
        var usesPatterns = false;
        Func<SvgPattern, string>? trackedPattern = registerPattern is null ? null : pattern =>
        {
            usesPatterns = true;
            return registerPattern(pattern);
        };
        var callbacks = new SvgCallbacks(
            renderText, registerGradient, trackedPattern, registerImage, registerForm, registerMask, registerAlphaMask,
            viewport.Value.MinX, viewport.Value.MinY,
            viewport.Value.MinX + viewport.Value.UserWidth, viewport.Value.MinY + viewport.Value.UserHeight);
        var (scaleX, scaleY, translateX, translateY, clip) = ViewBoxTransform(root, viewport.Value, reports);
        var transformed = scaleX != 1 || scaleY != 1 || translateX != 0 || translateY != 0 || clip;
        if (transformed)
        {
            content.Append("q\n");
            if (clip)
            {
                content.Append("0 0 ").Append(Number(viewport.Value.UserWidth)).Append(' ')
                    .Append(Number(viewport.Value.UserHeight)).Append(" re W n\n");
            }

            content.Append(Number(scaleX)).Append(" 0 0 ").Append(Number(scaleY)).Append(' ')
                .Append(Number(translateX)).Append(' ').Append(Number(translateY)).Append(" cm\n");
        }

        var clips = root.Descendants().Where(element => IsElement(element, "clipPath") && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var gradients = root.Descendants().Where(element => (IsElement(element, "linearGradient") || IsElement(element, "radialGradient"))
                && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var patterns = root.Descendants().Where(element => IsElement(element, "pattern") && element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        // Every referenced element counts, not only the paint servers: <use> points at any id.
        var ids = root.Descendants().Where(element => element.Attribute("id") is not null)
            .GroupBy(element => element.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var rules = ReadStyleSheets(root, reports);
        var rootStyle = SvgStyle.Default.Clone();
        rootStyle.Ctm = new Matrix(scaleX, 0, 0, scaleY, translateX, translateY);
        RenderElement(
            root, rootStyle, content, reports, alphaState, clips, gradients, patterns, ids, rules,
            callbacks, depth: 0);
        if (transformed)
        {
            content.Append("Q\n");
        }

        warnings = reports;
        return new SvgDrawing(
            viewport.Value.DisplayWidth,
            viewport.Value.DisplayHeight,
            viewport.Value.UserWidth,
            viewport.Value.UserHeight,
            content.ToString(),
            usesPatterns);
    }

    // MARK: - Viewport

    private readonly record struct Viewport(
        double MinX,
        double MinY,
        double UserWidth,
        double UserHeight,
        double DisplayWidth,
        double DisplayHeight,
        bool HasViewBox);

    /// <summary>
    /// Reads the user space and the display size of the SVG. The display size is returned in
    /// points, the user space in SVG units.
    /// </summary>
    private static Viewport? ReadViewport(XElement root, List<string> reports)
    {
        var viewBox = ParseNumbers(root.Attribute("viewBox")?.Value);
        var widthAttribute = root.Attribute("width")?.Value;
        var heightAttribute = root.Attribute("height")?.Value;

        double userWidth = 0;
        double userHeight = 0;
        var hasViewBox = viewBox is { Count: >= 4 } && viewBox[2] > 0 && viewBox[3] > 0;
        if (hasViewBox)
        {
            userWidth = viewBox![2];
            userHeight = viewBox[3];
        }

        var displayWidth = widthAttribute is null ? 0 : ParseLength(widthAttribute, reports, "width");
        var displayHeight = heightAttribute is null ? 0 : ParseLength(heightAttribute, reports, "height");

        if (userWidth <= 0 || userHeight <= 0)
        {
            // Without a view box the user units are the CSS pixels of the size attributes.
            userWidth = widthAttribute is null ? 0 : ParseLength(widthAttribute, reports, "width", unit: LengthUnit.Pixel);
            userHeight = heightAttribute is null ? 0 : ParseLength(heightAttribute, reports, "height", unit: LengthUnit.Pixel);
            if (userWidth <= 0 || userHeight <= 0)
            {
                reports.Add("SVG: the image has neither a view box nor a size.");
                return null;
            }
        }

        if (displayWidth <= 0 && displayHeight <= 0)
        {
            displayWidth = userWidth * 72.0 / PixelsPerInch;
            displayHeight = userHeight * 72.0 / PixelsPerInch;
        }
        else if (displayWidth <= 0)
        {
            displayWidth = displayHeight * userWidth / userHeight;
        }
        else if (displayHeight <= 0)
        {
            displayHeight = displayWidth * userHeight / userWidth;
        }

        return new Viewport(hasViewBox ? viewBox![0] : 0, hasViewBox ? viewBox![1] : 0,
            userWidth, userHeight, displayWidth, displayHeight, hasViewBox);
    }

    /// <summary>The validated <c>preserveAspectRatio</c> of an element.</summary>
    private static (string Align, string Mode) ParsePreserveAspectRatio(XElement element, List<string> reports)
    {
        var parts = (element.Attribute("preserveAspectRatio")?.Value ?? "xMidYMid meet")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var align = parts.Length > 0 ? parts[0] : "xMidYMid";
        var mode = parts.Length > 1 ? parts[1] : "meet";
        if (parts.Length > 2 || (mode != "meet" && mode != "slice")
            || (align != "none" && (align.Length != 8 || !align.StartsWith('x') || align[4] != 'Y'
                || align[1..4] is not ("Min" or "Mid" or "Max") || align[5..8] is not ("Min" or "Mid" or "Max"))))
        {
            reports.Add("SVG: the preserveAspectRatio value is invalid; xMidYMid meet is used.");
            align = "xMidYMid";
            mode = "meet";
        }

        return (align, mode);
    }

    private static (double ScaleX, double ScaleY, double X, double Y, bool Clip) ViewBoxTransform(
        XElement root, Viewport viewport, List<string> reports)
    {
        if (!viewport.HasViewBox)
        {
            return (1, 1, 0, 0, false);
        }

        var (align, mode) = ParsePreserveAspectRatio(root, reports);
        if (align == "none")
        {
            return (1, 1, -viewport.MinX, -viewport.MinY, false);
        }

        var xScale = viewport.DisplayWidth / viewport.UserWidth;
        var yScale = viewport.DisplayHeight / viewport.UserHeight;
        var uniform = mode == "slice" ? Math.Max(xScale, yScale) : Math.Min(xScale, yScale);
        var scaleX = uniform / xScale;
        var scaleY = uniform / yScale;
        static double Alignment(string value) => value switch { "Min" => 0, "Mid" => 0.5, _ => 1 };
        var x = ((1 - scaleX) * viewport.UserWidth * Alignment(align[1..4])) - (viewport.MinX * scaleX);
        var y = ((1 - scaleY) * viewport.UserHeight * Alignment(align[5..8])) - (viewport.MinY * scaleY);
        return (scaleX, scaleY, x, y, mode == "slice");
    }

    // MARK: - Style sheets

    /// <summary>
    /// Paints a stroke whose colour is a gradient or a pattern (OF-B07). PDF cannot stroke with a
    /// shading, so the stroked shape is rendered once as an alpha soft mask and the paint server is
    /// laid over the shape through that mask.
    /// </summary>
    private static void RenderStrokePaint(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth,
        string paint,
        Func<string, double, double, double, double, string> registerMask)
    {
        // The shape as a silhouette: solid stroke, no fill and no paint server, so it does not recurse.
        var silhouette = style.Clone();
        silhouette.StrokePaint = null;
        silhouette.Fill = null;
        silhouette.FillGradient = null;
        silhouette.GroupOpacity = 1;
        silhouette.OwnOpacity = null;
        silhouette.Mask = null;
        silhouette.Stroke = Color.Black;
        silhouette.StrokeAlpha = 1;
        // The element's own transform is applied by RenderElement; adding it here would apply it twice.
        var mask = new StringBuilder();
        RenderElement(
            element,
            silhouette,
            mask,
            new List<string>(),
            _ => "UNSUPPORTED-SILHOUETTE",
            ids,
            gradients,
            patterns,
            ids,
            rules,
            new SvgCallbacks(null, null, null, null, null, null, null) { Budget = callbacks.Budget },
            depth + 1,
            forced: silhouette);

        var state = registerMask(mask.ToString(), callbacks.BoxMinX, callbacks.BoxMinY, callbacks.BoxMaxX, callbacks.BoxMaxY);

        // The paint server as a fill over the same shape, limited to the stroke by the mask.
        var paintStyle = style.Clone();
        paintStyle.Stroke = null;
        paintStyle.StrokePaint = null;
        paintStyle.Mask = null;
        paintStyle.OwnOpacity = null;
        paintStyle.GroupOpacity = 1;
        paintStyle.Fill = null;
        paintStyle.FillGradient = paint;
        content.Append("q\n/").Append(state).Append(" gs\n");
        RenderElement(
            element,
            paintStyle,
            content,
            reports,
            alphaState,
            clips,
            gradients,
            patterns,
            ids,
            rules,
            new SvgCallbacks(null, callbacks.RegisterGradient, callbacks.RegisterPattern, null, null, null, null) { Budget = callbacks.Budget },
            depth + 1,
            forced: paintStyle);
        content.Append("Q\n");
    }

    /// <summary>An on-curve vertex of a shape with its incoming and outgoing direction (OF-B07 markers).</summary>
    private sealed record SvgVertex(
        double X,
        double Y,
        double InX,
        double InY,
        double OutX,
        double OutY,
        bool SubpathStart = false,
        bool SubpathEnd = false);

    /// <summary>A marker attribute value that actually references a marker.</summary>
    private static string? MarkerReference(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (value is null)
        {
            value = element.Attribute("marker")?.Value;
        }

        return string.IsNullOrEmpty(value) || value == "none" ? null : value;
    }

    /// <summary>
    /// Draws the <c>marker-start</c>, <c>marker-mid</c> and <c>marker-end</c> of a shape (OF-B07):
    /// the marker is placed at the vertex, oriented along the tangent when <c>orient</c> is
    /// <c>auto</c>, and scaled with the stroke width unless <c>markerUnits</c> is
    /// <c>userSpaceOnUse</c>. A marker with a view box is reported.
    /// </summary>
    private static void RenderMarkers(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        IReadOnlyList<SvgVertex> vertices,
        SvgCallbacks callbacks,
        int depth)
    {
        var start = MarkerReference(element, "marker-start");
        var mid = MarkerReference(element, "marker-mid");
        var end = MarkerReference(element, "marker-end");

        if (vertices.Count == 0 || (start is null && mid is null && end is null))
        {
            return;
        }

        foreach (var vertex in vertices)
        {
            if (start is not null && vertex.SubpathStart)
            {
                // auto-start-reverse turns the start marker against the path direction.
                DrawMarker(start, vertex, vertex.OutX, vertex.OutY, reverse: true);
            }

            if (end is not null && vertex.SubpathEnd)
            {
                DrawMarker(end, vertex, vertex.InX, vertex.InY, reverse: true);
            }

            if (mid is not null && !vertex.SubpathStart && !vertex.SubpathEnd)
            {
                var lengthIn = Math.Sqrt((vertex.InX * vertex.InX) + (vertex.InY * vertex.InY));
                var lengthOut = Math.Sqrt((vertex.OutX * vertex.OutX) + (vertex.OutY * vertex.OutY));
                var directionX = (lengthIn > 0 ? vertex.InX / lengthIn : 0) + (lengthOut > 0 ? vertex.OutX / lengthOut : 0);
                var directionY = (lengthIn > 0 ? vertex.InY / lengthIn : 0) + (lengthOut > 0 ? vertex.OutY / lengthOut : 0);
                if (Math.Abs(directionX) + Math.Abs(directionY) < 1e-9)
                {
                    directionX = vertex.OutX;
                    directionY = vertex.OutY;
                }

                DrawMarker(mid, vertex, directionX, directionY, reverse: false);
            }
        }

        void DrawMarker(string reference, SvgVertex vertex, double directionX, double directionY, bool reverse)
        {
            var id = Reference(reference);
            if (id is null || !ids.TryGetValue(id, out var marker) || marker.Name.LocalName != "marker")
            {
                reports.Add($"SVG: the marker '{reference}' is not found.");
                return;
            }

            if (marker.Attribute("viewBox") is not null)
            {
                reports.Add("SVG: a marker with a view box is not drawn.");
                return;
            }

            var width = ParseNumber(marker.Attribute("markerWidth")?.Value) ?? 3;
            var height = ParseNumber(marker.Attribute("markerHeight")?.Value) ?? 3;
            var refX = ParseNumber(marker.Attribute("refX")?.Value) ?? 0;
            var refY = ParseNumber(marker.Attribute("refY")?.Value) ?? 0;
            var units = marker.Attribute("markerUnits")?.Value ?? "strokeWidth";
            var scale = units == "userSpaceOnUse" ? 1 : Math.Max(0.1, style.StrokeWidth);
            var orient = marker.Attribute("orient")?.Value ?? "0";
            var angle = orient == "auto"
                ? Math.Atan2(directionY, directionX) * 180.0 / Math.PI
                : orient == "auto-start-reverse" && reverse
                    ? (Math.Atan2(directionY, directionX) * 180.0 / Math.PI) + 180
                    : ParseNumber(orient) ?? 0;
            if (Math.Sqrt((directionX * directionX) + (directionY * directionY)) < 1e-9 && orient.StartsWith("auto", StringComparison.Ordinal))
            {
                angle = 0;
            }

            var radians = -angle * Math.PI / 180.0;
            content.Append("q\n");
            AppendMatrix(content, transform);
            content.Append(Number(Math.Cos(radians) * scale)).Append(' ').Append(Number(Math.Sin(radians) * scale))
                .Append(' ').Append(Number(-Math.Sin(radians) * scale)).Append(' ').Append(Number(Math.Cos(radians) * scale))
                .Append(' ').Append(Number(vertex.X)).Append(' ').Append(Number(vertex.Y)).Append(" cm\n");
            content.Append("1 0 0 1 ").Append(Number(-refX)).Append(' ').Append(Number(-refY)).Append(" cm\n");
            content.Append("0 0 ").Append(Number(width)).Append(' ').Append(Number(height)).Append(" re W n\n");
            var markerStyle = ReadStyle(marker, SvgStyle.Default, reports, rules);
            foreach (var child in marker.Elements())
            {
                RenderElement(child, markerStyle, content, reports, _ => "UNSUPPORTED-MARKER", new Dictionary<string, XElement>(), new Dictionary<string, XElement>(), new Dictionary<string, XElement>(), ids, rules, callbacks, depth + 1);
            }

            content.Append("Q\n");
        }
    }

    /// <summary>One piece of SVG text with its position, shift, rotation and target length (OF-B07).</summary>
    private sealed record SvgTextSegment(
        string Text,
        SvgStyle Style,
        double? X,
        double? Y,
        double DX,
        double DY,
        double Rotate,
        double? TextLength);

    /// <summary>
    /// Resolves a paint server that references another one (<c>href</c>, common in exported SVGs):
    /// the chain is merged with the child winning per attribute, and the stops or children of the
    /// first definition that has them are taken (OF-B07).
    /// </summary>
    private static XElement? ResolvePaintServer(
        XElement source, IReadOnlyDictionary<string, XElement> pool, string kind, List<string> reports)
    {
        var chain = new List<XElement>();
        var visited = new HashSet<XElement>();
        var current = source;
        var resolved = true;
        while (resolved)
        {
            if (!visited.Add(current))
            {
                reports.Add($"SVG: the {kind} reference chain is cyclic and is not supported.");
                return null;
            }

            chain.Add(current);
            var href = current.Attribute("href")?.Value ?? current.Attribute(XLink + "href")?.Value;
            if (href is null)
            {
                break;
            }

            var id = href.Length > 0 && href[0] == '#' ? href[1..] : href;
            if (!pool.TryGetValue(id, out var next))
            {
                reports.Add($"SVG: the referenced {kind} '{href}' is not found.");
                return null;
            }

            current = next;
        }

        if (chain.Count == 1)
        {
            return source;
        }

        var merged = new XElement(chain[^1]);
        foreach (var element in chain)
        {
            foreach (var attribute in element.Attributes())
            {
                merged.SetAttributeValue(attribute.Name, attribute.Value);
            }
        }

        // The stops or children of the first definition in the chain win.
        foreach (var element in chain)
        {
            if (!element.Elements().Any())
            {
                continue;
            }

            merged.Elements().Remove();
            merged.Add(element.Elements());
            break;
        }

        return merged;
    }

    /// <summary>The id of a <c>url(#id)</c> reference, or null.</summary>
    private static string? Reference(string value)
    {
        var start = value.IndexOf('#');
        var end = value.IndexOf(')', start < 0 ? 0 : start);
        return start < 0 || end < start ? null : value[(start + 1)..end];
    }

    /// <summary>The rotation list of a text element; one value rotates the whole text.</summary>
    private static List<double>? Rotations(XElement element, List<string> reports)
    {
        if (element.Attribute("rotate")?.Value is not { } value)
        {
            return null;
        }

        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            reports.Add("SVG: an empty rotation list is ignored.");
            return null;
        }

        var rotations = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var rotation))
            {
                reports.Add($"SVG: the rotation '{part}' is not a number; the text is not rotated.");
                return null;
            }

            rotations.Add(rotation);
        }

        return rotations;
    }

    /// <summary>A single rotation for a <c>tspan</c>; a per-glyph list is reported.</summary>
    private static double Rotation(XElement element, List<string> reports)
    {
        var rotations = Rotations(element, reports);
        if (rotations is null)
        {
            return 0;
        }

        if (rotations.Count > 1)
        {
            reports.Add("SVG: a rotation per glyph on a tspan is not supported; the text is not rotated.");
            return 0;
        }

        return rotations[0];
    }

    /// <summary>The grapheme clusters of a text, one per glyph for a rotation list.</summary>
    private static List<string> TextElements(string text)
    {
        var elements = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }

        return elements;
    }

    /// <summary>The callbacks a conversion needs: text, gradients, patterns, images and forms.</summary>
    internal sealed record SvgCallbacks(
        Func<string, string?, bool, bool, double, Color, (string Content, double Width)?>? RenderText,
        Func<SvgGradient, string>? RegisterGradient,
        Func<SvgPattern, string>? RegisterPattern,
        Func<byte[], ImageMediaType, PdfImageHandle?>? RegisterImage,
        Func<string, double, double, double, double, string>? RegisterForm = null,
        Func<string, double, double, double, double, string>? RegisterMask = null,
        Func<string, double, double, double, double, string>? RegisterAlphaMask = null,
        double BoxMinX = 0,
        double BoxMinY = 0,
        double BoxMaxX = 0,
        double BoxMaxY = 0)
    {
        /// <summary>The element count of the whole conversion, shared by the callbacks of masks, patterns and paints.</summary>
        public SvgBudget Budget { get; init; } = new();
    }

    /// <summary>
    /// How many elements a conversion has drawn. The depth limit alone does not bound the work: a group that uses a
    /// group ten times, which uses one ten times, … grows tenfold per level (1.7 KB of SVG drew a million rectangles).
    /// </summary>
    internal sealed class SvgBudget
    {
        /// <summary>The most elements one conversion draws, repeated <c>&lt;use&gt;</c> references included.</summary>
        public const int MaxElements = 250_000;

        public int Drawn { get; set; }

        public bool Reported { get; set; }
    }

    /// <summary>One declaration of a CSS rule of a <c>&lt;style&gt;</c> element, in cascade order.</summary>
    private sealed record SvgStyleRule(int Specificity, int Order, string? Tag, string? Id, string? Class, string Name, string Value);

    /// <summary>
    /// Reads the rules of the <c>style</c> elements of the document, sorted by specificity and
    /// document order. A supported selector names an element, a class or an id, also combined
    /// (for example <c>circle.big</c>); any other selector is reported and not applied.
    /// </summary>
    private static List<SvgStyleRule> ReadStyleSheets(XElement root, List<string> reports)
    {
        var rules = new List<SvgStyleRule>();
        foreach (var sheet in root.Descendants().Where(element => IsElement(element, "style")))
        {
            var type = sheet.Attribute("type")?.Value;
            if (type is not (null or "" or "text/css"))
            {
                reports.Add($"SVG: the style sheet type '{type}' is not applied.");
                continue;
            }

            foreach (var (selectors, body) in ReadStyleRules(RemoveStyleComments(sheet.Value), reports))
            {
                var declarations = ReadStyleDeclarations(body);
                if (declarations.Count == 0)
                {
                    continue;
                }

                foreach (var selector in selectors.Split(','))
                {
                    var text = selector.Trim();
                    if (!TryReadSelector(text, out var tag, out var id, out var className, out var specificity))
                    {
                        reports.Add($"SVG: the style selector '{text}' is not applied.");
                        continue;
                    }

                    foreach (var (name, value) in declarations)
                    {
                        rules.Add(new SvgStyleRule(specificity, rules.Count, tag, id, className, name, value));
                    }
                }
            }
        }

        rules.Sort(static (left, right) => left.Specificity != right.Specificity
            ? left.Specificity.CompareTo(right.Specificity)
            : left.Order.CompareTo(right.Order));
        return rules;
    }

    /// <summary>Reads the <c>selector { declarations }</c> groups of a style sheet.</summary>
    private static List<(string Selectors, string Body)> ReadStyleRules(string text, List<string> reports)
    {
        var groups = new List<(string, string)>();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf("{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var selectors = text[position..open].Trim();
            var depth = 1;
            var end = open + 1;
            while (end < text.Length && depth > 0)
            {
                depth += text[end] switch { '{' => 1, '}' => -1, _ => 0 };
                end++;
            }

            var bodyEnd = depth == 0 ? end - 1 : text.Length;
            if (selectors.StartsWith('@'))
            {
                // Conditional groups such as @media need their own evaluation.
                reports.Add($"SVG: the style sheet group '{selectors}' is not applied.");
            }
            else
            {
                groups.Add((selectors, text[(open + 1)..bodyEnd]));
            }

            position = end;
        }

        return groups;
    }

    /// <summary>Reads the <c>name: value</c> declarations of one rule; <c>!important</c> is dropped.</summary>
    private static List<(string Name, string Value)> ReadStyleDeclarations(string body)
    {
        var declarations = new List<(string, string)>();
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            var important = value.IndexOf('!', StringComparison.Ordinal);
            if (important >= 0)
            {
                value = value[..important].Trim();
            }

            if (name.Length > 0 && value.Length > 0)
            {
                declarations.Add((name, value));
            }
        }

        return declarations;
    }

    /// <summary>Reads a selector made of an element name, one id and one class.</summary>
    private static bool TryReadSelector(
        string selector, out string? tag, out string? id, out string? className, out int specificity)
    {
        tag = null;
        id = null;
        className = null;
        specificity = 0;
        var position = 0;
        while (position < selector.Length && char.IsLetter(selector[position]))
        {
            position++;
        }

        if (position > 0)
        {
            tag = selector[..position];
            specificity = 1;
        }

        while (position < selector.Length)
        {
            var end = position + 1;
            while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '-' or '_'))
            {
                end++;
            }

            var marker = selector[position];
            var name = selector[(position + 1)..end];
            if (name.Length == 0 || marker == '#' && id is not null || marker == '.' && className is not null)
            {
                return false;
            }

            switch (marker)
            {
                case '#':
                    id = name;
                    specificity += 100;
                    break;
                case '.':
                    className = name;
                    specificity += 10;
                    break;
                default:
                    return false;
            }

            position = end;
        }

        return tag is not null || id is not null || className is not null;
    }

    /// <summary>Removes <c>/* … */</c> comments of a style sheet.</summary>
    private static string RemoveStyleComments(string text)
    {
        var start = text.IndexOf("/*", StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var result = new StringBuilder();
        var position = 0;
        while (start >= 0)
        {
            result.Append(text, position, start - position);
            var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                // An unterminated comment reaches the end of the sheet.
                return result.ToString();
            }

            position = end + 2;
            start = text.IndexOf("/*", position, StringComparison.Ordinal);
        }

        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    // MARK: - Style

    /// <summary>The inherited presentation attributes of an element.</summary>
    private sealed class SvgStyle
    {
        public static SvgStyle Default { get; } = new();

        /// <summary>The fill color; null means "none".</summary>
        public Color? Fill { get; set; } = Color.Black;

        /// <summary>The stroke color; null means "none".</summary>
        public Color? Stroke { get; set; }

        /// <summary>The id of a gradient or pattern that paints the stroke (OF-B07); null means none.</summary>
        public string? StrokePaint { get; set; }

        /// <summary>The stroke width in user units.</summary>
        public double StrokeWidth { get; set; } = 1;

        /// <summary>The lengths of the dashes and gaps (<c>stroke-dasharray</c>); null is a solid line.</summary>
        public double[]? DashArray { get; set; }

        /// <summary>Where the dash pattern starts (<c>stroke-dashoffset</c>) in user units.</summary>
        public double DashOffset { get; set; }

        /// <summary>The end of a line (<c>stroke-linecap</c>): 0 butt, 1 round, 2 square.</summary>
        public int LineCap { get; set; }

        /// <summary>How two segments meet (<c>stroke-linejoin</c>): 0 miter, 1 round, 2 bevel.</summary>
        public int LineJoin { get; set; }

        /// <summary>The length limit of a miter corner (<c>stroke-miterlimit</c>) in stroke widths.</summary>
        public double MiterLimit { get; set; } = 4;

        /// <summary>Whether the even-odd rule is used for filling.</summary>
        public bool EvenOdd { get; set; }

        /// <summary>The opacity of the fill (0–1).</summary>
        public double FillOpacity { get; set; } = 1;

        /// <summary>The opacity of the stroke (0–1).</summary>
        public double StrokeOpacity { get; set; } = 1;

        /// <summary>The alpha of the fill color itself (<c>rgba</c>, <c>#RRGGBBAA</c>); multiplies <see cref="FillOpacity"/>.</summary>
        public double FillAlpha { get; set; } = 1;

        /// <summary>The alpha of the stroke color itself; multiplies <see cref="StrokeOpacity"/>.</summary>
        public double StrokeAlpha { get; set; } = 1;

        /// <summary>
        /// The <c>opacity</c> of the element and of all its ancestors (inherited, multiplied down).
        /// It multiplies the fill and the stroke opacity of everything drawn inside, because the
        /// converter has no transparency group.
        /// </summary>
        public double GroupOpacity { get; set; } = 1;

        /// <summary>The opacity the element itself sets (null when it sets none).</summary>
        public double? OwnOpacity { get; set; }

        /// <summary>The id of the mask element (OF-B07); null means none.</summary>
        public string? Mask { get; set; }

        /// <summary><c>visibility</c> (inherited; a child may set it back to visible).</summary>
        public bool Visible { get; set; } = true;

        /// <summary><c>display: none</c>: the element and its children are not rendered (not inherited).</summary>
        public bool DisplayNone { get; set; }

        public string? ClipPath { get; set; }
        public string? FillGradient { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; } = 16;
        public bool FontBold { get; set; }
        public bool FontItalic { get; set; }
        public string TextAnchor { get; set; } = "start";

        /// <summary>
        /// The product of all <c>cm</c> operators in effect (view box, groups, viewports, element
        /// transforms). A PDF pattern's matrix is relative to the enclosing content stream and does
        /// not see them, so a pattern must include this matrix.
        /// </summary>
        public Matrix Ctm { get; set; } = Matrix.Identity;

        public SvgStyle Clone() => (SvgStyle)MemberwiseClone();
    }

    // MARK: - Elements

    private static void RenderElement(
        XElement element,
        SvgStyle inherited,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth,
        SvgStyle? forced = null)
    {
        if (depth > 32)
        {
            reports.Add("SVG: the nesting is too deep; the rest is not drawn.");
            return;
        }

        if (++callbacks.Budget.Drawn > SvgBudget.MaxElements)
        {
            if (!callbacks.Budget.Reported)
            {
                callbacks.Budget.Reported = true;
                reports.Add($"SVG: the drawing has more than {SvgBudget.MaxElements} elements (repeated references included); the rest is not drawn.");
            }

            return;
        }

        var style = forced ?? ReadStyle(element, inherited, reports, rules);
        if (style.DisplayNone)
        {
            return; // hidden layers of Inkscape/Illustrator exports
        }

        var transform = ReadTransform(element.Attribute("transform")?.Value, reports);
        style = style.Clone();
        style.Ctm = Matrix.Multiply(style.Ctm, transform);
        var clipped = style.ClipPath is { } clipPath && ApplyClip(clipPath, clips, element, transform, content, reports);
        if (clipped)
        {
            transform = Matrix.Identity;
        }

        // OF-B07: a mask is painted as a luminance soft mask over the element's content.
        var masked = false;
        if (style.Mask is { } maskId
            && ids.GetValueOrDefault(maskId) is { Name.LocalName: "mask" } maskElement
            && callbacks.RegisterMask is { } registerMask)
        {
            var maskContent = new StringBuilder();
            maskContent.Append("q\n");
            AppendMatrix(maskContent, transform);
            var maskCallbacks = new SvgCallbacks(null, callbacks.RegisterGradient, callbacks.RegisterPattern, null, null) { Budget = callbacks.Budget };
            foreach (var child in maskElement.Elements())
            {
                RenderElement(child, SvgStyle.Default, maskContent, reports, _ => "UNSUPPORTED-MASK", clips, gradients, patterns, ids, rules, maskCallbacks, depth + 1);
            }

            maskContent.Append("Q\n");
            if (maskContent.ToString().Contains("UNSUPPORTED-MASK", StringComparison.Ordinal))
            {
                reports.Add("SVG: a mask that needs unsupported content is not applied.");
            }
            else
            {
                var state = registerMask(
                    maskContent.ToString(), callbacks.BoxMinX, callbacks.BoxMinY, callbacks.BoxMaxX, callbacks.BoxMaxY);
                content.Append("q\n/").Append(state).Append(" gs\n");
                masked = true;
            }
        }
        else if (style.Mask is { } missingMask)
        {
            reports.Add($"SVG: the mask 'url(#{missingMask})' is not found and is not applied.");
        }

        var name = element.Name.LocalName;
        if (style.StrokePaint is { } strokePaint && name is not ("svg" or "g" or "a" or "switch" or "defs" or "text"))
        {
            if (callbacks.RegisterAlphaMask is { } strokeMask)
            {
                RenderStrokePaint(element, style, transform, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth, strokePaint, strokeMask);
            }
            else
            {
                reports.Add($"SVG: the stroke paint 'url(#{strokePaint})' is not applied.");
            }
        }

        var paintDrawn = false;
        if (style.FillGradient is { } paint && name is not ("svg" or "g" or "a" or "switch" or "defs"))
            paintDrawn = patterns.ContainsKey(paint)
                ? RenderPattern(element, style, transform, content, reports, alphaState, clips, gradients, patterns, ids, rules, paint, callbacks, depth)
                : RenderGradient(element, style, transform, content, reports, alphaState, gradients, paint, callbacks.RegisterGradient, callbacks);
        if (paintDrawn)
        {
            style = style.Clone();
            style.Fill = null;
        }
        switch (name)
        {
            case "svg" when depth > 0:
                // OF-B07: a nested <svg> is a viewport of its own (x/y/width/height and viewBox).
                RenderViewport(element, style, transform, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth, widthOverride: null, heightOverride: null);
                break;

            case "svg":
            case "g":
            case "a":
            case "switch":
                RenderGroup(element, style, transform, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth);
                break;

            case "symbol":
                // Only drawn through a <use> element.
                break;

            case "path":
                RenderPath(element, style, transform, content, reports, alphaState, ids, rules, callbacks, depth);
                break;

            case "rect":
                RenderRect(element, style, transform, content, reports, alphaState);
                break;

            case "circle":
                RenderEllipse(element, style, transform, content, reports, alphaState, circle: true);
                break;

            case "ellipse":
                RenderEllipse(element, style, transform, content, reports, alphaState, circle: false);
                break;

            case "line":
                RenderLine(element, style, transform, content, reports, alphaState, ids, rules, callbacks, depth);
                break;

            case "polyline":
            case "polygon":
                RenderPolyline(element, style, transform, content, reports, alphaState, close: name == "polygon", ids, rules, callbacks, depth);
                break;

            case "defs":
            case "title":
            case "desc":
            case "metadata":
                // No drawing content. The rules of a <style> element are read before the drawing starts.
            case "style":
                break;

            case "text":
                RenderText(element, style, transform, content, reports, alphaState, callbacks.RenderText, rules);
                break;

            case "image":
                RenderImage(element, content, reports, callbacks.RegisterImage);
                break;

            case "use":
                RenderUse(element, style, transform, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth);
                break;

            default:
                reports.Add($"SVG: the element <{name}> is not drawn.");
                break;
        }

        // OF-B07: markers are drawn on a path, a line, a polyline and a polygon; other shapes report them.
        if (name is not ("path" or "line" or "polyline" or "polygon"))
        {
            foreach (var attribute in new[] { "marker", "marker-start", "marker-mid", "marker-end" })
            {
                if (element.Attribute(attribute)?.Value is { } marker && marker != "none")
                {
                    reports.Add($"SVG: the marker '{marker}' on a <{name}> is not drawn.");
                }
            }
        }

        if (masked)
        {
            content.Append("Q\n");
        }

        if (clipped)
        {
            content.Append("Q\n");
        }
    }

    /// <summary>
    /// Draws a container as a viewport of its own (OF-B07): a nested <c>svg</c> or a <c>symbol</c> that
    /// a <c>use</c> places. The content is shifted by <c>x</c>/<c>y</c>, clipped to the viewport and
    /// its <c>viewBox</c> is mapped into it.
    /// </summary>
    private static void RenderViewport(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth,
        double? widthOverride,
        double? heightOverride)
    {
        var x = ParseOptionalLength(element.Attribute("x"), reports, "x") ?? 0;
        var y = ParseOptionalLength(element.Attribute("y"), reports, "y") ?? 0;
        var width = widthOverride ?? ParseOptionalLength(element.Attribute("width"), reports, "width");
        var height = heightOverride ?? ParseOptionalLength(element.Attribute("height"), reports, "height");

        content.Append("q\n");
        AppendMatrix(content, transform);
        style = style.Clone();
        if (x != 0 || y != 0)
        {
            AppendMatrix(content, new Matrix(1, 0, 0, 1, x, y));
            style.Ctm = Matrix.Multiply(style.Ctm, new Matrix(1, 0, 0, 1, x, y));
        }

        var viewBox = ParseNumbers(element.Attribute("viewBox")?.Value);
        var hasViewBox = viewBox is { Count: >= 4 } && viewBox[2] > 0 && viewBox[3] > 0;
        if (width is { } viewportWidth && height is { } viewportHeight && viewportWidth > 0 && viewportHeight > 0)
        {
            // The view box is mapped into the viewport; unlike the root, the content here is in user
            // space, so the scale is the ratio of viewport to view box.
            var minX = hasViewBox ? viewBox![0] : 0;
            var minY = hasViewBox ? viewBox![1] : 0;
            var userWidth = hasViewBox ? viewBox![2] : viewportWidth;
            var userHeight = hasViewBox ? viewBox![3] : viewportHeight;
            var (align, mode) = ParsePreserveAspectRatio(element, reports);
            double scaleX, scaleY;
            if (align == "none")
            {
                scaleX = viewportWidth / userWidth;
                scaleY = viewportHeight / userHeight;
            }
            else
            {
                scaleX = scaleY = mode == "slice"
                    ? Math.Max(viewportWidth / userWidth, viewportHeight / userHeight)
                    : Math.Min(viewportWidth / userWidth, viewportHeight / userHeight);
            }

            static double Alignment(string value) => value switch { "Min" => 0, "Mid" => 0.5, _ => 1 };
            var translateX = ((viewportWidth - (userWidth * scaleX)) * Alignment(align[1..4])) - (minX * scaleX);
            var translateY = ((viewportHeight - (userHeight * scaleY)) * Alignment(align[5..8])) - (minY * scaleY);
            content.Append("0 0 ").Append(Number(viewportWidth)).Append(' ').Append(Number(viewportHeight)).Append(" re W n\n");
            if (scaleX != 1 || scaleY != 1 || translateX != 0 || translateY != 0)
            {
                content.Append(Number(scaleX)).Append(" 0 0 ").Append(Number(scaleY)).Append(' ')
                    .Append(Number(translateX)).Append(' ').Append(Number(translateY)).Append(" cm\n");
                style.Ctm = Matrix.Multiply(style.Ctm, new Matrix(scaleX, 0, 0, scaleY, translateX, translateY));
            }
        }
        else if (hasViewBox)
        {
            reports.Add("SVG: a view box without a size is not mapped.");
        }

        foreach (var child in element.Elements())
        {
            RenderElement(child, style, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth + 1);
        }

        content.Append("Q\n");
    }

    /// <summary>A length attribute of a container; zero when it is missing.</summary>
    private static double? ParseOptionalLength(XAttribute? attribute, List<string> reports, string name)
        => attribute is null ? null : ParseLength(attribute.Value, reports, name, LengthUnit.Pixel);

    private static void RenderGroup(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth)
    {
        // OF-B07: a group with its own opacity is composited as a unit, so overlapping children do not
        // show through each other; without a form XObject the opacity only multiplies.
        if (style.OwnOpacity is { } alpha && alpha < 1 && callbacks.RegisterForm is { } registerForm)
        {
            var group = new StringBuilder();
            var inner = style.Clone();
            inner.GroupOpacity = 1;
            inner.OwnOpacity = null;
            inner.Ctm = Matrix.Identity; // inside the form, pattern matrices are relative to the form
            foreach (var child in element.Elements())
            {
                RenderElement(child, inner, group, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth + 1);
            }

            if (group.Length > 0)
            {
                var name = registerForm(group.ToString(), callbacks.BoxMinX, callbacks.BoxMinY, callbacks.BoxMaxX, callbacks.BoxMaxY);
                content.Append("q\n");
                AppendMatrix(content, transform);
                content.Append('/').Append(alphaState($"both:{Number(alpha)}")).Append(" gs\n");
                content.Append('/').Append(name).Append(" Do\nQ\n");
                return;
            }
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        foreach (var child in element.Elements())
        {
            RenderElement(child, style, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth + 1);
        }

        content.Append("Q\n");
    }


    /// <summary>
    /// Draws a <c>use</c> element: the referenced element is drawn again at the place of the
    /// <c>use</c> element, shifted by <c>x</c> and <c>y</c>. The copy takes the style of the
    /// <c>use</c> element, and the referenced element keeps its own <c>transform</c>.
    /// </summary>
    private static void RenderUse(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth)
    {
        var reference = element.Attribute("href")?.Value ?? element.Attribute(XLink + "href")?.Value;
        if (string.IsNullOrEmpty(reference))
        {
            reports.Add("SVG: a <use> element without a reference is not drawn.");
            return;
        }

        var id = reference![0] == '#' ? reference[1..] : reference;
        var target = ids.GetValueOrDefault(id);
        if (target is null || ReferenceEquals(target, element))
        {
            reports.Add($"SVG: the <use> reference '{reference}' is not found.");
            return;
        }

        var offsetX = ParseLength(element.Attribute("x")?.Value ?? "0", reports, "x", LengthUnit.Pixel);
        var offsetY = ParseLength(element.Attribute("y")?.Value ?? "0", reports, "y", LengthUnit.Pixel);
        if (target.Name.LocalName == "symbol")
        {
            // OF-B07: a symbol is a viewport that the use element places and sizes.
            content.Append("q\n");
            AppendMatrix(content, transform);
            AppendMatrix(content, new Matrix(1, 0, 0, 1, offsetX, offsetY));
            var symbolStyle = style.Clone();
            symbolStyle.Ctm = Matrix.Multiply(style.Ctm, new Matrix(1, 0, 0, 1, offsetX, offsetY));
            RenderViewport(
                target,
                symbolStyle,
                Matrix.Identity,
                content,
                reports,
                alphaState,
                clips,
                gradients,
                patterns,
                ids,
                rules,
                callbacks,
                depth + 1,
                ParseOptionalLength(element.Attribute("width"), reports, "width")
                    ?? ParseOptionalLength(target.Attribute("width"), reports, "width"),
                ParseOptionalLength(element.Attribute("height"), reports, "height")
                    ?? ParseOptionalLength(target.Attribute("height"), reports, "height"));
            content.Append("Q\n");
            return;
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        AppendMatrix(content, new Matrix(1, 0, 0, 1, offsetX, offsetY));
        var useStyle = style.Clone();
        useStyle.Ctm = Matrix.Multiply(style.Ctm, new Matrix(1, 0, 0, 1, offsetX, offsetY));
        RenderElement(target, useStyle, content, reports, alphaState, clips, gradients, patterns, ids, rules, callbacks, depth + 1);
        content.Append("Q\n");
    }

    /// <summary>
    /// Draws an embedded raster image (OF-B07). Only a <c>data:</c> URI is supported, because an
    /// external file would have to be loaded; its box is clipped and the image is placed with its
    /// top edge at <c>y</c>, as SVG coordinates grow downwards.
    /// </summary>
    private static void RenderImage(
        XElement element,
        StringBuilder content,
        List<string> reports,
        Func<byte[], ImageMediaType, PdfImageHandle?>? registerImage)
    {
        var reference = element.Attribute("href")?.Value ?? element.Attribute(XLink + "href")?.Value;
        if (string.IsNullOrEmpty(reference))
        {
            reports.Add("SVG: an image without a reference is not drawn.");
            return;
        }

        if (registerImage is null || !reference!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            reports.Add("SVG: an embedded image with an external reference is not drawn.");
            return;
        }

        var marker = reference.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            reports.Add("SVG: an embedded image that is not base64 encoded is not drawn.");
            return;
        }

        var mediaType = reference[5..marker].ToLowerInvariant() switch
        {
            "image/png" => ImageMediaType.Png,
            "image/jpeg" or "image/jpg" => ImageMediaType.Jpeg,
            _ => (ImageMediaType?)null,
        };
        if (mediaType is not { } type)
        {
            reports.Add("SVG: an embedded image of an unsupported type is not drawn.");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(reference[(marker + 8)..]);
        }
        catch (FormatException)
        {
            reports.Add("SVG: an embedded image is not valid base64 and is not drawn.");
            return;
        }

        if (registerImage(bytes, type) is not { } handle)
        {
            reports.Add("SVG: an embedded image could not be embedded and is not drawn.");
            return;
        }

        var x = element.Attribute("x") is { } xValue ? ParseLength(xValue.Value, reports, "x", LengthUnit.Pixel) : 0;
        var y = element.Attribute("y") is { } yValue ? ParseLength(yValue.Value, reports, "y", LengthUnit.Pixel) : 0;
        // Without a size the image keeps its intrinsic one; the handle is in points.
        var naturalWidth = handle.Width * PixelsPerInch / 72.0;
        var naturalHeight = handle.Height * PixelsPerInch / 72.0;
        var width = element.Attribute("width") is { } widthValue ? ParseLength(widthValue.Value, reports, "width", LengthUnit.Pixel) : 0;
        var height = element.Attribute("height") is { } heightValue ? ParseLength(heightValue.Value, reports, "height", LengthUnit.Pixel) : 0;
        if (width <= 0 && height <= 0)
        {
            width = naturalWidth;
            height = naturalHeight;
        }
        else if (width <= 0)
        {
            width = height * naturalWidth / naturalHeight;
        }
        else if (height <= 0)
        {
            height = width * naturalHeight / naturalWidth;
        }

        if (width <= 0 || height <= 0)
        {
            reports.Add("SVG: an embedded image without a usable size is not drawn.");
            return;
        }

        content.Append("q\n");
        content.Append(Number(x)).Append(' ').Append(Number(y)).Append(' ')
            .Append(Number(width)).Append(' ').Append(Number(height)).Append(" re W n\n");
        // The unit square of the image is mapped to the box; the content space grows downwards.
        content.Append(Number(width)).Append(" 0 0 ").Append(Number(-height)).Append(' ')
            .Append(Number(x)).Append(' ').Append(Number(y + height)).Append(" cm\n");
        content.Append('/').Append(handle.ResourceName).Append(" Do\nQ\n");
    }

    private static void RenderText(
        XElement element, SvgStyle style, Matrix transform, StringBuilder content, List<string> reports,
        Func<string, string> alphaState,
        Func<string, string?, bool, bool, double, Color, (string Content, double Width)?>? renderText,
        IReadOnlyList<SvgStyleRule> rules)
    {
        if (element.Attributes().Any(attribute => attribute.Name.LocalName is "dx" or "dy"))
        {
            reports.Add("SVG: complex texts are not drawn.");
            return;
        }

        // OF-B07: a single rotation and textLength stretch the text; a rotation list is per glyph.
        var rotations = Rotations(element, reports);
        var rotate = rotations is { Count: 1 } ? rotations[0] : 0;
        var textLength = element.Attribute("textLength") is { } textLengthValue
            ? ParseLength(textLengthValue.Value, reports, "textLength", LengthUnit.Pixel)
            : (double?)null;
        var x = element.Attribute("x") is { } xAttribute ? ParseNumber(xAttribute.Value) : 0;
        var y = element.Attribute("y") is { } yAttribute ? ParseNumber(yAttribute.Value) : 0;
        var preserve = (string?)element.Attribute(XNamespace.Xml + "space") == "preserve";
        // OF-B07: a tspan may place the cursor (x/y) or shift it (dx/dy) — the multi-line text pattern.
        var segments = new List<SvgTextSegment>();
        var started = false;
        var pendingSpace = false;
        bool Collect(XElement parent, SvgStyle current, int depth)
        {
            if (depth > 32) return false;
            foreach (var node in parent.Nodes())
            {
                if (node is XText text)
                {
                    var value = new StringBuilder();
                    foreach (var character in text.Value)
                    {
                        if (!preserve && character is ' ' or '\t' or '\r' or '\n') { pendingSpace = started; continue; }
                        if (pendingSpace) { value.Append(' '); pendingSpace = false; }
                        value.Append(character);
                        started = true;
                    }
                    if (value.Length > 0)
                    {
                        segments.Add(new SvgTextSegment(value.ToString(), current, null, null, 0, 0, rotate, textLength));
                    }
                }
                else if (node is XElement span)
                {
                    if (!IsElement(span, "tspan") || span.Attributes().Any(attribute => attribute.Name.LocalName is
                            "rotate" or "textLength" or "lengthAdjust" or "transform" or "clip-path"
                            || attribute.Name == XNamespace.Xml + "space")) return false;
                    var child = ReadStyle(span, current, reports, rules);
                    if (child.TextAnchor != style.TextAnchor || child.ClipPath is not null) return false;
                    if (!child.DisplayNone)
                    {
                        var before = segments.Count;
                        if (!Collect(span, child, depth + 1)) return false;
                        if (segments.Count > before)
                        {
                            // x/y place the cursor absolutely, dx/dy shift it; the shift belongs to
                            // the first text of the tspan.
                            var toX = span.Attribute("x") is { } xValue ? ParseLength(xValue.Value, reports, "x", LengthUnit.Pixel) : (double?)null;
                            var toY = span.Attribute("y") is { } yValue ? ParseLength(yValue.Value, reports, "y", LengthUnit.Pixel) : (double?)null;
                            var byX = span.Attribute("dx") is { } dxValue ? ParseLength(dxValue.Value, reports, "dx", LengthUnit.Pixel) : 0;
                            var byY = span.Attribute("dy") is { } dyValue ? ParseLength(dyValue.Value, reports, "dy", LengthUnit.Pixel) : 0;
                            var first = segments[before];
                            segments[before] = first with
                            {
                                X = toX ?? first.X,
                                Y = toY ?? first.Y,
                                DX = byX,
                                DY = byY,
                                Rotate = span.Attribute("rotate") is not null ? Rotation(span, reports) : first.Rotate,
                                TextLength = span.Attribute("textLength") is { } spanLength
                                    ? ParseLength(spanLength.Value, reports, "textLength", LengthUnit.Pixel)
                                    : first.TextLength,
                            };
                        }
                    }
                }
            }
            return true;
        }
        if (!Collect(element, style, 0))
        {
            reports.Add("SVG: complex texts are not drawn.");
            return;
        }
        if (segments.Count == 0) return;
        if (rotations is { Count: > 1 })
        {
            if (textLength is not null || segments.Any(segment => segment.TextLength is not null))
            {
                reports.Add("SVG: a rotation per glyph combined with textLength is not supported; the text is not rotated.");
                rotations = null;
            }
            else
            {
                if (segments.Any(segment => Math.Abs(segment.Rotate) > 1e-9))
                {
                    reports.Add("SVG: a tspan rotation combined with a rotation per glyph is not supported; the outer list is used.");
                }

                var expanded = new List<SvgTextSegment>();
                var glyph = 0;
                foreach (var segment in segments)
                {
                    var firstInSegment = true;
                    foreach (var textElement in TextElements(segment.Text))
                    {
                        expanded.Add(segment with
                        {
                            Text = textElement,
                            Rotate = rotations[Math.Min(glyph, rotations.Count - 1)],
                            X = firstInSegment ? segment.X : null,
                            Y = firstInSegment ? segment.Y : null,
                            DX = firstInSegment ? segment.DX : 0,
                            DY = firstInSegment ? segment.DY : 0,
                        });
                        firstInSegment = false;
                        glyph++;
                    }
                }

                segments = expanded;
            }
        }

        if (segments.Any(segment => segment.Style.Visible && segment.Style.Stroke is not null))
            reports.Add("SVG: text strokes are not drawn.");
        if (segments.All(segment => !segment.Style.Visible || segment.Style.Fill is null)) return;
        var rendered = new List<(SvgStyle Style, string Content, double Width)>();
        foreach (var segment in segments)
        {
            var part = renderText?.Invoke(segment.Text, segment.Style.FontFamily, segment.Style.FontBold, segment.Style.FontItalic,
                segment.Style.FontSize, segment.Style.Fill ?? Color.Black);
            if (part is null)
            {
                reports.Add("SVG: texts with unsupported positioning or characters are not drawn.");
                return;
            }
            rendered.Add((segment.Style, part.Value.Content, part.Value.Width));
        }
        if (x is null || y is null)
        {
            reports.Add("SVG: texts with unsupported positioning or characters are not drawn.");
            return;
        }
        // An anchored text is offset as a whole; a tspan that places itself starts on its own.
        var totalWidth = rendered.Sum(part => part.Width);
        var anchored = segments.All(segment => segment.X is null && segment.Y is null);
        var offset = anchored ? style.TextAnchor switch { "middle" => totalWidth / 2, "end" => totalWidth, _ => 0 } : 0;
        content.Append("q\n");
        AppendMatrix(content, transform);
        var cursor = x.Value - offset;
        var baseline = y.Value;
        for (var index = 0; index < rendered.Count; index++)
        {
            var part = rendered[index];
            var segment = segments[index];
            if (segment.X is { } placedX) cursor = placedX;
            if (segment.Y is { } placedY) baseline = placedY;
            cursor += segment.DX;
            baseline += segment.DY;
            var advance = segment.TextLength ?? part.Width;
            if (part.Style.Visible && part.Style.Fill is not null)
            {
                content.Append("q\n");
                AppendAlpha(content, alphaState, "fill:", part.Style.FillOpacity * part.Style.FillAlpha * part.Style.GroupOpacity);
                if (Math.Abs(segment.Rotate) > 1e-9)
                {
                    // SVG rotates clockwise and the content space grows downwards.
                    var radians = segment.Rotate * Math.PI / 180.0;
                    content.Append(Number(Math.Cos(radians))).Append(' ').Append(Number(Math.Sin(radians)))
                        .Append(' ').Append(Number(-Math.Sin(radians))).Append(' ').Append(Number(Math.Cos(radians)))
                        .Append(' ').Append(Number(cursor)).Append(' ').Append(Number(baseline)).Append(" cm\n");
                    content.Append("1 0 0 -1 0 0 cm\n");
                }
                else
                {
                    content.Append("1 0 0 -1 ").Append(Number(cursor)).Append(' ').Append(Number(baseline)).Append(" cm\n");
                }

                if (segment.TextLength is { } length && part.Width > 0)
                {
                    // lengthAdjust="spacing" widens the gaps; the glyphs are stretched instead.
                    content.Append(Number(length / part.Width)).Append(" 0 0 1 0 0 cm\n");
                }

                content.Append(part.Content).Append("Q\n");
            }

            cursor += advance;
        }
        content.Append("Q\n");
    }

    private static bool RenderGradient(
        XElement target, SvgStyle style, Matrix transform, StringBuilder content, List<string> reports,
        Func<string, string> alphaState, IReadOnlyDictionary<string, XElement> gradients, string id,
        Func<SvgGradient, string>? registerGradient, SvgCallbacks callbacks)
    {
        if (!style.Visible) return true;
        if (registerGradient is null || !gradients.TryGetValue(id, out var reference))
        {
            reports.Add($"SVG: gradient '{id}' is unavailable.");
            return false;
        }

        if (ResolvePaintServer(reference, gradients, "gradient", reports) is not { } source)
        {
            return false;
        }

        var spread = source.Attribute("spreadMethod")?.Value ?? "pad";
        if (spread is not ("pad" or "reflect" or "repeat"))
        {
            reports.Add($"SVG: the gradient spread method '{spread}' is not supported.");
            return false;
        }
        var stops = source.Elements().Where(element => IsElement(element, "stop")).ToArray();
        (Color Color, double Alpha)? StopColor(XElement stop)
        {
            var raw = ClipProperty(stop, "stop-color") ?? "black";
            var color = ParseColor(raw, reports, out var alpha);
            if (raw.Equals("transparent", StringComparison.OrdinalIgnoreCase)) { color = Color.Black; alpha = 0; }
            var opacity = ClipProperty(stop, "stop-opacity") is { } value ? ParseOpacity(value) : 1;
            return color is not null && opacity is not null ? (color.Value, alpha * opacity.Value) : null;
        }
        var parsedStops = new List<(double Offset, Color Color)>();
        double? gradientAlpha = null;
        foreach (var stop in stops)
        {
            var color = StopColor(stop);
            var offset = GradientOffset(stop);
            if (color is null || offset is null || !double.IsFinite(offset.Value))
            {
                reports.Add("SVG: invalid gradient stops are not supported.");
                return false;
            }
            if (gradientAlpha is { } previousAlpha && Math.Abs(previousAlpha - color.Value.Alpha) > 1e-9)
            {
                reports.Add("SVG: varying gradient stop opacity is not supported.");
                return false;
            }
            gradientAlpha = color.Value.Alpha;
            var position = Math.Clamp(offset.Value, 0, 1);
            if (parsedStops.Count > 0 && position <= parsedStops[^1].Offset)
            {
                reports.Add("SVG: repeated or unordered gradient stops are not supported.");
                return false;
            }
            parsedStops.Add((position, color.Value.Color));
        }
        if (parsedStops.Count == 0) return true;
        if (parsedStops[0].Offset > 0) parsedStops.Insert(0, (0, parsedStops[0].Color));
        if (parsedStops[^1].Offset < 1) parsedStops.Add((1, parsedStops[^1].Color));
        var units = source.Attribute("gradientUnits")?.Value ?? "objectBoundingBox";
        if (units is not ("objectBoundingBox" or "userSpaceOnUse"))
        {
            reports.Add("SVG: gradient units are not supported.");
            return false;
        }
        var bounds = units == "objectBoundingBox" ? ClipTargetBounds(target) : Matrix.Identity;
        if (bounds is null)
        {
            reports.Add("SVG: gradient object bounding-box geometry is not supported.");
            return false;
        }
        double? Coordinate(string name, double fallback)
        {
            var value = source.Attribute(name)?.Value;
            if (value is null) return fallback;
            var number = ReadNumberPrefix(value.Trim(), out var suffix);
            if (number is null) return null;
            return suffix.Trim() switch { "" => number, "%" when units == "objectBoundingBox" => number / 100, _ => null };
        }
        var radial = IsElement(source, "radialGradient");
        if (units == "userSpaceOnUse" && (radial ? new[] { "cx", "cy", "r" } : new[] { "x1", "y1", "x2", "y2" })
            .Any(name => source.Attribute(name) is null))
        {
            reports.Add("SVG: percentage defaults in user-space gradient coordinates are not supported.");
            return false;
        }
        double?[] values;
        if (radial)
        {
            var cx = Coordinate("cx", 0.5); var cy = Coordinate("cy", 0.5); var radius = Coordinate("r", 0.5);
            values = [Coordinate("fx", cx ?? 0.5), Coordinate("fy", cy ?? 0.5), 0, cx, cy, radius];
            if (source.Attribute("fr") is not null || radius is not > 0) values = [];
        }
        else values = [Coordinate("x1", 0), Coordinate("y1", 0), Coordinate("x2", 1), Coordinate("y2", 0)];
        if (values.Length == 0 || values.Any(value => value is null || !double.IsFinite(value.Value))
            || units == "userSpaceOnUse" && source.Attributes().Any(attribute => attribute.Value.EndsWith('%')))
        {
            reports.Add("SVG: gradient coordinates are not supported.");
            return false;
        }
        if (!radial && values[0] == values[2] && values[1] == values[3])
        {
            reports.Add("SVG: a gradient with coincident endpoints is not supported.");
            return false;
        }
        if (radial && (Math.Pow((values[0]!.Value - values[3]!.Value) / values[5]!.Value, 2)
            + Math.Pow((values[1]!.Value - values[4]!.Value) / values[5]!.Value, 2) > 1))
        {
            reports.Add("SVG: a radial gradient focal point outside its circle is not supported.");
            return false;
        }
        var path = PaintServerPath(target, reports, "gradients");
        if (path is null) return false;

        if (spread != "pad")
        {
            if (source.Attribute("gradientTransform") is not null)
            {
                reports.Add("SVG: a repeating gradient with a transform is not supported.");
                return false;
            }

            if (radial)
            {
                // OF-B07: a repeating radial gradient is one shading whose colour function samples
                // the stops periodically along the radius. The outer radius covers the whole shape,
                // so the sampling (not a tile) produces the rings.
                var centerX = values[3]!.Value;
                var centerY = values[4]!.Value;
                var radius = values[5]!.Value;
                if (Math.Abs(values[0]!.Value - centerX) > 1e-9 || Math.Abs(values[1]!.Value - centerY) > 1e-9)
                {
                    reports.Add("SVG: a repeating radial gradient with an offset focal point is not supported.");
                    return false;
                }

                double? extent;
                if (units == "objectBoundingBox")
                {
                    extent = MaxDistanceToUnitSquare(centerX, centerY);
                }
                else if (ClipTargetBounds(target) is { } targetBounds)
                {
                    extent = MaxDistanceToBounds(targetBounds, centerX, centerY);
                }
                else
                {
                    extent = null;
                }

                if (extent is not > 0 || !double.IsFinite(extent.Value))
                {
                    reports.Add("SVG: a repeating radial gradient without an extent is not supported.");
                    return false;
                }

                var periods = extent.Value / radius;
                if (periods > 16)
                {
                    reports.Add("SVG: a repeating radial gradient with too many periods is not supported.");
                    return false;
                }

                const int sampleCount = 512;
                var samples = new Color[sampleCount];
                for (var index = 0; index < sampleCount; index++)
                {
                    var phase = index / (double)(sampleCount - 1) * periods;
                    if (spread == "reflect")
                    {
                        phase %= 2;
                        if (phase > 1)
                        {
                            phase = 2 - phase;
                        }
                    }
                    else
                    {
                        phase -= Math.Floor(phase);
                    }

                    samples[index] = ColorAt(parsedStops, phase);
                }

                var repeating = new SvgGradient(
                    true,
                    [centerX, centerY, 0, centerX, centerY, extent.Value],
                    parsedStops,
                    Extend: true,
                    Samples: samples);
                var repeatingName = registerGradient(repeating);
                content.Append("q\n");
                AppendMatrix(content, transform);
                content.Append(path).Append(style.EvenOdd ? "W* n\n" : "W n\n");
                AppendAlpha(content, alphaState, "fill:", style.FillOpacity * (gradientAlpha ?? 1) * style.GroupOpacity);
                AppendMatrix(content, bounds.Value);
                content.Append('/').Append(repeatingName).Append(" sh\nQ\n");
                return true;
            }

            // A repeating linear gradient is a tiling pattern whose cell holds the shading; reflect
            // mirrors the shading in every second cell.
            if (callbacks.RegisterPattern is null)
            {
                reports.Add("SVG: a repeating gradient without patterns is not supported.");
                return false;
            }

            var startX = values[0]!.Value;
            var startY = values[1]!.Value;
            var endX = values[2]!.Value;
            var endY = values[3]!.Value;
            var length = Math.Sqrt(Math.Pow(endX - startX, 2) + Math.Pow(endY - startY, 2));
            if (length <= 0)
            {
                reports.Add("SVG: a repeating gradient without a length is not supported.");
                return false;
            }

            var mirror = spread == "reflect";
            var cell = new StringBuilder();
            var forward = registerGradient(new SvgGradient(false, [0, 0, length, 0], parsedStops, Extend: false));
            cell.Append("q\n/").Append(forward).Append(" sh\nQ\n");
            if (mirror)
            {
                var backward = registerGradient(new SvgGradient(false, [2 * length, 0, length, 0], parsedStops, Extend: false));
                cell.Append("q\n/").Append(backward).Append(" sh\nQ\n");
            }

            var angle = Math.Atan2(endY - startY, endX - startX);
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var cellWidth = mirror ? 2 * length : length;
            // The tile is stretched perpendicular to the gradient axis; an axial shading does not vary there.
            var cellHeight = Math.Max(1, length);
            // Pattern space -> form space: the gradient axis (in bounding-box units if needed) under every cm.
            var axis = Matrix.Multiply(Matrix.Multiply(style.Ctm, bounds.Value), new Matrix(cos, sin, -sin, cos, startX, startY));
            var name = callbacks.RegisterPattern(new SvgPattern(
                0, 0, cellWidth, cellHeight, [axis.A, axis.B, axis.C, axis.D, axis.E, axis.F], cell.ToString()));
            content.Append("q\n");
            AppendMatrix(content, transform);
            AppendAlpha(content, alphaState, "fill:", style.FillOpacity * (gradientAlpha ?? 1) * style.GroupOpacity);
            content.Append("/Pattern cs\n/").Append(name).Append(" scn\n").Append(path)
                .Append(style.EvenOdd ? "f*\nQ\n" : "f\nQ\n");
            return true;
        }

        var shading = registerGradient(new SvgGradient(radial, values.Select(value => value!.Value).ToArray(), parsedStops));
        content.Append("q\n");
        AppendMatrix(content, transform);
        content.Append(path).Append(style.EvenOdd ? "W* n\n" : "W n\n");
        AppendAlpha(content, alphaState, "fill:", style.FillOpacity * (gradientAlpha ?? 1) * style.GroupOpacity);
        AppendMatrix(content, bounds.Value);
        AppendMatrix(content, ReadTransform(source.Attribute("gradientTransform")?.Value, reports));
        content.Append('/').Append(shading).Append(" sh\nQ\n");
        return true;
    }

    /// <summary>The farthest unit-square corner from a point, used as the outer radius of a repeating radial gradient.</summary>
    private static double MaxDistanceToUnitSquare(double x, double y)
        => Math.Max(
            Math.Max(Distance(x, y, 0, 0), Distance(x, y, 1, 0)),
            Math.Max(Distance(x, y, 0, 1), Distance(x, y, 1, 1)));

    /// <summary>The farthest bounding-box corner from a point, used as the outer radius of a repeating radial gradient.</summary>
    private static double MaxDistanceToBounds(Matrix bounds, double x, double y)
        => Math.Max(
            Math.Max(Distance(x, y, bounds.E, bounds.F), Distance(x, y, bounds.E + bounds.A, bounds.F)),
            Math.Max(Distance(x, y, bounds.E, bounds.F + bounds.D), Distance(x, y, bounds.E + bounds.A, bounds.F + bounds.D)));

    private static double Distance(double x1, double y1, double x2, double y2)
        => Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));

    /// <summary>The colour of the gradient stops at an offset, interpolated linearly.</summary>
    private static Color ColorAt(IReadOnlyList<(double Offset, Color Color)> stops, double offset)
    {
        if (offset <= stops[0].Offset)
        {
            return stops[0].Color;
        }

        for (var index = 1; index < stops.Count; index++)
        {
            if (offset > stops[index].Offset)
            {
                continue;
            }

            var previous = stops[index - 1];
            var next = stops[index];
            var span = next.Offset - previous.Offset;
            var t = span <= 0 ? 0 : (offset - previous.Offset) / span;
            return new Color(
                (byte)Math.Round(previous.Color.Red + (next.Color.Red - previous.Color.Red) * t),
                (byte)Math.Round(previous.Color.Green + (next.Color.Green - previous.Color.Green) * t),
                (byte)Math.Round(previous.Color.Blue + (next.Color.Blue - previous.Color.Blue) * t));
        }

        return stops[^1].Color;
    }

    private static string? PaintServerPath(XElement target, List<string> reports, string description)
    {
        var path = new StringBuilder();
        switch (target.Name.LocalName)
        {
            case "rect" when target.Attribute("rx") is null && target.Attribute("ry") is null:
                var rect = ClipTargetBounds(target);
                if (rect is null) return null;
                path.Append(Number(rect.Value.E)).Append(' ').Append(Number(rect.Value.F)).Append(' ')
                    .Append(Number(rect.Value.A)).Append(' ').Append(Number(rect.Value.D)).Append(" re\n");
                break;
            case "circle":
            case "ellipse":
                var ellipse = ClipTargetBounds(target);
                if (ellipse is null) return null;
                AppendEllipse(path, ellipse.Value.E + ellipse.Value.A / 2, ellipse.Value.F + ellipse.Value.D / 2,
                    ellipse.Value.A / 2, ellipse.Value.D / 2);
                break;
            case "path" when target.Attribute("d")?.Value is { Length: > 0 } data:
                BuildPath(data, path, reports);
                break;
            default:
                reports.Add($"SVG: {description} on <{target.Name.LocalName}> are not supported.");
                return null;
        }
        return path.ToString();
    }

    private static double? GradientOffset(XElement stop)
    {
        var value = (string?)stop.Attribute("offset") ?? "0";
        var number = ReadNumberPrefix(value.Trim(), out var suffix);
        return suffix.Trim() switch { "" => number, "%" => number / 100, _ => null };
    }

    private static bool RenderPattern(
        XElement target, SvgStyle style, Matrix transform, StringBuilder content, List<string> reports,
        Func<string, string> alphaState, IReadOnlyDictionary<string, XElement> clips,
        IReadOnlyDictionary<string, XElement> gradients,
        IReadOnlyDictionary<string, XElement> patterns, IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules, string id, SvgCallbacks callbacks, int depth)
    {
        if (!style.Visible) return true;
        if (ResolvePaintServer(patterns[id], patterns, "pattern", reports) is not { } source)
        {
            return false;
        }

        var units = source.Attribute("patternUnits")?.Value ?? "objectBoundingBox";
        var contentUnits = source.Attribute("patternContentUnits")?.Value ?? "userSpaceOnUse";
        if (callbacks.RegisterPattern is null || units is not ("userSpaceOnUse" or "objectBoundingBox")
            || contentUnits is not ("userSpaceOnUse" or "objectBoundingBox")
            || units == "objectBoundingBox" && source.Attribute("patternTransform") is not null
            || source.Attribute("viewBox") is not null)
        {
            reports.Add("SVG: this pattern's units, reference or view box are not supported.");
            return false;
        }
        double? Coordinate(string name, double fallback)
        {
            var value = source.Attribute(name)?.Value;
            if (value is null) return fallback;
            var number = ReadNumberPrefix(value.Trim(), out var suffix);
            return suffix.Trim() switch
            {
                "" => number, "px" when units == "userSpaceOnUse" => number,
                "%" when units == "objectBoundingBox" => number / 100, _ => null,
            };
        }
        var x = Coordinate("x", 0); var y = Coordinate("y", 0);
        var width = Coordinate("width", 0); var height = Coordinate("height", 0);
        var bounds = units == "objectBoundingBox" || contentUnits == "objectBoundingBox" ? ClipTargetBounds(target) : Matrix.Identity;
        if (bounds is null)
        {
            reports.Add("SVG: pattern object bounding-box geometry is not supported.");
            return false;
        }
        if (units == "objectBoundingBox")
        {
            x = bounds.Value.E + x * bounds.Value.A;
            y = bounds.Value.F + y * bounds.Value.D;
            width *= bounds.Value.A;
            height *= bounds.Value.D;
        }
        if (x is null || y is null || width is not > 0 || height is not > 0
            || !double.IsFinite(x.Value + width.Value) || !double.IsFinite(y.Value + height.Value))
        {
            reports.Add("SVG: pattern dimensions are not supported.");
            return false;
        }
        var path = PaintServerPath(target, reports, "patterns");
        if (path is null) return false;
        var inner = new StringBuilder();
        var innerReports = new List<string>();
        var patternStyle = ReadStyle(source, SvgStyle.Default, innerReports, rules);
        if (contentUnits == "objectBoundingBox")
        {
            inner.Append("q\n");
            AppendMatrix(inner, bounds.Value);
        }

        // A tile may hold an image or a gradient of its own (OF-B07); its resources are the page's.
        var tileCallbacks = new SvgCallbacks(null, callbacks.RegisterGradient, callbacks.RegisterPattern, callbacks.RegisterImage) { Budget = callbacks.Budget };
        foreach (var child in source.Elements())
            RenderElement(
                child,
                patternStyle,
                inner,
                innerReports,
                _ => "UNSUPPORTED",
                clips,
                gradients,
                patterns,
                ids,
                rules,
                tileCallbacks,
                depth + 1);
        if (contentUnits == "objectBoundingBox") inner.Append("Q\n");
        if (innerReports.Count > 0 || inner.ToString().Contains("/UNSUPPORTED gs", StringComparison.Ordinal))
        {
            reports.Add("SVG: pattern content requiring unsupported elements or opacity is not drawn.");
            reports.AddRange(innerReports);
            return false;
        }
        var matrix = Matrix.Multiply(style.Ctm, ReadTransform(source.Attribute("patternTransform")?.Value, innerReports));
        if (innerReports.Count > 0)
        {
            reports.AddRange(innerReports);
            return false;
        }
        var name = callbacks.RegisterPattern(new SvgPattern(x.Value, y.Value, width.Value, height.Value,
            [matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F], inner.ToString()));
        content.Append("q\n");
        AppendMatrix(content, transform);
        AppendAlpha(content, alphaState, "fill:", style.FillOpacity * style.GroupOpacity);
        content.Append("/Pattern cs\n/").Append(name).Append(" scn\n").Append(path)
            .Append(style.EvenOdd ? "f*\nQ\n" : "f\nQ\n");
        return true;
    }

    private static bool ApplyClip(
        string reference, IReadOnlyDictionary<string, XElement> clips, XElement target, Matrix transform,
        StringBuilder content, List<string> reports)
        => ApplyClipCore(reference, clips, target, transform, content, reports, transformApplied: false, depth: 0);

    /// <summary>
    /// Applies one clipping path. A nested <c>clip-path</c> on the only shape of the clip is applied
    /// first; the two clips then intersect in the graphics state. <paramref name="transformApplied"/>
    /// is true when an enclosing clip has already applied the target transform.
    /// </summary>
    private static bool ApplyClipCore(
        string reference,
        IReadOnlyDictionary<string, XElement> clips,
        XElement target,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        bool transformApplied,
        int depth)
    {
        if (depth > 16)
        {
            reports.Add("SVG: clipping paths are nested too deeply.");
            return false;
        }

        if (!reference.StartsWith("url(#", StringComparison.Ordinal) || !reference.EndsWith(')')
            || !clips.TryGetValue(reference[5..^1], out var clip))
        {
            reports.Add($"SVG: clipping path '{reference}' is not found or supported.");
            return false;
        }

        var children = clip.Elements().ToArray();
        var units = clip.Attribute("clipPathUnits")?.Value ?? "userSpaceOnUse";
        if (units is not ("userSpaceOnUse" or "objectBoundingBox"))
        {
            reports.Add("SVG: this clipping path needs unsupported units.");
            return false;
        }

        if (children.Length == 0)
        {
            reports.Add("SVG: an empty clipping path is not supported.");
            return false;
        }

        var nested = children
            .Select(shape => ClipProperty(shape, "clip-path"))
            .Where(value => value is not null && value != "none")
            .ToArray();

        var bounds = units == "objectBoundingBox" ? ClipTargetBounds(target) : Matrix.Identity;
        if (bounds is null)
        {
            reports.Add("SVG: this clipping path needs unsupported object bounding-box geometry.");
            return false;
        }

        var path = new StringBuilder();
        foreach (var shape in children)
        {
            if (!TryAppendClipShape(shape, path, reports))
            {
                return false;
            }
        }

        var rule = ClipProperty(children[0], "clip-rule") ?? ClipProperty(clip, "clip-rule");
        if (children.Any(shape => ClipProperty(shape, "clip-rule") == "evenodd"))
        {
            rule = "evenodd";
        }

        if (nested.Length > 0 && children.Length > 1)
        {
            // ⋃ᵢ (shapeᵢ ∩ nestedᵢ) = ⋂ over every choice of shapeᵢ or nestedᵢ of the union of the
            // chosen terms. Each factor is a path union and becomes one intersecting clip, so
            // different nested references are exact too (2^k clauses for k clipped shapes).
            var terms = new string?[children.Length];
            var clipped = new List<string>();
            for (var index = 0; index < children.Length; index++)
            {
                var own = ClipProperty(children[index], "clip-path");
                if (own is not null and not "none")
                {
                    terms[index] = own;
                    clipped.Add(own);
                }
            }

            if (clipped.Count > 4)
            {
                reports.Add("SVG: too many nested clipping paths in a multi-shape clipping path are not supported.");
                return false;
            }

            if (!transformApplied)
            {
                content.Append("q\n");
                AppendMatrix(content, transform);
            }

            var clipTransform = ReadTransform(clip.Attribute("transform")?.Value, reports);
            for (var mask = 0; mask < 1 << clipped.Count; mask++)
            {
                var clause = new StringBuilder();
                var clippedIndex = 0;
                for (var index = 0; index < children.Length; index++)
                {
                    if (terms[index] is { } nestedReference && (mask & (1 << clippedIndex)) != 0)
                    {
                        if (!TryAppendNestedClipGeometry(nestedReference, clips, clause, reports, depth + 1))
                        {
                            return false;
                        }
                    }
                    else if (!TryAppendClipShape(children[index], clause, reports))
                    {
                        return false;
                    }

                    if (terms[index] is not null)
                    {
                        clippedIndex++;
                    }
                }

                content.Append("q\n");
                AppendMatrix(content, bounds.Value);
                AppendMatrix(content, clipTransform);
                content.Append(clause);
                content.Append("Q\n");
                content.Append(rule == "evenodd" ? "W* n\n" : "W n\n");
            }

            return true;
        }

        if (nested.Length == 1)
        {
            // The only shape is clipped by another path: apply the inner clip first, then add the
            // shape's own path as a second, intersecting clip in the same graphics state.
            if (!ApplyClipCore(nested[0]!, clips, target, transform, content, reports, transformApplied, depth + 1))
            {
                return false;
            }

            content.Append("q\n");
            AppendMatrix(content, bounds.Value);
            AppendMatrix(content, ReadTransform(clip.Attribute("transform")?.Value, reports));
            content.Append(path);
            content.Append("Q\n");
            content.Append(rule == "evenodd" ? "W* n\n" : "W n\n");
            return true;
        }

        if (!transformApplied)
        {
            content.Append("q\n");
            AppendMatrix(content, transform);
        }

        content.Append("q\n");
        AppendMatrix(content, bounds.Value);
        AppendMatrix(content, ReadTransform(clip.Attribute("transform")?.Value, reports));

        content.Append(path);
        content.Append("Q\n");
        content.Append(rule == "evenodd" ? "W* n\n" : "W n\n");
        return true;
    }

    /// <summary>
    /// Appends the geometry of a nested <c>clip-path</c> reference so it can be unioned with the
    /// unclipped shapes of a multi-shape clip. Only user-space units without further nesting are
    /// supported here; the single-shape nested case applies clips recursively instead.
    /// </summary>
    private static bool TryAppendNestedClipGeometry(
        string reference,
        IReadOnlyDictionary<string, XElement> clips,
        StringBuilder path,
        List<string> reports,
        int depth)
    {
        if (depth > 16)
        {
            reports.Add("SVG: clipping paths are nested too deeply.");
            return false;
        }

        if (!reference.StartsWith("url(#", StringComparison.Ordinal) || !reference.EndsWith(')')
            || !clips.TryGetValue(reference[5..^1], out var clip))
        {
            reports.Add($"SVG: clipping path '{reference}' is not found or supported.");
            return false;
        }

        var children = clip.Elements().ToArray();
        if (children.Length == 0)
        {
            reports.Add("SVG: an empty clipping path is not supported.");
            return false;
        }

        if (children.Any(shape => ClipProperty(shape, "clip-path") is not null and not "none"))
        {
            reports.Add("SVG: nested clipping paths inside a multi-shape clipping path are not supported.");
            return false;
        }

        if ((clip.Attribute("clipPathUnits")?.Value ?? "userSpaceOnUse") != "userSpaceOnUse")
        {
            reports.Add(
                "SVG: object bounding-box units in a nested multi-shape clipping path are not supported.");
            return false;
        }

        var geometry = new StringBuilder();
        foreach (var shape in children)
        {
            if (!TryAppendClipShape(shape, geometry, reports))
            {
                return false;
            }
        }

        path.Append("q\n");
        AppendMatrix(path, ReadTransform(clip.Attribute("transform")?.Value, reports));
        path.Append(geometry);
        path.Append("Q\n");
        return true;
    }

    /// <summary>Appends one shape of a clipping path; the union of all shapes is the clip area.</summary>
    private static bool TryAppendClipShape(XElement shape, StringBuilder path, List<string> reports)
    {
        foreach (var attribute in shape.Attributes().Where(attribute => attribute.Name.LocalName is "x" or "y" or "width" or "height" or "cx" or "cy" or "r" or "rx" or "ry"))
        {
            var number = ReadNumberPrefix(attribute.Value.Trim(), out var suffix);
            if (number is null || (suffix.Trim() is not ("" or "px")))
            {
                reports.Add($"SVG: clipping length '{attribute.Value}' is not supported.");
                return false;
            }
        }

        if (ClipProperty(shape, "display") == "none" || ClipProperty(shape, "visibility") is "hidden" or "collapse")
        {
            return true;
        }

        var transform = shape.Attribute("transform");
        if (transform is not null)
        {
            path.Append("q\n");
            AppendMatrix(path, ReadTransform(transform.Value, reports));
        }

        var appended = TryAppendClipShapeBody(shape, path, reports);
        if (transform is not null)
        {
            path.Append("Q\n");
        }

        return appended;
    }

    /// <summary>Appends the geometry of one clipping shape in the current coordinate system.</summary>
    private static bool TryAppendClipShapeBody(XElement shape, StringBuilder path, List<string> reports)
    {
        switch (shape.Name.LocalName)
        {
            case "path" when shape.Attribute("d")?.Value is { Length: > 0 } data:
                BuildPath(data, path, reports);
                return true;
            case "rect":
                var x = ParseNumber(shape.Attribute("x")?.Value) ?? 0;
                var y = ParseNumber(shape.Attribute("y")?.Value) ?? 0;
                var width = ParseNumber(shape.Attribute("width")?.Value) ?? 0;
                var height = ParseNumber(shape.Attribute("height")?.Value) ?? 0;
                if (width <= 0 || height <= 0)
                {
                    return true;
                }

                var radiusX = Math.Max(0, ParseNumber(shape.Attribute("rx")?.Value)
                    ?? ParseNumber(shape.Attribute("ry")?.Value) ?? 0);
                var radiusY = Math.Max(0, ParseNumber(shape.Attribute("ry")?.Value)
                    ?? ParseNumber(shape.Attribute("rx")?.Value) ?? 0);
                if (radiusX > 0 && radiusY > 0)
                {
                    AppendRoundedRectangle(
                        path, x, y, width, height, Math.Min(radiusX, width / 2), Math.Min(radiusY, height / 2));
                }
                else
                {
                    path.Append(Number(x)).Append(' ').Append(Number(y)).Append(' ')
                        .Append(Number(width)).Append(' ').Append(Number(height)).Append(" re\n");
                }

                return true;
            case "circle":
            case "ellipse":
                var cx = ParseNumber(shape.Attribute("cx")?.Value) ?? 0;
                var cy = ParseNumber(shape.Attribute("cy")?.Value) ?? 0;
                var rx = ParseNumber(shape.Attribute(shape.Name.LocalName == "circle" ? "r" : "rx")?.Value) ?? 0;
                var ry = shape.Name.LocalName == "circle" ? rx : ParseNumber(shape.Attribute("ry")?.Value) ?? 0;
                if (rx > 0 && ry > 0)
                {
                    AppendEllipse(path, cx, cy, rx, ry);
                }

                return true;
            default:
                reports.Add($"SVG: clipping shape <{shape.Name.LocalName}> is not supported.");
                return false;
        }
    }

    private static Matrix? ClipTargetBounds(XElement target)
    {
        double? Value(string name, double defaultValue = 0)
        {
            var attribute = target.Attribute(name);
            if (attribute is null) return defaultValue;
            var value = ReadNumberPrefix(attribute.Value.Trim(), out var suffix);
            return suffix.Trim() is "" or "px" ? value : null;
        }

        double? x, y, width, height;
        switch (target.Name.LocalName)
        {
            case "rect":
                x = Value("x"); y = Value("y"); width = Value("width"); height = Value("height");
                break;
            case "circle":
            case "ellipse":
                var cx = Value("cx"); var cy = Value("cy");
                var rx = Value(target.Name.LocalName == "circle" ? "r" : "rx");
                var ry = target.Name.LocalName == "circle" ? rx : Value("ry");
                x = cx - rx; y = cy - ry; width = rx * 2; height = ry * 2;
                break;
            case "path":
            {
                var subpaths = new List<List<(double X, double Y)>>();
                BuildPath(target.Attribute("d")?.Value ?? string.Empty, new StringBuilder(), new List<string>(), subpaths);
                var points = subpaths.SelectMany(path => path).ToList();
                if (points.Count == 0) return null;
                x = points.Min(point => point.X); y = points.Min(point => point.Y);
                width = points.Max(point => point.X) - x; height = points.Max(point => point.Y) - y;
                break;
            }

            case "polyline":
            case "polygon":
            {
                var numbers = ParseNumbers(target.Attribute("points")?.Value);
                if (numbers is not { Count: >= 4 }) return null;
                var xs = numbers.Where((_, index) => index % 2 == 0).ToList();
                var ys = numbers.Where((_, index) => index % 2 == 1).ToList();
                x = xs.Min(); y = ys.Min(); width = xs.Max() - x; height = ys.Max() - y;
                break;
            }

            case "line":
            {
                var x1 = Value("x1") ?? 0; var y1 = Value("y1") ?? 0;
                var x2 = Value("x2") ?? 0; var y2 = Value("y2") ?? 0;
                x = Math.Min(x1, x2); y = Math.Min(y1, y2);
                width = Math.Abs(x2 - x.Value); height = Math.Abs(y2 - y.Value);
                break;
            }

            default:
                return null;
        }
        return x is { } left && y is { } top && width is > 0 && height is > 0
            && double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width.Value) && double.IsFinite(height.Value)
            ? new Matrix(width.Value, 0, 0, height.Value, left, top) : null;
    }

    private static string? ClipProperty(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        foreach (var declaration in (element.Attribute("style")?.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator >= 0 && declaration[..separator].Trim() == name)
            {
                value = declaration[(separator + 1)..].Trim();
            }
        }

        return value;
    }

    private static void RenderPath(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth)
    {
        var data = element.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        var path = new StringBuilder();
        var subpaths = new List<List<(double X, double Y)>>();
        BuildPath(data!, path, reports, subpaths);
        content.Append(path);
        Paint(content, style, alphaState);
        content.Append("Q\n");

        // OF-B07: markers sit in the untransformed user space of the path.
        RenderMarkers(element, style, transform, content, reports, ids, rules, MarkerVertices(subpaths), callbacks, depth);
    }

    private static void RenderRect(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState)
    {
        var x = ParseNumber(element.Attribute("x")?.Value) ?? 0;
        var y = ParseNumber(element.Attribute("y")?.Value) ?? 0;
        var width = ParseNumber(element.Attribute("width")?.Value) ?? 0;
        var height = ParseNumber(element.Attribute("height")?.Value) ?? 0;
        var rx = ParseNumber(element.Attribute("rx")?.Value);
        var ry = ParseNumber(element.Attribute("ry")?.Value);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        if (rx is { } radiusX && ry is { } radiusY && radiusX > 0 && radiusY > 0)
        {
            AppendRoundedRectangle(content, x, y, width, height, Math.Min(radiusX, width / 2), Math.Min(radiusY, height / 2));
        }
        else if (rx is { } onlyX && onlyX > 0)
        {
            AppendRoundedRectangle(content, x, y, width, height, Math.Min(onlyX, width / 2), Math.Min(onlyX, height / 2));
        }
        else
        {
            content.Append(Number(x)).Append(' ').Append(Number(y)).Append(' ')
                .Append(Number(width)).Append(' ').Append(Number(height)).Append(" re\n");
        }

        Paint(content, style, alphaState);
        content.Append("Q\n");
    }

    private static void RenderEllipse(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        bool circle)
    {
        double centerX;
        double centerY;
        double radiusX;
        double radiusY;
        if (circle)
        {
            centerX = ParseNumber(element.Attribute("cx")?.Value) ?? 0;
            centerY = ParseNumber(element.Attribute("cy")?.Value) ?? 0;
            radiusX = ParseNumber(element.Attribute("r")?.Value) ?? 0;
            radiusY = radiusX;
        }
        else
        {
            centerX = ParseNumber(element.Attribute("cx")?.Value) ?? 0;
            centerY = ParseNumber(element.Attribute("cy")?.Value) ?? 0;
            radiusX = ParseNumber(element.Attribute("rx")?.Value) ?? 0;
            radiusY = ParseNumber(element.Attribute("ry")?.Value) ?? 0;
        }

        if (radiusX <= 0 || radiusY <= 0)
        {
            return;
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        AppendEllipse(content, centerX, centerY, radiusX, radiusY);
        Paint(content, style, alphaState);
        content.Append("Q\n");
    }

    private static void RenderLine(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth)
    {
        var x1 = ParseNumber(element.Attribute("x1")?.Value) ?? 0;
        var y1 = ParseNumber(element.Attribute("y1")?.Value) ?? 0;
        var x2 = ParseNumber(element.Attribute("x2")?.Value) ?? 0;
        var y2 = ParseNumber(element.Attribute("y2")?.Value) ?? 0;

        content.Append("q\n");
        AppendMatrix(content, transform);
        content.Append(Number(x1)).Append(' ').Append(Number(y1)).Append(" m\n");
        content.Append(Number(x2)).Append(' ').Append(Number(y2)).Append(" l\n");
        Paint(content, style, alphaState, withFill: false);
        content.Append("Q\n");
        RenderMarkers(
            element,
            style,
            transform,
            content,
            reports,
            ids,
            rules,
            [
                new SvgVertex(x1, y1, 0, 0, x2 - x1, y2 - y1, SubpathStart: true),
                new SvgVertex(x2, y2, x2 - x1, y2 - y1, 0, 0, SubpathEnd: true),
            ],
            callbacks,
            depth);
    }

    private static void RenderPolyline(
        XElement element,
        SvgStyle style,
        Matrix transform,
        StringBuilder content,
        List<string> reports,
        Func<string, string> alphaState,
        bool close,
        IReadOnlyDictionary<string, XElement> ids,
        IReadOnlyList<SvgStyleRule> rules,
        SvgCallbacks callbacks,
        int depth)
    {
        var points = ParseNumbers(element.Attribute("points")?.Value);
        if (points is not { Count: >= 4 })
        {
            return;
        }

        content.Append("q\n");
        AppendMatrix(content, transform);
        content.Append(Number(points[0])).Append(' ').Append(Number(points[1])).Append(" m\n");
        for (var index = 2; index + 1 < points.Count; index += 2)
        {
            content.Append(Number(points[index])).Append(' ').Append(Number(points[index + 1])).Append(" l\n");
        }

        if (close)
        {
            content.Append("h\n");
        }

        Paint(content, style, alphaState);
        content.Append("Q\n");
        RenderMarkers(element, style, transform, content, reports, ids, rules, MarkerVertices(points), callbacks, depth);
    }

    // MARK: - Painting

    /// <summary>Writes the fill and stroke operators of the current path.</summary>
    private static void Paint(
        StringBuilder content,
        SvgStyle style,
        Func<string, string> alphaState,
        bool withFill = true)
    {
        var fillColor = withFill && style.Visible ? style.Fill : null;
        var strokeColor = style.Visible && style.StrokeWidth > 0 ? style.Stroke : null; // width 0 means no stroke
        if (fillColor is null && strokeColor is null)
        {
            content.Append("n\n"); // end the path without painting (a path must be closed by an operator)
            return;
        }

        if (fillColor is { } fillRgb)
        {
            content.Append(Number(fillRgb.Red / 255.0)).Append(' ')
                .Append(Number(fillRgb.Green / 255.0)).Append(' ')
                .Append(Number(fillRgb.Blue / 255.0)).Append(" rg\n");
        }

        if (strokeColor is { } strokeRgb)
        {
            content.Append(Number(strokeRgb.Red / 255.0)).Append(' ')
                .Append(Number(strokeRgb.Green / 255.0)).Append(' ')
                .Append(Number(strokeRgb.Blue / 255.0)).Append(" RG\n");
            content.Append(Number(style.StrokeWidth)).Append(" w\n");
            AppendStrokeSettings(content, style);
        }

        // One graphics state carries both opacities: two separate ones would reset each other.
        var fillOpacity = fillColor is null ? 1 : style.FillOpacity * style.FillAlpha * style.GroupOpacity;
        var strokeOpacity = strokeColor is null ? 1 : style.StrokeOpacity * style.StrokeAlpha * style.GroupOpacity;
        if (fillOpacity < 1 || strokeOpacity < 1)
        {
            content.Append('/').Append(alphaState($"pair:{Number(fillOpacity)}:{Number(strokeOpacity)}")).Append(" gs\n");
        }

        content.Append((fillColor, strokeColor, style.EvenOdd) switch
        {
            (not null, not null, true) => "B*\n",
            (not null, not null, false) => "B\n",
            (not null, null, true) => "f*\n",
            (not null, null, false) => "f\n",
            _ => "S\n",
        });
    }

    /// <summary>
    /// Writes the stroke settings that differ from the PDF defaults. Every path is painted inside its
    /// own <c>q</c>/<c>Q</c> pair, so the settings of one shape cannot reach the next one.
    /// </summary>
    private static void AppendStrokeSettings(StringBuilder content, SvgStyle style)
    {
        if (style.DashArray is { Length: > 0 } dashes)
        {
            content.Append('[');
            for (var index = 0; index < dashes.Length; index++)
            {
                if (index > 0)
                {
                    content.Append(' ');
                }

                content.Append(Number(dashes[index]));
            }

            content.Append("] ").Append(Number(style.DashOffset)).Append(" d\n");
        }

        if (style.LineCap != 0)
        {
            content.Append(style.LineCap).Append(" J\n");
        }

        if (style.LineJoin != 0)
        {
            content.Append(style.LineJoin).Append(" j\n");
        }

        if (style.MiterLimit != 4)
        {
            content.Append(Number(style.MiterLimit)).Append(" M\n");
        }
    }

    private static void AppendAlpha(StringBuilder content, Func<string, string> alphaState, string kind, double opacity)
    {
        if (opacity < 1)
        {
            content.Append('/').Append(alphaState(kind + Number(opacity))).Append(" gs\n");
        }
    }

    // MARK: - Geometry

    private static void AppendEllipse(StringBuilder content, double centerX, double centerY, double radiusX, double radiusY)
    {
        var kappaX = radiusX * EllipseKappa;
        var kappaY = radiusY * EllipseKappa;
        content.Append(Number(centerX + radiusX)).Append(' ').Append(Number(centerY)).Append(" m\n");
        content.Append(Number(centerX + radiusX)).Append(' ').Append(Number(centerY + kappaY)).Append(' ')
            .Append(Number(centerX + kappaX)).Append(' ').Append(Number(centerY + radiusY)).Append(' ')
            .Append(Number(centerX)).Append(' ').Append(Number(centerY + radiusY)).Append(" c\n");
        content.Append(Number(centerX - kappaX)).Append(' ').Append(Number(centerY + radiusY)).Append(' ')
            .Append(Number(centerX - radiusX)).Append(' ').Append(Number(centerY + kappaY)).Append(' ')
            .Append(Number(centerX - radiusX)).Append(' ').Append(Number(centerY)).Append(" c\n");
        content.Append(Number(centerX - radiusX)).Append(' ').Append(Number(centerY - kappaY)).Append(' ')
            .Append(Number(centerX - kappaX)).Append(' ').Append(Number(centerY - radiusY)).Append(' ')
            .Append(Number(centerX)).Append(' ').Append(Number(centerY - radiusY)).Append(" c\n");
        content.Append(Number(centerX + kappaX)).Append(' ').Append(Number(centerY - radiusY)).Append(' ')
            .Append(Number(centerX + radiusX)).Append(' ').Append(Number(centerY - kappaY)).Append(' ')
            .Append(Number(centerX + radiusX)).Append(' ').Append(Number(centerY)).Append(" c\n");
        content.Append("h\n");
    }

    private static void AppendRoundedRectangle(
        StringBuilder content,
        double x,
        double y,
        double width,
        double height,
        double radiusX,
        double radiusY)
    {
        var kappaX = radiusX * EllipseKappa;
        var kappaY = radiusY * EllipseKappa;
        content.Append(Number(x + radiusX)).Append(' ').Append(Number(y)).Append(" m\n");
        content.Append(Number(x + width - radiusX)).Append(' ').Append(Number(y)).Append(" l\n");
        content.Append(Number(x + width - radiusX + kappaX)).Append(' ').Append(Number(y)).Append(' ')
            .Append(Number(x + width)).Append(' ').Append(Number(y + radiusY - kappaY)).Append(' ')
            .Append(Number(x + width)).Append(' ').Append(Number(y + radiusY)).Append(" c\n");
        content.Append(Number(x + width)).Append(' ').Append(Number(y + height - radiusY)).Append(" l\n");
        content.Append(Number(x + width)).Append(' ').Append(Number(y + height - radiusY + kappaY)).Append(' ')
            .Append(Number(x + width - radiusX + kappaX)).Append(' ').Append(Number(y + height)).Append(' ')
            .Append(Number(x + width - radiusX)).Append(' ').Append(Number(y + height)).Append(" c\n");
        content.Append(Number(x + radiusX)).Append(' ').Append(Number(y + height)).Append(" l\n");
        content.Append(Number(x + radiusX - kappaX)).Append(' ').Append(Number(y + height)).Append(' ')
            .Append(Number(x)).Append(' ').Append(Number(y + height - radiusY + kappaY)).Append(' ')
            .Append(Number(x)).Append(' ').Append(Number(y + height - radiusY)).Append(" c\n");
        content.Append(Number(x)).Append(' ').Append(Number(y + radiusY)).Append(" l\n");
        content.Append(Number(x)).Append(' ').Append(Number(y + radiusY - kappaY)).Append(' ')
            .Append(Number(x + radiusX - kappaX)).Append(' ').Append(Number(y)).Append(' ')
            .Append(Number(x + radiusX)).Append(' ').Append(Number(y)).Append(" c\n");
        content.Append("h\n");
    }

    /// <summary>Converts the path data of an SVG path into PDF path operators.</summary>
    private static void BuildPath(
        string data, StringBuilder content, List<string> reports, List<List<(double X, double Y)>>? subpaths = null)
    {
        var scanner = new PathScanner(data);
        var currentX = 0.0;
        var currentY = 0.0;
        var startX = 0.0;
        var startY = 0.0;
        var lastControlX = 0.0;
        var lastControlY = 0.0;
        var hasControl = false;
        var previous = '\0';

        while (scanner.TryReadCommand(out var command))
        {
            var relative = char.IsLower(command);
            var upper = char.ToUpperInvariant(command);
            var first = true;

            do
            {
                switch (upper)
                {
                    case 'M':
                    {
                        if (!scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x += currentX;
                            y += currentY;
                        }

                        if (first)
                        {
                            content.Append(Number(x)).Append(' ').Append(Number(y)).Append(" m\n");
                            currentX = x;
                            currentY = y;
                            startX = x;
                            startY = y;
                            subpaths?.Add([(x, y)]);
                        }
                        else
                        {
                            // Further pairs after a moveto are line commands.
                            content.Append(Number(x)).Append(' ').Append(Number(y)).Append(" l\n");
                            currentX = x;
                            currentY = y;
                        }

                        break;
                    }

                    case 'L':
                    {
                        if (!scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x += currentX;
                            y += currentY;
                        }

                        content.Append(Number(x)).Append(' ').Append(Number(y)).Append(" l\n");
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'H':
                    {
                        if (!scanner.TryReadNumber(out var x))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x += currentX;
                        }

                        content.Append(Number(x)).Append(' ').Append(Number(currentY)).Append(" l\n");
                        currentX = x;
                        break;
                    }

                    case 'V':
                    {
                        if (!scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            y += currentY;
                        }

                        content.Append(Number(currentX)).Append(' ').Append(Number(y)).Append(" l\n");
                        currentY = y;
                        break;
                    }

                    case 'C':
                    {
                        if (!scanner.TryReadNumber(out var x1) || !scanner.TryReadNumber(out var y1)
                            || !scanner.TryReadNumber(out var x2) || !scanner.TryReadNumber(out var y2)
                            || !scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x1 += currentX;
                            y1 += currentY;
                            x2 += currentX;
                            y2 += currentY;
                            x += currentX;
                            y += currentY;
                        }

                        AppendCurve(content, x1, y1, x2, y2, x, y);
                        lastControlX = x2;
                        lastControlY = y2;
                        hasControl = true;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'S':
                    {
                        if (!scanner.TryReadNumber(out var x2) || !scanner.TryReadNumber(out var y2)
                            || !scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x2 += currentX;
                            y2 += currentY;
                            x += currentX;
                            y += currentY;
                        }

                        var smooth = previous is 'C' or 'S' && hasControl;
                        var x1 = smooth ? 2 * currentX - lastControlX : currentX;
                        var y1 = smooth ? 2 * currentY - lastControlY : currentY;
                        AppendCurve(content, x1, y1, x2, y2, x, y);
                        lastControlX = x2;
                        lastControlY = y2;
                        hasControl = true;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'Q':
                    {
                        if (!scanner.TryReadNumber(out var qx) || !scanner.TryReadNumber(out var qy)
                            || !scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            qx += currentX;
                            qy += currentY;
                            x += currentX;
                            y += currentY;
                        }

                        AppendQuadratic(content, currentX, currentY, qx, qy, x, y);
                        lastControlX = qx;
                        lastControlY = qy;
                        hasControl = true;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'T':
                    {
                        if (!scanner.TryReadNumber(out var x) || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x += currentX;
                            y += currentY;
                        }

                        var smooth = previous is 'Q' or 'T' && hasControl;
                        var qx = smooth ? 2 * currentX - lastControlX : currentX;
                        var qy = smooth ? 2 * currentY - lastControlY : currentY;
                        AppendQuadratic(content, currentX, currentY, qx, qy, x, y);
                        lastControlX = qx;
                        lastControlY = qy;
                        hasControl = true;
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'A':
                    {
                        if (!scanner.TryReadNumber(out var radiusX) || !scanner.TryReadNumber(out var radiusY)
                            || !scanner.TryReadNumber(out var rotation) || !scanner.TryReadNumber(out var largeArc)
                            || !scanner.TryReadNumber(out var sweep) || !scanner.TryReadNumber(out var x)
                            || !scanner.TryReadNumber(out var y))
                        {
                            return;
                        }

                        if (relative)
                        {
                            x += currentX;
                            y += currentY;
                        }

                        AppendArc(content, currentX, currentY, radiusX, radiusY, rotation, largeArc != 0, sweep != 0, x, y, reports);
                        currentX = x;
                        currentY = y;
                        break;
                    }

                    case 'Z':
                    {
                        content.Append("h\n");
                        currentX = startX;
                        currentY = startY;
                        hasControl = false;
                        break;
                    }

                    default:
                        reports.Add("SVG: an unknown path command was ignored.");
                        return;
                }

                // The on-curve point of the segment, for markers.
                if (subpaths is { Count: > 0 } && upper is 'L' or 'H' or 'V' or 'C' or 'S' or 'Q' or 'T' or 'A'
                    && subpaths[^1] is { } points && (points.Count == 0 || points[^1].X != currentX || points[^1].Y != currentY))
                {
                    points.Add((currentX, currentY));
                }

                first = false;
                previous = upper; // per segment, so a repeated S/T reflects its own predecessor
            }
            // Every command except Z repeats implicitly while coordinates follow (SVG 1.1, 8.3.2).
            while (upper is not 'Z' && scanner.TryPeekNumber());
        }
    }

    /// <summary>The on-curve vertices of a shape, with subpath boundaries for markers (OF-B07).</summary>
    private static List<SvgVertex> MarkerVertices(IReadOnlyList<List<(double X, double Y)>> subpaths)
    {
        var vertices = new List<SvgVertex>();
        foreach (var points in subpaths)
        {
            vertices.AddRange(SubpathVertices(points));
        }

        return vertices;
    }

    /// <summary>The on-curve vertices of one subpath, with incoming and outgoing directions.</summary>
    private static List<SvgVertex> SubpathVertices(IReadOnlyList<(double X, double Y)> points)
    {
        var vertices = new List<SvgVertex>();
        if (points.Count < 2)
        {
            return vertices;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var previous = index == 0 ? points[0] : points[index - 1];
            var next = index == points.Count - 1 ? points[index] : points[index + 1];
            var inX = index == 0 ? points[1].X - points[0].X : points[index].X - previous.X;
            var inY = index == 0 ? points[1].Y - points[0].Y : points[index].Y - previous.Y;
            var outX = index == points.Count - 1 ? inX : next.X - points[index].X;
            var outY = index == points.Count - 1 ? inY : next.Y - points[index].Y;
            vertices.Add(new SvgVertex(
                points[index].X,
                points[index].Y,
                inX,
                inY,
                outX,
                outY,
                SubpathStart: index == 0,
                SubpathEnd: index == points.Count - 1));
        }

        return vertices;
    }

    /// <summary>The marker vertices of a polyline or polygon.</summary>
    private static List<SvgVertex> MarkerVertices(IReadOnlyList<double> coordinates)
    {
        var points = new List<(double X, double Y)>();
        for (var index = 0; index + 1 < coordinates.Count; index += 2)
        {
            points.Add((coordinates[index], coordinates[index + 1]));
        }

        return SubpathVertices(points);
    }

    private static void AppendCurve(
        StringBuilder content,
        double x1,
        double y1,
        double x2,
        double y2,
        double x,
        double y)
        => content.Append(Number(x1)).Append(' ').Append(Number(y1)).Append(' ')
            .Append(Number(x2)).Append(' ').Append(Number(y2)).Append(' ')
            .Append(Number(x)).Append(' ').Append(Number(y)).Append(" c\n");

    /// <summary>Converts a quadratic curve into a cubic one (PDF has no quadratic curves).</summary>
    private static void AppendQuadratic(
        StringBuilder content,
        double startX,
        double startY,
        double controlX,
        double controlY,
        double endX,
        double endY)
    {
        var x1 = startX + 2.0 / 3.0 * (controlX - startX);
        var y1 = startY + 2.0 / 3.0 * (controlY - startY);
        var x2 = endX + 2.0 / 3.0 * (controlX - endX);
        var y2 = endY + 2.0 / 3.0 * (controlY - endY);
        AppendCurve(content, x1, y1, x2, y2, endX, endY);
    }

    /// <summary>Converts an elliptical arc into cubic Bézier curves.</summary>
    private static void AppendArc(
        StringBuilder content,
        double startX,
        double startY,
        double radiusX,
        double radiusY,
        double rotation,
        bool largeArc,
        bool sweep,
        double endX,
        double endY,
        List<string> reports)
    {
        if (radiusX == 0 || radiusY == 0)
        {
            // A degenerate arc is a straight line.
            content.Append(Number(endX)).Append(' ').Append(Number(endY)).Append(" l\n");
            return;
        }

        var radians = rotation * Math.PI / 180.0;
        var cosRotation = Math.Cos(radians);
        var sinRotation = Math.Sin(radians);

        // Endpoint to center parameterization (SVG 1.1, appendix F.6.5).
        var dx = (startX - endX) / 2.0;
        var dy = (startY - endY) / 2.0;
        var x1 = cosRotation * dx + sinRotation * dy;
        var y1 = -sinRotation * dx + cosRotation * dy;

        var lambda = (x1 * x1) / (radiusX * radiusX) + (y1 * y1) / (radiusY * radiusY);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            radiusX *= scale;
            radiusY *= scale;
        }

        var sign = largeArc == sweep ? -1.0 : 1.0;
        var numerator = radiusX * radiusX * radiusY * radiusY
            - radiusX * radiusX * y1 * y1
            - radiusY * radiusY * x1 * x1;
        var denominator = radiusX * radiusX * y1 * y1 + radiusY * radiusY * x1 * x1;
        var coefficient = denominator == 0 ? 0 : sign * Math.Sqrt(Math.Max(0, numerator / denominator));

        var centerXPrime = coefficient * (radiusX * y1 / radiusY);
        var centerYPrime = -coefficient * (radiusY * x1 / radiusX);
        var centerX = cosRotation * centerXPrime - sinRotation * centerYPrime + (startX + endX) / 2.0;
        var centerY = sinRotation * centerXPrime + cosRotation * centerYPrime + (startY + endY) / 2.0;

        var startAngle = Math.Atan2((y1 - centerYPrime) / radiusY, (x1 - centerXPrime) / radiusX);
        var endAngle = Math.Atan2((-y1 - centerYPrime) / radiusY, (-x1 - centerXPrime) / radiusX);
        var delta = endAngle - startAngle;
        if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }
        else if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }

        var segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
        if (segments < 1)
        {
            segments = 1;
        }

        var deltaAngle = delta / segments;
        var alpha = 4.0 / 3.0 * Math.Tan(deltaAngle / 4.0);
        var angle = startAngle;
        var pointX = startX;
        var pointY = startY;

        for (var segment = 0; segment < segments; segment++)
        {
            var nextAngle = angle + deltaAngle;
            var cosA = Math.Cos(angle);
            var sinA = Math.Sin(angle);
            var cosB = Math.Cos(nextAngle);
            var sinB = Math.Sin(nextAngle);

            var nextX = centerX + cosRotation * radiusX * cosB - sinRotation * radiusY * sinB;
            var nextY = centerY + sinRotation * radiusX * cosB + cosRotation * radiusY * sinB;

            var derivativeX1 = -cosRotation * radiusX * sinA - sinRotation * radiusY * cosA;
            var derivativeY1 = -sinRotation * radiusX * sinA + cosRotation * radiusY * cosA;
            var derivativeX2 = -cosRotation * radiusX * sinB - sinRotation * radiusY * cosB;
            var derivativeY2 = -sinRotation * radiusX * sinB + cosRotation * radiusY * cosB;

            AppendCurve(
                content,
                pointX + alpha * derivativeX1,
                pointY + alpha * derivativeY1,
                nextX - alpha * derivativeX2,
                nextY - alpha * derivativeY2,
                nextX,
                nextY);

            pointX = nextX;
            pointY = nextY;
            angle = nextAngle;
        }
    }

    // MARK: - Transformations

    /// <summary>A 2D transformation matrix (like the PDF <c>cm</c> operator).</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

        /// <summary>Multiplies two matrices; the right one is applied first.</summary>
        public static Matrix Multiply(Matrix left, Matrix right) => new(
            left.A * right.A + left.C * right.B,
            left.B * right.A + left.D * right.B,
            left.A * right.C + left.C * right.D,
            left.B * right.C + left.D * right.D,
            left.A * right.E + left.C * right.F + left.E,
            left.B * right.E + left.D * right.F + left.F);
    }

    private static void AppendMatrix(StringBuilder content, Matrix matrix)
    {
        if (matrix == Matrix.Identity)
        {
            return;
        }

        content.Append(Number(matrix.A)).Append(' ').Append(Number(matrix.B)).Append(' ')
            .Append(Number(matrix.C)).Append(' ').Append(Number(matrix.D)).Append(' ')
            .Append(Number(matrix.E)).Append(' ').Append(Number(matrix.F)).Append(" cm\n");
    }

    /// <summary>Reads a <c>transform</c> attribute into a matrix.</summary>
    private static Matrix ReadTransform(string? value, List<string> reports)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Matrix.Identity;
        }

        var result = Matrix.Identity;
        var position = 0;
        var text = value!;
        while (position < text.Length)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == ','))
            {
                position++;
            }

            var nameStart = position;
            while (position < text.Length && char.IsLetter(text[position]))
            {
                position++;
            }

            if (nameStart == position)
            {
                break;
            }

            var name = text[nameStart..position];
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position >= text.Length || text[position] != '(')
            {
                break;
            }

            var end = text.IndexOf(')', position);
            if (end < 0)
            {
                break;
            }

            var arguments = ParseNumbers(text[(position + 1)..end]) ?? [];
            position = end + 1;

            var matrix = name switch
            {
                "translate" when arguments.Count >= 1 => new Matrix(1, 0, 0, 1, arguments[0], arguments.Count > 1 ? arguments[1] : 0),
                "scale" when arguments.Count >= 1 => new Matrix(arguments[0], 0, 0, arguments.Count > 1 ? arguments[1] : arguments[0], 0, 0),
                "rotate" when arguments.Count >= 1 => Rotate(arguments),
                "matrix" when arguments.Count >= 6 => new Matrix(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5]),
                "skewX" when arguments.Count >= 1 => new Matrix(1, 0, Math.Tan(arguments[0] * Math.PI / 180.0), 1, 0, 0),
                "skewY" when arguments.Count >= 1 => new Matrix(1, Math.Tan(arguments[0] * Math.PI / 180.0), 0, 1, 0, 0),
                _ => Matrix.Identity,
            };

            if (matrix == Matrix.Identity && !string.Equals(name, "matrix", StringComparison.Ordinal))
            {
                reports.Add($"SVG: the transformation '{name}' is not supported.");
            }

            var combined = Matrix.Multiply(result, matrix);
            if (!double.IsFinite(combined.A) || !double.IsFinite(combined.B) || !double.IsFinite(combined.C)
                || !double.IsFinite(combined.D) || !double.IsFinite(combined.E) || !double.IsFinite(combined.F))
            {
                reports.Add("SVG: a transformation exceeds the supported numeric range.");
                return Matrix.Identity;
            }
            result = combined;
        }

        return result;
    }

    private static Matrix Rotate(IReadOnlyList<double> arguments)
    {
        var radians = arguments[0] * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        if (arguments.Count < 3)
        {
            return new Matrix(cos, sin, -sin, cos, 0, 0);
        }

        var centerX = arguments[1];
        var centerY = arguments[2];
        return Matrix.Multiply(
            Matrix.Multiply(new Matrix(1, 0, 0, 1, centerX, centerY), new Matrix(cos, sin, -sin, cos, 0, 0)),
            new Matrix(1, 0, 0, 1, -centerX, -centerY));
    }

    // MARK: - Style attributes

    /// <summary>Reads the presentation attributes of an element (the inline style wins).</summary>
    private static SvgStyle ReadStyle(
        XElement element, SvgStyle inherited, List<string> reports, IReadOnlyList<SvgStyleRule> rules)
    {
        var style = inherited.Clone();
        style.ClipPath = null;

        void Apply(string name, string? rawValue)
        {
            if (rawValue is null)
            {
                return;
            }

            var value = rawValue.Trim();
            switch (name)
            {
                case "font-family":
                    style.FontFamily = value.Split(',')[0].Trim().Trim('\'', '"');
                    break;
                case "font-size":
                    var fontSize = ReadNumberPrefix(value, out var fontUnit);
                    var factor = fontUnit.Trim() switch
                    {
                        "" or "px" => 1, "pt" => 96.0 / 72, "pc" => 16, "in" => 96,
                        "cm" => 96.0 / 2.54, "mm" => 96.0 / 25.4, "em" => style.FontSize, "%" => style.FontSize / 100,
                        _ => 0,
                    };
                    var resolvedSize = fontSize * factor;
                    if (resolvedSize is > 0 && double.IsFinite(resolvedSize.Value)) style.FontSize = resolvedSize.Value;
                    else reports.Add($"SVG: font size '{value}' is not supported.");
                    break;
                case "font-weight":
                    if (value == "bold" || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) && weight >= 600)
                        style.FontBold = true;
                    else if (value == "normal" || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        style.FontBold = false;
                    else reports.Add($"SVG: font weight '{value}' is not supported.");
                    break;
                case "font-style":
                    if (value is "normal" or "italic" or "oblique") style.FontItalic = value != "normal";
                    else reports.Add($"SVG: font style '{value}' is not supported.");
                    break;
                case "text-anchor":
                    if (value is "start" or "middle" or "end") style.TextAnchor = value;
                    else reports.Add($"SVG: text anchor '{value}' is not supported.");
                    break;
                case "letter-spacing":
                case "word-spacing":
                    if (value is not ("normal" or "0" or "0px")) reports.Add($"SVG: {name} is not supported.");
                    break;
                case "clip-path":
                    style.ClipPath = value == "none" ? null : value;
                    break;
                case "filter":
                    // A filter needs a second rendering pass: the shape is drawn without it.
                    if (value != "none")
                    {
                        reports.Add($"SVG: the filter '{value}' is not applied.");
                    }

                    break;
                case "mask":
                    // OF-B07: a mask becomes a luminance soft mask; "none" removes it.
                    style.Mask = value == "none" ? null : Reference(value);
                    break;
                case "marker":
                case "marker-start":
                case "marker-mid":
                case "marker-end":
                    // Drawn with the shape (RenderMarkers); a missing target is reported there.
                    break;
                case "fill":
                    if (value.StartsWith("url(#", StringComparison.Ordinal) && value.EndsWith(')'))
                    {
                        style.FillGradient = value[5..^1];
                        style.Fill = null;
                        style.FillAlpha = 1;
                    }
                    else
                    {
                        style.FillGradient = null;
                        style.Fill = ParseColor(value, reports, out var fillAlpha);
                        style.FillAlpha = fillAlpha;
                    }
                    break;
                case "stroke":
                    if (value.StartsWith("url(#", StringComparison.Ordinal) && value.EndsWith(')'))
                    {
                        // OF-B07: the paint server is applied to the stroke through an alpha mask.
                        style.StrokePaint = Reference(value);
                        style.Stroke = null;
                        style.StrokeAlpha = 1;
                    }
                    else
                    {
                        style.Stroke = ParseColor(value, reports, out var strokeAlpha);
                        style.StrokeAlpha = strokeAlpha;
                    }

                    break;
                case "display":
                    style.DisplayNone = string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
                    break;
                case "visibility":
                    style.Visible = !(value.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("collapse", StringComparison.OrdinalIgnoreCase));
                    break;
                case "stroke-width":
                    if (ParseNumber(value) is { } width && width >= 0)
                    {
                        style.StrokeWidth = width;
                    }

                    break;
                case "stroke-dasharray":
                    style.DashArray = ParseDashArray(value, reports);
                    break;
                case "stroke-dashoffset":
                    if (ParseNumber(value) is { } offset && offset >= 0 && double.IsFinite(offset))
                    {
                        style.DashOffset = offset;
                    }
                    else
                    {
                        reports.Add($"SVG: the stroke dash offset '{value}' is not supported.");
                    }

                    break;
                case "stroke-linecap":
                    switch (value.ToLowerInvariant())
                    {
                        case "butt":
                            style.LineCap = 0;
                            break;
                        case "round":
                            style.LineCap = 1;
                            break;
                        case "square":
                            style.LineCap = 2;
                            break;
                        default:
                            reports.Add($"SVG: the stroke line cap '{value}' is not supported.");
                            break;
                    }

                    break;
                case "stroke-linejoin":
                    switch (value.ToLowerInvariant())
                    {
                        case "miter":
                            style.LineJoin = 0;
                            break;
                        case "round":
                            style.LineJoin = 1;
                            break;
                        case "bevel":
                            style.LineJoin = 2;
                            break;
                        default:
                            reports.Add($"SVG: the stroke line join '{value}' is not supported.");
                            break;
                    }

                    break;
                case "stroke-miterlimit":
                    if (ParseNumber(value) is { } miter && miter >= 1 && double.IsFinite(miter))
                    {
                        style.MiterLimit = miter;
                    }
                    else
                    {
                        reports.Add($"SVG: the stroke miter limit '{value}' is not supported.");
                    }

                    break;
                case "fill-rule":
                    style.EvenOdd = string.Equals(value, "evenodd", StringComparison.OrdinalIgnoreCase);
                    break;
                case "fill-opacity":
                    if (ParseOpacity(value) is { } fillOpacity)
                    {
                        style.FillOpacity = fillOpacity;
                    }

                    break;
                case "stroke-opacity":
                    if (ParseOpacity(value) is { } strokeOpacity)
                    {
                        style.StrokeOpacity = strokeOpacity;
                    }

                    break;
                case "opacity":
                    // OF-B07: a group with its own opacity becomes a transparency group; for an element
                    // the value multiplies the fill and the stroke opacity (and is inherited).
                    if (ParseOpacity(value) is { } elementOpacity)
                    {
                        style.GroupOpacity *= elementOpacity;
                        style.OwnOpacity = style.OwnOpacity is { } previous ? previous * elementOpacity : elementOpacity;
                    }
                    else
                    {
                        reports.Add($"SVG: the opacity '{value}' is not supported.");
                    }

                    break;
                default:
                    break;
            }
        }

        // A rule of a <style> element comes before the presentation attributes and before the
        // inline style: the cascade runs from the sheet over the attributes to the inline style.
        if (rules.Count > 0)
        {
            var id = element.Attribute("id")?.Value;
            var classes = element.Attribute("class")?.Value?.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var rule in rules)
            {
                if ((rule.Tag is null || IsElement(element, rule.Tag))
                    && (rule.Id is null || rule.Id == id)
                    && (rule.Class is null || classes is not null && Array.IndexOf(classes, rule.Class) >= 0))
                {
                    Apply(rule.Name, rule.Value);
                }
            }
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace != XNamespace.None)
            {
                continue;
            }

            Apply(attribute.Name.LocalName, attribute.Value);
        }

        var inline = element.Attribute("style")?.Value;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            foreach (var declaration in inline!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = declaration.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                Apply(declaration[..separator].Trim(), declaration[(separator + 1)..].Trim());
            }
        }

        return style;
    }

    /// <summary>Parses a color value; <c>null</c> means "none". <paramref name="alpha"/> is the color's own alpha.</summary>
    private static Color? ParseColor(string value, List<string> reports, out double alpha)
    {
        alpha = 1;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return Color.Black;
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3 && hex.All(char.IsAsciiHexDigit))
            {
                var red = Convert.ToInt32(hex[0].ToString(), 16) * 17;
                var green = Convert.ToInt32(hex[1].ToString(), 16) * 17;
                var blue = Convert.ToInt32(hex[2].ToString(), 16) * 17;
                return new Color((byte)red, (byte)green, (byte)blue);
            }

            if (hex.Length == 6 && hex.All(char.IsAsciiHexDigit))
            {
                return Color.FromHex(hex);
            }

            if (hex.Length == 8 && hex.All(char.IsAsciiHexDigit))
            {
                alpha = Convert.ToInt32(hex[6..], 16) / 255.0;
                return Color.FromHex(hex[..6]);
            }

            reports.Add($"SVG: the color '{value}' is not supported.");
            return null;
        }

        var open = value.IndexOf('(');
        if (open > 0 && value.EndsWith(')')
            && value[..open].Trim() is var function
            && (function.Equals("rgb", StringComparison.OrdinalIgnoreCase) || function.Equals("rgba", StringComparison.OrdinalIgnoreCase)))
        {
            // rgb(255,0,0), rgb(100%,0%,0%), rgba(255,0,0,0.5); a fourth part is the alpha in both forms.
            var parts = value[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is 3 or 4
                && Channel(parts[0]) is { } red
                && Channel(parts[1]) is { } green
                && Channel(parts[2]) is { } blue
                && (parts.Length == 3 || ParseOpacity(parts[3]) is not null))
            {
                alpha = parts.Length == 4 ? ParseOpacity(parts[3])!.Value : 1;
                return new Color(red, green, blue);
            }
        }

        if (NamedColors.TryGetValue(value, out var named))
        {
            return named;
        }

        reports.Add($"SVG: the color '{value}' is not supported.");
        return null;
    }

    /// <summary>A color channel as a number (0–255) or a percentage.</summary>
    private static byte? Channel(string part)
    {
        var percent = part.EndsWith('%');
        if (!double.TryParse(percent ? part[..^1] : part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            return null;
        }

        return (byte)Math.Round(Math.Clamp(percent ? value * 2.55 : value, 0, 255));
    }

    /// <summary>
    /// Reads a <c>stroke-dasharray</c> value. An empty list means a solid line. A dash pattern is
    /// always symmetric, so an odd number of lengths is repeated (both SVG and PDF require this).
    /// </summary>
    private static double[]? ParseDashArray(string value, List<string> reports)
    {
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lengths = ParseNumbers(value);
        if (lengths is not { Count: > 0 } || lengths.Exists(length => length < 0 || !double.IsFinite(length)))
        {
            reports.Add($"SVG: the stroke dash pattern '{value}' is not supported.");
            return null;
        }

        // A pattern of only zeros draws nothing; a PDF renderer would show a solid line instead.
        if (lengths.TrueForAll(length => length == 0))
        {
            return null;
        }

        return lengths.Count % 2 == 0 ? [.. lengths] : [.. lengths, .. lengths];
    }

    private static double? ParseOpacity(string value)
    {
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && double.IsFinite(percent))
        {
            return Math.Clamp(percent / 100.0, 0, 1);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            return Math.Clamp(number, 0, 1);
        }

        return null;
    }

    /// <summary>The SVG color names that are supported.</summary>
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Color.Black,
        ["white"] = Color.White,
        ["red"] = new Color(255, 0, 0),
        ["green"] = new Color(0, 128, 0),
        ["lime"] = new Color(0, 255, 0),
        ["blue"] = new Color(0, 0, 255),
        ["yellow"] = new Color(255, 255, 0),
        ["cyan"] = new Color(0, 255, 255),
        ["aqua"] = new Color(0, 255, 255),
        ["magenta"] = new Color(255, 0, 255),
        ["fuchsia"] = new Color(255, 0, 255),
        ["gray"] = new Color(128, 128, 128),
        ["grey"] = new Color(128, 128, 128),
        ["silver"] = new Color(192, 192, 192),
        ["maroon"] = new Color(128, 0, 0),
        ["olive"] = new Color(128, 128, 0),
        ["navy"] = new Color(0, 0, 128),
        ["teal"] = new Color(0, 128, 128),
        ["purple"] = new Color(128, 0, 128),
        ["orange"] = new Color(255, 165, 0),
        ["pink"] = new Color(255, 192, 203),
        ["brown"] = new Color(165, 42, 42),
    };

    // MARK: - Numbers and lengths

    private enum LengthUnit
    {
        Pixel,
        Point,
        Millimeter,
        Centimeter,
        Inch,
        Pica,
    }

    /// <summary>Reads a length and converts it into points.</summary>
    private static double ParseLength(
        string value,
        List<string> reports,
        string name,
        LengthUnit? unit = null)
    {
        var text = value.Trim();
        if (text.EndsWith('%'))
        {
            // Percentages need a reference size that is not known here.
            reports.Add($"SVG: the relative length '{name}' is not supported.");
            return 0;
        }

        var number = ReadNumberPrefix(text, out var rest);
        if (number is null)
        {
            reports.Add($"SVG: the length '{value}' is not valid.");
            return 0;
        }

        var factor = unit switch
        {
            LengthUnit.Point => 1.0,
            LengthUnit.Millimeter => 72.0 / 25.4,
            LengthUnit.Centimeter => 72.0 / 2.54,
            LengthUnit.Inch => 72.0,
            LengthUnit.Pica => 12.0,
            _ => 72.0 / PixelsPerInch,
        };

        var suffix = rest.Trim().ToLowerInvariant();
        switch (suffix)
        {
            case "":
            case "px":
                break;
            case "pt":
                factor = 1.0;
                break;
            case "mm":
                factor = 72.0 / 25.4;
                break;
            case "cm":
                factor = 72.0 / 2.54;
                break;
            case "in":
                factor = 72.0;
                break;
            case "pc":
                factor = 12.0;
                break;
            default:
                reports.Add($"SVG: the unit '{suffix}' is not supported.");
                break;
        }

        var points = number.Value * factor;
        return unit == LengthUnit.Pixel ? points * PixelsPerInch / 72.0 : points;
    }

    /// <summary>Reads the number at the start of a text.</summary>
    private static double? ReadNumberPrefix(string text, out string rest)
    {
        var number = ReadNumberAt(text, 0, out var end);
        rest = number is null ? text : text[end..];
        return number;
    }

    /// <summary>
    /// Reads the number at <paramref name="start"/> without copying the rest of the text (a long
    /// path would otherwise be copied once per number). A second dot starts the next number
    /// (minified <c>M.5.5</c>); non-finite values (<c>1e400</c>) are rejected.
    /// </summary>
    private static double? ReadNumberAt(string text, int start, out int end)
    {
        var position = start;
        if (position < text.Length && (text[position] == '-' || text[position] == '+'))
        {
            position++;
        }

        var digitsStart = position;
        var seenDot = false;
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || (text[position] == '.' && !seenDot)))
        {
            seenDot |= text[position] == '.';
            position++;
        }

        if (position == digitsStart || (position == digitsStart + 1 && seenDot))
        {
            end = start;
            return null;
        }

        // An exponent belongs to the number (SVG/SVG path numbers allow it).
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            var exponent = position;
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            var digits = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == digits)
            {
                position = exponent;
            }
        }

        if (double.TryParse(text.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value))
        {
            end = position;
            return value;
        }

        end = start;
        return null;
    }

    private static double? ParseNumber(string? value)
        => value is null ? null : ReadNumberPrefix(value.Trim(), out _);

    /// <summary>Reads a list of numbers separated by whitespace or commas.</summary>
    private static List<double>? ParseNumbers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var numbers = new List<double>();
        var text = value!;
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && (char.IsWhiteSpace(text[position]) || text[position] == ','))
            {
                position++;
            }

            if (position >= text.Length)
            {
                break;
            }

            var number = ReadNumberAt(text, position, out position);
            if (number is null)
            {
                return null;
            }

            numbers.Add(number.Value);
        }

        return numbers;
    }

    /// <summary>Formats a number for a PDF operator.</summary>
    private static string Number(double value)
    {
        // Overflowing arithmetic (huge coordinates times a transform) must not write "Infinity" or "NaN".
        if (!double.IsFinite(value) || Math.Abs(value) < 0.00005)
        {
            value = 0;
        }

        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static bool IsElement(XElement element, string localName)
        => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

    /// <summary>Scans the data of a path for commands and numbers.</summary>
    private struct PathScanner
    {
        private readonly string _text;
        private int _position;

        public PathScanner(string text)
        {
            _text = text;
            _position = 0;
        }

        /// <summary>Reads the next path command.</summary>
        public bool TryReadCommand(out char command)
        {
            command = '\0';
            while (_position < _text.Length)
            {
                var character = _text[_position];
                if (char.IsWhiteSpace(character) || character == ',')
                {
                    _position++;
                    continue;
                }

                if (char.IsLetter(character))
                {
                    _position++;
                    command = character;
                    return true;
                }

                return false;
            }

            return false;
        }

        /// <summary>Reads the next number of the path data.</summary>
        public bool TryReadNumber(out double value)
        {
            value = 0;
            while (_position < _text.Length)
            {
                var character = _text[_position];
                if (char.IsWhiteSpace(character) || character == ',')
                {
                    _position++;
                    continue;
                }

                break;
            }

            if (_position >= _text.Length)
            {
                return false;
            }

            var number = ReadNumberAt(_text, _position, out var end);
            if (number is null)
            {
                return false;
            }

            _position = end;
            value = number.Value;
            return true;
        }

        /// <summary>Whether another number follows.</summary>
        public bool TryPeekNumber()
        {
            var position = _position;
            while (position < _text.Length)
            {
                var character = _text[position];
                if (char.IsWhiteSpace(character) || character == ',')
                {
                    position++;
                    continue;
                }

                break;
            }

            if (position >= _text.Length)
            {
                return false;
            }

            var character2 = _text[position];
            return char.IsDigit(character2) || character2 is '-' or '+' or '.';
        }
    }
}

/// <summary>A converted SVG image: the display size in points and the PDF content operators.</summary>
internal sealed record SvgDrawing(
    double Width,
    double Height,
    double UserWidth,
    double UserHeight,
    string Content,
    bool UsesPatterns = false);

/// <summary>An SVG gradient expressed in its own coordinate system.</summary>
internal sealed record SvgGradient(
    bool Radial,
    IReadOnlyList<double> Coordinates,
    IReadOnlyList<(double Offset, Color Color)> Stops,
    bool Extend = true,
    IReadOnlyList<Color>? Samples = null);

/// <summary>A user-space SVG tile and its PDF drawing commands.</summary>
internal sealed record SvgPattern(double X, double Y, double Width, double Height, IReadOnlyList<double> Matrix, string Content);
