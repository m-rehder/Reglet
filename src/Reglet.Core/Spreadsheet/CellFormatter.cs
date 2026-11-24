using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Formats a cell value with an Excel number format code as Excel shows it (ADR-0043): up to four sections
/// (positive; negative; zero; text), digit placeholders <c>0 # ?</c>, thousands separators and scaling commas, percent,
/// quoted and escaped literals, <c>[$…]</c> currency tokens, <c>_x</c> spacing, <c>@</c>, date and time codes,
/// conditions (<c>[&gt;=100]</c>), scientific notation (<c>0.00E+00</c>, <c>##0.0E+0</c>) and fractions (<c># ?/?</c>,
/// <c># ?/8</c>), elapsed time (<c>[h]:mm</c>), fractions of a second (<c>ss.00</c>) and literals between digit
/// placeholders (<c>000-00-0000</c>).
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

        var sections = Sections(code!).Select(Tokens).ToList();
        if (ChooseSection(sections, number) is not { } index)
        {
            // No section's condition matches and there is no default section.
            return new string('#', 8);
        }

        var tokens = sections[index];
        // As Excel (and ExcelNumberFormat): the negative section and a conditional first section show no sign.
        var unsigned = index == 1 || (index == 0 && tokens.Any(token => token.Kind == TokenKind.Condition));
        var value = Math.Abs(number);
        var sign = number < 0 && !unsigned ? "-" : string.Empty;
        if (tokens.All(token => token.Kind == TokenKind.Condition))
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

        value *= Math.Pow(100, tokens.Count(token => token.Kind == TokenKind.Percent));
        var exponent = tokens.FindIndex(token => token.Kind == TokenKind.Exponent);
        if (exponent >= 0)
        {
            return sign + Scientific(value, tokens, exponent, culture);
        }

        var slash = tokens.FindIndex(token => token.Kind == TokenKind.Literal && token.Value == "/");
        if (slash > 0 && tokens[slash - 1].Kind == TokenKind.Digits)
        {
            return sign + Fraction(value, tokens, slash, culture);
        }

        return sign + Distribute(value, tokens, culture);
    }

    /// <summary>
    /// The section for a number: with conditions the first matching one (or the next without a condition), otherwise
    /// positive, negative and zero as Excel picks them; null when a condition leaves no section.
    /// </summary>
    private static int? ChooseSection(List<List<Token>> sections, double number)
    {
        Token? Condition(int index) => sections[index].FirstOrDefault(token => token.Kind == TokenKind.Condition) is { Value: not null } found ? found : null;
        if (Condition(0) is { } first)
        {
            if (Matches(first.Value, number))
            {
                return 0;
            }

            if (sections.Count == 1)
            {
                return null;
            }

            if (Condition(1) is { } second)
            {
                return Matches(second.Value, number) ? 1 : sections.Count >= 3 ? 2 : null;
            }

            return 1;
        }

        if (number > 0 || sections.Count == 1 || (sections.Count == 2 && number == 0))
        {
            return 0;
        }

        return number < 0 ? 1 : sections.Count >= 3 ? 2 : 0;
    }

    /// <summary>Evaluates a condition such as <c>&gt;=100</c>.</summary>
    private static bool Matches(string condition, double number)
    {
        var length = condition.StartsWith("<=", StringComparison.Ordinal) || condition.StartsWith(">=", StringComparison.Ordinal) || condition.StartsWith("<>", StringComparison.Ordinal) ? 2 : 1;
        if (!double.TryParse(condition[length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var operand))
        {
            return false;
        }

        return condition[..length] switch
        {
            "<" => number < operand,
            ">" => number > operand,
            "=" => number == operand,
            "<=" => number <= operand,
            ">=" => number >= operand,
            _ => number != operand,
        };
    }

    /// <summary>
    /// The number placed into its digit runs. One run takes the whole number; with literals between runs
    /// (<c>000-00-0000</c>) the integer digits fill the runs from the right, the first run taking what is left, and the
    /// decimals fill the runs after the point from the left.
    /// </summary>
    private static string Distribute(double value, List<Token> tokens, CultureInfo culture)
    {
        var runs = Enumerable.Range(0, tokens.Count).Where(index => tokens[index].Kind == TokenKind.Digits).ToList();
        var pattern = string.Concat(runs.Select(index => tokens[index].Value));
        var text = RenderNumber(value, pattern, culture);
        if (runs.Count == 1)
        {
            return Place(tokens, index => index == runs[0] ? text : null);
        }

        var separator = culture.NumberFormat.NumberDecimalSeparator;
        var pointRun = runs.FirstOrDefault(index => tokens[index].Value.Contains('.'), -1);
        var split = pointRun < 0 ? -1 : text.LastIndexOf(separator, StringComparison.Ordinal);
        var integerText = split < 0 ? text : text[..split];
        var decimalText = split < 0 ? string.Empty : text[(split + separator.Length)..];

        static int Count(string run) => run.Count(placeholder => placeholder is '0' or '#' or '?');
        string IntegerPortion(int index) => index == pointRun ? tokens[index].Value[..tokens[index].Value.IndexOf('.')] : tokens[index].Value;
        string DecimalPortion(int index) => index == pointRun ? tokens[index].Value[(tokens[index].Value.IndexOf('.') + 1)..] : tokens[index].Value;

        var pieces = runs.ToDictionary(index => index, _ => string.Empty);
        var integerRuns = runs.Where(index => pointRun < 0 || index <= pointRun).ToList();
        for (var position = integerRuns.Count - 1; position >= 0; position--)
        {
            var take = position == 0 ? integerText.Length : Math.Min(integerText.Length, Count(IntegerPortion(integerRuns[position])));
            pieces[integerRuns[position]] = integerText[^take..];
            integerText = integerText[..^take];
        }

        if (pointRun >= 0)
        {
            var decimalRuns = runs.Where(index => index >= pointRun).ToList();
            for (var position = 0; position < decimalRuns.Count; position++)
            {
                var take = position == decimalRuns.Count - 1 ? decimalText.Length : Math.Min(decimalText.Length, Count(DecimalPortion(decimalRuns[position])));
                var piece = decimalText[..take];
                decimalText = decimalText[take..];
                pieces[decimalRuns[position]] = position == 0 ? pieces[pointRun] + separator + piece : piece;
            }
        }

        return Place(tokens, index => pieces.TryGetValue(index, out var piece) ? piece : null);
    }

    /// <summary>Joins the tokens: <paramref name="replace"/> gives the text of a digit token (null drops it); literals stay.</summary>
    private static string Place(List<Token> tokens, Func<int, string?> replace)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (replace(index) is { } text)
            {
                builder.Append(text);
                continue;
            }

            switch (token.Kind)
            {
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

    /// <summary>Scientific notation: <c>0.00E+00</c>, and <c>##0.0E+0</c> with the exponent a multiple of the integer digits.</summary>
    private static string Scientific(double value, List<Token> tokens, int exponentAt, CultureInfo culture)
    {
        var mantissaPattern = string.Concat(tokens.Take(exponentAt).Where(token => token.Kind == TokenKind.Digits).Select(token => token.Value));
        var exponentDigits = exponentAt + 1 < tokens.Count && tokens[exponentAt + 1].Kind == TokenKind.Digits ? tokens[exponentAt + 1].Value : "0";
        var point = mantissaPattern.IndexOf('.');
        var integerPattern = (point < 0 ? mantissaPattern : mantissaPattern[..point]).Replace(",", string.Empty, StringComparison.Ordinal);
        var decimals = point < 0 ? 0 : mantissaPattern[(point + 1)..].Count(placeholder => placeholder is '0' or '#' or '?');
        var places = Math.Max(1, integerPattern.Length);
        var engineering = integerPattern.Contains('#');

        int Exponent(double magnitude)
        {
            if (magnitude == 0)
            {
                return 0;
            }

            var power = (int)Math.Floor(Math.Log10(magnitude));
            return engineering ? (int)Math.Floor(power / (double)places) * places : power - (places - 1);
        }

        var exponent = Exponent(value);
        var mantissa = Math.Round(value / Math.Pow(10, exponent), decimals, MidpointRounding.AwayFromZero);
        if (value != 0 && mantissa >= Math.Pow(10, places))
        {
            exponent = Exponent(mantissa * Math.Pow(10, exponent));
            mantissa = Math.Round(value / Math.Pow(10, exponent), decimals, MidpointRounding.AwayFromZero);
        }

        var marker = tokens[exponentAt].Value;
        var exponentText = (exponent < 0 ? "-" : marker[1] == '+' ? "+" : string.Empty)
            + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(exponentDigits.Count(placeholder => placeholder == '0'), '0');
        var firstDigits = tokens.FindIndex(token => token.Kind == TokenKind.Digits);
        return Place(tokens, index => index == firstDigits && index < exponentAt
            ? RenderNumber(mantissa, mantissaPattern, culture)
            : index == exponentAt
                ? marker[0] + exponentText
                : tokens[index].Kind == TokenKind.Digits && (index < exponentAt || index == exponentAt + 1) ? string.Empty : null);
    }

    /// <summary>
    /// Fractions: <c># ?/?</c>, <c># ??/??</c>, <c>?/?</c> (an improper fraction without an integer part) and fixed
    /// denominators such as <c># ?/8</c>. The closest fraction whose denominator fits the placeholders is shown.
    /// </summary>
    private static string Fraction(double value, List<Token> tokens, int slash, CultureInfo culture)
    {
        var numeratorAt = slash - 1;
        var integerAt = tokens.FindIndex(token => token.Kind == TokenKind.Digits);
        var hasInteger = integerAt >= 0 && integerAt < numeratorAt;
        var denominatorEnd = slash + 1;
        var fixedDenominator = new StringBuilder();
        string denominatorPattern;
        if (denominatorEnd < tokens.Count && tokens[denominatorEnd].Kind == TokenKind.Digits)
        {
            denominatorPattern = tokens[denominatorEnd].Value;
            denominatorEnd++;
        }
        else
        {
            while (denominatorEnd < tokens.Count && tokens[denominatorEnd].Kind == TokenKind.Literal && tokens[denominatorEnd].Value is [>= '0' and <= '9'])
            {
                fixedDenominator.Append(tokens[denominatorEnd].Value);
                denominatorEnd++;
            }

            denominatorPattern = fixedDenominator.ToString();
        }

        var whole = hasInteger ? Math.Floor(value) : 0;
        var fraction = value - whole;
        long numerator;
        long denominator;
        if (fixedDenominator.Length > 0 && long.TryParse(denominatorPattern, NumberStyles.None, CultureInfo.InvariantCulture, out var fixedValue) && fixedValue > 0)
        {
            denominator = fixedValue;
            numerator = (long)Math.Round(fraction * denominator, MidpointRounding.AwayFromZero);
        }
        else
        {
            var limit = (long)Math.Pow(10, Math.Clamp(denominatorPattern.Length, 1, 4)) - 1;
            (numerator, denominator) = Approximate(fraction, limit);
        }

        if (hasInteger && numerator == denominator && fixedDenominator.Length == 0)
        {
            (whole, numerator) = (whole + 1, 0);
        }

        var numeratorWidth = tokens[numeratorAt].Value.Length;
        var integerPattern = hasInteger ? tokens[integerAt].Value : string.Empty;
        string integerText;
        string fractionText;
        if (numerator == 0)
        {
            integerText = hasInteger ? RenderNumber(whole, integerPattern.Contains('0') || whole != 0 ? integerPattern : "0", culture) : "0";
            fractionText = hasInteger ? new string(' ', numeratorWidth + 1 + denominatorPattern.Length) : string.Empty;
        }
        else
        {
            integerText = hasInteger ? (whole == 0 && !integerPattern.Contains('0') ? string.Empty : RenderNumber(whole, integerPattern, culture)) : string.Empty;
            fractionText = numerator.ToString(CultureInfo.InvariantCulture).PadLeft(numeratorWidth)
                + "/" + denominator.ToString(CultureInfo.InvariantCulture).PadRight(denominatorPattern.Length);
        }

        return Place(tokens, index =>
            index == integerAt && hasInteger ? integerText
            : index == numeratorAt ? (hasInteger || numerator != 0 ? fractionText : integerText)
            : index >= slash && index < denominatorEnd ? string.Empty
            : integerText.Length == 0 && hasInteger && index > integerAt && index < numeratorAt && tokens[index].Kind == TokenKind.Literal ? new string(' ', tokens[index].Value.Length)
            : null);
    }

    /// <summary>
    /// The fraction closest to <paramref name="value"/> (0 ≤ value ≤ 1 for a mixed number, any for an improper one)
    /// whose denominator is at most <paramref name="limit"/>: continued-fraction convergents and the last semiconvergent.
    /// </summary>
    internal static (long Numerator, long Denominator) Approximate(double value, long limit)
    {
        if (!double.IsFinite(value) || value > 1e15)
        {
            // Beyond a long's exact range a fraction part is meaningless; the whole number stands alone.
            return (double.IsFinite(value) ? (long)Math.Min(value, long.MaxValue) : 0, 1);
        }

        long p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        var rest = value;
        for (var step = 0; step < 64; step++)
        {
            var whole = (long)Math.Floor(rest);
            var q2 = q0 + (whole * q1);
            if (q2 > limit)
            {
                break;
            }

            (p0, q0, p1, q1) = (p1, q1, p0 + (whole * p1), q2);
            var fraction = rest - whole;
            if (fraction < 1e-12)
            {
                break;
            }

            rest = 1 / fraction;
        }

        if (q1 == 0)
        {
            return ((long)Math.Round(value), 1);
        }

        var k = (limit - q0) / q1;
        var (semiNumerator, semiDenominator) = (p0 + (k * p1), q0 + (k * q1));
        return Math.Abs(value - ((double)p1 / q1)) <= Math.Abs(value - ((double)semiNumerator / semiDenominator))
            ? (p1, q1)
            : (semiNumerator, semiDenominator);
    }

    private static bool IsGeneral(string? code) => string.IsNullOrEmpty(code) || code.Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>Excel's General: up to 10 significant digits, without trailing zeros.</summary>
    private static string General(double number, CultureInfo culture)
        => Math.Round(number, 10).ToString(Math.Abs(number) is >= 1e11 or (> 0 and < 1e-9) ? "0.#####E+00" : "G10", culture);

    /// <summary>A non-negative number with a digit pattern such as <c>#,##0.00</c> (grouping, scaling commas, 0 # ?).</summary>
    private static string RenderNumber(double value, string pattern, CultureInfo culture)
    {
        var point = pattern.IndexOf('.');
        var integerPart = point < 0 ? pattern : pattern[..point];
        var decimalPart = point < 0 ? string.Empty : pattern[(point + 1)..];

        // Commas right of the last integer placeholder divide by 1000 each; a comma between placeholders groups.
        var trimmed = integerPart.TrimEnd(',');
        var scale = integerPart.Length - trimmed.Length;
        var grouped = trimmed.Contains(',');
        integerPart = trimmed.Replace(",", string.Empty, StringComparison.Ordinal);
        decimalPart = decimalPart.Replace(",", string.Empty, StringComparison.Ordinal);

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
        return integerDigits + (point < 0 ? string.Empty : culture.NumberFormat.NumberDecimalSeparator + decimalText);
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

        // Excel rounds the time to the shown precision: whole seconds, or the fraction digits after ss (ss.00).
        bool IsFraction(int index) => tokens[index].Kind == TokenKind.Digits && tokens[index].Value.StartsWith('.')
            && index > 0 && tokens[index - 1].Kind == TokenKind.Date && Letter(tokens[index - 1]) == 's';
        var fractionDigits = Math.Min(3, Enumerable.Range(0, tokens.Count).Where(IsFraction)
            .Select(index => tokens[index].Value.Count(placeholder => placeholder == '0')).DefaultIfEmpty(0).Max());
        var totalSeconds = Math.Round(serial * 86400, fractionDigits, MidpointRounding.AwayFromZero);
        var date = new DateTime(1899, 12, 30).AddSeconds(totalSeconds);
        var twelveHour = tokens.Any(token => token.Kind == TokenKind.AmPm);
        var builder = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (IsFraction(index))
            {
                var fraction = totalSeconds - Math.Floor(totalSeconds);
                var digits = token.Value.Count(placeholder => placeholder == '0');
                builder.Append(culture.NumberFormat.NumberDecimalSeparator)
                    .Append(Math.Round(fraction * Math.Pow(10, digits)).ToString(new string('0', Math.Max(1, digits)), CultureInfo.InvariantCulture)[..digits]);
                continue;
            }

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
            if (part[0] == '[')
            {
                // Elapsed time: [h] counts hours past 24, [m] minutes, [s] seconds.
                var elapsed = Math.Floor(part[1] switch
                {
                    'h' => Math.Floor(totalSeconds) / 3600,
                    'm' => Math.Floor(totalSeconds) / 60,
                    _ => totalSeconds,
                });
                builder.Append(elapsed.ToString(new string('0', length - 2), culture));
                continue;
            }

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
        return before.Value is not null && Letter(before) == 'h' || after.Value is not null && Letter(after) == 's';
    }

    /// <summary>The unit letter of a date token, also of an elapsed one such as <c>[h]</c>.</summary>
    private static char Letter(Token token) => char.ToLowerInvariant(token.Value[0] == '[' ? token.Value[1] : token.Value[0]);

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
        Condition,
        Exponent,
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
                    if (inner.Length > 0 && char.ToLowerInvariant(inner[0]) is 'h' or 'm' or 's' && inner.All(letter => char.ToLowerInvariant(letter) == char.ToLowerInvariant(inner[0])))
                    {
                        tokens.Add(new(TokenKind.Date, "[" + inner + "]"));
                    }
                    else if (inner.Length > 0 && inner[0] is '<' or '>' or '=')
                    {
                        tokens.Add(new(TokenKind.Condition, inner));
                    }
                    else if (inner.StartsWith('$'))
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
                case 'E' or 'e' when index + 1 < section.Length && section[index + 1] is '+' or '-':
                    tokens.Add(new(TokenKind.Exponent, section.Substring(index, 2)));
                    index++;
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
