using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Reglet;

/// <summary>
/// a minimal JSON reader for the mail merge (phase AJ, AJ03): an array of objects with scalar
/// values. The library uses no serialization library (zero dependency, AOT safety), so the
/// reader is written by hand.
/// </summary>
internal sealed class MergeJsonReader
{
    private static readonly Regex Number = new("^-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?\\z", RegexOptions.CultureInvariant);

    private readonly string _text;
    private int _index;

    private MergeJsonReader(string text)
    {
        _text = text;
    }

    /// <summary>
    /// reads the rows of a JSON array of objects. A value that is not a string is written as
    /// text (a number as it stands, a boolean as "true"/"false"), null stays null.
    /// </summary>
    /// <exception cref="InvalidDataException">The JSON is not well formed or is no array.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ReadRows(string text)
    {
        var reader = new MergeJsonReader(text);
        var rows = reader.ReadArray();
        reader.SkipWhitespace();
        return reader._index == text.Length ? rows : throw new InvalidDataException("The JSON data source is not well formed.");
    }

    private List<IReadOnlyDictionary<string, string?>> ReadArray()
    {
        SkipWhitespace();
        if (_index >= _text.Length || _text[_index] != '[')
        {
            throw new InvalidDataException("The JSON data source must be an array of objects.");
        }

        _index++;
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        SkipWhitespace();
        if (Peek() == ']')
        {
            _index++;
            return rows;
        }

        while (true)
        {
            rows.Add(ReadObject());
            SkipWhitespace();
            var next = Peek();
            if (next == ',')
            {
                _index++;
                continue;
            }

            if (next == ']')
            {
                _index++;
                return rows;
            }

            throw new InvalidDataException("The JSON data source is not well formed.");
        }
    }

    private Dictionary<string, string?> ReadObject()
    {
        SkipWhitespace();
        if (Peek() != '{')
        {
            throw new InvalidDataException("The JSON data source must be an array of objects.");
        }

        _index++;
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        SkipWhitespace();
        if (Peek() == '}')
        {
            _index++;
            return row;
        }

        while (true)
        {
            SkipWhitespace();
            var name = ReadString();
            SkipWhitespace();
            if (Peek() != ':')
            {
                throw new InvalidDataException("The JSON data source is not well formed.");
            }

            _index++;
            SkipWhitespace();
            row[name] = ReadValue();
            SkipWhitespace();
            var next = Peek();
            if (next == ',')
            {
                _index++;
                continue;
            }

            if (next == '}')
            {
                _index++;
                return row;
            }

            throw new InvalidDataException("The JSON data source is not well formed.");
        }
    }

    /// <summary>Reads a value; a nested object or array is kept as text.</summary>
    private string? ReadValue()
    {
        var character = Peek();
        switch (character)
        {
            case '"':
                return ReadString();
            case '{':
            case '[':
                return ReadComposite();
            default:
                return ReadLiteral();
        }
    }

    /// <summary>Reads a number, a boolean or null.</summary>
    private string? ReadLiteral()
    {
        var start = _index;
        while (_index < _text.Length && _text[_index] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n'))
        {
            _index++;
        }

        if (start == _index)
        {
            throw new InvalidDataException("The JSON data source is not well formed.");
        }

        var literal = _text[start.._index];
        if (literal is "true" or "false")
        {
            return literal;
        }

        if (literal == "null")
        {
            return null;
        }

        if (Number.IsMatch(literal))
        {
            return literal;
        }

        throw new InvalidDataException("The JSON data source is not well formed.");
    }

    /// <summary>Reads a nested object or array as text.</summary>
    private string ReadComposite()
    {
        var start = _index;
        var depth = 0;
        var inString = false;
        while (_index < _text.Length)
        {
            var character = _text[_index];
            if (inString)
            {
                if (character == '\\')
                {
                    _index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }
            }
            else if (character == '"')
            {
                inString = true;
            }
            else if (character is '{' or '[')
            {
                depth++;
            }
            else if (character is '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    _index++;
                    return _text[start.._index];
                }
            }

            _index++;
        }

        throw new InvalidDataException("The JSON data source is not well formed.");
    }

    /// <summary>Reads a string with its escape sequences.</summary>
    private string ReadString()
    {
        if (Peek() != '"')
        {
            throw new InvalidDataException("The JSON data source is not well formed.");
        }

        _index++;
        var builder = new StringBuilder();
        while (_index < _text.Length)
        {
            var character = _text[_index++];
            if (character == '"')
            {
                return builder.ToString();
            }

            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (_index >= _text.Length)
            {
                break;
            }

            var escape = _text[_index++];
            switch (escape)
            {
                case '"':
                    builder.Append('"');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '/':
                    builder.Append('/');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (_index + 4 > _text.Length
                        || !ushort.TryParse(
                            _text.AsSpan(_index, 4),
                            NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture,
                            out var code))
                    {
                        throw new InvalidDataException("The JSON data source is not well formed.");
                    }

                    builder.Append((char)code);
                    _index += 4;
                    break;
                default:
                    throw new InvalidDataException("The JSON data source is not well formed.");
            }
        }

        throw new InvalidDataException("The JSON data source is not well formed.");
    }

    /// <summary>The character at the current position; '\0' at the end.</summary>
    private char Peek() => _index < _text.Length ? _text[_index] : '\0';

    private void SkipWhitespace()
    {
        while (_index < _text.Length && _text[_index] is ' ' or '\t' or '\r' or '\n')
        {
            _index++;
        }
    }
}
