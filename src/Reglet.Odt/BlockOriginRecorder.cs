namespace Reglet;

/// <summary>
/// Records which top-level <c>content.xml</c> element produced each model block while reading an
/// ODT (AF12). A paragraph with an inline frame produces a paragraph block and one or more image
/// blocks; all of them share the same source index, so the writer can decide atomically whether
/// the whole source element may be re-emitted verbatim.
/// </summary>
internal sealed class BlockOriginRecorder
{
    private readonly Dictionary<Block, int> _origins = new(ReferenceEqualityComparer.Instance);
    private int _next;

    /// <summary>The recorded source index per block, keyed by reference identity.</summary>
    public IReadOnlyDictionary<Block, int> Origins => _origins;

    /// <summary>Reserves the index of the next top-level source element.</summary>
    public int Next() => _next++;

    /// <summary>Assigns the source index to the blocks one element produced.</summary>
    public void Record(IReadOnlyList<Block> blocks, int first, int origin)
    {
        for (var index = first; index < blocks.Count; index++)
        {
            _origins[blocks[index]] = origin;
        }
    }
}
