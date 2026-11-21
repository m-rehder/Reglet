namespace Reglet;

/// <summary>
/// configurable limits for resource consumption and document size. Protect
/// server processes from oversized input.
/// </summary>
public sealed record ResourceLimits
{
    /// <summary>The default limits.</summary>
    public static ResourceLimits Default { get; } = new();

    /// <summary>Maximum byte count of an image (default: 32 MiB).</summary>
    public long MaxImageBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Maximum pixel count of an image (default: 100 megapixels).</summary>
    public long MaxImagePixels { get; init; } = 100_000_000;

    /// <summary>Maximum number of blocks in the document (default: 1,000,000).</summary>
    public int MaxBlocks { get; init; } = 1_000_000;

    /// <summary>Maximum number of table cells in the document (default: 1,000,000).</summary>
    public int MaxTableCells { get; init; } = 1_000_000;

    /// <summary>Maximum text length of a text run (default: 10,000,000 characters).</summary>
    public int MaxTextLength { get; init; } = 10_000_000;

    /// <summary>
    /// Maximum nesting depth of tables, lists and notes (default: 256). The writers recurse per level,
    /// and a stack overflow would end the process instead of throwing.
    /// </summary>
    public int MaxNestingDepth { get; init; } = 256;

    /// <summary>
    /// Maximum number of pages a PDF may get (default: 100,000). Pages cost layout work and output that no other limit
    /// bounds: a line taller than the page or a large header repeated on every page can make very many of them.
    /// </summary>
    public int MaxPages { get; init; } = 100_000;

    /// <summary>
    /// Maximum number of characters that binding a template may add in total (default: 256 MiB, the
    /// size up to which the readers accept a decompressed part).
    /// </summary>
    public long MaxTemplateCharacters { get; init; } = 256L * 1024 * 1024;
}
