using System.Globalization;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Resolves SpreadsheetML colours to RGB: <c>rgb</c>, <c>theme</c> (the workbook theme's colour scheme), <c>indexed</c>
/// (the legacy palette, or the workbook's own) and a <c>tint</c> on any of them. <c>auto</c> and the system colours
/// 64/65 stay null (automatic).
/// </summary>
internal sealed class Palette
{
    private static readonly string[] DefaultIndexed =
    [
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF", "00FFFF",
        "800000", "008000", "000080", "808000", "800080", "008080", "C0C0C0", "808080",
        "9999FF", "993366", "FFFFCC", "CCFFFF", "660066", "FF8080", "0066CC", "CCCCFF",
        "000080", "FF00FF", "FFFF00", "00FFFF", "800080", "800000", "008080", "0000FF",
        "00CCFF", "CCFFFF", "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99",
        "3366FF", "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
        "003366", "339966", "003300", "333300", "993300", "993366", "333399", "333333",
    ];

    // SpreadsheetML theme indices swap the first two pairs of the scheme: 0 = lt1 (Background 1), 1 = dk1 (Text 1).
    private static readonly string[] ThemeOrder = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

    private readonly Color?[] _theme;
    private readonly Color?[] _indexed;

    public Palette(XElement? theme, XElement? styles)
    {
        XNamespace a = XlsxConstants.DrawingMain;
        var scheme = theme?.Element(a + "themeElements")?.Element(a + "clrScheme");
        _theme = ThemeOrder.Select(name =>
        {
            var entry = scheme?.Element(a + name);
            var hex = (string?)entry?.Element(a + "srgbClr")?.Attribute("val") ?? (string?)entry?.Element(a + "sysClr")?.Attribute("lastClr");
            return Color.TryParseHex(hex, out var color) ? color : (Color?)null;
        }).ToArray();

        XNamespace s = XlsxConstants.Main;
        var custom = styles?.Element(s + "colors")?.Element(s + "indexedColors")?.Elements(s + "rgbColor")
            .Select(color => Argb((string?)color.Attribute("rgb"))).ToArray();
        _indexed = custom is { Length: > 0 } ? custom : DefaultIndexed.Select(hex => (Color?)Color.FromHex(hex)).ToArray();
    }

    public Color? Resolve(XElement? color)
    {
        if (color is null || (string?)color.Attribute("auto") is "1" or "true")
        {
            return null;
        }

        Color? resolved = null;
        if (color.Attribute("rgb") is { } rgb)
        {
            resolved = Argb(rgb.Value);
        }
        else if (Index(color, "theme") is { } theme)
        {
            resolved = theme < _theme.Length ? _theme[theme] : null;
        }
        else if (Index(color, "indexed") is { } indexed)
        {
            resolved = indexed < _indexed.Length ? _indexed[indexed] : null;
        }

        return resolved is { } value && double.TryParse((string?)color.Attribute("tint"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tint)
            && double.IsFinite(tint) && tint != 0
            ? Tint(value, Math.Clamp(tint, -1, 1))
            : resolved;
    }

    /// <summary>Excel's tint: the lightness moves toward black (negative) or white (positive) in HSL.</summary>
    internal static Color Tint(Color color, double tint)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        lightness = tint < 0 ? lightness * (1 + tint) : (lightness * (1 - tint)) + tint;
        return FromHsl(hue, saturation, lightness);
    }

    private static Color? Argb(string? value)
        => value is { Length: 8 } argb && Color.TryParseHex(argb[2..], out var color) ? color
            : Color.TryParseHex(value, out var plain) ? plain : null;

    private static int? Index(XElement color, string name)
        => int.TryParse((string?)color.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.Red / 255.0, g = color.Green / 255.0, b = color.Blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        var hue = max == r ? ((g - b) / delta) + (g < b ? 6 : 0) : max == g ? ((b - r) / delta) + 2 : ((r - g) / delta) + 4;
        return (hue / 6, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation == 0)
        {
            var gray = Channel(lightness);
            return new Color(gray, gray, gray);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;
        return new Color(Channel(Hue(p, q, hue + (1 / 3.0))), Channel(Hue(p, q, hue)), Channel(Hue(p, q, hue - (1 / 3.0))));

        static double Hue(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6) : p;
        }
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
