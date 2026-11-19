using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// a minimal DER encoder (phase BJ, BJ02). The library uses no ASN.1 library (zero
/// dependency, AOT safety), so the few structures that the CMS needs are encoded by hand:
/// distinguished encoding rules as in ITU-T X.690.
/// </summary>
internal static class PdfDer
{
    /// <summary>Encodes a value with a tag and a content (tag, length, content).</summary>
    public static byte[] Tlv(byte tag, byte[] content)
    {
        var length = Length(content.Length);
        var result = new byte[1 + length.Length + content.Length];
        result[0] = tag;
        length.CopyTo(result, 1);
        content.CopyTo(result, 1 + length.Length);
        return result;
    }

    /// <summary>Encodes a SEQUENCE.</summary>
    public static byte[] Sequence(params byte[][] elements) => Tlv(0x30, Concat(elements));

    /// <summary>
    /// Encodes a SET OF: the elements are sorted by their encoding, as DER requires. The
    /// order matters for the signature over the signed attributes.
    /// </summary>
    public static byte[] SetOf(IEnumerable<byte[]> elements)
    {
        var sorted = elements.OrderBy(element => element, ByteComparer.Instance).ToList();
        return Tlv(0x31, Concat(sorted));
    }

    /// <summary>Encodes an INTEGER (two's complement, minimal length).</summary>
    public static byte[] Integer(long value)
    {
        var bytes = new List<byte>();
        var remaining = value;
        bytes.Add((byte)(remaining & 0xFF));
        remaining >>= 8;
        while (remaining != 0 && remaining != -1)
        {
            bytes.Add((byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        if (bytes.Count == 0)
        {
            bytes.Add(0);
        }

        // The first byte must not look like a negative number.
        if ((bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, value >= 0 ? (byte)0x00 : (byte)0xFF);
        }

        return Tlv(0x02, [.. bytes]);
    }

    /// <summary>Encodes an OBJECT IDENTIFIER (the dotted notation).</summary>
    public static byte[] Oid(string dotted)
    {
        var parts = dotted.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => long.Parse(part, CultureInfo.InvariantCulture))
            .ToList();
        if (parts.Count < 2)
        {
            throw new ArgumentException("An object identifier needs at least two parts.", nameof(dotted));
        }

        var content = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        for (var index = 2; index < parts.Count; index++)
        {
            var value = parts[index];
            var group = new List<byte> { (byte)(value & 0x7F) };
            value >>= 7;
            while (value > 0)
            {
                group.Insert(0, (byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            content.AddRange(group);
        }

        return Tlv(0x06, [.. content]);
    }

    /// <summary>Encodes an OCTET STRING.</summary>
    public static byte[] OctetString(byte[] data) => Tlv(0x04, data);

    /// <summary>Encodes a NULL.</summary>
    public static byte[] Null() => Tlv(0x05, []);

    /// <summary>Encodes a UTF8String.</summary>
    public static byte[] Utf8String(string value) => Tlv(0x0C, Encoding.UTF8.GetBytes(value));

    /// <summary>Encodes a PrintableString.</summary>
    public static byte[] PrintableString(string value) => Tlv(0x13, Encoding.ASCII.GetBytes(value));

    /// <summary>Encodes a UTCTime (the format of the CMS attribute "signing time").</summary>
    public static byte[] UtcTime(DateTimeOffset value)
        => Tlv(0x17, Encoding.ASCII.GetBytes(value.UtcDateTime.ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));

    /// <summary>Encodes a context specific value (the tag [n]).</summary>
    public static byte[] Context(int number, bool constructed, byte[] content)
        => Tlv((byte)(0x80 | (constructed ? 0x20 : 0) | number), content);

    /// <summary>The content of an encoded value (without tag and length).</summary>
    public static byte[] ContentOf(byte[] encoded)
    {
        if (encoded.Length < 2)
        {
            return [];
        }

        var first = encoded[1];
        if (first < 0x80)
        {
            return encoded[2..];
        }

        var count = first & 0x7F;
        var length = 0;
        for (var index = 0; index < count; index++)
        {
            length = (length << 8) | encoded[2 + index];
        }

        return encoded[(2 + count)..];
    }

    /// <summary>Concatenates the elements.</summary>
    internal static byte[] Concat(IEnumerable<byte[]> elements)
    {
        var result = new List<byte>();
        foreach (var element in elements)
        {
            result.AddRange(element);
        }

        return [.. result];
    }

    /// <summary>The length in the DER format (short or long form).</summary>
    private static byte[] Length(int length)
    {
        if (length < 0x80)
        {
            return [(byte)length];
        }

        var bytes = new List<byte>();
        var remaining = length;
        while (remaining > 0)
        {
            bytes.Insert(0, (byte)(remaining & 0xFF));
            remaining >>= 8;
        }

        var result = new List<byte> { (byte)(0x80 | bytes.Count) };
        result.AddRange(bytes);
        return [.. result];
    }

    /// <summary>Compares byte arrays (the order of the SET OF elements).</summary>
    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var length = Math.Min(x.Length, y.Length);
            for (var index = 0; index < length; index++)
            {
                if (x[index] != y[index])
                {
                    return x[index].CompareTo(y[index]);
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
