namespace Reglet;

/// <summary>
/// resolves the numbering level of a list against the definitions of a document. Used by the
/// writers; a name that does not resolve is a model error and is reported instead of guessed.
/// </summary>
internal sealed class NumberingLookup
{
    private readonly Dictionary<string, NumberingDefinition> _definitions = new(StringComparer.Ordinal);

    public NumberingLookup(IReadOnlyList<NumberingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            _definitions.TryAdd(definition.Name, definition);
        }
    }

    /// <summary>
    /// the level of the list at the given nesting depth (1 = top level); a depth deeper than
    /// the definition repeats its last level.
    /// </summary>
    /// <exception cref="DocumentValidationException">The list references an unknown definition.</exception>
    public NumberingLevel Level(ListBlock list, int depth)
    {
        var definition = Definition(list);
        return definition.Levels[Math.Clamp(depth, 1, NumberingDefinition.MaxLevels) - 1];
    }

    /// <summary>The definition a list references.</summary>
    /// <exception cref="DocumentValidationException">The name does not resolve.</exception>
    public NumberingDefinition Definition(ListBlock list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!_definitions.TryGetValue(list.NumberingName, out var definition))
        {
            throw new DocumentValidationException(
                $"The list references the unknown numbering definition '{list.NumberingName}'.");
        }

        return definition;
    }
}
