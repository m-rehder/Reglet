using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// a minimal BER/DER reader for the CMS structures of a PDF signature (the counterpart of
/// <see cref="PdfDer"/>). The library uses no ASN.1 library (zero dependency, AOT safety). Definite
/// lengths and the indefinite length of BER (some signers write it) are read; tags above 30 are not.
/// </summary>
internal readonly struct DerNode
{
    private const int MaxDepth = 32;

    private DerNode(byte[] buffer, int start, int contentStart, int contentEnd, int end)
    {
        Buffer = buffer;
        Start = start;
        ContentStart = contentStart;
        ContentEnd = contentEnd;
        End = end;
    }

    public byte[] Buffer { get; }

    /// <summary>The offset of the tag.</summary>
    public int Start { get; }

    public int ContentStart { get; }

    public int ContentEnd { get; }

    /// <summary>The offset after the element (including the end marker of an indefinite length).</summary>
    public int End { get; }

    public byte Tag => Buffer[Start];

    public bool IsConstructed => (Tag & 0x20) != 0;

    public ReadOnlySpan<byte> Content => Buffer.AsSpan(ContentStart, ContentEnd - ContentStart);

    /// <summary>The whole encoding (tag, length and content) as a copy.</summary>
    public byte[] Raw => Buffer.AsSpan(Start, End - Start).ToArray();

    /// <summary>reads one element at <paramref name="offset"/>.</summary>
    /// <exception cref="DocumentValidationException">The structure is truncated or malformed.</exception>
    public static DerNode Parse(byte[] buffer, int offset, int limit) => Parse(buffer, offset, limit, 0);

    /// <summary>The elements inside a constructed element.</summary>
    public List<DerNode> Children()
    {
        var result = new List<DerNode>();
        var position = ContentStart;
        while (position < ContentEnd)
        {
            var child = Parse(Buffer, position, ContentEnd, 1);
            result.Add(child);
            position = child.End;
        }

        return result;
    }

    /// <summary>The bytes of an OCTET STRING (a constructed one is joined from its segments).</summary>
    public byte[] OctetValue()
    {
        if (!IsConstructed)
        {
            return Content.ToArray();
        }

        using var joined = new MemoryStream();
        foreach (var child in Children())
        {
            joined.Write(child.OctetValue());
        }

        return joined.ToArray();
    }

    /// <summary>The dotted form of an OBJECT IDENTIFIER.</summary>
    public string OidValue()
    {
        var content = Content;
        if (Tag != 0x06 || content.Length == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append(content[0] / 40).Append('.').Append(content[0] % 40);
        long value = 0;
        for (var index = 1; index < content.Length; index++)
        {
            value = (value << 7) | (uint)(content[index] & 0x7F);
            if ((content[index] & 0x80) == 0)
            {
                text.Append('.').Append(value.ToString(CultureInfo.InvariantCulture));
                value = 0;
            }
            else if (value > long.MaxValue >> 8)
            {
                return string.Empty;
            }
        }

        return text.ToString();
    }

    /// <summary>The value of a small INTEGER (version, salt length).</summary>
    public long IntegerValue()
    {
        long value = 0;
        foreach (var next in Content[..Math.Min(Content.Length, 8)])
        {
            value = (value << 8) | next;
        }

        return value;
    }

    /// <summary>The time of a UTCTime or GeneralizedTime, or null for another value.</summary>
    public DateTimeOffset? TimeValue()
    {
        var text = Encoding.ASCII.GetString(Content);
        string[] formats = Tag == 0x17
            ? ["yyMMddHHmmss'Z'", "yyMMddHHmm'Z'"]
            : ["yyyyMMddHHmmss'Z'", "yyyyMMddHHmmss.FFFFFFF'Z'"];
        return DateTimeOffset.TryParseExact(
            text, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
    }

    private static DerNode Parse(byte[] buffer, int offset, int limit, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new DocumentValidationException("PDF signature: the structure is nested too deeply.");
        }

        if (offset + 2 > limit)
        {
            throw new DocumentValidationException("PDF signature: the structure ends unexpectedly.");
        }

        if ((buffer[offset] & 0x1F) == 0x1F)
        {
            throw new DocumentValidationException("PDF signature: a long tag form is not supported.");
        }

        var position = offset + 1;
        int first = buffer[position++];
        if (first == 0x80)
        {
            // Indefinite length: the children follow up to the end marker 00 00.
            if ((buffer[offset] & 0x20) == 0)
            {
                throw new DocumentValidationException("PDF signature: an indefinite length on a primitive element.");
            }

            var contentStart = position;
            while (true)
            {
                if (position + 2 > limit)
                {
                    throw new DocumentValidationException("PDF signature: an indefinite length is not closed.");
                }

                if (buffer[position] == 0 && buffer[position + 1] == 0)
                {
                    return new DerNode(buffer, offset, contentStart, position, position + 2);
                }

                position = Parse(buffer, position, limit, depth + 1).End;
            }
        }

        long length = first;
        if ((first & 0x80) != 0)
        {
            var count = first & 0x7F;
            if (count is 0 or > 4 || position + count > limit)
            {
                throw new DocumentValidationException("PDF signature: an invalid length.");
            }

            length = 0;
            for (var index = 0; index < count; index++)
            {
                length = (length << 8) | buffer[position++];
            }
        }

        if (length > limit - position)
        {
            throw new DocumentValidationException("PDF signature: an element is longer than its container.");
        }

        return new DerNode(buffer, offset, position, position + (int)length, position + (int)length);
    }
}
