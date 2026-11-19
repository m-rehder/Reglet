namespace Reglet;

/// <summary>
/// common base type for all block elements of a section: paragraph, heading,
/// list, table, image and page break.
/// </summary>
public abstract class Block
{
    private protected Block() { }
}

/// <summary>A manual page break.</summary>
public sealed class PageBreak : Block;
