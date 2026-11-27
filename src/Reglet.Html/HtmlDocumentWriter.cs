using System.Globalization;
using System.Net;
using System.Text;

namespace Reglet;

/// <summary>Writes a <see cref="PortableDocument"/> as one self-contained UTF-8 HTML5 file (ADR-0032).</summary>
public sealed class HtmlDocumentWriter : IDocumentWriter
{
    /// <summary>Writes the document as HTML to the stream.</summary>
    public Task<WriteResult> WriteAsync(
        PortableDocument document,
        Stream output,
        HtmlWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        var effective = options ?? HtmlWriteOptions.Default;

        // The same resource limits as the other writers (blocks, cells, text, nesting depth).
        DocumentLimits.Enforce(document, effective.Limits);
        var warnings = new List<WriteWarning>();
        var handler = new UnsupportedFeatureHandler(effective.UnsupportedContent, warnings);
        return WriterOutput.Write(
            output,
            stream =>
            {
                TableScan.ReportEmptyTables(document, handler, "HTML");
                var html = new HtmlBuilder(document, effective, handler, cancellationToken).Build();
                stream.Write(new UTF8Encoding(false).GetBytes(html));
                return null;
            },
            warnings,
            effective,
            cancellationToken);
    }

    Task<WriteResult> IDocumentWriter.WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken)
        => WriteAsync(document, output, null, cancellationToken);
}

internal sealed class HtmlBuilder(PortableDocument document, HtmlWriteOptions options, UnsupportedFeatureHandler handler, CancellationToken cancellationToken)
{
    private readonly StringBuilder _out = new();
    private readonly List<IReadOnlyList<Block>> _notes = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListKind> _kinds = document.Numbering.ToDictionary(
        definition => definition.Name,
        definition => definition.Levels.Count > 0 ? definition.Levels[0].Kind : ListKind.Bullet,
        StringComparer.Ordinal);

