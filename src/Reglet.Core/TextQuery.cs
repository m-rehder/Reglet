using System.Text.RegularExpressions;

namespace Reglet;

/// <summary>
/// the criteria of a search in the document model (phase AH). The search runs over the text
/// of the text runs; a text that is spread over several runs is not found.
/// </summary>
public sealed record TextQuery
{
    /// <summary>Creates a search query.</summary>
    /// <param name="text">The search text; with <see cref="IsRegex"/> a regular expression.</param>
    /// <exception cref="ArgumentException">The search text is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public TextQuery(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
    }

    /// <summary>The search text; with <see cref="IsRegex"/> a regular expression.</summary>
    public string Text { get; }

    /// <summary>Whether the search distinguishes upper and lower case (default: no).</summary>
    public bool MatchCase { get; init; }

    /// <summary>Whether only whole words match (default: no).</summary>
    public bool WholeWord { get; init; }

    /// <summary>Whether <see cref="Text"/> is a regular expression (default: no).</summary>
    public bool IsRegex { get; init; }

    /// <summary>The comparison used for a literal search.</summary>
    internal StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The options used for a regular expression search.</summary>
    internal RegexOptions Options => MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
}

/// <summary>a found occurrence of a search text (phase AH).</summary>
public sealed record TextMatch
{
    internal TextMatch(int sectionIndex, Block block, TextRun run, int index, string text, int length)
    {
        SectionIndex = sectionIndex;
        Block = block;
        Run = run;
        Index = index;
        Text = text;
        Length = length;
    }

    /// <summary>The index of the section in which the occurrence was found.</summary>
    public int SectionIndex { get; }

    /// <summary>The block that contains the occurrence.</summary>
    public Block Block { get; }

    /// <summary>The text run that contains the occurrence.</summary>
    public TextRun Run { get; }

    /// <summary>The position of the occurrence inside the text of the run.</summary>
    public int Index { get; }

    /// <summary>The length of the found text.</summary>
    public int Length { get; }

    /// <summary>The found text.</summary>
    public string Text { get; }
}
