namespace Reglet;

/// <summary>
/// encodes text for the PDF default fonts in the WinAnsi character set (CP1252). Characters
/// outside WinAnsi are replaced with <c>?</c> (fallback behavior follows later).
/// </summary>
internal static class PdfWinAnsi
{
    private static readonly Dictionary<char, byte> Specials = new()
    {
        ['\u20AC'] = 0x80,
        ['\u201A'] = 0x82,
        ['\u0192'] = 0x83,
        ['\u201E'] = 0x84,
        ['\u2026'] = 0x85,
        ['\u2020'] = 0x86,
        ['\u2021'] = 0x87,
        ['\u02C6'] = 0x88,
        ['\u2030'] = 0x89,
        ['\u0160'] = 0x8A,
        ['\u2039'] = 0x8B,
        ['\u0152'] = 0x8C,
        ['\u017D'] = 0x8E,
        ['\u2018'] = 0x91,
        ['\u2019'] = 0x92,
        ['\u201C'] = 0x93,
        ['\u201D'] = 0x94,
        ['\u2022'] = 0x95,
        ['\u2013'] = 0x96,
        ['\u2014'] = 0x97,
        ['\u02DC'] = 0x98,
        ['\u2122'] = 0x99,
        ['\u0161'] = 0x9A,
        ['\u203A'] = 0x9B,
        ['\u0153'] = 0x9C,
        ['\u017E'] = 0x9E,
        ['\u0178'] = 0x9F,
    };

    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            bytes[i] = character switch
            {
                <= (char)0x7F => (byte)character,
                >= (char)0xA0 and <= (char)0xFF => (byte)character,
                _ => Specials.TryGetValue(character, out var mapped) ? mapped : (byte)'?',
            };
        }

        return bytes;
    }

    public static bool CanEncode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.All(character => character <= 0x7F
            || (character >= 0xA0 && character <= 0xFF)
            || Specials.ContainsKey(character));
    }

    /// <summary>Decodes WinAnsi bytes back to characters (for the PDF reader).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var builder = new System.Text.StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            builder.Append(DecodeTable[value]);
        }

        return builder.ToString();
    }

    private static readonly char[] DecodeTable = BuildDecodeTable();

    private static char[] BuildDecodeTable()
    {
        var table = new char[256];
        for (var index = 0; index < table.Length; index++)
        {
            table[index] = (char)index;
        }

        for (var index = 0x80; index <= 0x9F; index++)
        {
            table[index] = '\uFFFD';
        }

        foreach (var (character, value) in Specials)
        {
            table[value] = character;
        }

        return table;
    }
}
