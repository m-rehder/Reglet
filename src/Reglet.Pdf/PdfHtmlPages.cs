using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Reglet;

/// <summary>
/// Paged HTML (ADR-0039): the document is laid out by the PDF writer, and every PDF page becomes an
/// SVG page with real text, so page breaks, line breaks, headers, footers and fields are the PDF's.
/// </summary>
internal static class PdfHtmlWriter
{
    public static async Task<WriteResult> WriteAsync(
        PortableDocument document,
        Stream output,
        PdfWriteOptions? pdfOptions,
        PdfHtmlOptions settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        // Encryption and a signature protect the file, not the layout; the preview needs neither.
        var pdf = (pdfOptions ?? PdfWriteOptions.Default) with { Encryption = null, Signature = null };
        using var buffer = new MemoryStream();
        var result = await new PdfDocumentWriter().WriteAsync(document, buffer, pdf, cancellationToken).ConfigureAwait(false);
        var warnings = result.Warnings.ToList();
        var fonts = PdfDocumentBuilder.ResolveFonts(pdf, new UnsupportedFeatureHandler(UnsupportedContentBehavior.Warn, []));
        var html = PdfDocumentReader.PagesToHtml(buffer.ToArray(), document.Metadata, fonts, settings, warnings, cancellationToken);
        await output.WriteAsync(new UTF8Encoding(false).GetBytes(html), cancellationToken).ConfigureAwait(false);
        return new WriteResult(warnings.DistinctBy(warning => warning.Message));
    }
}

public sealed partial class PdfDocumentReader
{
    internal static string PagesToHtml(
        byte[] data, DocumentMetadata metadata, FontCollection? fonts, PdfHtmlOptions settings, List<WriteWarning> warnings, CancellationToken cancellationToken)
    {
        var file = PdfFile.Load(data);
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Pages")) is not { } pagesRoot)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<PageEntry>();
        CollectPages(file, pagesRoot, new PageEntry(pagesRoot, null, null), pages, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), depth: 0);

