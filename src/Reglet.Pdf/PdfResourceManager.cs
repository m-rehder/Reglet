using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>Writes the resource dictionary with every resource (for forms and appearance streams).</summary>
    public void Write(PdfTokenWriter tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Write(tokens, _images);
    }

    /// <summary>
    /// Writes the resource dictionary of a page: the resources registered so far, and of the images and forms only those
    /// its content draws, so that a page taken out on its own does not carry the pictures of the others.
    /// </summary>
    public void WritePage(PdfTokenWriter tokens, byte[] content)
    {
        // shortcut: a form drawn on the page still points at the shared dictionary with every image; split it if SVG pages get extracted often.
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(content);
        var drawn = Regex.Matches(Encoding.Latin1.GetString(content), @"/((?:Im|Fm)\d+)\s+Do\b")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Write(tokens, _images.Where(entry => drawn.Contains(entry.Name)).ToList());
    }

    private void Write(PdfTokenWriter tokens, List<(string Name, int ObjectNumber)> xObjects)
        => tokens.WriteDictionary(resources =>
        {
            WriteGroup(resources, "Font", _fonts);
            WriteGroup(resources, "XObject", xObjects);
            WriteGroup(resources, "ExtGState", _extGStates);
            WriteGroup(resources, "Shading", _shadings);
            WriteGroup(resources, "Pattern", _patterns);
        });

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
