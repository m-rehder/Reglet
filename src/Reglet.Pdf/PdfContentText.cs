using System.Text;

namespace Reglet;

/// <summary>
/// A text piece from a PDF content stream (position in user coordinates). <see cref="EndX"/>
/// is the position after the last glyph, derived from the font widths; it lets callers decide
/// whether two pieces on a line are separated by a space.
/// </summary>
internal readonly record struct PdfTextSegment(
    double X,
    double Y,
    double Size,
    string Text,
    bool Bold,
    bool Italic,
    bool Unmapped,
    double EndX,
    int Image = -1,
    bool Rotated = false,
    bool Rule = false,
    bool Path = false);

/// <summary>
/// maps glyph/byte codes of a PDF font to Unicode and supplies the glyph widths:
/// a ToUnicode CMap wins (for simple and Type0 fonts), otherwise simple fonts are decoded
/// as WinAnsi. Type0 fonts use 2-byte codes (Identity-H).
/// </summary>
internal sealed class PdfFontDecoder
{
    private readonly PdfToUnicodeMap? _toUnicode;
    private readonly Func<int, double?> _width;
    private readonly IPdfFontMetrics? _standardMetrics;

    private readonly Dictionary<int, string>? _differences;
    private readonly Dictionary<int, string>? _texTable;

