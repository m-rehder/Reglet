using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Reads HTML into the content model (ADR-0032): a tolerant subset reader. It maps text, headings, lists, tables, images
/// from <c>data:</c> URIs and links; it never fetches anything and drops scripts and styles.
/// </summary>
public sealed class HtmlDocumentReader : IDocumentReader
{
    /// <inheritdoc />
    public ReadResult Read(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ReaderGuard.ReadAllBounded(input, "HTML");
        var warnings = new List<ReadWarning>();
        var html = HtmlEncoding.Decode(bytes, message => HtmlConverter.Warn(warnings, message));
        var root = HtmlParser.Parse(html);
        var converter = new HtmlConverter(warnings, cancellationToken);
        return converter.Convert(root);
    }
}

/// <summary>Finds the character set of an HTML byte stream: a byte order mark, then a meta tag in the first 1,024 bytes.</summary>
internal static class HtmlEncoding
{
    internal static string Decode(byte[] bytes, Action<string> warn)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 1024));
        var index = head.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = index + "charset".Length;
            while (start < head.Length && (head[start] is '=' or ' ' or '"' or '\''))
            {
                start++;
            }

            var end = start;
            while (end < head.Length && (char.IsLetterOrDigit(head[end]) || head[end] is '-' or '_'))
            {
                end++;
            }

            switch (head[start..end].ToLowerInvariant())
            {
                case "utf-8" or "utf8" or "":
                    break;
                case "iso-8859-1" or "latin1" or "windows-1252" or "cp1252" or "us-ascii" or "ascii":
                    return DecodeWindows1252(bytes);
                case "utf-16" or "utf-16le":
                    return Encoding.Unicode.GetString(bytes);
                case "utf-16be":
                    return Encoding.BigEndianUnicode.GetString(bytes);
                default:
                    warn($"HTML: the character set '{head[start..end]}' is not known; the file is read as UTF-8.");
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static readonly char[] Cp1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    private static string DecodeWindows1252(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = bytes[i] is >= 0x80 and <= 0x9F ? Cp1252High[bytes[i] - 0x80] : (char)bytes[i];
        }

        return new string(chars);
    }
}

/// <summary>The character formatting while walking the tree.</summary>
internal sealed record HtmlFormat
{
    internal bool Bold { get; init; }

    internal bool Italic { get; init; }

    internal bool Underline { get; init; }

    internal bool Strike { get; init; }

    internal TextBaseline? Baseline { get; init; }

    internal Color? Color { get; init; }

    internal Color? Background { get; init; }

    internal string? Family { get; init; }

    internal double? Size { get; init; }

    internal bool Preformatted { get; init; }
}

