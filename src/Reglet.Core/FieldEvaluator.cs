using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// calculates the fields of a document (phase AJ, AJ02). Supported field codes:
/// <c>IF</c>, <c>MERGEFIELD</c>, <c>SEQ</c>, <c>REF</c>, <c>DOCPROPERTY</c>,
/// <c>DOCVARIABLE</c>, <c>PAGE</c>, <c>NUMPAGES</c>, <c>DATE</c>, <c>TIME</c> and
/// <c>QUOTE</c>. Nested fields (a field code in braces) are calculated first.
///
/// <para>
/// The calculation follows the rules of Word: names are compared case-insensitively, the
/// comparison of <c>IF</c> is numeric when both operands are numbers and otherwise a
/// case-insensitive text comparison. A field whose value cannot be resolved (an unknown
/// name, a missing page number) returns null; the caller decides whether to report it or to
/// keep the cached result.
/// </para>
/// </summary>
public static class FieldEvaluator
{
    /// <summary>Maximum depth of nested fields; a deeper field cannot be calculated.</summary>
    private const int MaxNestingDepth = 64;

    /// <summary>Calculates a field.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="context"/> is null.</exception>
    public static string? Evaluate(Field field, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(field.Instruction, context);
    }

    /// <summary>Calculates a field code.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="instruction"/> or <paramref name="context"/> is null.</exception>
    public static string? Evaluate(string instruction, FieldContext context)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(context);

