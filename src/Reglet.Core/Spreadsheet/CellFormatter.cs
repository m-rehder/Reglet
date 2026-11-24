using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Formats a cell value with an Excel number format code as Excel shows it (ADR-0043): up to four sections
/// (positive; negative; zero; text), digit placeholders <c>0 # ?</c>, thousands separators and scaling commas, percent,
/// quoted and escaped literals, <c>[$…]</c> currency tokens, <c>_x</c> spacing, <c>@</c> and date and time codes.
/// shortcut: conditions (<c>[&gt;100]</c>), scientific notation, fractions, elapsed time (<c>[h]</c>) and literals between
/// digit placeholders (<c>000-00-0000</c>, the number shows at the first run) are not evaluated; add them when a
/// workbook needs them.
/// </summary>
internal static class CellFormatter
{
    public static string Format(object? value, string? code, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case RichText rich:
                return rich.ToString();
            case bool flag:
                return flag ? "TRUE" : "FALSE";
            case string text:
                return FormatText(text, code);
            case DateTime date:
                return FormatNumber(date.ToOADate(), code ?? DefaultDateCode(date), culture);
            case double number:
                return FormatNumber(number, code, culture);
            default:
                return Convert.ToString(value, culture) ?? string.Empty;
        }
    }

    /// <summary>Whether a number format shows a date or time (a reader turns such numbers back into dates).</summary>
    internal static bool IsDateFormat(string? code)
        => !IsGeneral(code) && Tokens(Sections(code!)[0]).Any(token => token.Kind == TokenKind.Date);

    /// <summary>The format Excel gives a date without one: short date, with a time when there is one.</summary>
    internal static string DefaultDateCode(DateTime date) => date.TimeOfDay == TimeSpan.Zero ? "m/d/yyyy" : "m/d/yyyy h:mm";

    private static string FormatText(string text, string? code)
    {
        if (IsGeneral(code))
        {
            return text;
        }

        var sections = Sections(code!);
        // The fourth section formats text; a single section with @ does too.
        var section = sections.Count >= 4 ? sections[3] : sections.Count == 1 && Tokens(sections[0]).Any(token => token.Kind == TokenKind.Text) ? sections[0] : null;
        if (section is null)
        {
            return text;
        }

        var builder = new StringBuilder();
        foreach (var token in Tokens(section))
        {
            builder.Append(token.Kind switch
            {
                TokenKind.Text => text,
                TokenKind.Literal => token.Value,
                _ => string.Empty,
            });
        }

        return builder.ToString();
    }

    private static string FormatNumber(double number, string? code, CultureInfo culture)
    {
        if (IsGeneral(code))
        {
            return General(number, culture);
        }

        var sections = Sections(code!);
        string section;
        var value = number;
        var sign = string.Empty;
        if (number < 0 && sections.Count >= 2)
        {
            section = sections[1];
            value = -number;
        }
        else if (number == 0 && sections.Count >= 3)
        {
            section = sections[2];
        }
        else
        {
            section = sections[0];
            if (number < 0)
            {
                sign = "-";
                value = -number;
            }
        }

        var tokens = Tokens(section);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (tokens.Any(token => token.Kind == TokenKind.Date))
        {
            return sign + FormatDate(value, tokens, culture);
        }

        if (tokens.Any(token => token.Kind == TokenKind.General) || !tokens.Any(token => token.Kind == TokenKind.Digits))
        {
            // A section without digit placeholders (@, or only text) shows the number as General among its literals.
            return sign + string.Concat(tokens.Select(token => token.Kind is TokenKind.General or TokenKind.Text ? General(value, culture) : token.Kind == TokenKind.Literal ? token.Value : string.Empty));
        }

        return sign + FormatDigits(value, tokens, culture);
    }

    private static bool IsGeneral(string? code) => string.IsNullOrEmpty(code) || code.Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>Excel's General: up to 10 significant digits, without trailing zeros.</summary>
    private static string General(double number, CultureInfo culture)
        => Math.Round(number, 10).ToString(Math.Abs(number) is >= 1e11 or (> 0 and < 1e-9) ? "0.#####E+00" : "G10", culture);

    private static string FormatDigits(double value, List<Token> tokens, CultureInfo culture)
    {
        var digits = tokens.Where(token => token.Kind == TokenKind.Digits).Select(token => token.Value).ToList();
        var pattern = string.Concat(digits);
        var point = pattern.IndexOf('.');
        var integerPart = point < 0 ? pattern : pattern[..point];
        var decimalPart = point < 0 ? string.Empty : pattern[(point + 1)..];

        // Commas right of the last integer placeholder divide by 1000 each; a comma between placeholders groups.
        var trimmed = integerPart.TrimEnd(',');
        var scale = integerPart.Length - trimmed.Length;
        var grouped = trimmed.Contains(',');
        integerPart = trimmed.Replace(",", string.Empty, StringComparison.Ordinal);
        decimalPart = decimalPart.Replace(",", string.Empty, StringComparison.Ordinal);

        value *= Math.Pow(100, tokens.Count(token => token.Kind == TokenKind.Percent));
        value /= Math.Pow(1000, scale);
        var rounded = Math.Round(value, decimalPart.Length, MidpointRounding.AwayFromZero);

        var fixedText = rounded.ToString("F" + decimalPart.Length.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var dot = fixedText.IndexOf('.');
        var integerDigits = dot < 0 ? fixedText : fixedText[..dot];
        var decimalDigits = dot < 0 ? string.Empty : fixedText[(dot + 1)..];

        var minimum = integerPart.Count(placeholder => placeholder == '0');
        var spaced = integerPart.Count(placeholder => placeholder == '?');
        integerDigits = integerDigits.TrimStart('0');
        if (integerDigits.Length < minimum)
        {
            integerDigits = integerDigits.PadLeft(minimum, '0');
        }
        else if (integerDigits.Length < minimum + spaced)
        {
            integerDigits = integerDigits.PadLeft(minimum + spaced, ' ');
        }

        if (grouped)
        {
            integerDigits = Group(integerDigits, culture.NumberFormat.NumberGroupSeparator);
        }

        // Decimal placeholders: 0 always shows, # drops a trailing zero, ? turns it into a space.
        var decimals = new StringBuilder(decimalDigits);
        for (var index = decimalPart.Length - 1; index >= 0 && decimals[index] == '0'; index--)
        {
            if (decimalPart[index] == '0')
            {
                break;
            }

            decimals[index] = decimalPart[index] == '?' ? ' ' : '\0';
        }

        var decimalText = decimals.ToString().Replace("\0", string.Empty, StringComparison.Ordinal);
        var numberText = integerDigits + (point < 0 ? string.Empty : culture.NumberFormat.NumberDecimalSeparator + decimalText);

        var builder = new StringBuilder();
        var placed = false;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Digits when !placed:
                    builder.Append(numberText);
                    placed = true;
                    break;
                case TokenKind.Literal:
                    builder.Append(token.Value);
                    break;
                case TokenKind.Percent:
                    builder.Append('%');
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Group(string digits, string separator)
    {
        var start = digits.Length - digits.TrimStart(' ').Length;
        var builder = new StringBuilder(digits[..start]);
        var body = digits[start..];
        for (var index = 0; index < body.Length; index++)
        {
            if (index > 0 && (body.Length - index) % 3 == 0)
            {
                builder.Append(separator);
            }

            builder.Append(body[index]);
        }

        return builder.ToString();
    }

    private static string FormatDate(double serial, List<Token> tokens, CultureInfo culture)
    {
        if (serial >= 2958466)
        {
            return new string('#', 8);
        }

        var date = DateTime.FromOADate(serial);
        var twelveHour = tokens.Any(token => token.Kind == TokenKind.AmPm);
        var builder = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            // In a date, '.' and ',' are separators (dd.mm.yyyy), not digit placeholders.
            if (token.Kind is TokenKind.Literal or TokenKind.Digits)
            {
                builder.Append(token.Value);
                continue;
            }

            if (token.Kind is TokenKind.AmPm)
            {
                var pm = date.Hour >= 12;
                builder.Append(token.Value == "A/P" ? (pm ? "P" : "A") : pm ? "PM" : "AM");
                continue;
            }

            if (token.Kind is not TokenKind.Date)
            {
                continue;
            }

            var part = token.Value.ToLowerInvariant();
            var length = part.Length;
            switch (part[0])
            {
                case 'y':
                    builder.Append(length <= 2 ? (date.Year % 100).ToString("00", culture) : date.Year.ToString("0000", culture));
                    break;
                case 'd':
                    builder.Append(length switch
                    {
                        1 => date.Day.ToString(culture),
                        2 => date.Day.ToString("00", culture),
                        3 => culture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
                        _ => culture.DateTimeFormat.GetDayName(date.DayOfWeek),
                    });
                    break;
                case 'h':
                    var hour = twelveHour ? (date.Hour % 12 == 0 ? 12 : date.Hour % 12) : date.Hour;
                    builder.Append(length == 1 ? hour.ToString(culture) : hour.ToString("00", culture));
                    break;
                case 's':
                    builder.Append(length == 1 ? date.Second.ToString(culture) : date.Second.ToString("00", culture));
                    break;
                case 'm' when IsMinute(tokens, index):
                    builder.Append(length == 1 ? date.Minute.ToString(culture) : date.Minute.ToString("00", culture));
                    break;
                case 'm':
                    builder.Append(length switch
                    {
                        1 => date.Month.ToString(culture),
                        2 => date.Month.ToString("00", culture),
                        3 => culture.DateTimeFormat.GetAbbreviatedMonthName(date.Month),
                        4 => culture.DateTimeFormat.GetMonthName(date.Month),
                        _ => culture.DateTimeFormat.GetMonthName(date.Month)[..1],
                    });
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>An <c>m</c> right after an hour or right before a second is minutes.</summary>
    private static bool IsMinute(List<Token> tokens, int index)
    {
        if (tokens[index].Value.Length > 2)
        {
            return false;
        }

        var before = tokens.Take(index).LastOrDefault(token => token.Kind == TokenKind.Date);
        var after = tokens.Skip(index + 1).FirstOrDefault(token => token.Kind == TokenKind.Date);
        return before.Value is { } previous && char.ToLowerInvariant(previous[0]) == 'h'
            || after.Value is { } next && char.ToLowerInvariant(next[0]) == 's';
    }

    /// <summary>Splits a code at the semicolons outside quotes, escapes and brackets.</summary>
    private static List<string> Sections(string code)
    {
        var sections = new List<string>();
        var start = 0;
        for (var index = 0; index < code.Length; index++)
        {
            switch (code[index])
            {
                case '"':
                    index = Math.Max(index, code.IndexOf('"', index + 1) is var close and >= 0 ? close : code.Length);
                    break;
                case '\\' or '_' or '*':
                    index++;
                    break;
                case '[':
                    index = code.IndexOf(']', index) is var end and >= 0 ? end : code.Length;
                    break;
                case ';':
                    sections.Add(code[start..index]);
                    start = index + 1;
                    break;
            }
        }

        sections.Add(code[start..]);
        return sections;
    }

    private enum TokenKind
    {
        Literal,
        Digits,
        Percent,
        Text,
        Date,
        AmPm,
        General,
    }

    private readonly record struct Token(TokenKind Kind, string Value);

    private static List<Token> Tokens(string section)
    {
        var tokens = new List<Token>();
        for (var index = 0; index < section.Length; index++)
        {
            var character = section[index];
            switch (character)
            {
                case '"':
                    var close = section.IndexOf('"', index + 1);
                    var end = close < 0 ? section.Length : close;
                    tokens.Add(new(TokenKind.Literal, section[(index + 1)..end]));
                    index = end;
                    break;
                case '\\' when index + 1 < section.Length:
                    tokens.Add(new(TokenKind.Literal, section[index + 1].ToString()));
                    index++;
                    break;
                case '_' when index + 1 < section.Length:
                    // Space as wide as the next character: one space is the closest plain text.
                    tokens.Add(new(TokenKind.Literal, " "));
                    index++;
                    break;
                case '*' when index + 1 < section.Length:
                    index++;
                    break;
                case '[':
                    var bracket = section.IndexOf(']', index);
                    var inner = bracket < 0 ? section[(index + 1)..] : section[(index + 1)..bracket];
                    if (inner.StartsWith('$'))
                    {
                        // [$€-407]: the symbol before the locale id.
                        var dash = inner.IndexOf('-');
                        tokens.Add(new(TokenKind.Literal, dash < 0 ? inner[1..] : inner[1..dash]));
                    }

                    index = bracket < 0 ? section.Length : bracket;
                    break;
                case '0' or '#' or '?' or '.' or ',':
                    var digitStart = index;
                    while (index + 1 < section.Length && section[index + 1] is '0' or '#' or '?' or '.' or ',')
                    {
                        index++;
                    }

                    tokens.Add(new(TokenKind.Digits, section[digitStart..(index + 1)]));
                    break;
                case '%':
                    tokens.Add(new(TokenKind.Percent, "%"));
                    break;
                case '@':
                    tokens.Add(new(TokenKind.Text, "@"));
                    break;
                case 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S':
                    var lower = char.ToLowerInvariant(character);
                    var runStart = index;
                    while (index + 1 < section.Length && char.ToLowerInvariant(section[index + 1]) == lower)
                    {
                        index++;
                    }

                    tokens.Add(new(TokenKind.Date, section[runStart..(index + 1)]));
                    break;
                case 'A' or 'a' when section.AsSpan(index).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase):
                    tokens.Add(new(TokenKind.AmPm, "AM/PM"));
                    index += 4;
                    break;
                case 'A' or 'a' when section.AsSpan(index).StartsWith("A/P", StringComparison.OrdinalIgnoreCase):
                    tokens.Add(new(TokenKind.AmPm, "A/P"));
                    index += 2;
                    break;
                case 'G' or 'g' when section.AsSpan(index).StartsWith("General", StringComparison.OrdinalIgnoreCase):
                    tokens.Add(new(TokenKind.General, "General"));
                    index += 6;
                    break;
                default:
                    tokens.Add(new(TokenKind.Literal, character.ToString()));
                    break;
            }
        }

        return tokens;
    }
}
