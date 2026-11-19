using System.Text;

namespace Reglet;

public sealed partial class PdfDocumentReader
{
    private const string Relations = "=≈≠≤≥∼∝→⇒⇔<>≡≃";

    /// <summary>
    /// ADR-0034: replaces clusters of lines that form a display formula (around a fraction bar or a sum with limits, or a
    /// short indented line with a relation sign) with one line that carries an <see cref="Equation"/>.
    /// </summary>
    private static List<Line> RecoverFormulas(List<Line> lines, List<PdfTextSegment> rules, List<PdfTextSegment> ink, List<ReadWarning> warnings)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        var parent = Enumerable.Range(0, lines.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);
        var seeds = new HashSet<int>();
        bool IsText(int i) => lines[i].Image is null && lines[i].Segments.Count > 0;
        double MinX(int i) => lines[i].Segments.Min(segment => segment.X);
        double MaxX(int i) => lines[i].Segments.Max(segment => segment.EndX);
        bool Overlaps(int i, double from, double to) => MaxX(i) > from - 1 && MinX(i) < to + 1;

        // Fraction bars: the main line sits just under the bar, numerator lines above, denominator lines below.
        foreach (var rule in rules.Where(rule => rule.EndX - rule.X is >= 4 and <= 220))
        {
            var near = Enumerable.Range(0, lines.Count).Where(i => IsText(i) && Overlaps(i, rule.X - (3 * lines[i].Size), rule.EndX + (3 * lines[i].Size))).ToList();
            var mains = near.Where(i => Math.Abs(lines[i].Y - (rule.Y - (0.25 * lines[i].Size))) <= 0.2 * lines[i].Size).ToList();
            if (mains.Count == 0)
            {
                continue;
            }

            var main = mains[0];
            var size = lines[main].Size;
            var above = near.Where(i => i != main && Overlaps(i, rule.X, rule.EndX) && lines[i].Y > rule.Y + (0.1 * size) && lines[i].Y <= rule.Y + (1.7 * size)).ToList();
            var below = near.Where(i => i != main && Overlaps(i, rule.X, rule.EndX) && lines[i].Y < lines[main].Y - (0.3 * size) && lines[i].Y >= rule.Y - (1.7 * size)).ToList();
            if (above.Count == 0 || below.Count == 0)
            {
                continue;
            }

            foreach (var member in above.Concat(below))
            {
                Union(main, member);
            }

            seeds.Add(main);
        }

