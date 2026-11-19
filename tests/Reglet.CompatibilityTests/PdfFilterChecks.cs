using System.IO.Compression;
using Reglet;

/// <summary>
/// O04+: checks the PDF stream filters (ASCII85, LZW, RunLength) and the
/// /DecodeParms predictor (TIFF and PNG) directly.
/// </summary>
internal static class PdfFilterChecks
{
    internal static int Run()
    {
        try
        {
            DecodesAscii85();
            DecodesAscii85Zero();
            DecodesRunLength();
            DecodesLzwSpecVector();
            DecodesPngUpPredictor();
            DecodesTiffPredictor();
            Console.WriteLine("PDF filters: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF-Filter: " + exception);
            return 1;
        }
    }

    private static void DecodesAscii85()
    {
        // Bekannter Vektor: "Man " ist "9jqo^".
        var decoded = Decode("ASCII85Decode", "9jqo^~>");
        Require(decoded.AsSpan().SequenceEqual("Man "u8), "ASCII85: 'Man ' dekodiert.");
    }

    private static void DecodesAscii85Zero()
    {
        var decoded = Decode("ASCII85Decode", "z~>");
        Require(decoded.Length == 4 && decoded.All(value => value == 0), "ASCII85: 'z' is zero.");
    }

    private static void DecodesRunLength()
    {
        // 2 -> three literals, 254 -> 3 times the next byte, 128 -> end.
        var decoded = Decode("RunLengthDecode", new byte[] { 2, (byte)'A', (byte)'B', (byte)'C', 254, (byte)'x', 128 });
        Require(decoded.AsSpan().SequenceEqual("ABCxxx"u8), "RunLength: literals and repetition.");
    }

    private static void DecodesLzwSpecVector()
    {
        // PDF specification (7.4.4.2): codes for "-----A---B".
        var packed = PackNineBit([256, 45, 258, 258, 65, 259, 66, 257]);
        var decoded = Decode("LZWDecode", packed);
        Require(
            decoded.AsSpan().SequenceEqual(new byte[] { 45, 45, 45, 45, 45, 65, 45, 45, 45, 66 }),
            "LZW: Spec-Vektor '-----A---B'.");
    }

    private static void DecodesPngUpPredictor()
    {
        // Two rows of 3 bytes each; second row encoded with filter 2 (Up).
        var encoded = new byte[] { 0, 10, 20, 30, 2, 5, 5, 5 };
        var decoded = Decode("FlateDecode", Deflate(encoded), Parameters(predictor: 12, colors: 1, bitsPerComponent: 8, columns: 3));
        Require(decoded.AsSpan().SequenceEqual(new byte[] { 10, 20, 30, 15, 25, 35 }), "PNG-Predictor (Up).");
    }

    private static void DecodesTiffPredictor()
    {
        // TIFF predictor 2: each sample is the difference from the previous sample of the row.
        var encoded = new byte[] { 10, 10, 10, 15, 10, 10 };
        var decoded = Decode("FlateDecode", Deflate(encoded), Parameters(predictor: 2, colors: 1, bitsPerComponent: 8, columns: 3));
        Require(decoded.AsSpan().SequenceEqual(new byte[] { 10, 20, 30, 15, 25, 35 }), "TIFF-Predictor.");
    }

    private static byte[] Decode(string filter, string text) => Decode(filter, System.Text.Encoding.ASCII.GetBytes(text));

    private static byte[] Decode(string filter, byte[] data, PdfDictionary? parameters = null)
        => PdfStreamFilters.Decode(data, [new PdfFilter(filter, parameters)]);

    private static PdfDictionary Parameters(int predictor, int colors, int bitsPerComponent, int columns)
        => new(new Dictionary<string, PdfValue>
        {
            ["Predictor"] = new PdfInteger(predictor),
            ["Colors"] = new PdfInteger(colors),
            ["BitsPerComponent"] = new PdfInteger(bitsPerComponent),
            ["Columns"] = new PdfInteger(columns),
        });

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] PackNineBit(int[] codes)
    {
        var bits = new List<bool>();
        foreach (var code in codes)
        {
            for (var index = 8; index >= 0; index--)
            {
                bits.Add(((code >> index) & 1) == 1);
            }
        }

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var index = 0; index < bits.Count; index++)
        {
            if (bits[index])
            {
                bytes[index / 8] |= (byte)(1 << (7 - (index % 8)));
            }
        }

        return bytes;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
