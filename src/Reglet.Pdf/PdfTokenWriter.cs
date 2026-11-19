using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// writes PDF tokens (names, numbers, strings, arrays, dictionaries) ASCII-encoded with
/// InvariantCulture and correct escaping.
/// </summary>
internal sealed class PdfTokenWriter
{
    private readonly Stream _stream;

    public PdfTokenWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public long Position => _stream.Position;

    /// <summary>
    /// active security handler (phase AD). If set, strings are encrypted with the current
    /// object number unless <see cref="SuppressEncryption"/> is set (the <c>/Encrypt</c>
    /// dictionary and the trailer <c>/ID</c> stay unencrypted).
    /// </summary>
    public PdfStandardSecurity? Security { get; set; }

    /// <summary>object number used for the per-object key.</summary>
    public int ObjectNumber { get; set; }

    /// <summary>Suppresses encryption for the current scope (default: no encryption).</summary>
    public bool SuppressEncryption { get; set; } = true;

    /// <summary>Writes raw ASCII characters (structural tokens).</summary>
    public void WriteRaw(string ascii)
    {
        ArgumentNullException.ThrowIfNull(ascii);
        foreach (var character in ascii)
        {
            _stream.WriteByte((byte)character);
        }
    }

    public void WriteInteger(long value)
        => WriteRaw(value.ToString(CultureInfo.InvariantCulture) + " ");

    public void WriteNumber(double value)
    {
        var text = value.ToString("0.#####", CultureInfo.InvariantCulture);
        if (text is "-0" or "")
        {
            text = "0";
        }

        WriteRaw(text + " ");
    }

    /// <summary>Writes a number from a foreign file without rounding it.</summary>
    public void WriteExactNumber(double value)
    {
        string text;
        if (!double.IsFinite(value))
        {
            text = "0";
        }
        else if (Math.Abs(value) >= 1e28)
        {
            text = value.ToString("F0", CultureInfo.InvariantCulture);
        }
        else
        {
            // PDF has no exponent notation.
            text = value.ToString("R", CultureInfo.InvariantCulture);
            if (text.Contains('E', StringComparison.Ordinal))
            {
                text = ((decimal)value).ToString(CultureInfo.InvariantCulture);
            }
        }

        WriteRaw(text + " ");
    }

    public void WriteBoolean(bool value) => WriteRaw(value ? "true " : "false ");

    /// <summary>Writes a PDF name (with <c>/</c> and <c>#XX</c> escaping).</summary>
    public void WriteName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        WriteNameBytes(Encoding.UTF8.GetBytes(name));
    }

    /// <summary>Writes a name read from a file, whose characters are the original bytes.</summary>
    public void WriteParsedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        WriteNameBytes(Encoding.Latin1.GetBytes(name));
    }

    private void WriteNameBytes(byte[] bytes)
    {
        _stream.WriteByte((byte)'/');
        foreach (var value in bytes)
        {
            if (IsRegularNameCharacter(value))
            {
                _stream.WriteByte(value);
            }
            else
            {
                WriteRaw("#" + value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        _stream.WriteByte((byte)' ');
    }

    /// <summary>Writes a literal string from Latin1 bytes with escape sequences.</summary>
    public void WriteLiteralString(ReadOnlySpan<byte> bytes)
    {
        if (TryEncrypt(bytes, out var encrypted))
        {
            WriteHexStringRaw(encrypted);
            return;
        }

        _stream.WriteByte((byte)'(');
        foreach (var value in bytes)
        {
            if (value is (byte)'(' or (byte)')' or (byte)'\\')
            {
                _stream.WriteByte((byte)'\\');
                _stream.WriteByte(value);
            }
            else if (value < 32 || value > 126)
            {
                _stream.WriteByte((byte)'\\');
                WriteRaw(Convert.ToString(value, 8).PadLeft(3, '0'));
            }
            else
            {
                _stream.WriteByte(value);
            }
        }

        _stream.WriteByte((byte)')');
        _stream.WriteByte((byte)' ');
    }

    /// <summary>Writes ASCII directly and other texts losslessly as a UTF-16BE PDF string.</summary>
    public void WriteLiteralString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.All(character => character is >= ' ' and <= '~'))
        {
            WriteLiteralString(Encoding.ASCII.GetBytes(value));
            return;
        }

        var encoded = Encoding.BigEndianUnicode.GetBytes(value);
        var bytes = new byte[encoded.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        encoded.CopyTo(bytes, 2);
        WriteHexString(bytes);
    }

    /// <summary>Writes a hex string from bytes.</summary>
    public void WriteHexString(ReadOnlySpan<byte> bytes)
    {
        if (TryEncrypt(bytes, out var encrypted))
        {
            WriteHexStringRaw(encrypted);
            return;
        }

        WriteHexStringRaw(bytes);
    }

    private void WriteHexStringRaw(ReadOnlySpan<byte> bytes)
    {
        _stream.WriteByte((byte)'<');
        foreach (var value in bytes)
        {
            WriteRaw(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        _stream.WriteByte((byte)'>');
        _stream.WriteByte((byte)' ');
    }

    /// <summary>Writes an indirect reference <c>N 0 R</c>.</summary>
    public void WriteReference(int objectNumber)
        => WriteRaw(objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 R ");

    public void WriteNull() => WriteRaw("null ");

    /// <summary>Writes an array <c>[ … ]</c>.</summary>
    public void WriteArray(Action<PdfTokenWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        _stream.WriteByte((byte)'[');
        body(this);
        _stream.WriteByte((byte)']');
    }

    /// <summary>Writes a dictionary <c>&lt;&lt; … &gt;&gt;</c>.</summary>
    public void WriteDictionary(Action<PdfTokenWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        WriteRaw("<<");
        body(this);
        WriteRaw(">>");
    }

    private static bool IsRegularNameCharacter(byte value)
        => value is > 32 and < 127
            && value is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%' or (byte)'#');

    private bool TryEncrypt(ReadOnlySpan<byte> bytes, out byte[] encrypted)
    {
        if (Security is { } security && !SuppressEncryption)
        {
            encrypted = security.Encrypt(bytes.ToArray(), ObjectNumber);
            return true;
        }

        encrypted = [];
        return false;
    }
}
