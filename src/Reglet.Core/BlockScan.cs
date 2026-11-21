namespace Reglet;

/// <summary>Enumerates blocks of one type in document order.</summary>
internal static class BlockScan
{
    /// <summary>
    /// All blocks of type <typeparamref name="T"/> in document order (also in tables, lists and text boxes);
    /// <paramref name="more"/> gives the inner blocks of a block kind only one format places (or null).
    /// </summary>
    internal static IEnumerable<T> Enumerate<T>(IEnumerable<Block> blocks, Func<Block, IEnumerable<Block>?>? more = null)
        where T : Block
    {
        foreach (var block in blocks)
        {
            IEnumerable<Block>? inner = block switch
            {
                T => null,
                TableBlock table => table.Rows.SelectMany(row => row.Cells).SelectMany(cell => cell.Blocks),
                ListBlock list => list.Items.SelectMany(item => item.Blocks),
                DrawingShape { Content: { } content } => content,
                _ => more?.Invoke(block),
            };
            if (block is T item)
            {
                yield return item;
            }
            else if (inner is not null)
            {
                foreach (var found in Enumerate<T>(inner, more))
                {
                    yield return found;
                }
            }
        }
    }
}
