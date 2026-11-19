using System.Text;

namespace Reglet;

/// <summary>Measures text widths based on the font metrics.</summary>
internal static class PdfTextMeasurer
{
    /// <summary>Width of a text in points at the given font size.</summary>
    public static double Measure(IPdfFontMetrics metrics, string text, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(text);

        var units = 0.0;
        foreach (var character in text.EnumerateRunes())
        {
            units += metrics.GetWidth(character.Value);
        }

        return units * fontSize / 1000.0;
    }
}
