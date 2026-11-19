using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Werte des PDF-Objektmodells (Subset): Null, boolesche Werte, Zahlen, Strings, Namen,
/// arrays, dictionaries, indirect references and streams.
/// </summary>
internal abstract class PdfValue
{
    public static readonly PdfValue Null = new PdfNull();
}

internal sealed class PdfNull : PdfValue;

internal sealed class PdfBoolean(bool value) : PdfValue
{
    public bool Value { get; } = value;
}

internal sealed class PdfInteger(long value) : PdfValue
{
    public long Value { get; } = value;
}

internal sealed class PdfReal(double value) : PdfValue
{
    public double Value { get; } = value;
}

internal sealed class PdfString(byte[] bytes) : PdfValue
{
    public byte[] Bytes { get; } = bytes;
}

internal sealed class PdfName(string name) : PdfValue
{
    public string Name { get; } = name;
}

internal sealed class PdfArray(IReadOnlyList<PdfValue> items) : PdfValue
{
    public IReadOnlyList<PdfValue> Items { get; } = items;
}

internal sealed class PdfDictionary(IReadOnlyDictionary<string, PdfValue> entries) : PdfValue
{
    public IReadOnlyDictionary<string, PdfValue> Entries { get; } = entries;

    public PdfValue? Get(string key) => Entries.TryGetValue(key, out var value) ? value : null;
}

internal sealed class PdfReference(int objectNumber) : PdfValue
{
    public int ObjectNumber { get; } = objectNumber;
}

/// <summary>A stream: dictionary plus raw (still encoded) bytes.</summary>
internal sealed class PdfStreamValue(PdfDictionary dictionary, byte[] raw) : PdfValue
{
    public PdfDictionary Dictionary { get; } = dictionary;

    public byte[] Raw { get; } = raw;
}

internal enum PdfTokenKind
{
    Integer,
    Real,
    String,
    Name,
    ArrayOpen,
    ArrayClose,
    DictionaryOpen,
    DictionaryClose,
    Keyword,
    EndOfFile,
}

internal readonly record struct PdfToken(
    PdfTokenKind Kind,
    long Integer,
    double Real,
    byte[]? Bytes,
    string? Text,
    int End);

