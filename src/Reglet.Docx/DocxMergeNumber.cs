using System.Globalization;

namespace Reglet;

/// <summary>
/// Applies a Word <c>\#</c> numeric picture. Which of <c>.</c> and <c>,</c> is the decimal symbol
/// follows from the text where that is unambiguous; otherwise the document language decides.
/// </summary>
internal static class DocxMergeNumber
{
    // shortcut: a language list instead of CultureInfo, which has no data in invariant mode; extend it when a language is missing.
    private static readonly HashSet<string> DecimalCommaLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "bg", "ca", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hr", "hu", "id", "it", "lt", "lv", "nb", "nl", "nn", "no",
        "pl", "pt", "ro", "ru", "sk", "sl", "sr", "sv", "tr", "uk", "vi",
    };

    private static readonly HashSet<string> DecimalPointRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "de-CH", "de-LI", "es-MX", "es-US", "it-CH",
    };

    /// <summary>Whether the language writes a decimal comma; null when no language is known.</summary>
    private static bool? UsesDecimalComma(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return !DecimalPointRegions.Contains(language)
            && DecimalCommaLanguages.Contains(language.Split('-', '_')[0]);
    }

    /// <summary>Formats a text with a picture; false when the value or the picture cannot be read safely.</summary>
    public static bool TryFormat(string text, string picture, string? language, out string result)
    {
        result = string.Empty;
        var comma = UsesDecimalComma(language);
        if (!TryParseValue(text, comma, out var value))
        {
            return false;
        }

        picture = picture.Trim();
        var quoted = false;
        int first = -1, last = -1;
        for (var index = 0; index < picture.Length; index++)
        {
            if (picture[index] == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && picture[index] is '0' or '#')
            {
                first = first < 0 ? index : first;
                last = index;
            }
        }

        if (first < 0)
        {
            return false;
        }

        var core = picture[first..(last + 1)];
        if (core.Any(character => character is not ('0' or '#' or '.' or ','))
            || !TryFindDecimal(core, comma ?? false, out var decimalSymbol))
        {
            return false;
        }

        var groupSymbol = decimalSymbol == ',' ? '.' : ',';
        var net = string.Concat(core.Select(character => character == decimalSymbol ? '.' : character == groupSymbol ? ',' : character));
        string formatted;
        try
        {
            formatted = value.ToString(net, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return false;
        }

        var localized = string.Concat(formatted.Select(character => character == '.' ? decimalSymbol : character == ',' ? groupSymbol : character));
        result = picture[..first].Replace("'", string.Empty, StringComparison.Ordinal)
            + localized
            + picture[(last + 1)..].Replace("'", string.Empty, StringComparison.Ordinal);
        return true;
    }

    private static bool TryParseValue(string text, bool? comma, out decimal value)
    {
        value = 0;
        text = text.Trim();
        var digits = text.StartsWith('-') || text.StartsWith('+') ? text[1..] : text;
        if (digits.Length == 0 || digits.Any(character => !char.IsAsciiDigit(character) && character is not ('.' or ','))
            || !char.IsAsciiDigit(digits[0]) || !char.IsAsciiDigit(digits[^1]))
        {
            return false;
        }

        var dots = digits.Count(character => character == '.');
        var commas = digits.Count(character => character == ',');
        char? decimalSymbol;
        if (dots > 0 && commas > 0)
        {
            decimalSymbol = digits.LastIndexOf('.') > digits.LastIndexOf(',') ? '.' : ',';
            if ((decimalSymbol == '.' ? dots : commas) != 1)
            {
                return false;
            }
        }
        else if (dots + commas == 0 || dots > 1 || commas > 1)
        {
            decimalSymbol = null; // only digits, or a repeated separator that can only group
        }
        else
        {
            var separator = dots == 1 ? '.' : ',';
            var position = digits.IndexOf(separator, StringComparison.Ordinal);
            var couldGroup = digits.Length - position - 1 == 3 && position is >= 1 and <= 3 && digits[0] != '0';
            if (couldGroup)
            {
                if (comma is null)
                {
                    return false; // "1,234" is 1234 or 1.234 depending on the language
                }

                decimalSymbol = comma.Value == (separator == ',') ? separator : null;
            }
            else
            {
                decimalSymbol = separator;
            }
        }

        var whole = digits;
        var fraction = string.Empty;
        if (decimalSymbol is { } symbol)
        {
            var position = digits.LastIndexOf(symbol);
            whole = digits[..position];
            fraction = digits[(position + 1)..];
        }

        var groups = whole.Split('.', ',');
        if (groups.Length > 1 && (groups[0].Length is 0 or > 3 || groups.Skip(1).Any(group => group.Length != 3)))
        {
            return false;
        }

        var normalized = (text[0] == '-' ? "-" : string.Empty) + string.Concat(groups) + (fraction.Length > 0 ? "." + fraction : string.Empty);
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Finds the decimal symbol of a picture; it is '.' or ',' even when the picture has neither.</summary>
    private static bool TryFindDecimal(string core, bool comma, out char decimalSymbol)
    {
        var dots = core.Count(character => character == '.');
        var commas = core.Count(character => character == ',');
        decimalSymbol = comma ? ',' : '.';
        if (dots > 0 && commas > 0)
        {
            decimalSymbol = core.LastIndexOf('.') > core.LastIndexOf(',') ? '.' : ',';
            return (decimalSymbol == '.' ? dots : commas) == 1;
        }

        if (dots + commas != 1)
        {
            // Only placeholders, or one separator repeated: that one groups.
            if (dots > 1)
            {
                decimalSymbol = ',';
            }
            else if (commas > 1)
            {
                decimalSymbol = '.';
            }

            return true;
        }

        var separator = dots == 1 ? '.' : ',';
        if (core.Length - core.IndexOf(separator, StringComparison.Ordinal) - 1 == 3)
        {
            return true; // three places after it: the language decides, without one "," groups
        }

        decimalSymbol = separator;
        return true;
    }
}