        // Big operators: lines with limits above and below (a limit line may also hold the limits of a neighbour).
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsText(i))
            {
                continue;
            }

            foreach (var op in lines[i].Segments.Where(segment => segment.Text.Length == 1 && MathNary.Operators.Contains(segment.Text[0])))
            {
                for (var j = 0; j < lines.Count; j++)
                {
                    if (j != i && IsText(j) && lines[j].Size < op.Size * 0.9 && Overlaps(j, op.X, op.EndX) && Math.Abs(lines[j].Y - op.Y) <= 2 * op.Size
                        && lines[j].Segments.Any(segment => ((segment.X + segment.EndX) / 2) >= op.X - op.Size && ((segment.X + segment.EndX) / 2) <= op.EndX + op.Size))
                    {
                        Union(i, j);
                        seeds.Add(i);
                    }
                }
            }
        }

        // Short, indented lines with a relation sign (display equations without any structure).
        var prose = Enumerable.Range(0, lines.Count).Where(i => IsText(i) && LineText(lines[i]).Length >= 35).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsText(i) && IsDisplayLine(lines[i], lines, prose))
            {
                seeds.Add(i);
            }
        }

        var groups = seeds.GroupBy(Find).ToDictionary(group => group.Key, group => lines.Select((_, i) => i).Where(i => Find(i) == group.Key).ToList());
        var replace = new Dictionary<int, Line>();
        var drop = new HashSet<int>();
        foreach (var members in groups.Values)
        {
            var segments = members.SelectMany(i => lines[i].Segments).ToList();
            var minY = segments.Min(segment => segment.Y) - 3;
            var maxY = segments.Max(segment => segment.Y) + 3;
            var left = segments.Min(segment => segment.X);
            var right = segments.Max(segment => segment.EndX);
            var inside = rules.Where(rule => rule.Y >= minY - 6 && rule.Y <= maxY + (1.5 * segments.Max(segment => segment.Size)) && rule.EndX > left - 1 && rule.X < right + 1).ToList();

            // A short rule in line with text and with nothing above or below is a minus sign (some producers draw it as a line).
            foreach (var rule in inside.ToList())
            {
                var neighbour = segments.Where(segment => segment.Text.Length > 0).OrderBy(segment => Math.Abs(segment.Y - rule.Y + (0.25 * segment.Size))).FirstOrDefault();
                if (neighbour.Text is null)
                {
                    continue;
                }

                var size = neighbour.Size;
                var covered = segments.Any(segment => ((segment.X + segment.EndX) / 2) >= rule.X && ((segment.X + segment.EndX) / 2) <= rule.EndX
                    && Math.Abs(segment.Y - (rule.Y - (0.25 * size))) > 0.3 * size);
                var inLine = segments.Any(segment => Math.Abs(segment.Y - (rule.Y - (0.25 * size))) <= 0.3 * size && (Math.Abs(segment.X - rule.EndX) <= size || Math.Abs(rule.X - segment.EndX) <= size));
                if (rule.EndX - rule.X <= 2.5 * size && !covered && inLine)
                {
                    segments.Add(new PdfTextSegment(rule.X, rule.Y - (0.25 * size), size, "−", false, false, false, rule.EndX));
                    inside.Remove(rule);
                }
            }

            var number = TrailingNumber(segments, lines, prose, out var body, out var original);
            if (number is not null)
            {
                segments.Remove(original);
                if (body is not null)
                {
                    segments.Add(body.Value);
                }
            }

            if (!LooksLikeFormula(segments) || InDrawing(segments, ink, rules))
            {
                continue;
            }

            // Word and other Unicode math producers mark italics in the letters themselves (U+1D400 block).
            var unicodeItalics = segments.Any(segment => segment.Text.EnumerateRunes().Any(IsMathAlphanumeric));
            var root = new MathBuilder(inside, unicodeItalics).Build(segments, 0);
            if (number is not null)
            {
                root = new MathRow([root, new MathText(" " + number.Value.Text.Trim(), MathTextStyle.Normal)]);
            }

            if (root is MathText { Text.Length: < 2 } || (root is MathRow row && row.Items.All(item => item is MathText) && !segments.Any(segment => Relations.Contains(segment.Text.FirstOrDefault()))))
            {
                continue;
            }

            var first = members.Min();
            var main = lines[members.OrderByDescending(i => lines[i].Size).First()];
            var alt = string.Concat(segments.OrderByDescending(segment => segment.Y).ThenBy(segment => segment.X).Select(segment => segment.Text));
            replace[first] = new Line(main.Y, main.Size, [])
            {
                Segments = { segments.Count > 0 ? segments[0] : default },
                Image = new Equation(root) { AltText = alt.Length > 300 ? alt[..300] : alt },
            };
            foreach (var member in members.Where(member => member != first))
            {
                drop.Add(member);
            }
        }

        if (replace.Count == 0)
        {
            return lines;
        }

        if (!warnings.Any(warning => warning.Message.Contains("formula", StringComparison.Ordinal)))
        {
            warnings.Add(new ReadWarning("PDF: display formulas were recovered as equations; the structure is a guess."));
        }

        var result = new List<Line>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (drop.Contains(i))
            {
                continue;
            }

            result.Add(replace.TryGetValue(i, out var equation) ? equation : lines[i]);
        }

        return result;
    }

    /// <summary>
    /// ADR-0034: turns the outlines of operators (Path pseudo segments) into text segments of the operator, sized like the
    /// text next to them so that they join its line. A rule with two legs hanging from it is a product sign; integral
    /// outlines side by side are a double or triple integral, one with a ring a contour integral.
    /// </summary>
    private static List<PdfTextSegment> ResolveOperatorShapes(List<PdfTextSegment> segments)
    {
        var shapes = segments.Where(segment => segment.Path).ToList();
        if (shapes.Count == 0)
        {
            return segments;
        }

        // Ink and vertical strokes stay for the formula check (a label inside a chart).
        var result = segments.Where(segment => !segment.Path || segment.Text is "" or "|").ToList();
        var text = result.Where(segment => !segment.Rule && segment.Image < 0 && segment.Text.Trim().Length > 0).ToList();

        // Only in a formula: a relation sign close by, or (for a root sign) an overbar that starts where the sign ends.
        // A check mark in a table has the shape of a root sign, a zigzag in a chart may look like a sum.
        var rulesOnPage = result.Where(segment => segment.Rule).ToList();
        bool InFormula(string op, double minX, double maxX, double center, double height)
            => op == "√"
                ? rulesOnPage.Any(rule => Math.Abs(rule.X - maxX) <= 2 && rule.Y >= center && rule.Y <= center + (height / 2) + 2)
                : text.Any(segment => segment.Text.Any(ch => Relations.Contains(ch)) && Math.Abs(segment.Y - center) <= height
                    && segment.X <= maxX + (8 * height) && segment.EndX >= minX - (8 * height));

        void AddOperator(string op, double minX, double maxX, double center, double height)
        {
            if (!InFormula(op, minX, maxX, center, height))
            {
                return;
            }

            var middle = (minX + maxX) / 2;
            // The size of the formula around it: the largest text close by (the limits next to it are smaller).
            var near = text.Where(segment => Math.Abs(segment.Y - center) <= height && Math.Abs(((segment.X + segment.EndX) / 2) - middle) <= 4 * height).ToList();
            var size = near.Count > 0 ? near.Max(segment => segment.Size) : height / 1.5;
            result.Add(new PdfTextSegment(minX, center - (0.25 * size), size, op, false, false, false, maxX));
        }

        var legs = shapes.Where(shape => shape.Text == "|").ToList();
        foreach (var rule in result.Where(segment => segment.Rule).ToList())
        {
            var hanging = legs.Where(leg => leg.X >= rule.X - 1 && leg.EndX <= rule.EndX + 1 && Math.Abs(leg.Y + (leg.Size / 2) - rule.Y) <= 1.5).ToList();
            if (hanging.Count >= 2)
            {
                var bottom = hanging.Min(leg => leg.Y - (leg.Size / 2));
                result.Remove(rule);
                legs.RemoveAll(hanging.Contains);
                result.RemoveAll(hanging.Contains);
                AddOperator("∏", rule.X, rule.EndX, (rule.Y + bottom) / 2, rule.Y - bottom);
            }
        }

        var integrals = shapes.Where(shape => shape.Text == "∫").OrderByDescending(shape => Math.Round(shape.Y)).ThenBy(shape => shape.X).ToList();
        var rings = shapes.Where(shape => shape.Text == "○").ToList();
        for (var i = 0; i < integrals.Count;)
        {
            var first = integrals[i];
            var j = i + 1;
            while (j < integrals.Count && j - i < 3 && integrals[j].X - integrals[j - 1].X <= 0.6 * (integrals[j - 1].EndX - integrals[j - 1].X)
                && Math.Abs(integrals[j].Y - first.Y) < 1)
            {
                j++;
            }

            var last = integrals[j - 1];
            var ring = rings.Any(r => ((r.X + r.EndX) / 2) >= first.X && ((r.X + r.EndX) / 2) <= last.EndX && Math.Abs(r.Y - first.Y) <= first.Size / 2);
            var op = (j - i, ring) switch { (1, true) => "∮", (1, false) => "∫", (2, _) => "∬", _ => "∭" };
            AddOperator(op, first.X, last.EndX, first.Y, first.Size);
            i = j;
        }

        foreach (var shape in shapes.Where(shape => shape.Text is "∑" or "⋃" or "⋂" or "√"))
        {
            AddOperator(shape.Text, shape.X, shape.EndX, shape.Y, shape.Size);
        }

        return result;
    }

    /// <summary>
    /// A label inside a chart or on a picture: at least three pieces of graphics touch the area of the candidate widened by
    /// one text height: ink, vertical strokes (axes), images, and rules that are not within the candidate's own width
    /// (its fraction bars, overbars and minus signs are; the ticks beside a label are not).
    /// </summary>
    private static bool InDrawing(List<PdfTextSegment> segments, List<PdfTextSegment> ink, List<PdfTextSegment> rules)
    {
        var size = segments.Max(segment => segment.Size);
        var ownLeft = segments.Min(segment => segment.X);
        var ownRight = segments.Max(segment => segment.EndX);
        var left = ownLeft - size;
        var right = ownRight + size;
        var bottom = segments.Min(segment => segment.Y) - size;
        var top = segments.Max(segment => segment.Y) + (2 * size);
        bool Touches(PdfTextSegment piece) => piece.EndX >= left && piece.X <= right
            && piece.Y + (piece.Size / 2) >= bottom && piece.Y - (piece.Size / 2) <= top;
        var touching = ink.Count(Touches) + rules.Count(rule => Touches(rule) && (rule.X < ownLeft - 1 || rule.EndX > ownRight + 1));
        return touching >= 3;
    }

    private static bool IsMathAlphanumeric(Rune rune) => rune.Value is (>= 0x1D400 and <= 0x1D7FF) or 0x210E;

    /// <summary>
    /// Mathematical italic letters (𝑥, 𝛼, ℎ) become plain letters with the italic mark, so that the formula is edited like
    /// one typed in Word (which stores "x" with italic style). Other mathematical alphanumerics stay as they are.
    /// </summary>
    private static PdfTextSegment PlainLetters(PdfTextSegment segment)
    {
        if (!segment.Text.EnumerateRunes().Any(IsMathAlphanumeric))
        {
            return segment;
        }

        var text = new StringBuilder(segment.Text.Length);
        var italic = false;
        foreach (var rune in segment.Text.EnumerateRunes())
        {
            var value = rune.Value;
            string? plain = value switch
            {
                0x210E => "h",
                >= 0x1D434 and <= 0x1D467 => ((char)((value - 0x1D434) < 26 ? 'A' + (value - 0x1D434) : 'a' + (value - 0x1D434 - 26))).ToString(),
                >= 0x1D6E2 and <= 0x1D6FA => value - 0x1D6E2 == 17 ? "\u03F4" : ((char)(0x391 + (value - 0x1D6E2))).ToString(),
                >= 0x1D6FC and <= 0x1D714 => ((char)(0x3B1 + (value - 0x1D6FC))).ToString(),
                0x1D715 => "\u2202",
                0x1D716 => "\u03F5",
                0x1D717 => "\u03D1",
                0x1D718 => "\u03F0",
                0x1D719 => "\u03D5",
                0x1D71A => "\u03F1",
                0x1D71B => "\u03D6",
                _ => null,
            };
            italic |= plain is not null;
            text.Append(plain ?? rune.ToString());
        }

        return segment with { Text = text.ToString(), Italic = italic };
    }

    private static readonly System.Text.RegularExpressions.Regex Word = new(@"\p{L}{6,}", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly System.Text.RegularExpressions.Regex Numeric = new(@"\d+[.,]?\d*", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Prose, a figure's axis labels or a table cell is not a formula: short, few real words, few numbers.</summary>
    private static bool LooksLikeFormula(List<PdfTextSegment> segments)
    {
        var text = string.Join(" ", segments.OrderByDescending(segment => segment.Y).ThenBy(segment => segment.X).Select(segment => segment.Text));
        // Pieces often come one glyph at a time, so words are found in the text without separators.
        var joined = string.Concat(segments.OrderByDescending(segment => segment.Y).ThenBy(segment => segment.X).Select(segment => segment.Text));
        return text.Length <= 120 && segments.Count <= 60 && !Word.IsMatch(joined) && Numeric.Matches(text).Count <= 6
            && text.Count(ch => ch == '(') == text.Count(ch => ch == ')');
    }

    private static string LineText(Line line) => string.Concat(line.Segments.OrderBy(segment => segment.X).Select(segment => segment.Text));

    private static readonly System.Text.RegularExpressions.Regex EquationNumber = new(@"[,.;]?\(\d+(\.\d+)?[a-z]?\)$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The equation number "(7)" at the right end: a piece of its own, or the tail of the last piece (then split by width).</summary>
    private static PdfTextSegment? TrailingNumber(List<PdfTextSegment> segments, List<Line> lines, List<int> prose, out PdfTextSegment? body, out PdfTextSegment original)
    {
        body = null;
        var candidate = segments.OrderByDescending(segment => segment.EndX).FirstOrDefault();
        original = candidate;
        var text = candidate.Text ?? string.Empty;
        var match = EquationNumber.Match(text);
        if (!match.Success || prose.Count == 0 || segments.Count < 2 && match.Index == 0)
        {
            return null;
        }

        var proseRight = prose.Select(i => lines[i].Segments.Max(segment => segment.EndX)).Order().ElementAt(prose.Count / 2);
        if (candidate.EndX < proseRight - (2 * candidate.Size))
        {
            return null;
        }

        if (match.Index == 0)
        {
            return candidate;
        }

        var split = candidate.X + ((candidate.EndX - candidate.X) * match.Index / text.Length);
        body = candidate with { Text = text[..match.Index], EndX = split };
        return candidate with { Text = text[match.Index..], X = split };
    }

    private static bool IsDisplayLine(Line line, List<Line> lines, List<int> prose)
    {
        var text = LineText(line);
        if (text.Length is < 3 or > 70 || !text.Any(ch => Relations.Contains(ch)) || prose.Count == 0)
        {
            return false;
        }

        var segments = line.Segments;
        var biggest = segments.Max(segment => segment.Size);

        // Flush-left equations (as in many journals) have no indent: a relation with scripts and no real words is enough.
        if (text.Length <= 60 && segments.Any(segment => segment.Size < 0.85 * biggest) && LooksLikeFormula(segments))
        {
            return true;
        }

        var shortPieces = segments.Count(segment => segment.Text.Trim().Length <= 2);
        if (shortPieces * 2 < segments.Count)
        {
            return false;
        }

        var center = (segments.Min(segment => segment.X) + segments.Max(segment => segment.EndX)) / 2;
        var near = prose.Where(i => lines[i].Segments.Min(segment => segment.X) <= center && lines[i].Segments.Max(segment => segment.EndX) >= center).Select(i => lines[i]).ToList();
        if (near.Count == 0)
        {
            return false;
        }

        var proseLeft = near.Select(candidate => candidate.Segments.Min(segment => segment.X)).Order().ElementAt(near.Count / 2);
        var proseRight = near.Select(candidate => candidate.Segments.Max(segment => segment.EndX)).Order().ElementAt(near.Count / 2);
        var width = proseRight - proseLeft;
        var lastPiece = segments.OrderByDescending(segment => segment.EndX).First();
        var tail = EquationNumber.Match(lastPiece.Text);
        var end = tail.Success && tail.Index > 0
            ? lastPiece.X + ((lastPiece.EndX - lastPiece.X) * tail.Index / lastPiece.Text.Length)
            : tail.Success ? segments.Where(segment => segment != lastPiece).DefaultIfEmpty(lastPiece).Max(segment => segment.EndX) : lastPiece.EndX;
        return width > 0 && segments.Min(segment => segment.X) >= proseLeft + (0.08 * width) && end <= proseRight - (0.08 * width);
    }

    /// <summary>Builds the formula tree of one cluster from positioned pieces and rules (ADR-0034).</summary>
    private sealed class MathBuilder(List<PdfTextSegment> rules, bool unicodeItalics = false)
    {
        private sealed class Item(double x, double endX, double y, double size, string text, MathNode? node, bool italic = false)
        {
            public bool Italic { get; } = italic;

            public double X { get; } = x;

            public double EndX { get; } = endX;

            public double Y { get; } = y;

            public double Size { get; } = size;

            public string Text { get; } = text;

            public MathNode? Node { get; } = node;

            public List<PdfTextSegment> Sup { get; } = [];

            public List<PdfTextSegment> Sub { get; } = [];
        }

        // A bar that is used at one level must not be found again inside its own numerator, denominator or radicand.
        private MathBuilder Inner(List<PdfTextSegment> done) => new(rules.Where(rule => !done.Contains(rule)).ToList(), unicodeItalics);

        public MathNode Build(List<PdfTextSegment> segments, int depth)
        {
            var work = segments.Where(segment => !segment.Rule && segment.Text.Trim().Length > 0).Select(PlainLetters).ToList();
            var items = new List<Item>();
            if (work.Count == 0)
            {
                return new MathText(" ");
            }

            if (depth < 6)
            {
                var minX = work.Min(segment => segment.X);
                var maxX = work.Max(segment => segment.EndX);
                var minY = work.Min(segment => segment.Y);
                var maxY = work.Max(segment => segment.Y);
                var local = rules.Where(rule => rule.EndX > minX - 1 && rule.X < maxX + 1 && rule.Y >= minY - 4 && rule.Y <= maxY + (1.4 * work.Max(segment => segment.Size)) && rule.EndX - rule.X >= 4)
                    .OrderByDescending(rule => rule.EndX - rule.X).ToList();
                var done = new List<PdfTextSegment>();
                foreach (var rule in local)
                {
                    var covered = work.Where(segment => ((segment.X + segment.EndX) / 2) >= rule.X - 1 && ((segment.X + segment.EndX) / 2) <= rule.EndX + 1).ToList();
                    if (covered.Count == 0)
                    {
                        continue;
                    }

                    var size = covered.Max(segment => segment.Size);
                    if (done.Any(previous => rule.X >= previous.X - 1 && rule.EndX <= previous.EndX + 1 && Math.Abs(rule.Y - previous.Y) > 0.3 * size))
                    {
                        continue; // inside the numerator or denominator of a wider bar: found there
                    }

                    done.Add(rule);
                    var radical = work.FirstOrDefault(segment => segment.Text.Contains('√') && segment.EndX >= rule.X - 5 && segment.EndX <= rule.X + 3);
                    var overbar = radical.Text is null && covered.All(segment => segment.Y < rule.Y);
                    if (radical.Text is not null || overbar)
                    {
                        var radicand = covered.Where(segment => segment.Y < rule.Y && segment.Y > rule.Y - (2 * size) && !segment.Equals(radical)).ToList();
                        if (radicand.Count > 0)
                        {
                            // The degree sits over the hook of the root sign, smaller and higher than its baseline.
                            var degree = work.Where(segment => radical.Text is not null && segment.X < radical.X + (0.5 * (radical.EndX - radical.X)) && segment.EndX >= radical.X - (2 * size)
                                && segment.Size < radical.Size * 0.8 && segment.Y > radical.Y && !radicand.Contains(segment)).ToList();
                            var node = new MathRadical(Inner(done).Build(radicand, depth + 1), degree.Count > 0 ? Inner(done).Build(degree, depth + 1) : null);
                            items.Add(new Item(radical.Text is null ? rule.X : Math.Min(radical.X, rule.X), rule.EndX, radicand.Min(segment => segment.Y), size, string.Empty, node));
                            foreach (var used in radicand.Concat(degree).Concat(radical.Text is null ? [] : [radical]))
                            {
                                work.Remove(used);
                            }
                        }

                        continue;
                    }

                    var numerator = covered.Where(segment => segment.Y > rule.Y).ToList();
                    var denominator = covered.Where(segment => segment.Y < rule.Y).ToList();
                    if (numerator.Count == 0 || denominator.Count == 0)
                    {
                        continue;
                    }

                    items.Add(new Item(rule.X, rule.EndX, rule.Y - (0.25 * size), size, string.Empty, new MathFraction(Inner(done).Build(numerator, depth + 1), Inner(done).Build(denominator, depth + 1))));
                    foreach (var used in numerator.Concat(denominator))
                    {
                        work.Remove(used);
                    }
                }
            }

            if (work.Count == 0 && items.Count == 0)
            {
                return new MathText(" ");
            }

            var mainSize = Math.Max(work.Count > 0 ? work.Max(segment => segment.Size) : 0, items.Count > 0 ? items.Max(item => item.Size) : 0);
            var all = items.ToList();
            var scripts = new List<PdfTextSegment>();
            foreach (var segment in work.OrderBy(segment => segment.X))
            {
                if (segment.Size >= mainSize * 0.8 || segment.Text.Length > 3)
                {
                    all.Add(new Item(segment.X, segment.EndX, segment.Y, segment.Size, segment.Text, null, segment.Italic));
                }
                else
                {
                    scripts.Add(segment);
                }
            }

            all = all.OrderBy(item => item.X).ToList();
            foreach (var script in scripts)
            {
                var center = (script.X + script.EndX) / 2;
                var owner = all.FirstOrDefault(item => item.Text.Length == 1 && "∑∏⋃⋂".Contains(item.Text[0]) && center >= item.X - 1 && center <= item.EndX + 1)
                    ?? all.LastOrDefault(item => item.X < script.X + (0.3 * script.Size));
                if (owner is null)
                {
                    all.Insert(0, new Item(script.X, script.EndX, script.Y, script.Size, script.Text, null, script.Italic));
                    continue;
                }

                var offset = script.Y - owner.Y;
                if (offset > 0.2 * mainSize)
                {
                    owner.Sup.Add(script);
                }
                else if (offset < -0.2 * mainSize)
                {
                    owner.Sub.Add(script);
                }
                else
                {
                    all.Add(new Item(script.X, script.EndX, script.Y, script.Size, script.Text, null, script.Italic));
                    all = all.OrderBy(item => item.X).ToList();
                }
            }

            return Row(all, depth);
        }

        private MathNode Row(List<Item> all, int depth)
        {
            var nodes = new List<MathNode>();
            var pending = new StringBuilder();
            MathTextStyle? pendingStyle = null;
            Item? last = null;

            void Flush()
            {
                if (pending.Length > 0)
                {
                    nodes.Add(new MathText(pending.ToString(), pendingStyle ?? MathTextStyle.Normal));
                    pending.Clear();
                    pendingStyle = null;
                }
            }

            for (var index = 0; index < all.Count; index++)
            {
                var item = all[index];
                var gap = last is null ? 0 : item.X - last.EndX;
                var hasScripts = item.Sup.Count > 0 || item.Sub.Count > 0;
                MathNode? node = item.Node;
                if (node is null && item.Text.Length == 1 && MathNary.Operators.Contains(item.Text[0]))
                {
                    var stop = index + 1;
                    while (stop < all.Count && !IsOperatorItem(all[stop]))
                    {
                        stop++;
                    }

                    var body = stop > index + 1 ? Row(all.GetRange(index + 1, stop - index - 1), depth + 1) : new MathText(" ");
                    Flush();
                    nodes.Add(new MathNary(item.Text[0], body, item.Sub.Count > 0 ? Build(item.Sub, depth + 1) : null, item.Sup.Count > 0 ? Build(item.Sup, depth + 1) : null));
                    index = stop - 1;
                    last = all[Math.Max(0, stop - 1)];
                    continue;
                }

                var style = unicodeItalics
                    ? (item.Italic ? MathTextStyle.Italic : MathTextStyle.Normal)
                    : item.Text.Length == 1 && char.IsLetter(item.Text[0]) ? MathTextStyle.Italic : MathTextStyle.Normal;
                if (node is null && !hasScripts)
                {
                    if (pendingStyle is not null && pendingStyle != style || (gap > 0.25 * item.Size && pending.Length > 0))
                    {
                        if (pendingStyle == style || pendingStyle is null)
                        {
                            pending.Append(' ');
                        }
                        else
                        {
                            Flush();
                        }
                    }

                    pendingStyle ??= style;
                    pending.Append(item.Text);
                }
                else
                {
                    Flush();
                    node ??= new MathText(item.Text, style);
                    if (hasScripts)
                    {
                        node = new MathScript(node, item.Sup.Count > 0 ? Build(item.Sup, depth + 1) : null, item.Sub.Count > 0 ? Build(item.Sub, depth + 1) : null);
                    }

                    nodes.Add(node);
                }

                last = item;
            }

            Flush();
            return nodes.Count == 1 ? nodes[0] : new MathRow(nodes);
        }

        private static bool IsOperatorItem(Item item)
            => item.Node is null && item.Text.Length == 1 && (Relations.Contains(item.Text[0]) || "+−-±∓".Contains(item.Text[0]));
    }
}