    private PdfFontDecoder(string baseFont, PdfToUnicodeMap? toUnicode, bool isType0, Func<int, double?> width, Dictionary<int, string>? differences = null)
    {
        _toUnicode = toUnicode;
        _differences = differences;
        _texTable = isType0 ? null : TexTable(baseFont);
        _width = width;
        IsType0 = isType0;
        _standardMetrics = isType0 ? null : new PdfStandardFontMetrics(PdfStandardFonts.Resolve(baseFont, false, false));
        Bold = baseFont.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || baseFont.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || baseFont.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        Italic = baseFont.Contains("Italic", StringComparison.OrdinalIgnoreCase)
            || baseFont.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsType0 { get; }

    public bool Bold { get; }

    public bool Italic { get; }

    /// <summary>Bytes per character code.</summary>
    public int CodeLength => IsType0 ? 2 : 1;

    public static PdfFontDecoder Create(PdfFile file, PdfDictionary font)
    {
        var baseFont = file.Name(font.Get("BaseFont")) ?? string.Empty;
        var subtype = file.Name(font.Get("Subtype"));
        var isType0 = subtype == "Type0";
        PdfToUnicodeMap? toUnicode = null;

        if (file.GetStream(font.Get("ToUnicode")) is { } cmapStream)
        {
            toUnicode = PdfToUnicodeMap.Parse(Encoding.Latin1.GetString(file.DecodeStream(cmapStream)));
        }

        return new PdfFontDecoder(baseFont, toUnicode, isType0, isType0 ? CidWidths(file, font) : SimpleWidths(file, font),
            isType0 ? null : Differences(file, font));
    }

    /// <summary>The characters of one code plus its advance width (1/1000 text space units).</summary>
    public (string Text, double Width, bool Unmapped) DecodeCode(int code)
    {
        string text;
        var unmapped = false;
        if (_toUnicode is not null && _toUnicode.TryGet(code, out var mapped))
        {
            text = mapped;
        }
        else if (_differences is not null && _differences.TryGetValue(code, out var named))
        {
            text = named;
        }
        else if (_texTable is not null && _texTable.TryGetValue(code, out var tex))
        {
            text = tex;
        }
        else if (!IsType0)
        {
            text = PdfWinAnsi.Decode([(byte)code]);
        }
        else
        {
            text = "\uFFFD";
            unmapped = true;
        }

        var width = _width(code)
            ?? (_standardMetrics is not null && text.Length > 0 ? _standardMetrics.GetWidth(text.EnumerateRunes().FirstOrDefault().Value) : 500);
        return (text, width, unmapped);
    }

    public string Decode(byte[] bytes, out bool unmapped)
    {
        unmapped = false;
        var builder = new StringBuilder(bytes.Length);
        for (var index = 0; index + CodeLength <= bytes.Length; index += CodeLength)
        {
            var code = CodeLength == 2 ? (bytes[index] << 8) | bytes[index + 1] : bytes[index];
            var (text, _, missing) = DecodeCode(code);
            builder.Append(text);
            unmapped |= missing;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The big operators and radicals of the TeX extension font CMEX (its built-in encoding is not in the PDF):
    /// sums, products, integrals and root signs (ADR-0034).
    /// </summary>
    private static Dictionary<int, string>? TexTable(string baseFont)
    {
        var name = baseFont.Contains('+') ? baseFont[(baseFont.IndexOf('+') + 1)..] : baseFont;
        if (!name.StartsWith("CMEX", StringComparison.Ordinal))
        {
            return null;
        }

        return new Dictionary<int, string>
        {
            [0x50] = "∑", [0x58] = "∑", [0x51] = "∏", [0x59] = "∏", [0x52] = "∫", [0x5A] = "∫",
            [0x70] = "√", [0x71] = "√", [0x72] = "√", [0x73] = "√", [0x74] = "√", [0x75] = "√",
        };
    }

    /// <summary>The <c>/Differences</c> of a simple font's encoding as code to text (glyph names via the Adobe list).</summary>
    private static Dictionary<int, string>? Differences(PdfFile file, PdfDictionary font)
    {
        if (file.Dictionary(font.Get("Encoding")) is not { } encoding || file.Array(encoding.Get("Differences")) is not { } array)
        {
            return null;
        }

        var map = new Dictionary<int, string>();
        var code = 0;
        foreach (var item in array.Items.Take(1024))
        {
            if (file.Integer(item) is { } number)
            {
                code = (int)Math.Clamp(number, 0, 255);
            }
            else if (file.Name(item) is { } name)
            {
                if (PdfGlyphNames.ToUnicode(name) is { } text && code <= 255)
                {
                    map[code] = text;
                }

                code++;
            }
        }

        return map.Count == 0 ? null : map;
    }

    private static Func<int, double?> SimpleWidths(PdfFile file, PdfDictionary font)
    {
        var first = (int)Math.Clamp(file.Integer(font.Get("FirstChar")) ?? 0, 0, 255);
        var widths = file.Array(font.Get("Widths"))?.Items.Select(item => file.Number(item)).ToArray() ?? [];
        var missing = file.Dictionary(font.Get("FontDescriptor")) is { } descriptor
            ? file.Number(descriptor.Get("MissingWidth"))
            : null;
        if (widths.Length == 0)
        {
            return _ => null; // standard 14 font: metrics by character
        }

        return code => code - first is var index && index >= 0 && index < widths.Length
            ? widths[index] ?? missing
            : missing;
    }

    private static Func<int, double?> CidWidths(PdfFile file, PdfDictionary font)
    {
        var descendant = file.Array(font.Get("DescendantFonts")) is { Items.Count: > 0 } descendants
            ? file.Dictionary(descendants.Items[0])
            : null;
        var defaultWidth = descendant is null ? 1000 : file.Number(descendant.Get("DW")) ?? 1000;
        var map = new Dictionary<int, double>();
        if (descendant is not null && file.Array(descendant.Get("W")) is { } w)
        {
            var items = w.Items;
            for (var index = 0; index < items.Count && map.Count <= 0x10000;)
            {
                if (file.Integer(items[index]) is not { } firstCid)
                {
                    break;
                }

                if (index + 1 < items.Count && file.Array(items[index + 1]) is { } list)
                {
                    for (var offset = 0; offset < list.Items.Count && firstCid + offset <= 0xFFFF; offset++)
                    {
                        if (file.Number(list.Items[offset]) is { } value)
                        {
                            map[(int)(firstCid + offset)] = value;
                        }
                    }

                    index += 2;
                }
                else if (index + 2 < items.Count
                    && file.Integer(items[index + 1]) is { } lastCid
                    && file.Number(items[index + 2]) is { } value)
                {
                    for (var cid = firstCid; cid <= Math.Min(lastCid, 0xFFFF); cid++)
                    {
                        map[(int)cid] = value;
                    }

                    index += 3;
                }
                else
                {
                    break;
                }
            }
        }

        return code => map.TryGetValue(code, out var width) ? width : defaultWidth;
    }
}

/// <summary>Reads the relevant mappings of a ToUnicode CMap (bfchar and bfrange).</summary>
internal sealed class PdfToUnicodeMap
{
    private const int MaxCode = 0xFFFF;

    private readonly Dictionary<int, string> _map = [];

    public static PdfToUnicodeMap Parse(string cmap)
    {
        var result = new PdfToUnicodeMap();
        var parser = new PdfObjectParser(new PdfLexer(Encoding.ASCII.GetBytes(cmap)));

        while (true)
        {
            var token = parser.Peek(0);
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Kind != PdfTokenKind.Keyword)
            {
                parser.Advance();
                continue;
            }

            if (token.Text == "beginbfchar")
            {
                parser.Advance();
                result.ReadBfChar(parser);
            }
            else if (token.Text == "beginbfrange")
            {
                parser.Advance();
                result.ReadBfRange(parser);
            }
            else
            {
                parser.Advance();
            }
        }

        return result;
    }

    public bool TryGet(int code, out string text) => _map.TryGetValue(code, out text!);

    private void ReadBfChar(PdfObjectParser parser)
    {
        while (parser.Peek(0).Kind == PdfTokenKind.String)
        {
            var source = parser.ParseValue() as PdfString;
            var target = parser.ParseValue() as PdfString;
            if (source is not null && target is not null && TryCode(source.Bytes, out var code) && code <= MaxCode)
            {
                _map[(int)code] = DecodeTarget(target.Bytes);
            }
        }
    }

    private void ReadBfRange(PdfObjectParser parser)
    {
        while (parser.Peek(0).Kind == PdfTokenKind.String)
        {
            var low = parser.ParseValue() as PdfString;
            var high = parser.ParseValue() as PdfString;

            // The decoder reads 2-byte codes; larger codes are never reachable. The
            // limit also prevents a range like <0000> <FFFFFFFF>
            // creates billions of entries. An invalid range is skipped along with its target.
            long first = 0;
            long last = -1;
            var valid = low is not null && high is not null
                && TryCode(low.Bytes, out first) && TryCode(high.Bytes, out last)
                && first <= MaxCode;
            var start = valid ? (int)first : 0;
            var end = valid ? (int)Math.Min(last, MaxCode) : -1;

            if (parser.Peek(0).Kind == PdfTokenKind.ArrayOpen)
            {
                parser.Advance();
                var code = start;
                while (parser.Peek(0).Kind == PdfTokenKind.String)
                {
                    if (parser.ParseValue() is PdfString item && code <= end)
                    {
                        _map[code] = DecodeTarget(item.Bytes);
                    }

                    code++;
                }

                if (parser.Peek(0).Kind == PdfTokenKind.ArrayClose)
                {
                    parser.Advance();
                }
            }
            else if (parser.ParseValue() is PdfString target && DecodeTarget(target.Bytes) is { Length: > 0 } baseText)
            {
                // target is UTF-16BE; per code the last code unit is incremented
                // (ISO 32000-1, 9.10.3). This way surrogate pairs also remain valid as a base.
                var prefix = baseText[..^1];
                var lastUnit = baseText[^1];
                for (var code = start; code <= end && lastUnit + (code - start) <= char.MaxValue; code++)
                {
                    _map[code] = prefix + (char)(lastUnit + (code - start));
                }
            }
        }
    }

    private static bool TryCode(byte[] bytes, out long code)
    {
        code = 0;
        if (bytes.Length is 0 or > 4)
        {
            return false;
        }

        foreach (var value in bytes)
        {
            code = (code << 8) | value;
        }

        return true;
    }

    private static string DecodeTarget(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2));
    }
}