        // Ids and font families carry a token of the document, so two fragments on one host page do not collide.
        var p = settings.ClassPrefix;
        var painter = new HtmlPagePainter(file, fonts, p + Convert.ToHexString(SHA256.HashData(data))[..8].ToLowerInvariant(), warnings);
        var body = new StringBuilder();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            painter.Paint(page, body);
        }

        var html = new StringBuilder();
        if (settings.Output != HtmlOutput.Fragment)
        {
            html.Append("<!DOCTYPE html>\n<html");
            if (metadata.Language is { Length: > 0 } language)
            {
                html.Append(" lang=\"").Append(WebUtility.HtmlEncode(language)).Append('"');
            }

            html.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
                .Append(WebUtility.HtmlEncode(metadata.Title ?? string.Empty)).Append("</title>\n")
                .Append("<style>body{margin:0;padding:12pt 0;background:#e6e6e6}</style>\n</head>\n<body>\n");
        }

        html.Append("<div class=\"").Append(p).Append("document\">\n<style>")
            .Append('.').Append(p).Append("page{position:relative;overflow:hidden;background:#fff;margin:0 auto 12pt}")
            .Append('.').Append(p).Append("page>svg{display:block;width:100%;height:100%}")
            .Append('.').Append(p).Append("page text{white-space:pre}");
        painter.WriteStyles(html);
        html.Append("</style>\n<svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\"><defs>");
        painter.WriteDefinitions(html);
        html.Append("</defs></svg>\n").Append(body).Append("</div>\n");
        if (settings.Output != HtmlOutput.Fragment)
        {
            html.Append("</body>\n</html>\n");
        }

        return html.ToString();
    }

    /// <summary>Turns the content streams of Reglet's own PDF into SVG: paths, images, clips and text.</summary>
    private sealed class HtmlPagePainter(PdfFile file, FontCollection? collection, string token, List<WriteWarning> warnings)
    {
        private const int MaxFormDepth = 16;
        private readonly Dictionary<PdfDictionary, FontInfo> _fonts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PdfStreamValue, string?> _images = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, (string Family, FontFace Face)> _faces = new(StringComparer.Ordinal);
        private readonly StringBuilder _definitions = new();
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private int _clipCount;
        private int _pageNumber;

        private sealed record FontInfo(PdfFontDecoder Decoder, string Css);

        /// <summary>A text piece with its position in PDF space, for the reading order of PdfDocumentReader.</summary>
        private sealed record TextItem(double X, double Y, double Size, double EndX, string Text, string Markup);

        private struct State
        {
            public State()
            {
            }

            public Matrix Ctm { get; set; } = Matrix.Identity;

            public string Fill { get; set; } = "#000";

            public string Stroke { get; set; } = "#000";

            public double FillAlpha { get; set; } = 1;

            public double StrokeAlpha { get; set; } = 1;

            public double LineWidth { get; set; } = 1;

            public string? Dash { get; set; }

            public int Cap { get; set; }

            public int Join { get; set; }

            public double Miter { get; set; } = 10;

            public string? Clip { get; set; }

            public FontInfo? Font { get; set; }

            public double FontSize { get; set; } = 12;

            public double Leading { get; set; }

            public double CharSpacing { get; set; }

            public double WordSpacing { get; set; }

            public double Scale { get; set; } = 1;

            public double Rise { get; set; }

            public int Render { get; set; }
        }

        public void Paint(PageEntry page, StringBuilder output)
        {
            _pageNumber++;
            var box = file.Array(page.MediaBox) is { Items.Count: 4 } items && items.Items.Select(item => file.Number(item)).ToArray() is [{ } x0, { } y0, { } x1, { } y1]
                ? (X0: Math.Min(x0, x1), Y0: Math.Min(y0, y1), X1: Math.Max(x0, x1), Y1: Math.Max(y0, y1))
                : (X0: 0.0, Y0: 0.0, X1: 595.28, Y1: 841.89);
            var width = box.X1 - box.X0;
            var height = box.Y1 - box.Y0;

            // PDF space (y up) to page space (y down, origin at the top left of the media box).
            var flip = new Matrix(1, 0, 0, -1, -box.X0, box.Y1);
            var graphics = new StringBuilder();
            var texts = new List<TextItem>();
            Run(ReadContents(file, page.Page), page.Resources, new State { Ctm = flip }, graphics, texts, depth: 0);

            output.Append("<div class=\"").Append(token[..^8]).Append("page\" data-page=\"").Append(_pageNumber)
                .Append("\" style=\"width:").Append(N(width)).Append("pt;height:").Append(N(height)).Append("pt\">")
                .Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\">\n")
                .Append(graphics);
            WriteTexts(texts, output);
            output.Append("</svg></div>\n");
        }

        public void WriteStyles(StringBuilder output)
        {
            foreach (var (family, face) in _faces.Values)
            {
                output.Append("@font-face{font-family:\"").Append(family).Append("\";src:url(data:font/ttf;base64,")
                    .Append(Convert.ToBase64String(face.Data)).Append(") format(\"truetype\")}");
            }
        }

        public void WriteDefinitions(StringBuilder output) => output.Append(_definitions);

        /// <summary>Text goes last and in the order PdfDocumentReader extracts it, so selection, search and copy follow the reading order.</summary>
        private static void WriteTexts(List<TextItem> texts, StringBuilder output)
        {
            var lines = new List<List<TextItem>>();
            foreach (var item in texts.OrderByDescending(item => item.Y))
            {
                var line = lines.Count > 0 ? lines[^1] : null;
                if (line is not null && Math.Abs(line[0].Y - item.Y) <= Math.Max(1, item.Size * 0.3))
                {
                    line.Add(item);
                }
                else
                {
                    lines.Add([item]);
                }
            }

            foreach (var line in lines)
            {
                TextItem? previous = null;
                foreach (var item in line.OrderBy(item => item.X))
                {
                    // An invisible space where the reader inserts one, so a copied line keeps its words apart.
                    if (previous is not null && !previous.Text.EndsWith(' ') && !item.Text.StartsWith(' ')
                        && (item.X - previous.EndX > Math.Max(1, Math.Min(previous.Size, item.Size)) * 0.15
                            || item.X < previous.X - (Math.Max(1, Math.Min(previous.Size, item.Size)) * 0.5)))
                    {
                        output.Append("<text x=\"").Append(N(previous.EndX)).Append("\" y=\"").Append(N(-previous.Y))
                            .Append("\" font-size=\"").Append(N(Math.Max(1, previous.Size))).Append("\" fill-opacity=\"0\"> </text>");
                    }

                    output.Append(item.Markup);
                    previous = item;
                }

                output.Append('\n');
            }
        }

        private void Run(byte[] content, PdfDictionary? resources, State initial, StringBuilder graphics, List<TextItem> texts, int depth)
        {
            var parser = new PdfObjectParser(new PdfLexer(content));
            var operands = new List<PdfValue>();
            var state = initial;
            var stack = new Stack<State>();
            var path = new StringBuilder();
            var (currentX, currentY, startX, startY) = (0.0, 0.0, 0.0, 0.0);
            string? pendingClip = null;
            var text = Matrix.Identity;
            var line = Matrix.Identity;

            while (true)
            {
                var token = parser.Peek(0);
                if (token.Kind == PdfTokenKind.EndOfFile)
                {
                    break;
                }

                if (token.Kind != PdfTokenKind.Keyword)
                {
                    if (operands.Count > 4096)
                    {
                        operands.Clear();
                    }

                    operands.Add(parser.ParseValue());
                    continue;
                }

                var operation = parser.Advance().Text!;
                switch (operation)
                {
                    case "BI":
                        PdfTextExtractor.SkipInlineImage(content, parser);
                        Report("an inline image is not shown");
                        break;
                    case "q":
                        stack.Push(state);
                        break;
                    case "Q":
                        if (stack.Count > 0)
                        {
                            state = stack.Pop();
                        }

                        break;
                    case "cm" when Numbers(operands, 6) is { } m:
                        state.Ctm = Matrix.Multiply(new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]), state.Ctm);
                        break;
                    case "w" when Numbers(operands, 1) is { } w:
                        state.LineWidth = w[0];
                        break;
                    case "J" when Numbers(operands, 1) is { } cap:
                        state.Cap = (int)cap[0];
                        break;
                    case "j" when Numbers(operands, 1) is { } join:
                        state.Join = (int)join[0];
                        break;
                    case "M" when Numbers(operands, 1) is { } miter:
                        state.Miter = miter[0];
                        break;
                    case "d":
                        state.Dash = operands.Count > 0 && operands[0] is PdfArray { Items.Count: > 0 } dash
                            ? string.Join(' ', dash.Items.Select(item => N(file.Number(item) ?? 0)))
                            : null;
                        break;
                    case "gs" when Name(operands, 0) is { } stateName:
                        if (file.Dictionary(file.Dictionary(resources?.Get("ExtGState"))?.Get(stateName)) is { } extGState)
                        {
                            state.FillAlpha = file.Number(extGState.Get("ca")) ?? state.FillAlpha;
                            state.StrokeAlpha = file.Number(extGState.Get("CA")) ?? state.StrokeAlpha;
                        }

                        break;
                    case "g" or "rg" or "k" or "sc" or "scn":
                        state.Fill = Color(operands) ?? state.Fill;
                        break;
                    case "G" or "RG" or "K" or "SC" or "SCN":
                        state.Stroke = Color(operands) ?? state.Stroke;
                        break;
                    case "cs":
                        state.Fill = "#000";
                        break;
                    case "CS":
                        state.Stroke = "#000";
                        break;
                    case "m" when Numbers(operands, 2) is { } move:
                        path.Append('M').Append(Point(move[0], move[1]));
                        (currentX, currentY, startX, startY) = (move[0], move[1], move[0], move[1]);
                        break;
                    case "l" when Numbers(operands, 2) is { } to:
                        path.Append('L').Append(Point(to[0], to[1]));
                        (currentX, currentY) = (to[0], to[1]);
                        break;
                    case "c" when Numbers(operands, 6) is { } curve:
                        path.Append('C').Append(Point(curve[0], curve[1])).Append(' ').Append(Point(curve[2], curve[3])).Append(' ').Append(Point(curve[4], curve[5]));
                        (currentX, currentY) = (curve[4], curve[5]);
                        break;
                    case "v" when Numbers(operands, 4) is { } v:
                        // The first control point is the current point.
                        path.Append('C').Append(Point(currentX, currentY)).Append(' ').Append(Point(v[0], v[1])).Append(' ').Append(Point(v[2], v[3]));
                        (currentX, currentY) = (v[2], v[3]);
                        break;
                    case "y" when Numbers(operands, 4) is { } y:
                        // The second control point is the end point.
                        path.Append('C').Append(Point(y[0], y[1])).Append(' ').Append(Point(y[2], y[3])).Append(' ').Append(Point(y[2], y[3]));
                        (currentX, currentY) = (y[2], y[3]);
                        break;
                    case "h":
                        path.Append('Z');
                        (currentX, currentY) = (startX, startY);
                        break;
                    case "re" when Numbers(operands, 4) is { } r:
                        path.Append('M').Append(Point(r[0], r[1])).Append('L').Append(Point(r[0] + r[2], r[1]))
                            .Append('L').Append(Point(r[0] + r[2], r[1] + r[3])).Append('L').Append(Point(r[0], r[1] + r[3])).Append('Z');
                        (currentX, currentY, startX, startY) = (r[0], r[1], r[0], r[1]);
                        break;
                    case "W" or "W*":
                        pendingClip = operation;
                        break;
                    case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n":
                        if (operation is "s" or "b" or "b*")
                        {
                            path.Append('Z');
                        }

                        if (path.Length > 0 && operation != "n")
                        {
                            Emit(graphics, state, PathElement(path.ToString(), state, operation));
                        }

                        if (pendingClip is not null && path.Length > 0)
                        {
                            state.Clip = AddClip(path.ToString(), state, pendingClip == "W*");
                        }

                        pendingClip = null;
                        path.Clear();
                        break;
                    case "sh":
                        Report("a gradient is shown as a flat area or not at all");
                        break;
                    case "Do" when Name(operands, 0) is { } name
                        && file.GetStream(file.Dictionary(resources?.Get("XObject"))?.Get(name)) is { } xobject:
                        if (file.Name(xobject.Dictionary.Get("Subtype")) == "Image")
                        {
                            if (Image(xobject) is { } id)
                            {
                                var placement = Matrix.Multiply(new Matrix(1, 0, 0, -1, 0, 1), state.Ctm);
                                Emit(graphics, state, $"<use href=\"#{id}\" transform=\"{M(placement)}\"{Opacity("opacity", state.FillAlpha)}/>");
                            }
                        }
                        else if (depth < MaxFormDepth)
                        {
                            var formState = state;
                            if (Numbers(file.Array(xobject.Dictionary.Get("Matrix"))?.Items.ToList() ?? [], 6) is { } f)
                            {
                                formState.Ctm = Matrix.Multiply(new Matrix(f[0], f[1], f[2], f[3], f[4], f[5]), state.Ctm);
                            }

                            // shortcut: the form's /BBox is not applied as a clip; Reglet's forms draw inside it.
                            Run(file.DecodeStream(xobject), file.Dictionary(xobject.Dictionary.Get("Resources")) ?? resources, formState, graphics, texts, depth + 1);
                        }

                        break;
                    case "BT":
                        text = Matrix.Identity;
                        line = Matrix.Identity;
                        break;
                    case "Tf" when Name(operands, 0) is { } fontName && Numbers(operands.Skip(1).ToList(), 1) is { } size:
                        state.FontSize = size[0];
                        state.Font = file.Dictionary(file.Dictionary(resources?.Get("Font"))?.Get(fontName)) is { } fontDictionary
                            ? Font(fontDictionary)
                            : null;
                        break;
                    case "Tc" when Numbers(operands, 1) is { } tc:
                        state.CharSpacing = tc[0];
                        break;
                    case "Tw" when Numbers(operands, 1) is { } tw:
                        state.WordSpacing = tw[0];
                        break;
                    case "Tz" when Numbers(operands, 1) is { } tz:
                        state.Scale = tz[0] / 100;
                        break;
                    case "TL" when Numbers(operands, 1) is { } tl:
                        state.Leading = tl[0];
                        break;
                    case "Ts" when Numbers(operands, 1) is { } ts:
                        state.Rise = ts[0];
                        break;
                    case "Tr" when Numbers(operands, 1) is { } tr:
                        state.Render = (int)tr[0] % 4;
                        break;
                    case "Td" or "TD" when Numbers(operands, 2) is { } td:
                        if (operation == "TD")
                        {
                            state.Leading = -td[1];
                        }

                        line = Matrix.Multiply(Matrix.Translation(td[0], td[1]), line);
                        text = line;
                        break;
                    case "Tm" when Numbers(operands, 6) is { } tm:
                        line = new Matrix(tm[0], tm[1], tm[2], tm[3], tm[4], tm[5]);
                        text = line;
                        break;
                    case "T*":
                        line = Matrix.Multiply(Matrix.Translation(0, -state.Leading), line);
                        text = line;
                        break;
                    case "Tj" when String(operands, 0) is { } shown:
                        text = Show(shown, text, state, graphics, texts);
                        break;
                    case "'" when String(operands, 0) is { } next:
                        line = Matrix.Multiply(Matrix.Translation(0, -state.Leading), line);
                        text = Show(next, line, state, graphics, texts);
                        break;
                    case "\"" when operands.Count >= 3 && Numbers(operands, 2) is { } spacing && String(operands, 2) is { } quoted:
                        state.WordSpacing = spacing[0];
                        state.CharSpacing = spacing[1];
                        line = Matrix.Multiply(Matrix.Translation(0, -state.Leading), line);
                        text = Show(quoted, line, state, graphics, texts);
                        break;
                    case "TJ" when operands.Count > 0 && operands[0] is PdfArray array:
                        foreach (var item in array.Items)
                        {
                            if (item is PdfString piece)
                            {
                                text = Show(piece.Bytes, text, state, graphics, texts);
                            }
                            else if (file.Number(item) is { } adjustment)
                            {
                                text = Matrix.Multiply(Matrix.Translation(-adjustment / 1000 * state.FontSize * state.Scale, 0), text);
                            }
                        }

                        break;
                }

                operands.Clear();
            }
        }

        /// <summary>Draws one shown string as one SVG text whose length is the PDF's advance, and returns the text matrix after it.</summary>
        private Matrix Show(byte[] bytes, Matrix textMatrix, State state, StringBuilder graphics, List<TextItem> texts)
        {
            if (state.Font is not { } font || bytes.Length == 0)
            {
                return textMatrix;
            }

            var decoder = font.Decoder;
            var content = new StringBuilder(bytes.Length);
            var advance = 0.0;
            for (var index = 0; index + decoder.CodeLength <= bytes.Length; index += decoder.CodeLength)
            {
                var code = decoder.CodeLength == 2 ? (bytes[index] << 8) | bytes[index + 1] : bytes[index];
                var (glyph, width, _) = decoder.DecodeCode(code);
                content.Append(glyph);
                advance += (width / 1000 * state.FontSize) + state.CharSpacing + (decoder.CodeLength == 1 && code == 32 ? state.WordSpacing : 0);
            }

            var start = Matrix.Multiply(textMatrix, state.Ctm);
            var after = Matrix.Multiply(Matrix.Translation(advance * state.Scale, 0), textMatrix);
            if (content.Length == 0)
            {
                return after;
            }

            // Glyphs upright in page space: rise and horizontal scaling in text space, then the text and page matrices.
            var placement = Matrix.Multiply(new Matrix(1, 0, 0, -1, 0, 0), Matrix.Multiply(new Matrix(state.Scale, 0, 0, 1, 0, state.Rise), start));
            var paint = state.Render switch
            {
                1 => $" fill=\"none\" stroke=\"{state.Stroke}\" stroke-width=\"{N(state.LineWidth)}\"{Opacity("stroke-opacity", state.StrokeAlpha)}",
                2 => $" fill=\"{state.Fill}\"{Opacity("fill-opacity", state.FillAlpha)} stroke=\"{state.Stroke}\" stroke-width=\"{N(state.LineWidth)}\"{Opacity("stroke-opacity", state.StrokeAlpha)}",
                3 => " fill-opacity=\"0\"",
                _ => $" fill=\"{state.Fill}\"{Opacity("fill-opacity", state.FillAlpha)}",
            };
            var markup = new StringBuilder("<text transform=\"").Append(M(placement)).Append("\" font-size=\"").Append(N(state.FontSize))
                .Append("\" style=\"").Append(font.Css).Append('"').Append(paint);
            if (advance > 0)
            {
                markup.Append(" textLength=\"").Append(N(advance)).Append("\" lengthAdjust=\"spacingAndGlyphs\"");
            }

            markup.Append('>').Append(WebUtility.HtmlEncode(content.ToString())).Append("</text>");
            var element = markup.ToString();
            if (state.Clip is not null)
            {
                element = $"<g clip-path=\"url(#{state.Clip})\">{element}</g>";
            }

            // Page space has y down; the negated y orders lines like the reader's y-up baselines.
            var end = Matrix.Multiply(after, state.Ctm);
            texts.Add(new TextItem(start.E, -start.F, state.FontSize * Math.Max(Math.Abs(start.D), Math.Abs(start.B)), end.E, content.ToString(), element));
            return after;
        }

        private string PathElement(string d, State state, string operation)
        {
            var fill = operation is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";
            var stroke = operation is "S" or "s" or "B" or "B*" or "b" or "b*";
            var element = new StringBuilder("<path d=\"").Append(d).Append("\" transform=\"").Append(M(state.Ctm)).Append('"');
            element.Append(" fill=\"").Append(fill ? state.Fill : "none").Append('"');
            if (fill)
            {
                element.Append(Opacity("fill-opacity", state.FillAlpha));
                if (operation is "f*" or "B*" or "b*")
                {
                    element.Append(" fill-rule=\"evenodd\"");
                }
            }

            if (stroke)
            {
                element.Append(" stroke=\"").Append(state.Stroke).Append("\" stroke-width=\"").Append(N(Math.Max(state.LineWidth, 0.01))).Append('"')
                    .Append(Opacity("stroke-opacity", state.StrokeAlpha));
                if (state.Cap != 0)
                {
                    element.Append(" stroke-linecap=\"").Append(state.Cap == 1 ? "round" : "square").Append('"');
                }

                if (state.Join != 0)
                {
                    element.Append(" stroke-linejoin=\"").Append(state.Join == 1 ? "round" : "bevel").Append('"');
                }
                else if (Math.Abs(state.Miter - 4) > 1e-9)
                {
                    element.Append(" stroke-miterlimit=\"").Append(N(Math.Max(1, state.Miter))).Append('"');
                }

                if (state.Dash is { } dash)
                {
                    element.Append(" stroke-dasharray=\"").Append(dash).Append('"');
                }
            }

            return element.Append("/>").ToString();
        }

        private static void Emit(StringBuilder graphics, State state, string element)
        {
            if (state.Clip is { } clip)
            {
                graphics.Append("<g clip-path=\"url(#").Append(clip).Append(")\">").Append(element).Append("</g>\n");
            }
            else
            {
                graphics.Append(element).Append('\n');
            }
        }

        /// <summary>A clip path in page space, intersected with the current clip.</summary>
        private string AddClip(string d, State state, bool evenOdd)
        {
            var id = token + "-c" + ++_clipCount;
            _definitions.Append("<clipPath id=\"").Append(id).Append("\" clipPathUnits=\"userSpaceOnUse\"");
            if (state.Clip is { } outer)
            {
                _definitions.Append(" clip-path=\"url(#").Append(outer).Append(")\"");
            }

            _definitions.Append("><path d=\"").Append(d).Append("\" transform=\"").Append(M(state.Ctm)).Append('"')
                .Append(evenOdd ? " clip-rule=\"evenodd\"" : string.Empty).Append("/></clipPath>");
            return id;
        }

        /// <summary>The id of an image in the shared definitions; drawn several times, it is embedded once.</summary>
        private string? Image(PdfStreamValue stream)
        {
            if (_images.TryGetValue(stream, out var existing))
            {
                return existing;
            }

            var readWarnings = new List<ReadWarning>();
            string? id = null;
            if (PdfImageRecovery.Decode(file, stream, readWarnings) is { } image)
            {
                id = token + "-i" + (_images.Count + 1);
                _definitions.Append("<image id=\"").Append(id).Append("\" width=\"1\" height=\"1\" preserveAspectRatio=\"none\" href=\"data:")
                    .Append(image.MediaType == ImageMediaType.Jpeg ? "image/jpeg" : "image/png").Append(";base64,")
                    .Append(Convert.ToBase64String(image.RawData.Span)).Append("\"/>");
            }
            else
            {
                Report("an image could not be converted and is not shown");
            }

            _images[stream] = id;
            return id;
        }

        /// <summary>
        /// The CSS of a PDF font: an embedded font of the collection becomes an @font-face of its own, a standard
        /// font the matching system families.
        /// </summary>
        private FontInfo Font(PdfDictionary dictionary)
        {
            if (_fonts.TryGetValue(dictionary, out var known))
            {
                return known;
            }

            var baseFont = file.Name(dictionary.Get("BaseFont")) ?? string.Empty;
            var postScriptName = baseFont.Length > 7 && baseFont[6] == '+' && baseFont[..6].All(char.IsAsciiLetterUpper) ? baseFont[7..] : baseFont;
            var decoder = PdfFontDecoder.Create(file, dictionary);
            string css;
            if (!_faces.TryGetValue(postScriptName, out var face)
                && collection?.Families.SelectMany(collection.GetFaces).FirstOrDefault(item => item.PostScriptName == postScriptName) is { } match)
            {
                face = (token + "-f" + (_faces.Count + 1), match);
                _faces[postScriptName] = face;
            }

            if (face.Face is not null)
            {
                css = $"font-family:'{face.Family}'";
            }
            else
            {
                var family = baseFont.StartsWith("Times", StringComparison.Ordinal) ? "'Times New Roman',Times,serif"
                    : baseFont.StartsWith("Courier", StringComparison.Ordinal) ? "'Courier New',Courier,monospace"
                    : baseFont.StartsWith("Symbol", StringComparison.Ordinal) || baseFont.StartsWith("ZapfDingbats", StringComparison.Ordinal) ? "serif"
                    : "Helvetica,Arial,sans-serif";
                css = $"font-family:{family}" + (decoder.Bold ? ";font-weight:bold" : string.Empty) + (decoder.Italic ? ";font-style:italic" : string.Empty);
                if (decoder.IsType0)
                {
                    Report($"the font '{postScriptName}' is not in the font collection and is shown in a system font");
                }
            }

            var info = new FontInfo(decoder, css);
            _fonts[dictionary] = info;
            return info;
        }

        private string? Color(List<PdfValue> operands)
        {
            if (operands.Count > 0 && operands[^1] is PdfName)
            {
                Report("a pattern fill is shown in grey");
                return "#808080";
            }

            var values = operands.Select(item => file.Number(item)).ToArray();
            if (values.Any(value => value is null))
            {
                return null;
            }

            static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
            return values.Length switch
            {
                1 => Hex(Byte(values[0]!.Value), Byte(values[0]!.Value), Byte(values[0]!.Value)),
                3 => Hex(Byte(values[0]!.Value), Byte(values[1]!.Value), Byte(values[2]!.Value)),
                4 => Hex(
                    Byte((1 - values[0]!.Value) * (1 - values[3]!.Value)),
                    Byte((1 - values[1]!.Value) * (1 - values[3]!.Value)),
                    Byte((1 - values[2]!.Value) * (1 - values[3]!.Value))),
                _ => null,
            };
        }

        private static string Hex(int red, int green, int blue) => $"#{red:x2}{green:x2}{blue:x2}";

        private void Report(string message)
        {
            if (_reported.Add(message))
            {
                warnings.Add(new WriteWarning("HTML: " + message + "."));
            }
        }

        private double[]? Numbers(List<PdfValue> operands, int count)
        {
            if (operands.Count < count)
            {
                return null;
            }

            var values = new double[count];
            for (var index = 0; index < count; index++)
            {
                if (file.Number(operands[index]) is not { } value)
                {
                    return null;
                }

                values[index] = value;
            }

            return values;
        }

        private static string? Name(List<PdfValue> operands, int index)
            => index < operands.Count && operands[index] is PdfName name ? name.Name : null;

        private static byte[]? String(List<PdfValue> operands, int index)
            => index < operands.Count && operands[index] is PdfString text ? text.Bytes : null;

        private static string Point(double x, double y) => N(x) + " " + N(y);

        private static string Opacity(string name, double value) => value < 1 ? $" {name}=\"{N(Math.Max(0, value))}\"" : string.Empty;

        private static string M(Matrix m) => $"matrix({N(m.A)} {N(m.B)} {N(m.C)} {N(m.D)} {N(m.E)} {N(m.F)})";

        private static string N(double value) => (Math.Round(value, 3) is var rounded && rounded == 0 ? 0 : rounded).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity => new(1, 0, 0, 1, 0, 0);

        public static Matrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

        public static Matrix Multiply(Matrix left, Matrix right) => new(
            (left.A * right.A) + (left.B * right.C),
            (left.A * right.B) + (left.B * right.D),
            (left.C * right.A) + (left.D * right.C),
            (left.C * right.B) + (left.D * right.D),
            (left.E * right.A) + (left.F * right.C) + right.E,
            (left.E * right.B) + (left.F * right.D) + right.F);
    }
}
