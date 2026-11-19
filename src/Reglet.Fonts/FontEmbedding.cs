namespace Reglet;

/// <summary>Chooses the font files that DOCX and ODT embed.</summary>
internal static class FontEmbedding
{
    /// <summary>
    /// every style of the used families that the collection holds and that may be embedded.
    /// The messages name the families that are skipped (no font, or the licence forbids embedding).
    /// </summary>
    public static IReadOnlyList<(string Family, FontFace Face)> Select(
        FontCollection fonts,
        IEnumerable<string> families,
        Action<string> report)
    {
        var result = new List<(string, FontFace)>();
        foreach (var family in families.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var faces = fonts.GetFaces(family);
            if (faces.Count == 0)
            {
                continue;
            }

            foreach (var face in faces)
            {
                if (face.CanEmbed)
                {
                    result.Add((family, face));
                }
                else
                {
                    report($"Font '{family}' ({face.StyleName}) is not embedded: its licence does not allow it.");
                }
            }
        }

        return result;
    }
}
