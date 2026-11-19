namespace Reglet;

/// <summary>
/// The text of consecutive nodes while placeholders are cut out from the right. The offsets stay
/// valid in that order and shortened texts are written once at the end, because copying a long
/// text for every placeholder would cost quadratic time.
/// </summary>
internal sealed class TemplateTextCuts
{
    private readonly IReadOnlyList<string> _texts;
    private readonly int[] _offsets;
    private readonly int[] _kept;

    public TemplateTextCuts(IReadOnlyList<string> texts)
    {
        _texts = texts;
        _offsets = new int[texts.Count + 1];
        _kept = new int[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            _offsets[index + 1] = _offsets[index] + texts[index].Length;
            _kept[index] = texts[index].Length;
        }
    }

    /// <summary>Cuts a range and returns its nodes and the text behind it in the last node.</summary>
    public (int First, int Last, string Suffix) Cut(int start, int end)
    {
        var first = NodeEndingAt(start + 1);
        var last = NodeEndingAt(end);
        var skip = end - _offsets[last];
        var suffix = _texts[last].Substring(skip, Math.Max(0, _kept[last] - skip));
        _kept[first] = start - _offsets[first];
        for (var index = first + 1; index <= last; index++)
        {
            _kept[index] = 0;
        }

        return (first, last, suffix);
    }

    /// <summary>Hands every shortened text to the caller.</summary>
    public void Apply(Action<int, string> write)
    {
        for (var index = 0; index < _kept.Length; index++)
        {
            if (_kept[index] != _texts[index].Length)
            {
                write(index, _texts[index][.._kept[index]]);
            }
        }
    }

    /// <summary>The first node that ends at or after the position; the last node when none does.</summary>
    private int NodeEndingAt(int position)
    {
        var index = Array.BinarySearch(_offsets, 1, _offsets.Length - 1, position);
        if (index < 0)
        {
            return Math.Min(~index - 1, _kept.Length - 1);
        }

        // Empty texts share an offset: the first one that reaches the position counts.
        while (index > 1 && _offsets[index - 1] == position)
        {
            index--;
        }

        return index - 1;
    }
}
