namespace Reglet;

/// <summary>An item of a list.</summary>
public sealed record ListItem
{
    /// <summary>Creates a list item with exactly one block.</summary>
    public ListItem(Block block)
        : this(new[] { block })
    {
    }

    /// <summary>Creates a list item with several blocks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public ListItem(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = ModelCollections.Snapshot(blocks, nameof(blocks));
    }

    /// <summary>The blocks of the item.</summary>
    public IReadOnlyList<Block> Blocks { get; }
}
