using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>Finds the captions of a <see cref="TableOfFigures"/> and the marks of an <see cref="AlphabeticalIndex"/>.</summary>
internal static class FigureIndexScan
{
    /// <summary>A caption paragraph: its label, its text and the paragraph itself.</summary>
    internal sealed record Caption(string Label, string Text, Paragraph Paragraph, Field Sequence);

    /// <summary>The label of the first <c>SEQ</c> field of a paragraph, or null for another paragraph.</summary>
    public static string? LabelOf(Paragraph paragraph) => SequenceOf(paragraph)?.Label;

    /// <summary>The first <c>SEQ</c> field of a paragraph with its label, or null.</summary>
    public static (Field Field, string Label)? SequenceOf(Paragraph paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        foreach (var inline in paragraph.Inlines)
        {
            if (inline is Field field)
            {
                var parts = field.Instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("SEQ", StringComparison.OrdinalIgnoreCase) && !parts[1].StartsWith('\\'))
                {
                    return (field, parts[1].Trim('"'));
                }
            }
        }

        return null;
    }

    /// <summary>The text of a caption: the runs and the results of its fields.</summary>
    public static string CaptionText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        Append(text, paragraph.Inlines);
        return text.ToString().Trim();
    }

    /// <summary>The captions of one label in document order, also inside lists and table cells.</summary>
    public static IReadOnlyList<Caption> Captions(PortableDocument document, string label)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Captions(document.Sections.Select(section => (IEnumerable<Block>)section.Blocks), label);
    }

    /// <summary>The captions of one label in several block groups.</summary>
    public static IReadOnlyList<Caption> Captions(IEnumerable<IEnumerable<Block>> groups, string label)
    {
        var result = new List<Caption>();
        foreach (var blocks in groups)
        {
            Walk(blocks, paragraph =>
            {
                if (SequenceOf(paragraph) is { } found && found.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    var text = CaptionText(paragraph);
                    if (text.Length > 0)
                    {
                        result.Add(new Caption(found.Label, text, paragraph, found.Field));
                    }
                }
            });
        }

        return result;
    }

    /// <summary>The distinct index entries (main entry and sub entry) in index order.</summary>
    public static IReadOnlyList<(string Text, string? SubText)> Entries(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Entries(document.Sections.Select(section => (IEnumerable<Block>)section.Blocks));
    }

    /// <summary>The distinct index entries of several block groups in index order.</summary>
    public static IReadOnlyList<(string Text, string? SubText)> Entries(IEnumerable<IEnumerable<Block>> groups)
    {
        var entries = new HashSet<(string, string?)>();
        foreach (var blocks in groups)
        {
            Walk(blocks, paragraph => CollectEntries(paragraph.Inlines, entries), heading => CollectEntries(heading.Inlines, entries));
        }

        return [.. entries.OrderBy(entry => SortKey(entry.Item1), StringComparer.Ordinal)
            .ThenBy(entry => entry.Item2 is null ? string.Empty : SortKey(entry.Item2), StringComparer.Ordinal)
            .ThenBy(entry => entry.Item1, StringComparer.Ordinal)
            .ThenBy(entry => entry.Item2, StringComparer.Ordinal)];
    }

    /// <summary>The labels of all captions in the blocks.</summary>
    public static IReadOnlyList<string> CaptionLabels(IEnumerable<Block> blocks)
    {
        var labels = new List<string>();
        Walk(blocks, paragraph =>
        {
            if (LabelOf(paragraph) is { } label)
            {
                labels.Add(label);
            }
        });
        return labels;
    }

    /// <summary>All index marks of the body in document order (a repeated entry appears each time).</summary>
    public static IReadOnlyList<IndexEntry> Marks(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var marks = new List<IndexEntry>();
        void Collect(IReadOnlyList<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is IndexEntry entry)
                {
                    marks.Add(entry);
                }
                else if (inline is Hyperlink hyperlink)
                {
                    Collect(hyperlink.Inlines);
                }
            }
        }

        foreach (var section in document.Sections)
        {
            Walk(section.Blocks, paragraph => Collect(paragraph.Inlines), heading => Collect(heading.Inlines));
        }

        return marks;
    }

    /// <summary>Whether the document has a block that needs the scan (a table of figures or an index).</summary>
    public static bool HasListBlock(PortableDocument document)
    {
        var found = false;
        foreach (var section in document.Sections)
        {
            WalkBlocks(section.Blocks, block => found |= block is TableOfFigures or AlphabeticalIndex);
        }

        return found;
    }

    /// <summary>The sort key of an entry: without accents and case, so "Ångström" sorts with "A".</summary>
    public static string SortKey(string text)
    {
        // An unpaired surrogate makes Normalize throw; it sorts as U+FFFD.
        var decomposed = XmlText.Clean(text, InvalidXmlCharacterBehavior.Replace).Normalize(NormalizationForm.FormD);
        var key = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                key.Append(char.ToUpperInvariant(character));
            }
        }

        return key.ToString();
    }

    /// <summary>The letter under which an entry is listed ("#" for a digit or a symbol).</summary>
    public static string GroupOf(string text)
    {
        var key = SortKey(text);
        return key.Length > 0 && char.IsLetter(key[0]) ? key[..1] : "#";
    }

    private static void CollectEntries(IReadOnlyList<Inline> inlines, HashSet<(string, string?)> entries)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case IndexEntry entry:
                    entries.Add((entry.Text, entry.SubText));
                    break;
                case Hyperlink hyperlink:
                    CollectEntries(hyperlink.Inlines, entries);
                    break;
            }
        }
    }

    private static void Append(StringBuilder text, IReadOnlyList<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run when run.Hidden != true:
                    text.Append(run.Text);
                    break;
                case Field field:
                    text.Append(field.Result);
                    break;
                case Hyperlink hyperlink:
                    Append(text, hyperlink.Inlines);
                    break;
            }
        }
    }

    private static void Walk(IEnumerable<Block> blocks, Action<Paragraph> paragraph, Action<Heading>? heading = null)
        => WalkBlocks(blocks, block =>
        {
            if (block is Paragraph item)
            {
                paragraph(item);
            }
            else if (block is Heading title)
            {
                heading?.Invoke(title);
            }
        });

    private static void WalkBlocks(IEnumerable<Block> blocks, Action<Block> visit)
    {
        foreach (var block in blocks)
        {
            visit(block);
            switch (block)
            {
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        WalkBlocks(item.Blocks, visit);
                    }

                    break;
                case TableBlock table:
                    foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        WalkBlocks(cell.Blocks, visit);
                    }

                    break;
            }
        }
    }
}