/// <summary>
/// Interprets the text operators of a content stream (subset of ISO 32000-1, 9.4): text and
/// line matrices, <c>Tc</c>/<c>Tw</c>/<c>Tz</c>, <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>, the
/// graphics state (<c>q</c>/<c>Q</c>/<c>cm</c>) and form XObjects (<c>Do</c>). Glyph advances
/// come from the font widths so that pieces carry their real extent. Inline images
/// (<c>BI</c> … <c>ID</c> … <c>EI</c>) are skipped as raw data.
/// </summary>
internal static class PdfTextExtractor
{
    /// <summary>Maximum nesting of form XObjects (guards against cycles and hostile depth).</summary>
    private const int MaxFormDepth = 16;

    /// <summary>A form XObject resolved from the resources: content, its fonts and forms, matrix.</summary>
    internal sealed record FormXObject(
        byte[] Content,
        IReadOnlyDictionary<string, PdfFontDecoder> Fonts,
        Func<string, FormXObject?> Forms,
        double[] Matrix,
        Func<string, int?>? Images = null);

    public static List<PdfTextSegment> Extract(
        byte[] content,
        IReadOnlyDictionary<string, PdfFontDecoder> fonts,
        out bool hadUnmappedGlyphs)
        => Extract(content, fonts, _ => null, out hadUnmappedGlyphs);

    public static List<PdfTextSegment> Extract(
        byte[] content,
        IReadOnlyDictionary<string, PdfFontDecoder> fonts,
        Func<string, FormXObject?> forms,
        out bool hadUnmappedGlyphs,
        Func<string, int?>? images = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(forms);

        var segments = new List<PdfTextSegment>();
        Run(content, fonts, forms, Matrix.Identity, segments, depth: 0, images);
        hadUnmappedGlyphs = segments.Any(segment => segment.Unmapped);
        return segments;
    }

