using System.Xml;

namespace Reglet;

/// <summary>
/// a named multi-level numbering definition. Lists reference it by name
/// (<see cref="ListBlock.NumberingName"/>), so several lists share format, start value and
/// number formatting; the level is selected by the nesting depth.
/// </summary>
public sealed record NumberingDefinition
{
    /// <summary>Maximum number of levels (Word and ODF both define nine).</summary>
    public const int MaxLevels = 9;

    /// <summary>Creates a definition; missing levels repeat the last one.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid NCName.</exception>
    /// <exception cref="ArgumentOutOfRangeException">There is no level or more than <see cref="MaxLevels"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null.</exception>
    public NumberingDefinition(string name, IEnumerable<NumberingLevel> levels)
    {
        if (!IsNcName(name))
        {
            throw new ArgumentException("The name must be a valid NCName.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(levels);
        var items = ModelCollections.Snapshot(levels, nameof(levels));
        if (items.Count is < 1 or > MaxLevels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(levels), items.Count, $"A numbering definition needs 1 to {MaxLevels} levels.");
        }

        // A deeper list repeats the last level, so every definition carries all nine levels.
        Name = name;
        Levels = [.. items, .. Enumerable.Repeat(items[^1], MaxLevels - items.Count)];
    }

    /// <summary>Unique name of the definition; it is also the ODT list style name.</summary>
    public string Name { get; }

    /// <summary>The nine levels, level 1 first; levels above the given ones repeat the last.</summary>
    public IReadOnlyList<NumberingLevel> Levels { get; }

    /// <summary>Whether the name is a valid XML name without a namespace prefix.</summary>
    internal static bool IsNcName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