        return Evaluate(instruction, context, 0);
    }

    private static string? Evaluate(string instruction, FieldContext context, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            return null;
        }

        // A field code that consists of nothing but a nested field is that field.
        var trimmed = instruction.Trim();
        if (trimmed.Length > 1 && trimmed[0] == '{' && trimmed[^1] == '}' && FindClosingBrace(trimmed, 0) == trimmed.Length - 1)
        {
            return Evaluate(trimmed[1..^1], context, depth + 1);
        }

        var (text, literal) = ExpandNested(instruction, context, depth);
        var tokens = Tokenize(text, literal);
        if (tokens.Count == 0)
        {
            return null;
        }

        var command = tokens[0].Text.ToUpperInvariant();
        switch (command)
        {
            case "IF":
                return EvaluateIf(tokens, context);
            case "MERGEFIELD":
                return EvaluateLookup(tokens, context.MergeValues);
            case "DOCPROPERTY":
                return EvaluateLookup(tokens, context.Properties);
            case "DOCVARIABLE":
                return EvaluateLookup(tokens, context.Variables);
            case "REF":
                return EvaluateLookup(tokens, context.Bookmarks);
            case "SEQ":
                return EvaluateSequence(tokens, context);
            case "PAGE":
                return context.PageNumber?.ToString(CultureInfo.InvariantCulture);
            case "NUMPAGES":
                return context.TotalPages?.ToString(CultureInfo.InvariantCulture);
            case "DATE":
            case "TIME":
                return EvaluateDate(tokens, context.Now, context);
            case "SECTIONPAGES":
                return context.SectionPages?.ToString(CultureInfo.InvariantCulture);
            case "CREATEDATE":
                return context.Created is { } created ? EvaluateDate(tokens, created, context) : null;
            case "SAVEDATE":
                return context.Saved is { } saved ? EvaluateDate(tokens, saved, context) : null;
            case "PRINTDATE":
                return context.Printed is { } printed ? EvaluateDate(tokens, printed, context) : null;
            case "QUOTE":
                return string.Join(' ', tokens.Skip(1).Select(token => token.Text));
            default:
                return null;
        }
    }

    /// <summary>Calculates a field code and reports whether a value was found.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="instruction"/> or <paramref name="context"/> is null.</exception>
    public static bool TryEvaluate(string instruction, FieldContext context, out string? value)
    {
        value = Evaluate(instruction, context);
        return value is not null;
    }

    /// <summary>
    /// the value of a name (case-insensitive), or null when the name is not set.
    /// </summary>
    private static string? EvaluateLookup(
        IReadOnlyList<Token> tokens,
        IReadOnlyDictionary<string, string> values)
    {
        if (tokens.Count < 2)
        {
            return null;
        }

        var name = tokens[1].Text;
        var value = Lookup(values, name);
        if (value is null)
        {
            return null;
        }

        return ApplyTextSwitch(value, tokens);
    }

    /// <summary>
    /// the <c>IF</c> field: <c>IF left operator right true-text false-text</c>. The texts are
    /// optional; without them the result is empty.
    /// </summary>
    private static string? EvaluateIf(IReadOnlyList<Token> tokens, FieldContext context)
    {
        if (tokens.Count < 4)
        {
            return null;
        }

        var left = tokens[1].Text;
        var op = tokens[2].Text;
        var right = tokens[3].Text;

        if (!Compare(left, op, right))
        {
            return tokens.Count > 5 ? tokens[5].Text : string.Empty;
        }

        return tokens.Count > 4 ? tokens[4].Text : string.Empty;
    }

    /// <summary>
    /// the <c>SEQ</c> field: <c>SEQ name [\* format] [\r number] [\c]</c>. The counter belongs
    /// to the context, so the sequence continues over several fields.
    /// </summary>
    private static string? EvaluateSequence(IReadOnlyList<Token> tokens, FieldContext context)
    {
        if (tokens.Count < 2)
        {
            return null;
        }

        var name = tokens[1].Text;
        var repeat = false;
        var reset = (int?)null;

        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i].Text;
            if (token.Equals("\\*", StringComparison.Ordinal) && i + 1 < tokens.Count)
            {
                continue;
            }

            if (token.Equals("\\r", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count &&
                int.TryParse(tokens[i + 1].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
            {
                reset = start;
                i++;
                continue;
            }

            if (token.Equals("\\c", StringComparison.OrdinalIgnoreCase))
            {
                repeat = true;
            }
        }

        int value;
        if (repeat)
        {
            value = context.PeekSequenceValue(name);
        }
        else if (reset is { } start)
        {
            value = context.ResetSequenceValue(name, start);
        }
        else
        {
            value = context.NextSequenceValue(name);
        }

        var format = SwitchValue(tokens, "\\*");
        return format is null ? value.ToString(CultureInfo.InvariantCulture) : FormatNumber(value, format);
    }

    /// <summary>
    /// the date fields: the value is the current date, the picture comes from the
    /// <c>\@</c> switch (for example <c>DATE \@ "dd.MM.yyyy"</c>).
    /// </summary>
    private static string? EvaluateDate(IReadOnlyList<Token> tokens, DateTimeOffset? value, FieldContext context)
    {
        var now = value ?? context.Now ?? DateTimeOffset.Now;
        var picture = SwitchValue(tokens, "\\@");
        return picture is null
            ? now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : FormatDate(picture, now);
    }

    /// <summary>compares two operands as in Word: numeric when both are numbers, otherwise text.</summary>
    private static bool Compare(string left, string op, string right)
    {
        var leftIsNumber = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftValue);
        var rightIsNumber = double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightValue);

        int comparison;
        if (leftIsNumber && rightIsNumber)
        {
            comparison = leftValue.CompareTo(rightValue);
        }
        else
        {
            comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return op switch
        {
            "=" or "==" => comparison == 0,
            "<>" or "!=" => comparison != 0,
            "<" => comparison < 0,
            ">" => comparison > 0,
            "<=" => comparison <= 0,
            ">=" => comparison >= 0,
            _ => false,
        };
    }

    /// <summary>the value of a switch (the token after the switch marker), or null.</summary>
    private static string? SwitchValue(IReadOnlyList<Token> tokens, string marker)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Text.Equals(marker, StringComparison.Ordinal))
            {
                return tokens[i + 1].Text;
            }
        }

        return null;
    }

    /// <summary>applies the text format switch (<c>\* Upper</c>, <c>\* Lower</c>, …).</summary>
    private static string ApplyTextSwitch(string value, IReadOnlyList<Token> tokens)
    {
        var format = SwitchValue(tokens, "\\*");
        return format?.ToUpperInvariant() switch
        {
            "UPPER" => value.ToUpperInvariant(),
            "LOWER" => value.ToLowerInvariant(),
            "CAPS" => FieldCasing.Caps(value),
            "FIRSTCAP" => FieldCasing.FirstCap(value),
            _ => value,
        };
    }

    /// <summary>formats a number as the <c>SEQ</c> format switch demands.</summary>
    private static string FormatNumber(int value, string format) => format.ToUpperInvariant() switch
    {
        "ROMAN" => ToRoman(value),
        "ROMANL" or "ROMANLOWER" => ToRoman(value).ToLowerInvariant(),
        "ALPHABETIC" => ToAlphabetic(value),
        "ALPHABETICLOWER" => ToAlphabetic(value).ToLowerInvariant(),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>the roman number of a value (1 = I, 4 = IV).</summary>
    private static string ToRoman(int value)
    {
        if (value is <= 0 or > 3999)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        (int Number, string Symbol)[] table =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];

        var builder = new StringBuilder();
        var rest = value;
        foreach (var (number, symbol) in table)
        {
            while (rest >= number)
            {
                builder.Append(symbol);
                rest -= number;
            }
        }

        return builder.ToString();
    }

    /// <summary>the alphabetic number of a value (1 = a, 26 = z, 27 = aa).</summary>
    private static string ToAlphabetic(int value)
    {
        if (value <= 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        var rest = value;
        while (rest > 0)
        {
            rest--;
            builder.Insert(0, (char)('a' + (rest % 26)));
            rest /= 26;
        }

        return builder.ToString();
    }

    /// <summary>
    /// formats a date with a Word date picture (<c>yyyy</c>, <c>MM</c>, <c>dd</c>, <c>HH</c>,
    /// <c>mm</c>, <c>ss</c>, <c>MMM</c>, <c>MMMM</c>, <c>ddd</c>, <c>dddd</c>, <c>t</c>).
    /// </summary>
    internal static string FormatDate(string picture, DateTimeOffset value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < picture.Length; i++)
        {
            var c = picture[i];
            if (c == '\'')
            {
                // Text in apostrophes is literal, for example 'Uhr'.
                var close = picture.IndexOf('\'', i + 1);
                close = close < 0 ? picture.Length : close;
                builder.Append(picture, i + 1, close - i - 1);
                i = close;
                continue;
            }

            if (!IsDatePictureChar(c))
            {
                builder.Append(c);
                continue;
            }

            var length = 1;
            while (i + length < picture.Length && picture[i + length] == c)
            {
                length++;
            }

            i += length - 1;
            builder.Append(FormatDatePart(c, length, value));
        }

        return builder.ToString();
    }

    private static bool IsDatePictureChar(char c)
        => c is 'y' or 'M' or 'd' or 'H' or 'h' or 'm' or 's' or 't';

    // A lone letter is a .NET standard format ("d" = short date, "H" = error); "%" makes it custom.
    private static string FormatDatePart(char c, int length, DateTimeOffset value) => c switch
    {
        'y' => length >= 3
            ? value.ToString("yyyy", CultureInfo.InvariantCulture)
            : value.ToString("yy", CultureInfo.InvariantCulture),
        'M' => length switch
        {
            >= 4 => value.ToString("MMMM", CultureInfo.InvariantCulture),
            3 => value.ToString("MMM", CultureInfo.InvariantCulture),
            2 => value.ToString("MM", CultureInfo.InvariantCulture),
            _ => value.ToString("%M", CultureInfo.InvariantCulture),
        },
        'd' => length switch
        {
            >= 4 => value.ToString("dddd", CultureInfo.InvariantCulture),
            3 => value.ToString("ddd", CultureInfo.InvariantCulture),
            2 => value.ToString("dd", CultureInfo.InvariantCulture),
            _ => value.ToString("%d", CultureInfo.InvariantCulture),
        },
        'H' => length >= 2
            ? value.ToString("HH", CultureInfo.InvariantCulture)
            : value.ToString("%H", CultureInfo.InvariantCulture),
        'h' => length >= 2
            ? value.ToString("hh", CultureInfo.InvariantCulture)
            : value.ToString("%h", CultureInfo.InvariantCulture),
        'm' => length >= 2
            ? value.ToString("mm", CultureInfo.InvariantCulture)
            : value.ToString("%m", CultureInfo.InvariantCulture),
        's' => length >= 2
            ? value.ToString("ss", CultureInfo.InvariantCulture)
            : value.ToString("%s", CultureInfo.InvariantCulture),
        't' => value.Hour < 12 ? "AM" : "PM",
        _ => string.Empty,
    };

    /// <summary>
    /// replaces the nested fields (a field code in braces) with their calculated value. The
    /// mask marks the inserted characters: a value is data, its quotes and spaces are no syntax.
    /// </summary>
    private static (string Text, bool[]? Literal) ExpandNested(string instruction, FieldContext context, int depth)
    {
        if (!instruction.Contains('{', StringComparison.Ordinal))
        {
            return (instruction, null);
        }

        var values = new List<(int Start, int Length)>();
        var builder = new StringBuilder();
        var index = 0;
        while (index < instruction.Length)
        {
            var open = instruction.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(instruction, index, instruction.Length - index);
                break;
            }

            builder.Append(instruction, index, open - index);

            var close = FindClosingBrace(instruction, open);
            if (close < 0)
            {
                // An unbalanced brace: the rest is literal text.
                builder.Append(instruction, open, instruction.Length - open);
                break;
            }

            var inner = instruction[(open + 1)..close];
            var value = Evaluate(inner, context, depth + 1) ?? string.Empty;
            values.Add((builder.Length, value.Length));
            builder.Append(value);
            index = close + 1;
        }

        var literal = new bool[builder.Length];
        foreach (var (start, length) in values)
        {
            Array.Fill(literal, true, start, length);
        }

        return (builder.ToString(), literal);
    }

    /// <summary>the position of the brace that closes the one at <paramref name="open"/>.</summary>
    private static int FindClosingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// splits a field code into tokens. A quoted text stays one token; the quotes are removed
    /// for the value, but a token keeps the information whether it was quoted.
    /// </summary>
    private static List<Token> Tokenize(string text, bool[]? literal)
    {
        bool IsSpace(int position) => char.IsWhiteSpace(text[position]) && literal?[position] != true;
        bool IsQuote(int position) => text[position] == '"' && literal?[position] != true;

        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && IsSpace(index))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            if (IsQuote(index))
            {
                var close = index + 1;
                while (close < text.Length && !IsQuote(close))
                {
                    close++;
                }

                if (close >= text.Length)
                {
                    tokens.Add(new Token(text[index..], false));
                    break;
                }

                tokens.Add(new Token(text[(index + 1)..close], true));
                index = close + 1;
                continue;
            }

            var start = index;
            while (index < text.Length && !IsSpace(index))
            {
                index++;
            }

            tokens.Add(new Token(text[start..index], false));
        }

        return tokens;
    }

    /// <summary>the value of a name in a dictionary (case-insensitive), or null.</summary>
    private static string? Lookup(IReadOnlyDictionary<string, string> values, string name)
    {
        if (values.TryGetValue(name, out var value))
        {
            return value;
        }

        foreach (var (key, candidate) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>a token of a field code.</summary>
    private readonly record struct Token(string Text, bool Quoted);
}
