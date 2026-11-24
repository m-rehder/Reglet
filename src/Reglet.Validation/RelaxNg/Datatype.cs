using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Reglet.RelaxNg;

/// <summary>
/// A datatype of a RELAX NG schema: the built-in <c>string</c> and <c>token</c>, and the XML Schema datatypes the ODF
/// schemas use, with the facets <c>pattern</c>, <c>length</c>, <c>minLength</c>, <c>maxLength</c> and the numeric
/// bounds. shortcut: ID uniqueness and IDREF targets are not checked (RELAX NG DTD compatibility); add it when needed.
/// </summary>
internal sealed class Datatype
{
    private const string XsdLibrary = "http://www.w3.org/2001/XMLSchema-datatypes";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Dictionary<string, Regex> Regexes = new(StringComparer.Ordinal);

    private static readonly Regex NcName = new(@"^[\p{L}_][\p{L}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Lm}._\-·]*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Language = new("^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Integer = new("^[+-]?[0-9]+$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Decimal = new(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Zone = new("(Z|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Date = new("^-?[0-9]{4,}-[0-9]{2}-[0-9]{2}(Z|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Time = new(@"^[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex DateTime = new(@"^-?[0-9]{4,}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|[+-][0-9]{2}:[0-9]{2})?$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Duration = new(@"^-?P(?=[0-9T])([0-9]+Y)?([0-9]+M)?([0-9]+D)?(T(?=[0-9])([0-9]+H)?([0-9]+M)?([0-9]+(\.[0-9]+)?S)?)?$", RegexOptions.CultureInvariant, RegexTimeout);

    private readonly string _type;
    private readonly IReadOnlyList<(string Name, string Value)> _parameters;
    private readonly Regex[] _patterns;

    private Datatype(string library, string type, IReadOnlyList<(string Name, string Value)> parameters)
    {
        _type = library == XsdLibrary ? type : type == "string" ? "builtin-string" : "builtin-token";
        _parameters = parameters;
        _patterns = parameters.Where(parameter => parameter.Name == "pattern").Select(parameter => Pattern(parameter.Value)).ToArray();
        Key = _type + "|" + string.Join("|", parameters.Select(parameter => parameter.Name + "=" + parameter.Value));
    }

    /// <summary>A key equal for equal datatypes (type and parameters).</summary>
    public string Key { get; }

    /// <exception cref="NotSupportedException">The library or type is not supported.</exception>
    public static Datatype Create(string library, string type, IReadOnlyList<(string Name, string Value)> parameters)
    {
        if (library is not ("" or XsdLibrary) || (library == "" && type is not ("string" or "token")))
        {
            throw new NotSupportedException($"The datatype {library}#{type} is not supported.");
        }

        return new Datatype(library, type, parameters);
    }

    /// <summary>Whether the value is in the lexical space (and within the facets).</summary>
    public bool Allows(string value)
    {
        var text = _type is "string" or "builtin-string" ? value : Collapse(value);
        if (!Lexical(text))
        {
            return false;
        }

        foreach (var regex in _patterns)
        {
            if (!regex.IsMatch(text))
            {
                return false;
            }
        }

        foreach (var (name, parameter) in _parameters)
        {
            var ok = name switch
            {
                "length" => text.Length == Int(parameter),
                "minLength" => text.Length >= Int(parameter),
                "maxLength" => text.Length <= Int(parameter),
                "minInclusive" => Number(text) >= Number(parameter),
                "maxInclusive" => Number(text) <= Number(parameter),
                "minExclusive" => Number(text) > Number(parameter),
                "maxExclusive" => Number(text) < Number(parameter),
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a value equals the schema's value, compared in this datatype's value space.</summary>
    public bool Equal(string schemaValue, string value) => _type switch
    {
        "string" or "builtin-string" => schemaValue == value,
        "double" or "decimal" or "integer" or "nonNegativeInteger" or "positiveInteger"
            => Allows(value) && Number(Collapse(value)) == Number(Collapse(schemaValue)),
        _ => Collapse(schemaValue) == Collapse(value),
    };

    private bool Lexical(string text) => _type switch
    {
        "string" or "builtin-string" or "builtin-token" or "token" or "anyURI" => true,
        "NCName" or "ID" or "IDREF" => NcName.IsMatch(text),
        "IDREFS" => text.Length > 0 && text.Split(' ').All(NcName.IsMatch),
        "QName" => text.Split(':') is { Length: 1 or 2 } parts && parts.All(NcName.IsMatch),
        "language" => Language.IsMatch(text),
        "integer" => Integer.IsMatch(text),
        "nonNegativeInteger" => Integer.IsMatch(text) && Number(text) >= 0,
        "positiveInteger" => Integer.IsMatch(text) && Number(text) > 0,
        "decimal" => Decimal.IsMatch(text),
        "double" => text is "INF" or "-INF" or "NaN" || (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !text.Contains(',')),
        "boolean" => text is "true" or "false" or "1" or "0",
        "date" => Date.IsMatch(text),
        "time" => Time.IsMatch(text),
        "dateTime" => DateTime.IsMatch(text),
        "duration" => Duration.IsMatch(text),
        "base64Binary" => IsBase64(text),
        "hexBinary" => text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit),
        _ => throw new NotSupportedException($"The XML Schema datatype {_type} is not supported."),
    };

    private static bool IsBase64(string text)
    {
        var compact = new string(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
        return compact.Length % 4 == 0 && Convert.TryFromBase64String(compact, new byte[compact.Length], out _);
    }

    private static double Number(string text)
        => text switch
        {
            "INF" => double.PositiveInfinity,
            "-INF" => double.NegativeInfinity,
            _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN,
        };

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    internal static string Collapse(string value) => string.Join(' ', value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// An XML Schema regular expression as a .NET one: the whole value must match, <c>^</c> and <c>$</c> are literals,
    /// and <c>\i</c>/<c>\c</c> (XML name start and name characters) become classes.
    /// </summary>
    private static Regex Pattern(string expression)
    {
        lock (Regexes)
        {
            if (Regexes.TryGetValue(expression, out var cached))
            {
                return cached;
            }

            const string nameStart = @"_:A-Za-zÀ-ÖØ-öø-˿Ͱ-ͽͿ-῿‌-‍⁰-↏Ⰰ-⿯、-퟿豈-﷏ﷰ-�";
            const string nameChar = nameStart + @"\-.0-9·̀-ͯ‿-⁀";
            var builder = new StringBuilder("^(?:");
            var inClass = 0;
            for (var index = 0; index < expression.Length; index++)
            {
                var character = expression[index];
                if (character == '\\' && index + 1 < expression.Length)
                {
                    var next = expression[++index];
                    builder.Append(next switch
                    {
                        'i' => inClass > 0 ? nameStart : "[" + nameStart + "]",
                        'I' => inClass > 0 ? throw new NotSupportedException(@"\I inside a class") : "[^" + nameStart + "]",
                        'c' => inClass > 0 ? nameChar : "[" + nameChar + "]",
                        'C' => inClass > 0 ? throw new NotSupportedException(@"\C inside a class") : "[^" + nameChar + "]",
                        _ => "\\" + next,
                    });
                    continue;
                }

                if (character == '[')
                {
                    inClass++;
                }
                else if (character == ']' && inClass > 0)
                {
                    inClass--;
                }
                else if (inClass == 0 && character is '^' or '$')
                {
                    builder.Append('\\');
                }

                builder.Append(character);
            }

            builder.Append(")$");
            var regex = new Regex(builder.ToString(), RegexOptions.CultureInvariant, RegexTimeout);
            Regexes.Add(expression, regex);
            return regex;
        }
    }
}
