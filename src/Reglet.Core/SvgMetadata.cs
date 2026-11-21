using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// reads the intrinsic size of an SVG image (phase AA, AA03). Only the root element is
/// evaluated: the <c>width</c> and <c>height</c> attributes or, if they are missing, the
/// <c>viewBox</c>.
/// </summary>
internal static class SvgMetadata
{
    /// <summary>SVG pixels per inch (the CSS default of 96 dpi).</summary>
    private const double PixelsPerInch = 96.0;

    /// <summary>
    /// The intrinsic size of the image in points, or <c>null</c> when the file does not contain
    /// a usable size.
    /// </summary>
    public static (double Width, double Height)? Read(ReadOnlySpan<byte> data)
    {
        XDocument document;
        try
        {
            document = ReaderGuard.LoadXml(data.ToArray());
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { } root
            || !string.Equals(root.Name.LocalName, "svg", StringComparison.Ordinal))
        {
            return null;
        }

        var width = ReadLength(root.Attribute("width")?.Value);
        var height = ReadLength(root.Attribute("height")?.Value);

        double boxWidth;
        double boxHeight;
        if (width is { } specifiedWidth && height is { } specifiedHeight)
        {
            boxWidth = specifiedWidth;
            boxHeight = specifiedHeight;
        }
        else
        {
            var viewBox = ReadNumbers(root.Attribute("viewBox")?.Value);
            if (viewBox is not { Count: >= 4 } || viewBox[2] <= 0 || viewBox[3] <= 0)
            {
                return null;
            }

            boxWidth = width ?? (height is { } knownHeight ? knownHeight * viewBox[2] / viewBox[3] : viewBox[2] * 72.0 / PixelsPerInch);
            boxHeight = height ?? boxWidth * viewBox[3] / viewBox[2];
        }

        if (!double.IsFinite(boxWidth) || !double.IsFinite(boxHeight) || boxWidth <= 0 || boxHeight <= 0)
        {
            return null;
        }

        return (boxWidth, boxHeight);
    }

    /// <summary>Reads a length with an optional unit and converts it into points.</summary>
    private static double? ReadLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value!.Trim();
        var position = 0;
        if (position < text.Length && (text[position] == '-' || text[position] == '+'))
        {
            position++;
        }

        var start = position;
        while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
        {
            position++;
        }

        // An exponent belongs to the number (SVG/SVG path numbers allow it).
        if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
        {
            var exponent = position;
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
            {
                position++;
            }

            var digits = position;
            while (position < text.Length && char.IsDigit(text[position]))
            {
                position++;
            }

            if (position == digits)
            {
                position = exponent;
            }
        }

        if (position == start
            || !double.TryParse(text[..position], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var factor = text[position..].Trim().ToLowerInvariant() switch
        {
            "pt" => 1.0,
            "mm" => 72.0 / 25.4,
            "cm" => 72.0 / 2.54,
            "in" => 72.0,
            "pc" => 12.0,
            _ => 72.0 / PixelsPerInch,
        };

        return number * factor;
    }

    /// <summary>Reads a list of numbers separated by whitespace or commas.</summary>
    private static List<double>? ReadNumbers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var numbers = new List<double>();
        foreach (var part in value!.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers;
    }
}