    private static void Run(
        byte[] content,
        IReadOnlyDictionary<string, PdfFontDecoder> fonts,
        Func<string, FormXObject?> forms,
        Matrix ctm,
        List<PdfTextSegment> segments,
        int depth,
        Func<string, int?>? images = null)
    {
        var parser = new PdfObjectParser(new PdfLexer(content));
        var operands = new List<PdfValue>();
        var state = new TextState { Ctm = ctm, CollectRules = images is not null };
        var stack = new Stack<TextState>();
        var marked = new List<MarkedContentFrame>();

        while (true)
        {
            var token = parser.Peek(0);
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Kind != PdfTokenKind.Keyword)
            {
                // Operands without an operator are bounded (a hostile stream could otherwise
                // accumulate millions of values).
                if (operands.Count > 4096)
                {
                    operands.Clear();
                }

                operands.Add(parser.ParseValue());
                continue;
            }

            var operation = parser.Advance().Text!;
            if (operation == "BI")
            {
                // ADR-0034: TeX output (dvips) draws fraction bars and root overbars as thin image masks.
                if (state.CollectRules && ThinImageRule(state.Ctm) is { } inlineRule)
                {
                    segments.Add(inlineRule);
                }

                SkipInlineImage(content, parser);
            }
            else if (operation == "Do" && depth < MaxFormDepth && Name(operands, 0) is { } formName && forms(formName) is { } form)
            {
                var formMatrix = form.Matrix.Length == 6
                    ? new Matrix(form.Matrix[0], form.Matrix[1], form.Matrix[2], form.Matrix[3], form.Matrix[4], form.Matrix[5])
                    : Matrix.Identity;
                Run(form.Content, form.Fonts, form.Forms, Matrix.Multiply(formMatrix, state.Ctm), segments, depth + 1, form.Images);
            }
            else if (operation == "Do" && images is not null && Name(operands, 0) is { } imageName && images(imageName) is { } imageIndex)
            {
                // ADR-0033: an image XObject becomes a positioned pseudo segment (Image >= 0, no text);
                // a thin one is also a rule (ADR-0034), used only by the formula recovery.
                if (ThinImageRule(state.Ctm) is { } imageRule)
                {
                    segments.Add(imageRule);
                }

                var c = state.Ctm;
                segments.Add(new PdfTextSegment(
                    Math.Min(c.E, c.E + c.A), Math.Max(c.F, c.F + c.D), Math.Abs(c.D), string.Empty, false, false, false,
                    Math.Max(c.E, c.E + c.A), imageIndex));
            }
            else
            {
                Execute(operation, operands, ref state, stack, segments, fonts, marked);
            }

            operands.Clear();
        }
    }

    /// <summary>
    /// Skips an inline image: the dictionary up to the <c>ID</c> operator and the raw data up to
    /// <c>EI</c>. The binary data must not reach the lexer (it may contain any delimiter).
    /// </summary>
    private static void SkipInlineImage(byte[] content, PdfObjectParser parser)
    {
        var position = parser.Position;
        var id = FindOperator(content, "ID"u8, position);
        if (id < 0)
        {
            parser.ResetTo(content.Length);
            return;
        }

        // Exactly one white-space byte follows ID before the data.
        var data = id + 3;
        var end = FindOperator(content, "EI"u8, data);
        parser.ResetTo(end < 0 ? content.Length : end + 2);
    }

    /// <summary>Finds a two-letter operator delimited by white space (or the stream end).</summary>
    private static int FindOperator(byte[] content, ReadOnlySpan<byte> name, int start)
    {
        for (var index = Math.Max(start, 0); index + 1 < content.Length; index++)
        {
            if (content[index] == name[0] && content[index + 1] == name[1]
                && (index == 0 || IsWhite(content[index - 1]))
                && (index + 2 >= content.Length || IsWhite(content[index + 2])))
            {
                return index;
            }
        }

        return -1;

        static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    }

    private static void Execute(
        string operation,
        List<PdfValue> operands,
        ref TextState state,
        Stack<TextState> stack,
        List<PdfTextSegment> segments,
        IReadOnlyDictionary<string, PdfFontDecoder> fonts,
        List<MarkedContentFrame> marked)
    {
        switch (operation)
        {
            case "BT":
                state.TextMatrix = Matrix.Identity;
                state.LineMatrix = Matrix.Identity;
                break;
            case "Tf":
                state.FontName = Name(operands, 0);
                state.FontSize = Number(operands, 1) ?? state.FontSize;
                break;
            case "Tc":
                state.CharSpacing = Number(operands, 0) ?? 0;
                break;
            case "Tw":
                state.WordSpacing = Number(operands, 0) ?? 0;
                break;
            case "Tz":
                state.Scale = (Number(operands, 0) ?? 100) / 100.0;
                break;
            case "Td":
                MoveLine(ref state, Number(operands, 0) ?? 0, Number(operands, 1) ?? 0);
                break;
            case "TD":
                state.Leading = -(Number(operands, 1) ?? 0);
                MoveLine(ref state, Number(operands, 0) ?? 0, Number(operands, 1) ?? 0);
                break;
            case "Tm":
                state.TextMatrix = new Matrix(
                    Number(operands, 0) ?? 1, Number(operands, 1) ?? 0,
                    Number(operands, 2) ?? 0, Number(operands, 3) ?? 1,
                    Number(operands, 4) ?? 0, Number(operands, 5) ?? 0);
                state.LineMatrix = state.TextMatrix;
                break;
            case "T*":
                MoveLine(ref state, 0, -state.Leading);
                break;
            case "TL":
                state.Leading = Number(operands, 0) ?? state.Leading;
                break;
            case "Tj":
                Show(ref state, segments, fonts, marked, String(operands, 0));
                break;
            case "'":
                MoveLine(ref state, 0, -state.Leading);
                Show(ref state, segments, fonts, marked, String(operands, operands.Count - 1));
                break;
            case "\"":
                state.WordSpacing = Number(operands, 0) ?? state.WordSpacing;
                state.CharSpacing = Number(operands, 1) ?? state.CharSpacing;
                MoveLine(ref state, 0, -state.Leading);
                Show(ref state, segments, fonts, marked, String(operands, operands.Count - 1));
                break;
            case "TJ":
                if (operands.Count == 1 && operands[0] is PdfArray array)
                {
                    foreach (var item in array.Items)
                    {
                        if (item is PdfString element)
                        {
                            Show(ref state, segments, fonts, marked, element.Bytes);
                        }
                        else if (item is PdfInteger or PdfReal)
                        {
                            // A negative adjustment moves to the right (ISO 32000-1, 9.4.3).
                            var adjustment = item is PdfInteger integer ? integer.Value : ((PdfReal)item).Value;
                            Advance(ref state, -adjustment / 1000.0 * state.FontSize * state.Scale);
                        }
                    }
                }

                break;
            case "BMC":
                marked.Add(new MarkedContentFrame(null));
                break;
            case "BDC":
                marked.Add(new MarkedContentFrame(ActualText(operands)));
                break;
            case "EMC":
                if (marked.Count > 0)
                {
                    var frame = marked[^1];
                    marked.RemoveAt(marked.Count - 1);
                    if (frame.ActualText is { } actual
                        && frame.First is { } first
                        && !marked.Any(outer => outer.ActualText is not null))
                    {
                        segments.Add(first with { Text = actual });
                    }
                }

                break;
            case "q":
                stack.Push(state);
                break;
            case "Q":
                if (stack.Count > 0)
                {
                    // The graphics state (CTM and text state parameters) is restored; the text
                    // and line matrices belong to the text object and continue.
                    var saved = stack.Pop();
                    saved.TextMatrix = state.TextMatrix;
                    saved.LineMatrix = state.LineMatrix;
                    state = saved;
                }

                break;
            case "w":
                state.LineWidth = Number(operands, 0) ?? 1;
                break;
            case "re" when state.CollectRules && state.Rects.Count < 256:
                state.Rects.Add((Number(operands, 0) ?? 0, Number(operands, 1) ?? 0, Number(operands, 2) ?? 0, Number(operands, 3) ?? 0));
                break;
            case "m":
                state.Current = (Number(operands, 0) ?? 0, Number(operands, 1) ?? 0);
                if (state.CollectRules && state.Subpaths.Count < 256)
                {
                    state.Subpaths.Add([state.Current]);
                }

                break;
            case "c" or "v" or "y" when state.CollectRules && state.Subpaths.Count > 0 && state.Subpaths[^1].Count < 256 && operands.Count >= 2:
                // A curve counts by its end point: enough to recognise the outline of an operator.
                state.Subpaths[^1].Add((Number(operands, operands.Count - 2) ?? 0, Number(operands, operands.Count - 1) ?? 0));
                break;
            case "l" when state.CollectRules:
                state.Current = (Number(operands, 0) ?? 0, Number(operands, 1) ?? 0);
                if (state.Subpaths.Count > 0 && state.Subpaths[^1].Count < 256)
                {
                    state.Subpaths[^1].Add(state.Current);
                }
                break;
            case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                if (state.CollectRules)
                {
                    foreach (var (x, y, w, h) in state.Rects)
                    {
                        var c = state.Ctm;
                        var width = Math.Abs(w * c.A);
                        var height = Math.Abs(h * c.D);
                        if (height <= 2 && width >= 2 && Math.Abs(c.B) < 1e-6 && Math.Abs(c.C) < 1e-6)
                        {
                            var left = (Math.Min(x, x + w) * c.A) + c.E;
                            segments.Add(new PdfTextSegment(left, (((y + (h / 2)) * c.D) + c.F), Math.Max(height, 0.1), string.Empty, false, false, false, left + width, Rule: true));
                        }
                        else
                        {
                            AddInk(ref state, segments, [(x, y), (x + w, y + h)]);
                        }
                    }

                    foreach (var subpath in state.Subpaths)
                    {
                        AddInk(ref state, segments, subpath);
                    }
                }

                state.Rects.Clear();
                state.Subpaths.Clear();
                break;
            case "S" or "s":
                if (state.CollectRules)
                {
                    foreach (var subpath in state.Subpaths)
                    {
                        if (OperatorShape(subpath, state.Ctm) is { } shape)
                        {
                            segments.Add(shape);
                        }
                        else if (subpath.Count != 2 || Math.Abs(subpath[0].Y - subpath[1].Y) * Math.Abs(state.Ctm.D) >= 0.2)
                        {
                            AddInk(ref state, segments, subpath);
                        }
                    }

                    // A rule is a subpath of one horizontal segment; the edges of an outline are not rules.
                    foreach (var subpath in state.Subpaths.Where(subpath => subpath.Count == 2))
                    {
                        var c = state.Ctm;
                        var ((x0, y0), (x1, y1)) = (subpath[0], subpath[1]);
                        var ya = (y0 * c.D) + c.F;
                        var yb = (y1 * c.D) + c.F;
                        var xa = (x0 * c.A) + c.E;
                        var xb = (x1 * c.A) + c.E;
                        if (Math.Abs(ya - yb) < 0.2 && Math.Abs(xb - xa) >= 2 && Math.Abs(c.B) < 1e-6 && Math.Abs(c.C) < 1e-6)
                        {
                            segments.Add(new PdfTextSegment(Math.Min(xa, xb), ya, Math.Max(0.1, state.LineWidth * Math.Abs(c.D)), string.Empty, false, false, false, Math.Max(xa, xb), Rule: true));
                        }
                    }
                }

                state.Rects.Clear();
                state.Subpaths.Clear();
                break;
            case "n":
                state.Rects.Clear();
                state.Subpaths.Clear();
                break;
            case "cm":
                state.Ctm = Matrix.Multiply(
                    new Matrix(
                        Number(operands, 0) ?? 1, Number(operands, 1) ?? 0,
                        Number(operands, 2) ?? 0, Number(operands, 3) ?? 1,
                        Number(operands, 4) ?? 0, Number(operands, 5) ?? 0),
                    state.Ctm);
                break;
        }
    }

    /// <summary>
    /// ADR-0034: a stroked outline that has the shape of a big operator or a root sign (the library's own PDF writer and
    /// other producers without a math font draw them as lines) becomes a Path pseudo segment whose text is the operator:
    /// bounding box in X..EndX, centre in Y, height in Size. "|" is a vertical stroke (a leg of a product sign), "○" a
    /// closed loop (the ring of a contour integral).
    /// </summary>
    private static PdfTextSegment? OperatorShape(List<(double X, double Y)> raw, Matrix c)
    {
        if (raw.Count < 2)
        {
            return null;
        }

        var points = raw.Select(p => ((p.X * c.A) + (p.Y * c.C) + c.E, (p.X * c.B) + (p.Y * c.D) + c.F)).ToList();
        var minX = points.Min(p => p.Item1);
        var maxX = points.Max(p => p.Item1);
        var minY = points.Min(p => p.Item2);
        var maxY = points.Max(p => p.Item2);
        var w = maxX - minX;
        var h = maxY - minY;
        if (h < 3 || h > 80 || w > 80)
        {
            return null;
        }

        string? op = null;
        var u = points.Select(p => w < 1e-6 ? 0.5 : (p.Item1 - minX) / w).ToList();
        var v = points.Select(p => (p.Item2 - minY) / h).ToList();
        bool Near(double pu, double pv) => Enumerable.Range(0, points.Count).Any(i => Math.Abs(u[i] - pu) <= 0.15 && Math.Abs(v[i] - pv) <= 0.15);
        var n = points.Count;
        if (n == 2 && w < 0.3)
        {
            op = "|";
        }
        else if (n >= 8 && Math.Abs(u[0] - u[^1]) < 0.1 && Math.Abs(v[0] - v[^1]) < 0.1)
        {
            op = "○";
        }
        else if (n is >= 3 and <= 5 && u[^1] >= 0.9 && v[^1] >= 0.9 && u[0] <= 0.1 && v[0] is > 0.15 and < 0.75
            && Enumerable.Range(1, n - 2).Any(i => v[i] <= 0.1 && u[i] is > 0.2 and < 0.8))
        {
            op = "√";
        }
        else if (n >= 6 && Near(0, 1) && Near(1, 1) && Near(0, 0) && Near(1, 0)
            && Enumerable.Range(0, n).Any(i => v[i] is >= 0.3 and <= 0.7 && u[i] is >= 0.3 and <= 0.8))
        {
            op = "∑";
        }
        else if (n >= 5 && v[0] >= 0.9 && v[^1] >= 0.9 && u[0] <= 0.1 && u[^1] >= 0.9 && Enumerable.Range(0, n).Any(i => v[i] <= 0.05 && u[i] is > 0.3 and < 0.7))
        {
            op = "⋃";
        }
        else if (n >= 5 && v[0] <= 0.1 && v[^1] <= 0.1 && u[0] <= 0.1 && u[^1] >= 0.9 && Enumerable.Range(0, n).Any(i => v[i] >= 0.95 && u[i] is > 0.3 and < 0.7))
        {
            op = "⋂";
        }
        else if (n >= 5 && w <= 0.6 * h && v[0] >= 0.7 && u[0] >= 0.6 && v[^1] <= 0.3 && u[^1] <= 0.4)
        {
            op = "∫";
        }

        return op is null ? null : new PdfTextSegment(minX, (minY + maxY) / 2, h, op, false, false, false, maxX, Path: true);
    }

    /// <summary>
    /// ADR-0034: the bounding box of any other painted path ("ink": axes, ticks and curves of a chart) as a Path pseudo
    /// segment with empty text. A formula candidate surrounded by ink is a label inside a drawing.
    /// </summary>
    private static void AddInk(ref TextState state, List<PdfTextSegment> segments, List<(double X, double Y)> raw)
    {
        if (raw.Count == 0 || state.InkCount >= 4000)
        {
            return;
        }

        var c = state.Ctm;
        var points = raw.Select(p => ((p.X * c.A) + (p.Y * c.C) + c.E, (p.X * c.B) + (p.Y * c.D) + c.F)).ToList();
        var minX = points.Min(p => p.Item1);
        var maxX = points.Max(p => p.Item1);
        var minY = points.Min(p => p.Item2);
        var maxY = points.Max(p => p.Item2);
        state.InkCount++;
        segments.Add(new PdfTextSegment(minX, (minY + maxY) / 2, maxY - minY, string.Empty, false, false, false, maxX, Path: true));
    }

    /// <summary>An image drawn into a unit square that the CTM makes at most 2 pt high and at least 2 pt wide: a rule.</summary>
    private static PdfTextSegment? ThinImageRule(Matrix c)
    {
        if (Math.Abs(c.B) > 1e-6 || Math.Abs(c.C) > 1e-6 || Math.Abs(c.D) > 2 || Math.Abs(c.A) < 2)
        {
            return null;
        }

        var left = Math.Min(c.E, c.E + c.A);
        return new PdfTextSegment(left, c.F + (c.D / 2), Math.Max(Math.Abs(c.D), 0.1), string.Empty, false, false, false, left + Math.Abs(c.A), Rule: true);
    }

    private static void MoveLine(ref TextState state, double x, double y)
    {
        state.LineMatrix = Matrix.Multiply(Matrix.Translation(x, y), state.LineMatrix);
        state.TextMatrix = state.LineMatrix;
    }

    private static void Advance(ref TextState state, double tx)
        => state.TextMatrix = Matrix.Multiply(Matrix.Translation(tx, 0), state.TextMatrix);

    private static void Show(
        ref TextState state,
        List<PdfTextSegment> segments,
        IReadOnlyDictionary<string, PdfFontDecoder> fonts,
        List<MarkedContentFrame> marked,
        byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || state.FontName is null)
        {
            return;
        }

        var font = fonts.TryGetValue(state.FontName, out var decoder) ? decoder : null;
        var codeLength = font?.CodeLength ?? 1;

        // Trm = Tm × CTM (ISO 32000-1, 9.4.4); font size and rise do not change the origin.
        var start = Matrix.Multiply(state.TextMatrix, state.Ctm);
        var text = new StringBuilder(bytes.Length);
        var unmapped = false;
        for (var index = 0; index + codeLength <= bytes.Length; index += codeLength)
        {
            var code = codeLength == 2 ? (bytes[index] << 8) | bytes[index + 1] : bytes[index];
            var (glyphText, width, missing) = font is not null
                ? font.DecodeCode(code)
                : (PdfWinAnsi.Decode([(byte)code]), 500d, false);
            text.Append(glyphText);
            unmapped |= missing;

            // Word spacing applies to the single-byte code 32 only (ISO 32000-1, 9.3.3).
            var spacing = state.CharSpacing + (codeLength == 1 && code == 32 ? state.WordSpacing : 0);
            Advance(ref state, ((width / 1000.0 * state.FontSize) + spacing) * state.Scale);
        }

        if (text.Length == 0)
        {
            return;
        }

        var end = Matrix.Multiply(state.TextMatrix, state.Ctm);
        var size = state.FontSize * Math.Max(Math.Abs(start.D), Math.Abs(start.B)) is > 0 and var measured
            ? measured
            : state.FontSize;
        var actualText = marked.LastOrDefault(frame => frame.ActualText is not null);
        if (actualText is not null)
        {
            // The marked content replaces the glyphs; keep the position of the first run and emit
            // the replacement text when the EMC closes the frame.
            actualText.First ??= new PdfTextSegment(
                start.E, start.F, size, string.Empty, font?.Bold ?? false, font?.Italic ?? false, false, end.E);
            return;
        }

        segments.Add(new PdfTextSegment(
            start.E,
            start.F,
            size,
            text.ToString(),
            font?.Bold ?? false,
            font?.Italic ?? false,
            unmapped,
            end.E,
            Rotated: Math.Abs(start.B) + Math.Abs(start.C) > Math.Abs(start.A) + Math.Abs(start.D)));
    }

    /// <summary>The <c>/ActualText</c> of a <c>BDC</c> property dictionary, if present.</summary>
    private static string? ActualText(List<PdfValue> operands)
        => operands.Count > 1
            && operands[1] is PdfDictionary properties
            && properties.Get("ActualText") is PdfString text
                ? PdfTextString.Decode(text.Bytes)
                : null;

    private static string? Name(List<PdfValue> operands, int index)
        => index < operands.Count && operands[index] is PdfName name ? name.Name : null;

    private static byte[]? String(List<PdfValue> operands, int index)
        => index >= 0 && index < operands.Count && operands[index] is PdfString text ? text.Bytes : null;

    private static double? Number(List<PdfValue> operands, int index)
        => index < operands.Count
            ? operands[index] switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => real.Value,
                _ => null,
            }
            : null;

    /// <summary>A marked-content frame; a <c>BDC</c> may carry replacement text.</summary>
    private sealed class MarkedContentFrame(string? actualText)
    {
        public string? ActualText { get; } = actualText;

        /// <summary>Position of the first replaced run; null when the frame carries no text.</summary>
        public PdfTextSegment? First { get; set; }
    }

    private struct TextState
    {
        public TextState()
        {
        }

        public Matrix Ctm { get; set; } = Matrix.Identity;

        public Matrix TextMatrix { get; set; } = Matrix.Identity;

        public Matrix LineMatrix { get; set; } = Matrix.Identity;

        public string? FontName { get; set; }

        public double FontSize { get; set; } = 12;

        public double Leading { get; set; }

        public double CharSpacing { get; set; }

        public double WordSpacing { get; set; }

        public double Scale { get; set; } = 1;

        // ADR-0034: thin filled rectangles and horizontal strokes (fraction bars) are recorded as Rule pseudo segments.
        public bool CollectRules { get; set; }

        public double LineWidth { get; set; } = 1;

        public List<(double X, double Y, double W, double H)> Rects { get; } = [];

        public (double X, double Y) Current { get; set; }

        public List<List<(double X, double Y)>> Subpaths { get; } = [];

        public int InkCount { get; set; }
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
