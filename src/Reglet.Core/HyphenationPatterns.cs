using System.Text;

namespace Reglet;

/// <summary>
/// the hyphenation patterns of a language (the Liang algorithm, phase AJ, BJ01). The patterns
/// have the TeX format: letters with digits that give the priority of the position, for
/// example <c>hy3ph</c> or <c>.ach4</c>. A position may be broken where the priority is odd.
///
/// <para>
/// The core library ships no patterns; the caller supplies them, or uses the optional package
/// <c>Reglet.Hyphenation</c>, which carries the TeX patterns of several languages. Without
/// patterns the hyphenation does nothing.
/// </para>
/// </summary>
public sealed class HyphenationPatterns
{
    private readonly Dictionary<int, List<Pattern>> _patterns;

    private HyphenationPatterns(Dictionary<int, List<Pattern>> patterns, int count, int leftMinimum = 2, int rightMinimum = 3)
    {
        _patterns = patterns;
        Count = count;
        LeftMinimum = leftMinimum;
        RightMinimum = rightMinimum;
    }

    /// <summary>Creates a pattern set without patterns (no hyphenation).</summary>
    public HyphenationPatterns()
        : this(new Dictionary<int, List<Pattern>>(), 0)
    {
    }

    /// <summary>A pattern set without patterns (no hyphenation).</summary>
    public static HyphenationPatterns Empty { get; } = new();

    /// <summary>The number of patterns.</summary>
    public int Count { get; }

    /// <summary>
    /// The least number of characters before a break (TeX: <c>\lefthyphenmin</c>); the default
    /// is 2.
    /// </summary>
    public int LeftMinimum { get; init; }

    /// <summary>
    /// The least number of characters after a break (TeX: <c>\righthyphenmin</c>); the default
    /// is 3.
    /// </summary>
    public int RightMinimum { get; init; }

    /// <summary>
    /// reads the patterns (one per line; a line may hold several patterns separated by spaces,
    /// a pattern may be commented out with <c>%</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public static HyphenationPatterns Parse(TextReader reader) => Parse(reader, 2, 3);

    /// <summary>
    /// reads the patterns and sets the least number of characters before and after a break (the
    /// <c>\lefthyphenmin</c> and <c>\righthyphenmin</c> of the language).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A minimum is less than 1.</exception>
    public static HyphenationPatterns Parse(TextReader reader, int leftMinimum, int rightMinimum)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfLessThan(leftMinimum, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rightMinimum, 1);

        var patterns = new Dictionary<int, List<Pattern>>();
        var count = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var comment = line.IndexOf('%', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var pattern = Pattern.Parse(token);
                if (pattern is null)
                {
                    continue;
                }

                if (!patterns.TryGetValue(pattern.Letters.Length, out var list))
                {
                    list = [];
                    patterns[pattern.Letters.Length] = list;
                }

                list.Add(pattern);
                count++;
            }
        }

        return new HyphenationPatterns(patterns, count, leftMinimum, rightMinimum);
    }

    /// <summary>reads the patterns from a text (see <see cref="Parse(TextReader)"/>).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static HyphenationPatterns Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var reader = new StringReader(text);
        return Parse(reader);
    }

    /// <summary>
    /// the positions of a word where a break is allowed (the number of characters before the
    /// break). An empty list means that the word is not broken.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="word"/> is null.</exception>
    public IReadOnlyList<int> BreakPositions(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (Count == 0 || word.Length < LeftMinimum + RightMinimum)
        {
            return [];
        }

        // The word is surrounded by a point, so that the patterns for the word borders
        // (".ach4") match.
        var padded = "." + word.ToLowerInvariant() + ".";
        var values = new int[padded.Length + 1];

        foreach (var (length, list) in _patterns)
        {
            for (var start = 0; start + length <= padded.Length; start++)
            {
                foreach (var pattern in list)
                {
                    if (!pattern.Matches(padded, start))
                    {
                        continue;
                    }

                    for (var index = 0; index <= length; index++)
                    {
                        if (pattern.Values[index] > values[start + index])
                        {
                            values[start + index] = pattern.Values[index];
                        }
                    }
                }
            }
        }

        var positions = new List<int>();
        for (var index = 1; index < word.Length; index++)
        {
            // The position before the character at index: in the padded word the value at
            // index + 1 belongs to it.
            if (values[index + 1] % 2 == 0)
            {
                continue;
            }

            if (index < LeftMinimum || word.Length - index < RightMinimum)
            {
                continue;
            }

            positions.Add(index);
        }

        return positions;
    }

    /// <summary>
    /// inserts the soft hyphen (U+00AD) at the positions where a break is allowed. A word
    /// processor that honours the soft hyphen breaks the word there.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="word"/> is null.</exception>
    public string Apply(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var positions = BreakPositions(word);
        if (positions.Count == 0)
        {
            return word;
        }

        var builder = new StringBuilder(word.Length + positions.Count);
        var next = 0;
        foreach (var position in positions)
        {
            builder.Append(word, next, position - next);
            builder.Append('\u00AD');
            next = position;
        }

        builder.Append(word, next, word.Length - next);
        return builder.ToString();
    }

    /// <summary>
    /// inserts the soft hyphens in a text: every word between letters is hyphenated, the
    /// punctuation and the spaces stay as they are.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public string ApplyToText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Count == 0 || text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        var index = 0;
        while (index < text.Length)
        {
            if (!char.IsLetter(text[index]))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsLetter(text[index]))
            {
                index++;
            }

            builder.Append(Apply(text[start..index]));
        }

        return builder.ToString();
    }

    /// <summary>A pattern: the letters and the priorities of the positions.</summary>
    private sealed class Pattern
    {
        private Pattern(string letters, int[] values)
        {
            Letters = letters;
            Values = values;
        }

        /// <summary>The letters of the pattern.</summary>
        public string Letters { get; }

        /// <summary>The priorities of the positions (one more than the letters).</summary>
        public int[] Values { get; }

        /// <summary>Reads a pattern; null when it has no letters.</summary>
        public static Pattern? Parse(string token)
        {
            var letters = new StringBuilder();
            var values = new List<int> { 0 };
            foreach (var character in token)
            {
                if (char.IsAsciiDigit(character))
                {
                    values[^1] = character - '0';
                    continue;
                }

                // As in TeX, every character but a digit belongs to the pattern: a point marks the word border
                // (".ach4"; the word is padded with points), and hyphens or apostrophes (Ukrainian "обі3в'4є")
                // are part of words. Skipping them would make a different pattern that suppresses breaks.
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                letters.Append(char.ToLowerInvariant(character));
                values.Add(0);
            }

            return letters.Length == 0 ? null : new Pattern(letters.ToString(), [.. values]);
        }

        /// <summary>Whether the pattern matches the text at the position.</summary>
        public bool Matches(string text, int start)
        {
            if (start + Letters.Length > text.Length)
            {
                return false;
            }

            for (var index = 0; index < Letters.Length; index++)
            {
                if (text[start + index] != Letters[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
