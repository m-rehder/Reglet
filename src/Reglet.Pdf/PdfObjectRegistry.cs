namespace Reglet;

/// <summary>
/// assigns stable, consecutive PDF object numbers and prevents duplicate objects via
/// a logical key (e.g. the same font on several pages).
/// </summary>
internal sealed class PdfObjectRegistry
{
    private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    private int _last;

    /// <summary>Zuletzt vergebene Objektnummer.</summary>
    public int LastObjectNumber => _last;

    /// <summary>Reserves the next object number.</summary>
    public int Reserve() => ++_last;

    /// <summary>Returns the object number for a key, if present.</summary>
    public bool TryGet(string key, out int objectNumber)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _byKey.TryGetValue(key, out objectNumber);
    }

    /// <summary>
    /// returns the existing object number for the key or creates it via
    /// <paramref name="create"/> and remembers it.
    /// </summary>
    public int GetOrAdd(string key, Func<int> create)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);

        if (_byKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var objectNumber = create();
        _byKey[key] = objectNumber;
        return objectNumber;
    }
}