/// <summary>Walks the parsed tree and builds the blocks.</summary>
internal sealed class HtmlConverter(List<ReadWarning> warnings, CancellationToken cancellationToken)
{
    private const string BulletList = "html-bullet";
    private const string NumberList = "html-number";
    private const double BaseSize = 11;
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "table", "pre", "blockquote", "hr", "center", "section",
        "article", "aside", "header", "footer", "nav", "main", "figure", "figcaption", "address", "form", "fieldset", "dl", "dt", "dd",
        "details", "summary", "li", "body", "html",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "head", "noscript", "template", "meta", "link", "title",
    };

    private static readonly HashSet<string> Embedded = new(StringComparer.Ordinal)
    {
        "iframe", "object", "embed", "video", "audio", "canvas", "svg", "math", "map",
    };

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "input", "select", "textarea", "button", "datalist",
    };

    private int _blocks;
    private bool _usesBullet;
    private bool _usesNumber;
    private string? _title;

    internal static void Warn(List<ReadWarning> list, string message)
    {
        if (!list.Any(warning => warning.Message == message))
        {
            list.Add(new ReadWarning(message));
        }
    }

    private void Warn(string message) => Warn(warnings, message);

    internal ReadResult Convert(HtmlNode root)
    {
        _title = FindTitle(root);
        var output = new List<Block>();
        var builder = new Builder(null);
        Flow(root, new HtmlFormat(), builder, output, 0, 0);
        Flush(builder, output);
        if (output.Count == 0)
        {
            output.Add(new Paragraph(new TextRun(string.Empty)));
        }

        var numbering = new List<NumberingDefinition>();
        if (_usesBullet)
        {
            numbering.Add(new NumberingDefinition(BulletList, Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Bullet })));
        }

        if (_usesNumber)
        {
            numbering.Add(new NumberingDefinition(NumberList, Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Number })));
        }

        var document = new PortableDocument(new DocumentMetadata { Title = _title }, new Section(output)) { Numbering = numbering };
        return new ReadResult(document, warnings);
    }

    private static string? FindTitle(HtmlNode root)
    {
        var stack = new Stack<HtmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Name == "title")
            {
                var title = Collapse(node.TextContent()).Trim();
                return title.Length == 0 ? null : title;
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return null;
    }

    /// <summary>The paragraph that is being built: its inlines, the open run and the images that follow it.</summary>
    private sealed class Builder(ParagraphStyle? style)
    {
        internal ParagraphStyle? Style { get; } = style;

        internal List<Inline> Inlines { get; } = [];

        internal List<Block> Images { get; } = [];

        internal HtmlFormat? RunFormat { get; set; }

        internal StringBuilder Run { get; } = new();

        internal bool PendingSpace { get; set; }

        internal bool HasContent => Inlines.Count > 0 || Run.Length > 0;
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        if (space)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static TextRun MakeRun(string text, HtmlFormat format) => new(text)
    {
        Bold = format.Bold ? true : null,
        Italic = format.Italic ? true : null,
        Underline = format.Underline ? true : null,
        Strikethrough = format.Strike ? TextStrikethrough.Single : null,
        Baseline = format.Baseline,
        Color = format.Color,
        Background = format.Background,
        FontFamily = format.Family,
        FontSize = format.Size is { } size ? Length.FromPoints(size) : null,
    };

    private static void CloseRun(Builder builder)
    {
        if (builder.Run.Length > 0 && builder.RunFormat is { } format)
        {
            builder.Inlines.Add(MakeRun(builder.Run.ToString(), format));
        }

        builder.Run.Clear();
        builder.RunFormat = null;
    }

    private static void Append(Builder builder, string text, HtmlFormat format)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (builder.RunFormat is not null && builder.RunFormat != format)
        {
            CloseRun(builder);
        }

        builder.RunFormat = format;
        builder.Run.Append(text);
    }

    private static void AppendText(Builder builder, string raw, HtmlFormat format)
    {
        if (format.Preformatted)
        {
            Append(builder, raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\f", string.Empty, StringComparison.Ordinal), format);
            return;
        }

        var text = new StringBuilder(raw.Length);
        var last = builder.PendingSpace || !builder.HasContent;
        foreach (var c in raw)
        {
            var isSpace = c is ' ' or '\t' or '\n' or '\r' or '\f';
            if (isSpace && last)
            {
                continue;
            }

            text.Append(isSpace ? ' ' : c);
            last = isSpace;
        }

        builder.PendingSpace = last;
        Append(builder, text.ToString(), format);
    }

    private void Flush(Builder builder, List<Block> output)
    {
        CloseRun(builder);
        if (builder.Inlines.Count > 0 && builder.Inlines[^1] is TextRun last && last.Text.EndsWith(' '))
        {
            var trimmed = last.Text.TrimEnd(' ');
            builder.Inlines.RemoveAt(builder.Inlines.Count - 1);
            if (trimmed.Length > 0)
            {
                builder.Inlines.Add(CopyWithText(last, trimmed));
            }
        }

        if (builder.Inlines.Count > 0)
        {
            Count();
            output.Add(new Paragraph(builder.Inlines.ToArray()) { Style = builder.Style ?? ParagraphStyle.Default });
        }

        output.AddRange(builder.Images);
        builder.Inlines.Clear();
        builder.Images.Clear();
        builder.PendingSpace = false;
    }

    private static TextRun CopyWithText(TextRun run, string text) => new(text)
    {
        Bold = run.Bold, Italic = run.Italic, Underline = run.Underline, Strikethrough = run.Strikethrough, Baseline = run.Baseline,
        Color = run.Color, Background = run.Background, FontFamily = run.FontFamily, FontSize = run.FontSize,
    };

    private void Count()
    {
        if (++_blocks > ResourceLimits.Default.MaxBlocks)
        {
            throw new DocumentValidationException("The HTML document has too many blocks.", "input");
        }
    }

    private void Flow(HtmlNode node, HtmlFormat format, Builder builder, List<Block> output, int depth, int listDepth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReaderGuard.EnsureStack();
        foreach (var child in node.Children)
        {
            if (child.IsText)
            {
                AppendText(builder, child.Text, format);
                continue;
            }

            var name = child.Name!;
            if (Skipped.Contains(name))
            {
                continue;
            }

            if (Embedded.Contains(name))
            {
                Warn($"HTML: <{name}> is not mapped when reading.");
                continue;
            }

            if (FormControls.Contains(name))
            {
                Warn("HTML: form controls are not mapped when reading.");
                continue;
            }

            if (name == "br")
            {
                Append(builder, "\n", format);
                builder.PendingSpace = true;
                continue;
            }

            if (name == "img")
            {
                ReadImage(child, format, builder);
                continue;
            }

            if (BlockTags.Contains(name) || name == "table" || name == "caption")
            {
                Flush(builder, output);
                Block(child, format, output, depth, listDepth, builder.Style);
                continue;
            }

            var inner = HtmlStyles.Apply(format, child, BaseSize);
            if (name == "a" && child.Attribute("href") is { Length: > 0 } href)
            {
                CloseRun(builder);
                var start = builder.Inlines.Count;
                Flow(child, inner, builder, output, depth + 1, listDepth);
                CloseRun(builder);
                var linked = builder.Inlines.GetRange(start, builder.Inlines.Count - start);
                if (linked.Count > 0 && ReaderGuard.IsAllowedLinkTarget(href))
                {
                    builder.Inlines.RemoveRange(start, linked.Count);
                    builder.Inlines.Add(new Hyperlink(href.Trim(), linked));
                }
                else if (linked.Count > 0)
                {
                    Warn("HTML: a link with a disallowed target is read as text.");
                }

                continue;
            }

            Flow(child, inner, builder, output, depth + 1, listDepth);
        }
    }

    private void Block(HtmlNode element, HtmlFormat format, List<Block> output, int depth, int listDepth, ParagraphStyle? outerStyle)
    {
        var name = element.Name!;
        var inner = HtmlStyles.Apply(format, element, BaseSize);
        var style = HtmlStyles.ParagraphOf(element, outerStyle);
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                var builder = new Builder(null);
                Flow(element, inner, builder, output, depth + 1, listDepth);
                CloseRun(builder);
                if (builder.Inlines.Count > 0 && builder.Inlines[^1] is TextRun last && last.Text.EndsWith(' '))
                {
                    var trimmed = last.Text.TrimEnd(' ');
                    builder.Inlines.RemoveAt(builder.Inlines.Count - 1);
                    if (trimmed.Length > 0)
                    {
                        builder.Inlines.Add(CopyWithText(last, trimmed));
                    }
                }

                if (builder.Inlines.Count > 0)
                {
                    Count();
                    output.Add(new Heading((HeadingLevel)(name[1] - '0'), builder.Inlines.ToArray()));
                }

                output.AddRange(builder.Images);
                break;
            }

            case "ul" or "ol":
                ReadList(element, inner, output, depth, listDepth + 1);
                break;
            case "table":
                ReadTable(element, inner, output, depth);
                break;
            case "pre":
            {
                var builder = new Builder(style);
                var text = element.TextContent();
                if (text.StartsWith('\n'))
                {
                    text = text[1..];
                }

                var preformatted = inner with { Preformatted = true, Family = inner.Family ?? "Courier New" };
                Append(builder, text.TrimEnd('\n'), preformatted);
                Flush(builder, output);
                break;
            }

            case "hr":
                Warn("HTML: a horizontal rule is not mapped when reading.");
                break;
            case "blockquote":
            {
                var quote = (style ?? ParagraphStyle.Default) with { LeftIndent = Length.FromPoints((style?.LeftIndent?.Points ?? 0) + 36) };
                Container(element, inner, quote, output, depth, listDepth);
                break;
            }

            case "center":
                Container(element, inner, (style ?? ParagraphStyle.Default) with { Alignment = TextAlignment.Center }, output, depth, listDepth);
                break;
            case "dd":
                Container(element, inner, (style ?? ParagraphStyle.Default) with { LeftIndent = Length.FromPoints(36) }, output, depth, listDepth);
                break;
            default:
                Container(element, inner, style, output, depth, listDepth);
                break;
        }
    }

    private void Container(HtmlNode element, HtmlFormat format, ParagraphStyle? style, List<Block> output, int depth, int listDepth)
    {
        var builder = new Builder(style);
        Flow(element, format, builder, output, depth + 1, listDepth);
        Flush(builder, output);
    }

    private void ReadList(HtmlNode list, HtmlFormat format, List<Block> output, int depth, int level)
    {
        var ordered = list.Name == "ol";
        _usesBullet |= !ordered;
        _usesNumber |= ordered;
        var items = new List<ListItem>();
        foreach (var li in list.Children.Where(child => child.Name == "li"))
        {
            var inner = HtmlStyles.Apply(format, li, BaseSize);
            var blocks = new List<Block>();
            var builder = new Builder(HtmlStyles.ParagraphOf(li, null));
            Flow(li, inner, builder, blocks, depth + 1, level);
            Flush(builder, blocks);
            if (blocks.Count == 0)
            {
                blocks.Add(new Paragraph(new TextRun(string.Empty)));
            }

            items.Add(new ListItem(blocks));
        }

        if (items.Count == 0)
        {
            return;
        }

        Count();
        output.Add(new ListBlock(ordered ? NumberList : BulletList, items) { NestingLevel = Math.Min(level, ListBlock.MaxNestingLevel) });
    }

    private void ReadTable(HtmlNode table, HtmlFormat format, List<Block> output, int depth)
    {
        var rows = new List<(HtmlNode Row, bool InHead)>();
        foreach (var child in table.Children)
        {
            if (child.Name == "tr")
            {
                rows.Add((child, false));
            }
            else if (child.Name is "thead" or "tbody" or "tfoot")
            {
                rows.AddRange(child.Children.Where(row => row.Name == "tr").Select(row => (row, child.Name == "thead")));
            }
            else if (child.Name == "caption")
            {
                var caption = new List<Block>();
                Container(child, format, null, caption, depth, 0);
                output.AddRange(caption);
            }
        }

        var cellCount = 0;
        var tableRows = new List<TableRow>();
        foreach (var (row, inHead) in rows)
        {
            var cells = new List<TableCell>();
            var allHeader = true;
            foreach (var cell in row.Children.Where(child => child.Name is "td" or "th"))
            {
                allHeader &= cell.Name == "th";
                if (++cellCount > ResourceLimits.Default.MaxTableCells)
                {
                    throw new DocumentValidationException("The HTML table has too many cells.", "input");
                }

                var inner = HtmlStyles.Apply(format, cell, BaseSize);
                var blocks = new List<Block>();
                var builder = new Builder(HtmlStyles.ParagraphOf(cell, null));
                Flow(cell, cell.Name == "th" ? inner with { Bold = true } : inner, builder, blocks, depth + 1, 0);
                Flush(builder, blocks);
                if (blocks.Count == 0)
                {
                    blocks.Add(new Paragraph(new TextRun(string.Empty)));
                }

                cells.Add(new TableCell(blocks)
                {
                    ColumnSpan = Span(cell.Attribute("colspan")),
                    RowSpan = Span(cell.Attribute("rowspan")),
                    Background = HtmlStyles.BackgroundOf(cell),
                    Alignment = HtmlStyles.AlignmentOf(cell) ?? TextAlignment.Left,
                });
            }

            if (cells.Count > 0)
            {
                tableRows.Add(new TableRow(cells) { IsHeader = inHead || allHeader });
            }
        }

        if (tableRows.Count == 0)
        {
            return;
        }

        try
        {
            Count();
            var border = int.TryParse(table.Attribute("border"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width > 0
                ? new ParagraphBorder()
                : null;
            output.Add(new TableBlock(tableRows) { Border = border });
        }
        catch (ArgumentException)
        {
            Warn("HTML: a table with inconsistent rows is read as text.");
            foreach (var cell in tableRows.SelectMany(row => row.Cells))
            {
                output.AddRange(cell.Blocks);
            }
        }
    }

    private static int Span(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var span) ? Math.Clamp(span, 1, TableCell.MaxSpan) : 1;

    private void ReadImage(HtmlNode element, HtmlFormat format, Builder builder)
    {
        var alt = element.Attribute("alt") ?? string.Empty;
        var image = DataUri(element.Attribute("src"));
        if (image is null)
        {
            Warn("HTML: an image that is not a data: URI is not read (nothing is fetched).");
            if (alt.Length > 0)
            {
                AppendText(builder, alt, format);
            }

            return;
        }

        try
        {
            Count();
            var block = new ImageBlock(image.Value.Data, image.Value.Type)
            {
                AltText = alt.Length > 0 ? alt : null,
                Width = PixelLength(element.Attribute("width")),
                Height = PixelLength(element.Attribute("height")),
            };
            CloseRun(builder);
            builder.Images.Add(block);
        }
        catch (ArgumentException)
        {
            Warn("HTML: an image that cannot be decoded is not read.");
            if (alt.Length > 0)
            {
                AppendText(builder, alt, format);
            }
        }
    }

    private static Length? PixelLength(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels is > 0 and < 100_000
            ? Length.FromPoints(pixels * 0.75)
            : null;
    }

    private (byte[] Data, ImageMediaType Type)? DataUri(string? source)
    {
        if (source is null || !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = source.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = source[5..comma].ToLowerInvariant();
        var type = header.Split(';')[0] switch
        {
            "image/png" => ImageMediaType.Png,
            "image/jpeg" or "image/jpg" => ImageMediaType.Jpeg,
            "image/svg+xml" => ImageMediaType.Svg,
            _ => (ImageMediaType?)null,
        };
        if (type is null)
        {
            return null;
        }

        var payload = source[(comma + 1)..];
        if (payload.Length > ResourceLimits.Default.MaxImageBytes * 4 / 3 + 16)
        {
            Warn("HTML: an image exceeds the size limit and is not read.");
            return null;
        }

        try
        {
            return header.Contains(";base64", StringComparison.Ordinal)
                ? (System.Convert.FromBase64String(payload.Replace(" ", string.Empty, StringComparison.Ordinal)), type.Value)
                : (Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), type.Value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