    internal string Build()
    {
        _out.Append("<!DOCTYPE html>\n<html");
        if (document.Metadata.Language is { Length: > 0 } language)
        {
            _out.Append(" lang=\"").Append(Escape(language)).Append('"');
        }

        _out.Append(">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(Escape(document.Metadata.Title ?? string.Empty)).Append("</title>\n");
        _out.Append("<style>body{font-family:serif;line-height:1.4;max-width:50em;margin:2em auto}table{border-collapse:collapse}td,th{padding:.2em .5em;vertical-align:top}table.b td,table.b th{border:1px solid #000}</style>\n</head>\n<body>\n");
        foreach (var section in document.Sections)
        {
            Blocks(section.Blocks);
        }

        if (_notes.Count > 0)
        {
            _out.Append("<hr>\n<ol class=\"notes\">\n");
            for (var i = 0; i < _notes.Count; i++)
            {
                _out.Append("<li id=\"note").Append(i + 1).Append("\">");
                Blocks(_notes[i]);
                _out.Append("</li>\n");
            }

            _out.Append("</ol>\n");
        }

        _out.Append("</body>\n</html>\n");
        return _out.ToString();
    }

    private void Report(string what)
    {
        if (_reported.Add(what))
        {
            handler.Report($"HTML: {what} is not written.");
        }
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string Css(Color color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";

    private void Blocks(IEnumerable<Block> blocks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReaderGuard.EnsureStack();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Element("p", Style(paragraph.Style), paragraph.Inlines);
                    break;
                case Heading heading:
                    Element("h" + (int)heading.Level, Style(heading.Style), heading.Inlines);
                    break;
                case ListBlock list:
                    WriteList(list);
                    break;
                case TableBlock table:
                    WriteTable(table);
                    break;
                case ImageBlock image:
                    WriteImage(image);
                    break;
                case PageBreak:
                    _out.Append("<div style=\"page-break-after:always\"></div>\n");
                    break;
                case ColumnBreak:
                    break;
                default:
                    Report(block.GetType().Name);
                    break;
            }
        }
    }

    private static string Style(ParagraphStyle? style)
    {
        if (style is null)
        {
            return string.Empty;
        }

        var css = new StringBuilder();
        if (style.Alignment is { } alignment && alignment != TextAlignment.Left)
        {
            css.Append("text-align:").Append(alignment.ToString().ToLowerInvariant()).Append(';');
        }

        if (style.LeftIndent is { } indent && indent.Points > 0)
        {
            css.Append("margin-left:").Append(indent.Points.ToString("0.##", CultureInfo.InvariantCulture)).Append("pt;");
        }

        return css.Length == 0 ? string.Empty : $" style=\"{Escape(css.ToString())}\"";
    }

    private void Element(string tag, string attributes, IEnumerable<Inline> inlines)
    {
        _out.Append('<').Append(tag).Append(attributes).Append('>');
        Inlines(inlines);
        _out.Append("</").Append(tag).Append(">\n");
    }

    private void Inlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    WriteRun(run);
                    break;
                case Hyperlink link:
                    var allowed = ReaderGuard.IsAllowedLinkTarget(link.Target);
                    if (allowed)
                    {
                        _out.Append("<a href=\"").Append(Escape(link.Target)).Append("\">");
                    }
                    else
                    {
                        Report("a link with a disallowed target (kept as text)");
                    }

                    Inlines(link.Inlines);
                    if (allowed)
                    {
                        _out.Append("</a>");
                    }

                    break;
                case Bookmark bookmark:
                    _out.Append("<a id=\"").Append(Escape(bookmark.Name)).Append("\"></a>");
                    break;
                case BookmarkEnd:
                    break;
                case InlineImage image:
                    WriteImage(image.Image, inLine: true);
                    break;
                case Footnote note:
                    _notes.Add(note.Blocks);
                    _out.Append("<sup><a href=\"#note").Append(_notes.Count).Append("\">").Append(_notes.Count).Append("</a></sup>");
                    break;
                default:
                    Report(inline.GetType().Name);
                    break;
            }
        }
    }

    private void WriteRun(TextRun run)
    {
        var css = new StringBuilder();
        if (run.Color is { } color)
        {
            css.Append("color:").Append(Css(color)).Append(';');
        }

        if (run.Background is { } background || run.Highlight is not null)
        {
            css.Append("background-color:").Append(Css(run.Background ?? run.Highlight!.Value)).Append(';');
        }

        if (run.FontFamily is { Length: > 0 } family)
        {
            // A font name comes from foreign files: only name characters, so neither a quote nor a character
            // reference (&#39) can end the CSS string.
            static string Name(string name) => string.Concat(name.Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.'));
            css.Append("font-family:'").Append(Name(family)).Append('\'');
            // The East Asian font follows as the browser's fallback for the characters the first one lacks.
            if (run.EastAsianFontFamily is { Length: > 0 } eastAsian)
            {
                css.Append(",'").Append(Name(eastAsian)).Append('\'');
            }

            css.Append(';');
        }

        if (run.FontSize is { } size)
        {
            css.Append("font-size:").Append(size.Points.ToString("0.##", CultureInfo.InvariantCulture)).Append("pt;");
        }

        if (run.SmallCaps == true)
        {
            css.Append("font-variant:small-caps;");
        }

        if (run.AllCaps == true)
        {
            css.Append("text-transform:uppercase;");
        }

        var tags = new List<string>();
        if (run.Bold == true) { tags.Add("b"); }
        if (run.Italic == true) { tags.Add("i"); }
        if (run.Underline == true) { tags.Add("u"); }
        if (run.Strikethrough is { } strike && strike != TextStrikethrough.None) { tags.Add("s"); }
        if (run.Baseline == TextBaseline.Superscript) { tags.Add("sup"); }
        if (run.Baseline == TextBaseline.Subscript) { tags.Add("sub"); }

        var styled = css.Length > 0;
        if (styled)
        {
            _out.Append("<span style=\"").Append(Escape(css.ToString())).Append("\">");
        }

        foreach (var tag in tags)
        {
            _out.Append('<').Append(tag).Append('>');
        }

        _out.Append(Escape(run.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal).Replace("\t", "&emsp;", StringComparison.Ordinal));
        for (var i = tags.Count - 1; i >= 0; i--)
        {
            _out.Append("</").Append(tags[i]).Append('>');
        }

        if (styled)
        {
            _out.Append("</span>");
        }
    }

    private void WriteList(ListBlock list)
    {
        var ordered = _kinds.GetValueOrDefault(list.NumberingName, ListKind.Bullet) == ListKind.Number;
        var tag = ordered ? "ol" : "ul";
        _out.Append('<').Append(tag).Append(">\n");
        foreach (var item in list.Items)
        {
            _out.Append("<li>");
            Blocks(item.Blocks);
            _out.Append("</li>\n");
        }

        _out.Append("</").Append(tag).Append(">\n");
    }

    private void WriteTable(TableBlock table)
    {
        _out.Append(table.Border is null ? "<table class=\"b\">\n" : "<table class=\"b\">\n");
        foreach (var row in table.Rows)
        {
            _out.Append(row.KeepTogether ? "<tr style=\"break-inside:avoid\">" : "<tr>");
            foreach (var cell in row.Cells)
            {
                var tag = row.IsHeader ? "th" : "td";
                _out.Append('<').Append(tag);
                if (cell.ColumnSpan > 1)
                {
                    _out.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                }

                if (cell.RowSpan > 1)
                {
                    _out.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                }

                var css = string.Empty;
                if (cell.Background is { } background)
                {
                    css += $"background-color:{Css(background)};";
                }

                if (cell.Alignment != TextAlignment.Left)
                {
                    css += $"text-align:{cell.Alignment.ToString().ToLowerInvariant()};";
                }

                if (css.Length > 0)
                {
                    _out.Append(" style=\"").Append(Escape(css.ToString())).Append('"');
                }

                _out.Append('>');
                Blocks(cell.Blocks);
                _out.Append("</").Append(tag).Append('>');
            }

            _out.Append("</tr>\n");
        }

        _out.Append("</table>\n");
    }

    private void WriteImage(ImageBlock image, bool inLine = false)
    {
        ImageGuard.Check(image, options.Limits);
        if (!options.EmbedImages)
        {
            Report("an image (EmbedImages is off)");
            return;
        }

        var mime = image.MediaType switch
        {
            ImageMediaType.Png => "image/png",
            ImageMediaType.Jpeg => "image/jpeg",
            ImageMediaType.Svg => "image/svg+xml",
            _ => null,
        };
        if (mime is null)
        {
            Report("an image of this type");
            return;
        }

        _out.Append(inLine ? "<img style=\"vertical-align:baseline\" src=\"data:" : "<p><img src=\"data:").Append(mime).Append(";base64,").Append(Convert.ToBase64String(image.Data.Span)).Append("\" alt=\"").Append(Escape(image.AltText ?? string.Empty)).Append('"');
        if (image.Width is { } width)
        {
            _out.Append(" width=\"").Append((width.Points / 0.75).ToString("0", CultureInfo.InvariantCulture)).Append('"');
        }

        if (image.Height is { } height)
        {
            _out.Append(" height=\"").Append((height.Points / 0.75).ToString("0", CultureInfo.InvariantCulture)).Append('"');
        }

        _out.Append(inLine ? ">" : "></p>\n");
    }
}