/// <summary>Splits PDF syntax into tokens. Arithmetic and object construction are handled by the parser.</summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;
    private int _position;

    public PdfLexer(byte[] data, int position = 0)
    {
        _data = data;
        _position = position;
    }

    public int Position => _position;

    public void Reset(int position) => _position = position;

    public PdfToken Next()
    {
        SkipWhitespace();
        if (_position >= _data.Length)
        {
            return new PdfToken(PdfTokenKind.EndOfFile, 0, 0, null, null, _position);
        }

        var value = _data[_position];
        switch (value)
        {
            case (byte)'[':
                _position++;
                return Simple(PdfTokenKind.ArrayOpen);
            case (byte)']':
                _position++;
                return Simple(PdfTokenKind.ArrayClose);
            case (byte)'<':
                if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'<')
                {
                    _position += 2;
                    return Simple(PdfTokenKind.DictionaryOpen);
                }

                return ReadHexString();
            case (byte)'>':
                if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'>')
                {
                    _position += 2;
                    return Simple(PdfTokenKind.DictionaryClose);
                }

                throw new DocumentValidationException("Unexpected '>' in the PDF file.");
            case (byte)'/':
                return ReadName();
            case (byte)'(':
                return ReadLiteralString();
            default:
                if (value is (byte)'+' or (byte)'-' or (byte)'.' or (>= (byte)'0' and <= (byte)'9'))
                {
                    return ReadNumber();
                }

                return ReadKeyword();
        }
    }

    private PdfToken Simple(PdfTokenKind kind) => new(kind, 0, 0, null, null, _position);

    private void SkipWhitespace()
    {
        while (_position < _data.Length)
        {
            var value = _data[_position];
            if (IsWhitespace(value))
            {
                _position++;
            }
            else if (value == (byte)'%')
            {
                while (_position < _data.Length && _data[_position] is not (13 or 10))
                {
                    _position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private PdfToken ReadNumber()
    {
        var start = _position;
        var isReal = false;
        while (_position < _data.Length)
        {
            var value = _data[_position];
            if (value is (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'-')
            {
                _position++;
            }
            else if (value == (byte)'.')
            {
                isReal = true;
                _position++;
            }
            else
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(_data, start, _position - start);
        if (!isReal && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return new PdfToken(PdfTokenKind.Integer, integer, 0, null, null, _position);
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return new PdfToken(PdfTokenKind.Real, 0, real, null, null, _position);
        }

        throw new DocumentValidationException($"Invalid PDF number '{text}'.");
    }

    private PdfToken ReadKeyword()
    {
        var start = _position;
        while (_position < _data.Length && !IsWhitespace(_data[_position]) && !IsDelimiter(_data[_position]))
        {
            _position++;
        }

        // A standalone separator ('{', '}', ')') would result in an empty keyword without
        // progress; callers that skip keywords would then loop forever.
        if (_position == start)
        {
            _position++;
        }

        var text = Encoding.ASCII.GetString(_data, start, _position - start);
        return new PdfToken(PdfTokenKind.Keyword, 0, 0, null, text, _position);
    }

    private PdfToken ReadName()
    {
        _position++; // '/'
        var builder = new StringBuilder();
        while (_position < _data.Length && !IsWhitespace(_data[_position]) && !IsDelimiter(_data[_position]))
        {
            var value = _data[_position];
            if (value == (byte)'#'
                && _position + 2 < _data.Length
                && TryHex(_data[_position + 1], out var high)
                && TryHex(_data[_position + 2], out var low))
            {
                builder.Append((char)((high << 4) | low));
                _position += 3;
            }
            else
            {
                builder.Append((char)value);
                _position++;
            }
        }

        return new PdfToken(PdfTokenKind.Name, 0, 0, null, builder.ToString(), _position);
    }

    private PdfToken ReadHexString()
    {
        _position++; // '<'
        var bytes = new List<byte>();
        var high = -1;
        while (_position < _data.Length)
        {
            var value = _data[_position];
            _position++;
            if (value == (byte)'>')
            {
                break;
            }

            if (IsWhitespace(value))
            {
                continue;
            }

            if (!TryHex(value, out var nibble))
            {
                throw new DocumentValidationException("Invalid character in PDF hex string.");
            }

            if (high < 0)
            {
                high = nibble;
            }
            else
            {
                bytes.Add((byte)((high << 4) | nibble));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return new PdfToken(PdfTokenKind.String, 0, 0, bytes.ToArray(), null, _position);
    }

    private PdfToken ReadLiteralString()
    {
        _position++; // '('
        var bytes = new List<byte>();
        var depth = 1;
        while (_position < _data.Length)
        {
            var value = _data[_position];
            _position++;

            if (value == (byte)'\\')
            {
                if (_position >= _data.Length)
                {
                    break;
                }

                var escaped = _data[_position];
                _position++;
                switch (escaped)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'(':
                    case (byte)')':
                    case (byte)'\\':
                        bytes.Add(escaped);
                        break;
                    case (>= (byte)'0' and <= (byte)'7'):
                        var octal = escaped - (byte)'0';
                        for (var index = 0; index < 2 && _position < _data.Length
                            && _data[_position] is >= (byte)'0' and <= (byte)'7'; index++)
                        {
                            octal = (octal * 8) + (_data[_position] - (byte)'0');
                            _position++;
                        }

                        bytes.Add((byte)octal);
                        break;
                    case 13:
                        if (_position < _data.Length && _data[_position] == 10)
                        {
                            _position++;
                        }

                        break;
                    case 10:
                        break;
                    default:
                        bytes.Add(escaped);
                        break;
                }
            }
            else if (value == (byte)'(')
            {
                depth++;
                bytes.Add(value);
            }
            else if (value == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                bytes.Add(value);
            }
            else
            {
                bytes.Add(value);
            }
        }

        return new PdfToken(PdfTokenKind.String, 0, 0, bytes.ToArray(), null, _position);
    }

    private static bool TryHex(byte value, out int nibble)
    {
        switch (value)
        {
            case >= (byte)'0' and <= (byte)'9':
                nibble = value - (byte)'0';
                return true;
            case >= (byte)'a' and <= (byte)'f':
                nibble = value - (byte)'a' + 10;
                return true;
            case >= (byte)'A' and <= (byte)'F':
                nibble = value - (byte)'A' + 10;
                return true;
            default:
                nibble = 0;
                return false;
        }
    }

    private static bool IsWhitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte value) => value is (byte)'(' or (byte)')' or (byte)'<'
        or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}

/// <summary>Recursive PDF object parser with two-token lookahead for <c>N G R</c> references.</summary>
internal sealed class PdfObjectParser(PdfLexer lexer)
{
    /// <summary>
    /// Maximum nesting of arrays and dictionaries. Real files stay far below it; a hostile
    /// file with millions of <c>[</c> would otherwise overflow the stack and terminate the process.
    /// </summary>
    public const int MaxNestingDepth = 256;

    private readonly List<PdfToken> _lookahead = [];
    private int _depth;

    public int Position => lexer.Position;

    public void ResetTo(int position)
    {
        _lookahead.Clear();
        lexer.Reset(position);
    }

    public PdfValue ParseValue()
    {
        if (_depth >= MaxNestingDepth)
        {
            throw new DocumentValidationException($"PDF arrays or dictionaries are nested deeper than {MaxNestingDepth} levels.");
        }

        _depth++;
        try
        {
            return ParseValueCore();
        }
        finally
        {
            _depth--;
        }
    }

    private PdfValue ParseValueCore()
    {
        var token = Peek(0);
        switch (token.Kind)
        {
            case PdfTokenKind.Integer:
                if (Peek(1).Kind == PdfTokenKind.Integer
                    && Peek(2).Kind == PdfTokenKind.Keyword
                    && Peek(2).Text == "R")
                {
                    if (token.Integer is < 0 or > int.MaxValue)
                    {
                        throw new DocumentValidationException($"Invalid PDF object number {token.Integer}.");
                    }

                    Advance();
                    Advance();
                    Advance();
                    return new PdfReference((int)token.Integer);
                }

                Advance();
                return new PdfInteger(token.Integer);
            case PdfTokenKind.Real:
                Advance();
                return new PdfReal(token.Real);
            case PdfTokenKind.String:
                Advance();
                return new PdfString(token.Bytes!);
            case PdfTokenKind.Name:
                Advance();
                return new PdfName(token.Text!);
            case PdfTokenKind.ArrayOpen:
                Advance();
                var items = new List<PdfValue>();
                while (Peek(0).Kind != PdfTokenKind.ArrayClose)
                {
                    if (Peek(0).Kind == PdfTokenKind.EndOfFile)
                    {
                        throw new DocumentValidationException("PDF array is not closed.");
                    }

                    items.Add(ParseValue());
                }

                Advance();
                return new PdfArray(items);
            case PdfTokenKind.DictionaryOpen:
                Advance();
                var entries = new Dictionary<string, PdfValue>(StringComparer.Ordinal);
                while (Peek(0).Kind != PdfTokenKind.DictionaryClose)
                {
                    if (Peek(0).Kind == PdfTokenKind.EndOfFile)
                    {
                        throw new DocumentValidationException("PDF dictionary is not closed.");
                    }

                    if (ParseValue() is not PdfName key)
                    {
                        throw new DocumentValidationException("PDF dictionary key must be a name.");
                    }

                    entries[key.Name] = ParseValue();
                }

                Advance();
                return new PdfDictionary(entries);
            case PdfTokenKind.Keyword when token.Text == "true":
                Advance();
                return new PdfBoolean(true);
            case PdfTokenKind.Keyword when token.Text == "false":
                Advance();
                return new PdfBoolean(false);
            case PdfTokenKind.Keyword when token.Text == "null":
                Advance();
                return PdfValue.Null;
            default:
                throw new DocumentValidationException($"Unexpected PDF token '{token.Text ?? token.Kind.ToString()}'.");
        }
    }

    public PdfToken Peek(int index)
    {
        while (_lookahead.Count <= index)
        {
            _lookahead.Add(lexer.Next());
        }

        return _lookahead[index];
    }

    public PdfToken Advance()
    {
        var token = Peek(0);
        _lookahead.RemoveAt(0);
        return token;
    }
}

/// <summary>
/// Decodes PDF text strings (ISO 32000-1, 7.9.2.2): UTF-16BE with byte order mark, UTF-8 with
/// byte order mark (PDF 2.0), otherwise PDFDocEncoding (which differs from Latin-1 in
/// 0x18–0x1F and 0x80–0xA0, e.g. bullets, dashes and typographic quotes).
/// </summary>
internal static class PdfTextString
{
    private static readonly string HighTable =
        "•†‡…—–ƒ⁄‹›−‰„“”‘"
        + "’‚™ﬁﬂŁŒŠŸŽıłœšž�€";

    private static readonly string LowTable = "˘ˇˆ˙˝˛˚˜";

    /// <summary>The bytes <see cref="PdfTokenWriter.WriteLiteralString(string)"/> writes for a text string.</summary>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.All(character => character is >= ' ' and <= '~'))
        {
            return Encoding.ASCII.GetBytes(text);
        }

        return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
    }

    /// <summary>
    /// a UTF-16BE text string with byte order mark, the safe encoding for arbitrary Unicode
    /// (used for annotation entries, OF-F01).
    /// </summary>
    public static byte[] ToUtf16Be(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
    }

    /// <summary>
    /// A URI as 7-bit ASCII (ISO 32000-1, 12.6.4.7): IDN host names become punycode, other
    /// non-ASCII characters are percent-encoded as UTF-8.
    /// </summary>
    public static string ToAsciiUri(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            target = uri.AbsoluteUri;
            if (!string.IsNullOrEmpty(uri.Host) && uri.IdnHost != uri.Host)
            {
                target = new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
            }
        }

        var builder = new StringBuilder(target.Length);
        foreach (var value in Encoding.UTF8.GetBytes(target))
        {
            if (value is > 32 and < 127)
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The text in PDFDocEncoding, or null when a character is outside it.</summary>
    public static byte[]? EncodePdfDoc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            if (!PdfDocBytes.Value.TryGetValue(text[index], out bytes[index]))
            {
                return null;
            }
        }

        return bytes;
    }

    private static readonly Lazy<Dictionary<char, byte>> PdfDocBytes = new(() =>
    {
        var map = new Dictionary<char, byte>();
        for (var value = 0; value < 256; value++)
        {
            // 0x7F, 0x9F and 0xAD are undefined in PDFDocEncoding.
            if (value is not (0x7F or 0x9F or 0xAD))
            {
                map[value switch
                {
                    >= 0x18 and <= 0x1F => LowTable[value - 0x18],
                    >= 0x80 and <= 0xA0 => HighTable[value - 0x80],
                    _ => (char)value,
                }] = (byte)value;
            }
        }

        return map;
    });

    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, (bytes.Length - 2) & ~1);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        var builder = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            builder.Append(value switch
            {
                >= 0x18 and <= 0x1F => LowTable[value - 0x18],
                >= 0x80 and <= 0xA0 => HighTable[value - 0x80],
                _ => (char)value,
            });
        }

        return builder.ToString();
    }
}
