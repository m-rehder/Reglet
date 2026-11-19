namespace Reglet;

/// <summary>A simple header or footer with blocks.</summary>
public sealed record HeaderFooter
{
    /// <summary>Creates a header/footer with exactly one block.</summary>
    public HeaderFooter(Block block)
        : this(new[] { block })
    {
    }

    /// <summary>Creates a header/footer with several blocks.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    public HeaderFooter(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = ModelCollections.Snapshot(blocks, nameof(blocks));
    }

    /// <summary>The blocks of the header/footer.</summary>
    public IReadOnlyList<Block> Blocks { get; }
}
