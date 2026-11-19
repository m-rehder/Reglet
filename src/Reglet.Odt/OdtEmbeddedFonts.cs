namespace Reglet;

/// <summary>
/// fonts embedded in an ODT: the files go to <c>Fonts/</c> and the font declarations point to them
/// (<c>svg:font-face-uri</c>), keyed by the font name the declaration uses.
/// </summary>
internal sealed class OdtEmbeddedFonts
{
    private readonly Dictionary<string, List<string>> _paths = new(StringComparer.Ordinal);

    public IReadOnlyList<string> PathsOf(string fontName)
        => _paths.TryGetValue(fontName, out var paths) ? paths : [];

    /// <summary>Writes the font files into the package; null when there is nothing to embed.</summary>
    public static OdtEmbeddedFonts? Embed(
        OdtPackageWriter package,
        PortableDocument document,
        FontCollection? fonts,
        InvalidXmlCharacterBehavior xmlBehavior,
        UnsupportedFeatureHandler handler)
    {
        if (fonts is null)
        {
            return null;
        }

        var faces = FontEmbedding.Select(fonts, FontUsage.Families(document), message => handler.Report("ODT: " + message));
        if (faces.Count == 0)
        {
            return null;
        }

        var result = new OdtEmbeddedFonts();
        for (var index = 0; index < faces.Count; index++)
        {
            var (family, face) = faces[index];
            var name = OdtFontFaces.Name(family, xmlBehavior);
            var path = $"Fonts/font{index + 1}.ttf";
            package.AddBinaryPart(path, "application/x-font-ttf", face.Data);
            if (!result._paths.TryGetValue(name, out var list))
            {
                result._paths[name] = list = [];
            }

            list.Add(path);
        }

        return result;
    }
}
