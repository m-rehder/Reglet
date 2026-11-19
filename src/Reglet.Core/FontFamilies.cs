namespace Reglet;

/// <summary>General font category (like the generic CSS families).</summary>
internal enum GenericFontFamily
{
    /// <summary>Serifenschrift (CSS <c>serif</c>).</summary>
    Serif,

    /// <summary>Sans-serif font (CSS <c>sans-serif</c>).</summary>
    SansSerif,

    /// <summary>Monospaced font (CSS <c>monospace</c>).</summary>
    Monospace,
}

/// <summary>
/// resolves font names for formats that expect concrete font names (DOCX, ODT). The
/// generic names <c>serif</c>, <c>sans-serif</c> and <c>monospace</c> are mapped to widely
/// common fonts; otherwise Word, Pages and LibreOffice search for a font
/// that is literally named that. All other names remain unchanged. PDF maps the same
/// categories to Helvetica, Times and Courier.
/// </summary>
internal static class FontFamilies
{
    /// <summary>Concrete font name and, for generic names, the category.</summary>
    public static (string Name, GenericFontFamily? Generic) Resolve(string family)
    {
        ArgumentNullException.ThrowIfNull(family);

        return family.Trim().ToLowerInvariant() switch
        {
            "sans-serif" => ("Arial", GenericFontFamily.SansSerif),
            "serif" => ("Times New Roman", GenericFontFamily.Serif),
            "monospace" => ("Courier New", GenericFontFamily.Monospace),
            _ => (family, null),
        };
    }
}
