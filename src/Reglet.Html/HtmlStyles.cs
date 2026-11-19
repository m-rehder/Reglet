using System.Globalization;

namespace Reglet;

/// <summary>The few CSS properties and legacy attributes the HTML reader understands (ADR-0032).</summary>
internal static class HtmlStyles
{
    private static readonly Dictionary<string, Color> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new(0, 0, 0), ["white"] = new(255, 255, 255), ["red"] = new(255, 0, 0), ["green"] = new(0, 128, 0),
        ["blue"] = new(0, 0, 255), ["yellow"] = new(255, 255, 0), ["gray"] = new(128, 128, 128), ["grey"] = new(128, 128, 128),
        ["silver"] = new(192, 192, 192), ["maroon"] = new(128, 0, 0), ["purple"] = new(128, 0, 128), ["fuchsia"] = new(255, 0, 255),
        ["lime"] = new(0, 255, 0), ["olive"] = new(128, 128, 0), ["navy"] = new(0, 0, 128), ["teal"] = new(0, 128, 128),
        ["aqua"] = new(0, 255, 255), ["orange"] = new(255, 165, 0),
    };

    private static readonly double[] FontSizes = [8, 10, 12, 14, 18, 24, 36];

    internal static Dictionary<string, string> Declarations(string? style)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(style))
        {
            return result;
        }

        foreach (var part in style.Split(';'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                result[part[..colon].Trim().ToLowerInvariant()] = part[(colon + 1)..].Trim();
            }
        }

        return result;
    }

    internal static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (Named.TryGetValue(text, out var named))
        {
            return named;
        }

        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
                ? new Color((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF))
                : null;
        }

        if (text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
        {
            var parts = text[4..^1].Split(',');
            if (parts.Length == 3 && parts.All(part => int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(part => (byte)Math.Clamp(int.Parse(part.Trim(), CultureInfo.InvariantCulture), 0, 255)).ToArray();
                return new Color(v[0], v[1], v[2]);
            }
        }

        return null;
    }

    /// <summary>A length in points; em and percent are measured against <paramref name="reference"/>.</summary>
    internal static double? ParseLength(string? value, double reference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().ToLowerInvariant();
        double factor;
        string number;
        if (text.EndsWith("pt", StringComparison.Ordinal))
        {
            (factor, number) = (1, text[..^2]);
        }
        else if (text.EndsWith("px", StringComparison.Ordinal))
        {
            (factor, number) = (0.75, text[..^2]);
        }
        else if (text.EndsWith("em", StringComparison.Ordinal))
        {
            (factor, number) = (reference, text[..^2]);
        }
        else if (text.EndsWith('%'))
        {
            (factor, number) = (reference / 100, text[..^1]);
        }
        else
        {
            (factor, number) = (0.75, text);
        }

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && double.IsFinite(amount * factor)
            ? amount * factor
            : null;
    }

    internal static HtmlFormat Apply(HtmlFormat format, HtmlNode element, double baseSize)
    {
        var result = format;
        switch (element.Name)
        {
            case "b" or "strong":
                result = result with { Bold = true };
                break;
            case "i" or "em" or "cite" or "dfn" or "var":
                result = result with { Italic = true };
                break;
            case "u" or "ins":
                result = result with { Underline = true };
                break;
            case "s" or "strike" or "del":
                result = result with { Strike = true };
                break;
            case "sup":
                result = result with { Baseline = TextBaseline.Superscript };
                break;
            case "sub":
                result = result with { Baseline = TextBaseline.Subscript };
                break;
            case "code" or "kbd" or "samp" or "tt":
                result = result with { Family = "Courier New" };
                break;
            case "mark":
                result = result with { Background = new Color(255, 255, 0) };
                break;
            case "font":
                if (ParseColor(element.Attribute("color")) is { } fontColor)
                {
                    result = result with { Color = fontColor };
                }

                if (element.Attribute("face")?.Split(',')[0].Trim().Trim('"', '\'') is { Length: > 0 } face)
                {
                    result = result with { Family = face };
                }

                if (int.TryParse(element.Attribute("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 7)
                {
                    result = result with { Size = FontSizes[size - 1] };
                }

                break;
        }

        var style = Declarations(element.Attribute("style"));
        if (style.Count == 0)
        {
            return result;
        }

        if (style.TryGetValue("color", out var color) && ParseColor(color) is { } parsed)
        {
            result = result with { Color = parsed };
        }

        if (style.TryGetValue("background-color", out var background) || style.TryGetValue("background", out background))
        {
            if (ParseColor(background) is { } backgroundColor)
            {
                result = result with { Background = backgroundColor };
            }
        }

        if (style.TryGetValue("font-size", out var fontSize) && ParseLength(fontSize, result.Size ?? baseSize) is { } points && points is > 1 and < 1000)
        {
            result = result with { Size = Math.Round(points, 2) };
        }

        if (style.TryGetValue("font-family", out var family) && family.Split(',')[0].Trim().Trim('"', '\'') is { Length: > 0 } first)
        {
            result = result with { Family = first };
        }

        if (style.TryGetValue("font-weight", out var weight))
        {
            result = result with { Bold = weight.Equals("bold", StringComparison.OrdinalIgnoreCase) || weight.Equals("bolder", StringComparison.OrdinalIgnoreCase) || (int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w >= 600) };
        }

        if (style.TryGetValue("font-style", out var fontStyle))
        {
            result = result with { Italic = fontStyle.Equals("italic", StringComparison.OrdinalIgnoreCase) || fontStyle.Equals("oblique", StringComparison.OrdinalIgnoreCase) };
        }

        if (style.TryGetValue("text-decoration", out var decoration) || style.TryGetValue("text-decoration-line", out decoration))
        {
            result = result with
            {
                Underline = decoration.Contains("underline", StringComparison.OrdinalIgnoreCase) || (result.Underline && !decoration.Contains("none", StringComparison.OrdinalIgnoreCase)),
                Strike = decoration.Contains("line-through", StringComparison.OrdinalIgnoreCase) || (result.Strike && !decoration.Contains("none", StringComparison.OrdinalIgnoreCase)),
            };
        }

        return result;
    }

    internal static TextAlignment? AlignmentOf(HtmlNode element)
    {
        var value = Declarations(element.Attribute("style")).GetValueOrDefault("text-align") ?? element.Attribute("align");
        return value?.Trim().ToLowerInvariant() switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            "justify" => TextAlignment.Justify,
            "left" => TextAlignment.Left,
            _ => null,
        };
    }

    internal static Color? BackgroundOf(HtmlNode element)
        => ParseColor(Declarations(element.Attribute("style")).GetValueOrDefault("background-color")
            ?? Declarations(element.Attribute("style")).GetValueOrDefault("background") ?? element.Attribute("bgcolor"));

    /// <summary>The paragraph style a block element asks for (alignment, left margin); null when it asks for none.</summary>
    internal static ParagraphStyle? ParagraphOf(HtmlNode element, ParagraphStyle? outer)
    {
        var alignment = AlignmentOf(element);
        var margin = ParseLength(Declarations(element.Attribute("style")).GetValueOrDefault("margin-left"), 11);
        if (alignment is null && margin is null or <= 0)
        {
            return outer;
        }

        var style = outer ?? ParagraphStyle.Default;
        if (alignment is { } a)
        {
            style = style with { Alignment = a };
        }

        if (margin is > 0 and < 5000)
        {
            style = style with { LeftIndent = Length.FromPoints((style.LeftIndent?.Points ?? 0) + margin.Value) };
        }

        return style;
    }
}
