namespace Reglet;

/// <summary>
/// manages the resources of a PDF document and assigns stable names
/// (<c>F1</c>, <c>Im1</c>, <c>GS1</c>). Equal keys yield the same name.
/// </summary>
internal sealed class PdfResourceManager
{
    private readonly List<(string Name, int ObjectNumber)> _fonts = [];
    private readonly List<(string Name, int ObjectNumber)> _images = [];
    private readonly List<(string Name, int ObjectNumber)> _extGStates = [];
    private readonly List<(string Name, int ObjectNumber)> _shadings = [];
    private readonly List<(string Name, int ObjectNumber)> _patterns = [];
    private readonly Dictionary<string, string> _fontNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _imageNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extGStateNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shadingNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _patternNames = new(StringComparer.Ordinal);

    public string AddFont(string key, int objectNumber)
        => Add(key, objectNumber, _fonts, _fontNames, "F");

    public string AddImage(string key, int objectNumber)
        => Add(key, objectNumber, _images, _imageNames, "Im");

    public string AddExtGState(string key, int objectNumber)
        => Add(key, objectNumber, _extGStates, _extGStateNames, "GS");

    public string AddShading(string key, int objectNumber)
        => Add(key, objectNumber, _shadings, _shadingNames, "Sh");

    public string AddPattern(string key, int objectNumber)
        => Add(key, objectNumber, _patterns, _patternNames, "P");

    /// <summary>A form XObject (an SVG transparency group, OF-B07); forms live in the XObject group.</summary>
    public string AddForm(string key, int objectNumber)
        => Add(key, objectNumber, _images, _imageNames, "Fm");

    /// <summary>Writes the resource dictionary.</summary>
    public void Write(PdfTokenWriter tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        tokens.WriteDictionary(resources =>
        {
            WriteGroup(resources, "Font", _fonts);
            WriteGroup(resources, "XObject", _images);
            WriteGroup(resources, "ExtGState", _extGStates);
            WriteGroup(resources, "Shading", _shadings);
            WriteGroup(resources, "Pattern", _patterns);
        });
    }

    private static string Add(
        string key,
        int objectNumber,
        List<(string Name, int ObjectNumber)> entries,
        Dictionary<string, string> names,
        string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (names.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var name = prefix + (entries.Count + 1);
        entries.Add((name, objectNumber));
        names[key] = name;
        return name;
    }

    private static void WriteGroup(
        PdfTokenWriter tokens,
        string groupName,
        List<(string Name, int ObjectNumber)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        tokens.WriteName(groupName);
        tokens.WriteDictionary(group =>
        {
            foreach (var (name, objectNumber) in entries)
            {
                group.WriteName(name);
                group.WriteReference(objectNumber);
            }
        });
    }
}
