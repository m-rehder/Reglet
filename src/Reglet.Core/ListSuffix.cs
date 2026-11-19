namespace Reglet;

/// <summary>What follows the number or the bullet of a list item before the text.</summary>
public enum ListSuffix
{
    /// <summary>A tab to the text indent (the default).</summary>
    Tab = 0,

    /// <summary>A single space.</summary>
    Space = 1,

    /// <summary>Nothing: the text follows the marker directly.</summary>
    Nothing = 2,
}
