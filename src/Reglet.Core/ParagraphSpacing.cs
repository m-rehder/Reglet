namespace Reglet;

/// <summary>
/// shared rule of all writers for the spacing after a paragraph (see
/// <see cref="Typography.ParagraphSpaceAfter"/>). The writers carry the
/// blocks carry a <see cref="ParagraphSpacing"/> state.
/// </summary>
internal sealed class ParagraphSpacing
{
    private int _listDepth;
    private int _compactDepth;
    private Paragraph? _listEnd;

    /// <summary>
    /// whether a paragraph without an explicit <see cref="ParagraphStyle.SpaceAfter"/> is written
    /// compact (0) instead of with the default spacing.
    /// </summary>
    public bool IsCompact(Paragraph paragraph)
        => _compactDepth > 0 || (_listDepth > 0 && !ReferenceEquals(paragraph, _listEnd));

    /// <summary>Effective spacing after the paragraph in points.</summary>
    public double SpaceAfter(Paragraph paragraph) => SpaceAfter(paragraph.Style, IsCompact(paragraph));

    /// <summary>Effective spacing after a paragraph style in points.</summary>
    public static double SpaceAfter(ParagraphStyle style, bool compact)
        => style.SpaceAfter is { } value
            ? value.Points
            : compact ? 0 : Typography.ParagraphSpaceAfter.Points;

    /// <summary>Enters a list; the outermost list determines its last paragraph.</summary>
    public Scope EnterList(ListBlock list)
    {
        var previous = _listEnd;
        if (_listDepth == 0)
        {
            _listEnd = LastParagraph(list);
        }

        _listDepth++;
        return new Scope(() =>
        {
            _listDepth--;
            _listEnd = previous;
        });
    }

    /// <summary>Enters a table cell or a header/footer (always compact).</summary>
    public Scope EnterCompact()
    {
        _compactDepth++;
        return new Scope(() => _compactDepth--);
    }

    /// <summary>The paragraph that ends a list, or null (e.g. it ends with a table).</summary>
    private static Paragraph? LastParagraph(ListBlock list)
    {
        if (list.Items.Count == 0)
        {
            return null;
        }

        var blocks = list.Items[^1].Blocks;
        return blocks.Count == 0 ? null : blocks[^1] switch
        {
            Paragraph paragraph => paragraph,
            ListBlock nested => LastParagraph(nested),
            _ => null,
        };
    }

    /// <summary>Restores the previous state on leaving; <c>default</c> does nothing.</summary>
    public readonly struct Scope(Action? exit) : IDisposable
    {
        public void Dispose() => exit?.Invoke();
    }
}
