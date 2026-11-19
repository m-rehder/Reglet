using System.Globalization;
using System.Text.RegularExpressions;

namespace Reglet;

public sealed partial class PdfDocumentReader
{
    private static bool IsOneRun(List<PdfTextSegment> ordered, double size)
    {
        var end = ordered[0].EndX;
        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].X - end > 2 * size)
            {
                return false;
            }

            end = Math.Max(end, ordered[index].EndX);
        }

        return true;
    }

    /// <summary>
    /// ADR-0033: when the text of a page falls into two prose columns (a strip that stays empty in the column
    /// lines, with at least eight lines of broad text on both sides), the left column is built first, then the right one;
    /// lines across both columns stay in place between the bands.
    /// </summary>
    private static List<Line> BuildPageLines(List<PdfTextSegment> text, double pageWidth, List<ReadWarning> warnings)
    {
        if (text.Count < 20)
        {
            return BuildLines(text);
        }

        // Work on cells (runs of words close together): a full-width line is one cell and crosses any gutter.
        var together = BuildLines(text);
        var cells = together.SelectMany(line => StructureRecovery.Cells(line)).ToList();
        // Prose, not a table of short cells: formulas and labels on a page may pull the average down, so count the long cells.
        if (cells.Count < 12 || cells.Count(cell => cell.Segments.Sum(segment => segment.Text.Length) >= 25) < 10)
        {
            return together;
        }

        // The gutter is a strip that stays empty in the lines of the columns (a "river" of white space), also when it
        // is narrower than the gap that separates table cells. Lines that cross it (a title, a figure with labels) do
        // not count against it as long as at least as many lines have text on both sides of the strip.
        var textLines = together.Where(line => line.Segments.Count > 0).ToList();
        double? best = null;
        double bestScore = 0;
        double runStart = -1;
        double runScore = 0;
        var minimum = Math.Max(1, text.Select(segment => segment.Size).Order().ElementAt(text.Count / 2));
        for (var x = pageWidth * 0.3; x <= pageWidth * 0.7 + 1; x += 1)
        {
            var crossing = 0;
            var bothSides = 0;
            foreach (var line in textLines)
            {
                if (line.Segments.Any(segment => segment.X < x && segment.EndX > x))
                {
                    crossing++;
                }
                else
                {
                    var leftPart = line.Segments.Where(segment => segment.EndX <= x).OrderBy(segment => segment.X).ToList();
                    var rightPart = line.Segments.Where(segment => segment.X >= x).OrderBy(segment => segment.X).ToList();

                    // Prose columns: broad text on both sides, each side one run (the cells of a table are narrower and
                    // there are several per side).
                    if (leftPart.Count > 0 && rightPart.Count > 0
                        && leftPart[^1].EndX - leftPart[0].X >= pageWidth * 0.2 && rightPart.Max(segment => segment.EndX) - rightPart[0].X >= pageWidth * 0.2
                        && IsOneRun(leftPart, line.Size) && IsOneRun(rightPart, line.Size))
                    {
                        bothSides++;
                    }
                }
            }

            var free = bothSides >= 8 && bothSides >= crossing;
            if (free && runStart < 0)
            {
                runStart = x;
                runScore = 0;
            }

            if (free)
            {
                runScore += bothSides - crossing;
            }

            if ((!free || x > pageWidth * 0.7) && runStart >= 0)
            {
                var width = x - runStart;
                // The strip that separates the most column lines wins (not just the widest one).
                if (width >= Math.Max(3, minimum * 0.3) && runScore > bestScore)
                {
                    best = runStart + width / 2;
                    bestScore = runScore;
                }

                runStart = -1;
            }
        }

        if (best is not { } gutter)
        {
            return BuildLines(text);
        }

        if (!warnings.Any(warning => warning.Message.Contains("multi-column", StringComparison.Ordinal)))
        {
            warnings.Add(new ReadWarning("PDF: multi-column text was read column by column."));
        }

        // Top to bottom: a line that crosses the gutter stays where it is; the lines between two such lines are read
        // as one band, left column first.
        var lines = new List<Line>();
        var band = new List<PdfTextSegment>();
        void FlushBand()
        {
            lines.AddRange(BuildLines(band.Where(segment => (segment.X + segment.EndX) / 2 < gutter).ToList()));
            lines.AddRange(BuildLines(band.Where(segment => (segment.X + segment.EndX) / 2 >= gutter).ToList()));
            band.Clear();
        }

        foreach (var line in together)
        {
            if (line.Segments.Any(segment => segment.X < gutter && segment.EndX > gutter))
            {
                FlushBand();
                lines.Add(line);
            }
            else
            {
                band.AddRange(line.Segments);
            }
        }

        FlushBand();
        return lines;
    }

    /// <summary>
    /// ADR-0033: recovers headings, lists, tables, indented paragraphs and drops repeated headers/footers from the
    /// positioned lines of all pages. Heuristic; everything iterative.
    /// </summary>
    private sealed partial class StructureRecovery(
        List<List<Line>> pages,
        List<PageSize> sizes,
        List<ReadWarning> warnings,
        List<NumberingDefinition> numbering,
        CancellationToken cancellationToken)
    {
        private const int MaxLinesPerPage = 20_000;
        private static readonly Regex Bullet = BulletPattern();
        private static readonly Regex Number = NumberPattern();
        private double _body = 11;
        private double[] _headingSizes = [];
        private int _lists;
        private readonly Dictionary<string, ListKind> _kinds = new(StringComparer.Ordinal);

        [GeneratedRegex(@"^\s*([•◦▪‣·●○■–\-])\s*(?=\S)")]
        private static partial Regex BulletPattern();

        [GeneratedRegex(@"^\s*(\d{1,3}[.)]|[a-z][.)])\s+(?=\S)")]
        private static partial Regex NumberPattern();

        private bool _openEnd;
        private bool _startsAtMargin;
        private double _lastY;

        internal List<Block> Run()
        {
            if (pages.Any(page => page.Count > MaxLinesPerPage))
            {
                throw new DocumentValidationException("A PDF page has too many text lines.");
            }

            DropRepeated();
            _body = BodySize();
            _headingSizes = pages.SelectMany(page => page)
                .Where(line => line.Size >= _body * 1.15)
                .Select(line => Math.Round(line.Size * 2) / 2)
                .Distinct()
                .OrderByDescending(size => size)
                .ToArray();
            var minY = pages.SelectMany(page => page).Where(line => line.Image is null).Select(line => line.Y).DefaultIfEmpty(0).Min();
            var result = new List<Block>();
            var previousAtBottom = false;
            var previousOpen = false;
            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _openEnd = false;
                _startsAtMargin = false;
                _lastY = double.MaxValue;
                var blocks = Page(pages[index], sizes[index]);
                if (blocks.Count > 0 && result.Count > 0 && previousAtBottom)
                {
                    // A paragraph, table or list that runs on over the page boundary is one block again.
                    var merged = Merge(result[^1], blocks[0], previousOpen, _startsAtMargin);
                    if (merged is not null)
                    {
                        result[^1] = merged;
                        blocks.RemoveAt(0);
                    }
                }

                result.AddRange(blocks);
                if (blocks.Count > 0 || index == 0)
                {
                    previousAtBottom = _lastY <= minY + 3 * _body;
                    previousOpen = _openEnd;
                }
            }

            return result;
        }

        private static string TextOf(Line line) => string.Concat(line.Runs.Select(run => run.Text));

        private double BodySize()
        {
            var weights = new Dictionary<double, int>();
            foreach (var line in pages.SelectMany(page => page))
            {
                var key = Math.Round(line.Size * 2) / 2;
                weights[key] = weights.GetValueOrDefault(key) + TextOf(line).Length;
            }

            return weights.Count == 0 ? 11 : weights.MaxBy(pair => pair.Value).Key;
        }

        private void DropRepeated()
        {
            if (pages.Count < 3)
            {
                return;
            }

            static string Key(Line line) => Regex.Replace(TextOf(line).Trim(), @"\d+", "#");
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            bool Edge(Line line, PageSize size) => line.Y < size.Height.Points * 0.08 || line.Y > size.Height.Points * 0.92;
            for (var i = 0; i < pages.Count; i++)
            {
                foreach (var key in pages[i].Where(line => Edge(line, sizes[i])).Select(Key).Where(key => key.Length > 0).Distinct())
                {
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            var dropped = false;
            var minimum = (pages.Count + 1) / 2;
            for (var i = 0; i < pages.Count; i++)
            {
                dropped |= pages[i].RemoveAll(line => Edge(line, sizes[i]) && counts.GetValueOrDefault(Key(line)) >= minimum) > 0;
            }

            if (dropped)
            {
                warnings.Add(new ReadWarning("PDF: repeated header and footer lines were dropped when recovering the structure."));
            }
        }

        private List<Block> Page(List<Line> lines, PageSize size)
        {
            var blocks = new List<Block>();
            if (lines.Count == 0)
            {
                return blocks;
            }

            var textLines = lines.Where(line => line.Image is null).ToList();
            if (textLines.Count == 0)
            {
                return lines.Select(line => (Block)line.Image!).ToList();
            }

            var right = textLines.Max(line => line.Segments.Count > 0 ? line.Segments.Max(segment => segment.EndX) : 0);
            var left = textLines.Min(line => line.Segments.Count > 0 ? line.Segments.Min(segment => segment.X) : 0);
            _startsAtMargin = textLines[0].Segments.Count == 0 || textLines[0].Segments.Min(segment => segment.X) - left <= textLines[0].Size * 0.5;
            var paragraph = new List<Inline>();
            Line? previous = null;
            double pitch = 0;
            double paragraphLeft = 0;
            var paragraphLines = 0;
            var listStack = new List<(double X, ListBlock Block, List<ListItem> Items)>();
            List<Inline>? item = null;
            double itemTextX = 0;

            void FlushParagraph()
            {
                if (paragraph.Count > 0)
                {
                    blocks.Add(new Paragraph(paragraph));
                }

                paragraph = [];
                previous = null;
                paragraphLines = 0;
                pitch = 0;
            }

            void FlushItem()
            {
                if (item is { Count: > 0 } && listStack.Count > 0)
                {
                    listStack[^1].Items.Add(new ListItem(new Paragraph(item)));
                }

                item = null;
            }

            void FlushList()
            {
                FlushItem();
                // Close the stack from the inside: a nested list becomes part of its parent's last item.
                for (var depth = listStack.Count - 1; depth >= 0; depth--)
                {
                    var (_, block, items) = listStack[depth];
                    var built = new ListBlock(block.NumberingName, items) { NestingLevel = block.NestingLevel };
                    if (depth == 0)
                    {
                        blocks.Add(built);
                    }
                    else
                    {
                        var parent = listStack[depth - 1].Items;
                        var last = parent.Count > 0 ? parent[^1] : new ListItem(new Paragraph(new TextRun(string.Empty)));
                        if (parent.Count > 0)
                        {
                            parent.RemoveAt(parent.Count - 1);
                        }

                        parent.Add(new ListItem(last.Blocks.Append(built)));
                    }
                }

                listStack.Clear();
            }

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (line.Image is { } picture)
                {
                    FlushParagraph();
                    FlushList();
                    blocks.Add(picture);
                    continue;
                }

                var text = TextOf(line);
                if (text.Trim().Length == 0)
                {
                    continue;
                }

                var x0 = line.Segments.Count > 0 ? line.Segments.Min(segment => segment.X) : 0;

                // Table: consecutive lines whose cells start at the same x positions.
                var cells = Cells(line);
                if (cells.Count >= 2)
                {
                    var end = index + 1;
                    while (end < lines.Count && Aligned(cells, Cells(lines[end]), line.Size))
                    {
                        end++;
                    }

                    if (end - index >= 2)
                    {
                        FlushParagraph();
                        FlushList();
                        var table = Table(lines.GetRange(index, end - index));
                        if (IsColumns(table))
                        {
                            // Rows of prose in aligned cells are columns of text: read one column after the other.
                            for (var column = 0; column < table.Rows[0].Cells.Count; column++)
                            {
                                var joined = new List<Inline>();
                                foreach (var row in table.Rows)
                                {
                                    JoinInlines(joined, row.Cells[column].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).ToList());
                                }

                                blocks.Add(new Paragraph(joined));
                            }

                            if (!warnings.Any(warning => warning.Message.Contains("multi-column", StringComparison.Ordinal)))
                            {
                                warnings.Add(new ReadWarning("PDF: multi-column text was read column by column; paragraph breaks inside a column are not recovered."));
                            }
                        }
                        else
                        {
                            blocks.Add(table);
                        }
                        index = end - 1;
                        continue;
                    }
                }

                // Heading.
                if (IsHeading(line, text, right - left, listStack.Count > 0))
                {
                    FlushParagraph();
                    FlushList();
                    blocks.Add(new Heading(LevelOf(line), Trimmed(line.Runs)));
                    continue;
                }

                // List item.
                var marker = Bullet.Match(text);
                var kind = ListKind.Bullet;
                if (!marker.Success || (marker.Value.Trim() is "-" or "–" && !text.TrimStart().Skip(1).FirstOrDefault().Equals(' ')))
                {
                    marker = Number.Match(text);
                    kind = ListKind.Number;
                }

                if (marker.Success && x0 - left < size.Width.Points * 0.5)
                {
                    FlushParagraph();
                    FlushItem();
                    if (listStack.Count > 0 && Math.Abs(x0 - listStack[^1].X) <= line.Size * 0.5 && _kinds[listStack[^1].Block.NumberingName] != kind)
                    {
                        FlushList();
                    }

                    while (listStack.Count > 0 && x0 < listStack[^1].X - line.Size * 0.5)
                    {
                        CloseDeepest(listStack, blocks);
                    }

                    if (listStack.Count == 0 || x0 > listStack[^1].X + line.Size * 0.5)
                    {
                        if (listStack.Count >= ListBlock.MaxNestingLevel)
                        {
                            CloseDeepest(listStack, blocks);
                        }

                        var name = "pdf-list" + (++_lists).ToString(CultureInfo.InvariantCulture);
                        _kinds[name] = kind;
                        numbering.Add(new NumberingDefinition(name, Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = kind })));
                        listStack.Add((x0, new ListBlock(name, [new ListItem(new Paragraph(new TextRun(string.Empty)))]) { NestingLevel = listStack.Count + 1 }, []));
                    }

                    item = StripMarker(line.Runs, marker.Length);
                    itemTextX = line.Segments.Count > 1 ? line.Segments[1].X : x0 + line.Size;
                    previous = line;
                    continue;
                }

                if (item is not null)
                {
                    // Continuation of a list item at the hanging indent; anything else ends the list.
                    if (previous is { } before && before.Y - line.Y <= 1.6 * line.Size && x0 >= itemTextX - line.Size * 0.5)
                    {
                        AppendLine(item, previous, line);
                        previous = line;
                        continue;
                    }

                    FlushList();
                    previous = null;
                }

                // Paragraph.
                if (previous is { } last)
                {
                    var delta = last.Y - line.Y;
                    var lastRight = last.Segments.Count > 0 ? last.Segments.Max(segment => segment.EndX) : right;
                    var lastText = TextOf(last).TrimEnd();
                    var shortEnd = lastRight < right - 4 * last.Size && lastText.Length > 0 && ".!?:;".Contains(lastText[^1]);
                    var break_ = Math.Abs(last.Size - line.Size) > 0.75
                        || delta < 0
                        || delta > 1.6 * Math.Max(last.Size, line.Size)
                        || (paragraphLines >= 2 && pitch > 0 && delta > pitch * 1.3)
                        || shortEnd
                        || paragraphLeft - x0 > line.Size * 0.5;
                    if (break_)
                    {
                        FlushParagraph();
                    }
                }

                if (previous is null)
                {
                    paragraphLeft = x0;
                }
                else if (paragraphLines == 1)
                {
                    pitch = previous.Y - line.Y;
                }

                // An indented first line starts a paragraph that continues at the margin.
                if (previous is not null && x0 - paragraphLeft > line.Size * 0.5 && paragraphLines >= 1)
                {
                    FlushParagraph();
                    paragraphLeft = x0;
                }

                AppendLine(paragraph, previous, line);
                paragraphLines++;
                previous = line;
            }

            if (paragraph.Count > 0 && previous is { } finalLine)
            {
                var finalRight = finalLine.Segments.Count > 0 ? finalLine.Segments.Max(segment => segment.EndX) : right;
                var finalText = TextOf(finalLine).TrimEnd();
                _openEnd = !(finalRight < right - 4 * finalLine.Size && finalText.Length > 0 && ".!?:;".Contains(finalText[^1]));
            }

            _lastY = textLines.Min(line => line.Y);
            FlushParagraph();
            FlushList();
            return blocks;
        }

        private static bool IsColumns(TableBlock table)
            => table.Rows.Count >= 3
                && table.Rows.SelectMany(row => row.Cells).Average(cell => cell.Blocks.OfType<Paragraph>().Sum(p => p.Inlines.OfType<TextRun>().Sum(r => r.Text.Length))) >= 25;

        private static void JoinInlines(List<Inline> target, List<Inline> next)
        {
            if (target.Count > 0 && next.Count > 0)
            {
                var tail = target.OfType<TextRun>().LastOrDefault()?.Text ?? string.Empty;
                var head = next.OfType<TextRun>().FirstOrDefault()?.Text ?? string.Empty;
                if (tail.EndsWith('-') && head.Length > 0 && char.IsLetterOrDigit(head[0]))
                {
                    RemoveTrailingHyphen(target);
                }
                else if (!tail.EndsWith(' ') && !head.StartsWith(' '))
                {
                    target.Add(new TextRun(" "));
                }
            }

            target.AddRange(next);
        }

        private Block? Merge(Block before, Block after, bool open, bool startsAtMargin)
        {
            switch (before, after)
            {
                case (Paragraph a, Paragraph b) when open && startsAtMargin:
                    var inlines = a.Inlines.ToList();
                    var tail = inlines.OfType<TextRun>().LastOrDefault()?.Text ?? string.Empty;
                    var head = b.Inlines.OfType<TextRun>().FirstOrDefault()?.Text ?? string.Empty;
                    if (tail.EndsWith('-') && head.Length > 0 && char.IsLetterOrDigit(head[0]))
                    {
                        RemoveTrailingHyphen(inlines);
                    }
                    else if (!tail.EndsWith(' ') && !head.StartsWith(' '))
                    {
                        inlines.Add(new TextRun(" "));
                    }

                    inlines.AddRange(b.Inlines);
                    return new Paragraph(inlines) { Style = a.Style };
                case (TableBlock a, TableBlock b) when a.Rows[^1].Cells.Count == b.Rows[0].Cells.Count:
                    return new TableBlock(a.Rows.Concat(b.Rows).ToList()) { Border = a.Border };
                case (ListBlock a, ListBlock b) when a.NestingLevel == b.NestingLevel
                    && _kinds.GetValueOrDefault(a.NumberingName) == _kinds.GetValueOrDefault(b.NumberingName):
                    return new ListBlock(a.NumberingName, a.Items.Concat(b.Items).ToList()) { NestingLevel = a.NestingLevel };
                default:
                    return null;
            }
        }

        private static void CloseDeepest(List<(double X, ListBlock Block, List<ListItem> Items)> stack, List<Block> blocks)
        {
            var (_, block, items) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var built = new ListBlock(block.NumberingName, items) { NestingLevel = block.NestingLevel };
            if (stack.Count == 0)
            {
                blocks.Add(built);
                return;
            }

            var parent = stack[^1].Items;
            var last = parent.Count > 0 ? parent[^1] : new ListItem(new Paragraph(new TextRun(string.Empty)));
            if (parent.Count > 0)
            {
                parent.RemoveAt(parent.Count - 1);
            }

            parent.Add(new ListItem(last.Blocks.Append(built)));
        }

        private static List<Inline> StripMarker(List<TextRun> runs, int length)
        {
            var result = new List<Inline>();
            var remaining = length;
            foreach (var run in runs)
            {
                if (remaining <= 0)
                {
                    result.Add(run);
                }
                else if (run.Text.Length > remaining)
                {
                    result.Add(RunFormat.Of(run).ToRun(run.Text[remaining..], run.CharacterStyleName));
                    remaining = 0;
                }
                else
                {
                    remaining -= run.Text.Length;
                }
            }

            return result;
        }

        private static List<Inline> Trimmed(List<TextRun> runs)
        {
            var result = runs.Cast<Inline>().ToList();
            if (result.Count > 0 && result[0] is TextRun first && first.Text.StartsWith(' '))
            {
                result[0] = RunFormat.Of(first).ToRun(first.Text.TrimStart(), first.CharacterStyleName);
            }

            return result;
        }

        private static void AppendLine(List<Inline> target, Line? previous, Line line)
        {
            if (previous is not null && target.Count > 0)
            {
                var previousText = TextOf(previous);
                var nextText = TextOf(line);
                if (previousText.EndsWith('-') && nextText.Length > 0 && char.IsLetterOrDigit(nextText[0]))
                {
                    RemoveTrailingHyphen(target);
                }
                else if (!previousText.EndsWith(' ') && !nextText.StartsWith(' '))
                {
                    target.Add(new TextRun(" "));
                }
            }

            target.AddRange(line.Runs);
        }

        private bool IsHeading(Line line, string text, double width, bool inList)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 120 || ".,;".Contains(trimmed[^1]) || inList)
            {
                return false;
            }

            var lineWidth = line.Segments.Count > 0 ? line.Segments.Max(segment => segment.EndX) - line.Segments.Min(segment => segment.X) : 0;
            if (line.Size >= _body * 1.15)
            {
                return lineWidth < width * 0.9;
            }

            return line.Size >= _body * 0.95 && line.Segments.All(segment => segment.Bold) && lineWidth < width * 0.6 && trimmed.Split(' ').Length <= 12;
        }

        private HeadingLevel LevelOf(Line line)
        {
            if (line.Size < _body * 1.15)
            {
                return HeadingLevel.Level4;
            }

            var rank = Array.IndexOf(_headingSizes, Math.Round(line.Size * 2) / 2);
            return (HeadingLevel)Math.Clamp(rank + 1, 1, 3);
        }

        internal static List<(double X, double End, List<PdfTextSegment> Segments)> Cells(Line line, double gapFactor = 2)
        {
            var cells = new List<(double X, double End, List<PdfTextSegment> Segments)>();
            foreach (var segment in line.Segments.OrderBy(segment => segment.X))
            {
                if (cells.Count > 0 && segment.X - cells[^1].End <= gapFactor * line.Size)
                {
                    var last = cells[^1];
                    last.Segments.Add(segment);
                    cells[^1] = (last.X, Math.Max(last.End, segment.EndX), last.Segments);
                }
                else
                {
                    cells.Add((segment.X, segment.EndX, [segment]));
                }
            }

            return cells;
        }

        private static bool Aligned(List<(double X, double End, List<PdfTextSegment> Segments)> a, List<(double X, double End, List<PdfTextSegment> Segments)> b, double size)
        {
            if (a.Count != b.Count || b.Count < 2)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (Math.Abs(a[i].X - b[i].X) > size * 0.5)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<Inline> CellInlines(List<PdfTextSegment> segments)
        {
            var inlines = new List<Inline>();
            for (var index = 0; index < segments.Count; index++)
            {
                if (index > 0 && NeedsSpace(segments[index - 1], segments[index]))
                {
                    inlines.Add(new TextRun(" "));
                }

                inlines.Add(new TextRun(segments[index].Text) { Bold = segments[index].Bold, Italic = segments[index].Italic });
            }

            return inlines;
        }

        private static TableBlock Table(List<Line> rows)
            => new(rows.Select(line => new TableRow(Cells(line).Select(cell => new TableCell(new Paragraph(CellInlines(cell.Segments)))).ToList())).ToList())
            {
                Border = new ParagraphBorder(),
            };
    }
}
