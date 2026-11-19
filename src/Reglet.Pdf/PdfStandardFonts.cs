namespace Reglet;

/// <summary>Chooses one of the 14 PDF standard fonts (Helvetica, Times, Courier).</summary>
internal static class PdfStandardFonts
{
    public static string Resolve(string? fontFamily, bool bold, bool italic)
    {
        var family = fontFamily?.ToLowerInvariant() ?? string.Empty;

        if (family.Contains("courier", StringComparison.Ordinal) || family.Contains("mono", StringComparison.Ordinal))
        {
            return Courier(bold, italic);
        }

        // "sans-serif", "Microsoft Sans Serif" etc. contain "serif", but are sans-serif.
        if (family.Contains("sans", StringComparison.Ordinal))
        {
            return Helvetica(bold, italic);
        }

        if (family.Contains("times", StringComparison.Ordinal) || family.Contains("serif", StringComparison.Ordinal))
        {
            return Times(bold, italic);
        }

        return Helvetica(bold, italic);
    }

    private static string Helvetica(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => "Helvetica-BoldOblique",
        (true, false) => "Helvetica-Bold",
        (false, true) => "Helvetica-Oblique",
        _ => "Helvetica",
    };

    private static string Times(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => "Times-BoldItalic",
        (true, false) => "Times-Bold",
        (false, true) => "Times-Italic",
        _ => "Times-Roman",
    };

    private static string Courier(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => "Courier-BoldOblique",
        (true, false) => "Courier-Bold",
        (false, true) => "Courier-Oblique",
        _ => "Courier",
    };
}
