namespace Reglet;

/// <summary>The font families a document names (for embedding fonts in DOCX and ODT).</summary>
internal static class FontUsage
{
    /// <summary>
    /// the families of the defaults, the styles and the text runs of the body, headers and
    /// footers. shortcut: runs inside notes and text boxes are not scanned; their font is not embedded.
    /// </summary>
    public static IReadOnlyCollection<string> Families(PortableDocument document)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? family)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                families.Add(family);
            }
        }

        Add(document.Defaults.FontFamily);
        foreach (var style in document.Styles)
        {
            Add(style.FontFamily);
        }

        foreach (var style in document.CharacterStyles)
        {
            Add(style.FontFamily);
        }

        foreach (var section in document.Sections)
        {
            foreach (var block in DocumentSearch.BlocksOf(section))
            {
                foreach (var run in DocumentSearch.RunsOf(block))
                {
                    Add(run.FontFamily);
                }
            }
        }

        return families;
    }
}
