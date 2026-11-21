using System.Text;
using System.Text.RegularExpressions;

namespace Reglet;

/// <summary>
/// searches and replaces text in the document model (phase AH). The model is immutable, so
/// replacing returns a new document; the formatting of the affected runs is kept.
/// </summary>
public static class DocumentSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Finds all occurrences of the query in the document, including headers and footers.
    /// The search runs over the text of the text runs; a text that is spread over several runs
    /// is not found. Replace changes the same occurrences, headers and footers included.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentValidationException">A regular expression exceeded its time limit.</exception>
    public static IReadOnlyList<TextMatch> Find(PortableDocument document, TextQuery query)
        => Find(document, query, CancellationToken.None);

    /// <summary>Finds all occurrences of the query; the token is checked before each text run.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentValidationException">A regular expression exceeded its time limit.</exception>
    /// <exception cref="OperationCanceledException">The search was cancelled.</exception>
    public static IReadOnlyList<TextMatch> Find(PortableDocument document, TextQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);

        var matcher = CreateMatcher(query, cancellationToken);
        var matches = new List<TextMatch>();
        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            var section = document.Sections[sectionIndex];
            foreach (var block in BlocksOf(section))
            {
                foreach (var run in RunsOf(block))
                {
                    foreach (var (index, length) in matcher.Matches(run.Text))
                    {
                        matches.Add(new TextMatch(
                            sectionIndex,
                            block,
                            run,
                            index,
                            run.Text.Substring(index, length),
                            length));
                    }
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Replaces all occurrences of the query with the replacement and returns the new
    /// document. The original document is not changed. Headers and footers are replaced as well:
    /// a writer that adopts the source package keeps the band blocks the model did not change, so
    /// unmodelled band XML survives (DOCX; ODT writes its bands from the model).
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentValidationException">A regular expression exceeded its time limit.</exception>
    public static PortableDocument Replace(PortableDocument document, TextQuery query, string replacement)
        => Replace(document, query, replacement, CancellationToken.None);

    /// <summary>Replaces all occurrences of the query; the token is checked before each text run.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentValidationException">A regular expression exceeded its time limit.</exception>
    /// <exception cref="OperationCanceledException">The replacement was cancelled.</exception>
    public static PortableDocument Replace(PortableDocument document, TextQuery query, string replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(replacement);

        var matcher = CreateMatcher(query, cancellationToken);
        var sections = new List<Section>(document.Sections.Count);
        var changed = false;
        foreach (var section in document.Sections)
        {
            var replaced = ReplaceSection(section, matcher, replacement);
            changed |= !ReferenceEquals(replaced, section);
            sections.Add(replaced);
        }

        return changed ? document with { Sections = sections } : document;
    }

    // MARK: - Sections and blocks

    internal static IEnumerable<Block> BlocksOf(Section section)
    {
        foreach (var block in section.Blocks)
        {
            foreach (var inner in BlocksOfBlock(block))
            {
                yield return inner;
            }
        }

        foreach (var band in new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter })
        {
            if (band is null)
            {
                continue;
            }

            foreach (var block in band.Blocks)
            {
                foreach (var inner in BlocksOfBlock(block))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>The block itself and the blocks of nested tables and list items.</summary>
    private static IEnumerable<Block> BlocksOfBlock(Block block)
    {
        yield return block;

        switch (block)
        {
            case TableBlock table:
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row.Cells)
                    {
                        foreach (var inner in cell.Blocks)
                        {
                            foreach (var nested in BlocksOfBlock(inner))
                            {
                                yield return nested;
                            }
                        }
                    }
                }

                break;
            case ListBlock list:
                foreach (var item in list.Items)
                {
                    foreach (var inner in item.Blocks)
                    {
                        foreach (var nested in BlocksOfBlock(inner))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
        }
    }

    internal static IEnumerable<TextRun> RunsOf(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (var run in RunsOf(paragraph.Inlines))
                {
                    yield return run;
                }

                break;
            case Heading heading:
                foreach (var run in RunsOf(heading.Inlines))
                {
                    yield return run;
                }

                break;
        }
    }

    private static IEnumerable<TextRun> RunsOf(IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    yield return run;
                    break;
                case Hyperlink hyperlink:
                    foreach (var run in RunsOf(hyperlink.Inlines))
                    {
                        yield return run;
                    }

                    break;
            }
        }
    }

    // MARK: - Replacing

    private static Section ReplaceSection(Section section, Matcher matcher, string replacement)
    {
        var blocks = ReplaceBlocks(section.Blocks, matcher, replacement);
        var header = ReplaceBand(section.Header, matcher, replacement);
        var footer = ReplaceBand(section.Footer, matcher, replacement);
        var firstHeader = ReplaceBand(section.FirstHeader, matcher, replacement);
        var firstFooter = ReplaceBand(section.FirstFooter, matcher, replacement);
        var evenHeader = ReplaceBand(section.EvenHeader, matcher, replacement);
        var evenFooter = ReplaceBand(section.EvenFooter, matcher, replacement);

        return ReferenceEquals(blocks, section.Blocks)
            && ReferenceEquals(header, section.Header)
            && ReferenceEquals(footer, section.Footer)
            && ReferenceEquals(firstHeader, section.FirstHeader)
            && ReferenceEquals(firstFooter, section.FirstFooter)
            && ReferenceEquals(evenHeader, section.EvenHeader)
            && ReferenceEquals(evenFooter, section.EvenFooter)
                ? section
                : section with
                {
                    Blocks = blocks,
                    Header = header,
                    Footer = footer,
                    FirstHeader = firstHeader,
                    FirstFooter = firstFooter,
                    EvenHeader = evenHeader,
                    EvenFooter = evenFooter,
                };
    }

    private static HeaderFooter? ReplaceBand(HeaderFooter? band, Matcher matcher, string replacement)
    {
        if (band is null)
        {
            return null;
        }

        var blocks = ReplaceBlocks(band.Blocks, matcher, replacement);
        return ReferenceEquals(blocks, band.Blocks) ? band : new HeaderFooter(blocks);
    }

    private static IReadOnlyList<Block> ReplaceBlocks(
        IReadOnlyList<Block> blocks,
        Matcher matcher,
        string replacement)
    {
        var result = new List<Block>(blocks.Count);
        var changed = false;
        foreach (var block in blocks)
        {
            var replaced = ReplaceBlock(block, matcher, replacement);
            changed |= !ReferenceEquals(replaced, block);
            result.Add(replaced);
        }

        return changed ? result : blocks;
    }

    private static Block ReplaceBlock(Block block, Matcher matcher, string replacement)
    {
        switch (block)
        {
            case Paragraph paragraph:
            {
                var inlines = ReplaceInlines(paragraph.Inlines, matcher, replacement);
                return inlines == paragraph.Inlines
                    ? paragraph
                    : new Paragraph(inlines) { Style = paragraph.Style, SourceStyleBase = paragraph.SourceStyleBase };
            }

            case Heading heading:
            {
                var inlines = ReplaceInlines(heading.Inlines, matcher, replacement);
                return inlines == heading.Inlines
                    ? heading
                    : new Heading(heading.Level, inlines) { Style = heading.Style };
            }

            case ListBlock list:
            {
                var items = new List<ListItem>(list.Items.Count);
                var changed = false;
                foreach (var item in list.Items)
                {
                    var blocks = ReplaceBlocks(item.Blocks, matcher, replacement);
                    var replaced = ReferenceEquals(blocks, item.Blocks) ? item : new ListItem(blocks);
                    changed |= !ReferenceEquals(replaced, item);
                    items.Add(replaced);
                }

                return changed
                    ? new ListBlock(list.NumberingName, items) { NestingLevel = list.NestingLevel }
                    : list;
            }

            case TableBlock table:
            {
                var rows = new List<TableRow>(table.Rows.Count);
                var changed = false;
                foreach (var row in table.Rows)
                {
                    var cells = new List<TableCell>(row.Cells.Count);
                    var rowChanged = false;
                    foreach (var cell in row.Cells)
                    {
                        var blocks = ReplaceBlocks(cell.Blocks, matcher, replacement);
                        var replaced = ReferenceEquals(blocks, cell.Blocks)
                            ? cell
                            : cell.WithBlocks(blocks);
                        rowChanged |= !ReferenceEquals(replaced, cell);
                        cells.Add(replaced);
                    }

                    var replacedRow = rowChanged
                        ? row.WithCells(cells)
                        : row;
                    changed |= !ReferenceEquals(replacedRow, row);
                    rows.Add(replacedRow);
                }

                return changed
                    ? table.WithRows(rows)
                    : table;
            }

            default:
                return block;
        }
    }

    private static IReadOnlyList<Inline> ReplaceInlines(
        IReadOnlyList<Inline> inlines,
        Matcher matcher,
        string replacement)
    {
        var result = new List<Inline>(inlines.Count);
        var changed = false;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                {
                    var replaced = ReplaceRun(run, matcher, replacement);
                    if (replaced.Count == 1 && ReferenceEquals(replaced[0], run))
                    {
                        result.Add(run);
                    }
                    else
                    {
                        changed = true;
                        result.AddRange(replaced);
                    }

                    break;
                }

                case Hyperlink hyperlink:
                {
                    var inner = ReplaceInlines(hyperlink.Inlines, matcher, replacement);
                    changed |= !ReferenceEquals(inner, hyperlink.Inlines);
                    result.Add(inner == hyperlink.Inlines ? hyperlink : new Hyperlink(hyperlink.Target, inner));
                    break;
                }

                default:
                    result.Add(inline);
                    break;
            }
        }

        return changed ? result : inlines;
    }

    /// <summary>
    /// Replaces the text of a run; a run with several matches becomes several runs with the
    /// same formatting.
    /// </summary>
    private static IReadOnlyList<TextRun> ReplaceRun(TextRun run, Matcher matcher, string replacement)
    {
        var matches = matcher.Matches(run.Text);
        if (matches.Count == 0)
        {
            return [run];
        }

        var parts = new List<TextRun>();
        var position = 0;
        foreach (var (index, length) in matches)
        {
            if (index > position)
            {
                parts.Add(Copy(run, run.Text[position..index]));
            }

            parts.Add(Copy(run, replacement));
            position = index + length;
        }

        if (position < run.Text.Length)
        {
            parts.Add(Copy(run, run.Text[position..]));
        }

        return parts;
    }

    /// <summary>The run with a changed text; the formatting stays.</summary>
    internal static TextRun Copy(TextRun run, string text)
        => Copy(run, text, run.UnderlineStyle, run.Strikethrough, run.Color);

    /// <summary>The run with a changed text, underline, strikethrough and color (the revision markup of the PDF).</summary>
    internal static TextRun Copy(TextRun run, string text, TextUnderlineStyle? underlineStyle, TextStrikethrough? strikethrough, Color? color)
        => new(text)
        {
            Bold = run.Bold,
            Italic = run.Italic,
            Underline = run.Underline,
            UnderlineStyle = underlineStyle,
            UnderlineColor = run.UnderlineColor,
            Strikethrough = strikethrough,
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
            Color = color,
            CharacterStyleName = run.CharacterStyleName,
        };

    // MARK: - Matching

    /// <summary>Finds the occurrences of the query in a text.</summary>
    private static Matcher CreateMatcher(TextQuery query, CancellationToken cancellationToken)
    {
        if (query.IsRegex)
        {
            // shortcut: the limit applies to each match step, so a run with many matches can take longer; cancel for a total limit.
            var regex = new Regex(query.Text, query.Options, RegexTimeout);
            return new Matcher(cancellationToken, text =>
            {
                try
                {
                    return regex.Matches(text)
                        .Select(match => (match.Index, match.Length))
                        .ToList();
                }
                catch (RegexMatchTimeoutException exception)
                {
                    throw new DocumentValidationException("The regular expression exceeded its time limit of one second.", exception);
                }
            });
        }

        return new Matcher(cancellationToken, text =>
        {
            var result = new List<(int Index, int Length)>();
            var index = text.IndexOf(query.Text, query.Comparison);
            while (index >= 0)
            {
                if (!query.WholeWord || IsWholeWord(text, index, query.Text.Length))
                {
                    result.Add((index, query.Text.Length));
                }

                index = text.IndexOf(query.Text, index + Math.Max(1, query.Text.Length), query.Comparison);
            }

            return result;
        });
    }

    /// <summary>Whether the text at the position is surrounded by word boundaries.</summary>
    private static bool IsWholeWord(string text, int index, int length)
    {
        static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

        if (index > 0 && IsWordChar(text[index - 1]))
        {
            return false;
        }

        var end = index + length;
        return end >= text.Length || !IsWordChar(text[end]);
    }

    /// <summary>Matches a text against a query.</summary>
    private sealed class Matcher(CancellationToken cancellationToken, Func<string, List<(int Index, int Length)>> find)
    {
        public List<(int Index, int Length)> Matches(string text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return find(text);
        }
    }
}
