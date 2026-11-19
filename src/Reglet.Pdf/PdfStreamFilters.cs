using System.IO.Compression;
using System.Text;

namespace Reglet;

/// <summary>A filter to apply to a stream, with optional /DecodeParms.</summary>
internal sealed record PdfFilter(string Name, PdfDictionary? Params);

/// <summary>
/// applies PDF stream filters: FlateDecode, LZWDecode, ASCII85Decode, ASCIIHexDecode and
/// RunLengthDecode. For FlateDecode and LZWDecode the /DecodeParms predictor
/// (TIFF 2 as well as PNG 10–15) are applied. Image filters (DCT, JPX, CCITT) are clearly rejected.
/// </summary>
internal static class PdfStreamFilters
{
    public static byte[] Decode(byte[] data, IReadOnlyList<PdfFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(filters);

        foreach (var filter in filters)
        {
            data = filter.Name switch
            {
                "FlateDecode" or "Fl" => Predictor(Inflate(data), filter.Params),
                "LZWDecode" or "LZW" => Predictor(Lzw(data, EarlyChange(filter.Params)), filter.Params),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "RunLengthDecode" or "RL" => RunLength(data),
                _ => throw new UnsupportedFeatureException($"PDF filter '{filter.Name}' is not supported when reading."),
            };
            ReaderGuard.CheckDecodedLength(data.Length, "PDF-Stream");
        }

        return data;
    }

    private static byte[] Inflate(byte[] data)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            int read;
            while ((read = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                ReaderGuard.CheckDecodedLength(output.Length, "PDF-Stream (FlateDecode)");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new DocumentValidationException("PDF stream is corrupted (FlateDecode).", exception);
        }

        return output.ToArray();
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data);
        var end = text.IndexOf('>');
        if (end >= 0)
        {
            text = text[..end];
        }

        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length % 2 != 0)
        {
            hex += "0";
        }

        var bytes = new byte[hex.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var tuple = new int[5];
        var count = 0;

        foreach (var value in data)
        {
            if (value is 0 or 9 or 10 or 12 or 13 or 32)
            {
                continue;
            }

            if (value == (byte)'~')
            {
                break;
            }

            if (value == (byte)'z' && count == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                ReaderGuard.CheckDecodedLength(output.Count, "PDF-Stream (ASCII85Decode)");
                continue;
            }

            if (value is < (byte)'!' or > (byte)'u')
            {
                throw new DocumentValidationException("Invalid character in ASCII85 stream.");
            }

            tuple[count++] = value - (byte)'!';
            if (count == 5)
            {
                WriteAscii85Tuple(output, tuple, 4);
                count = 0;
            }
        }

        if (count > 0)
        {
            for (var index = count; index < 5; index++)
            {
                tuple[index] = 84;
            }

            WriteAscii85Tuple(output, tuple, count - 1);
        }

        return output.ToArray();
    }

    private static void WriteAscii85Tuple(List<byte> output, int[] tuple, int byteCount)
    {
        long value = 0;
        for (var index = 0; index < 5; index++)
        {
            value = (value * 85) + tuple[index];
        }

        for (var index = 0; index < 4; index++)
        {
            var shift = (3 - index) * 8;
            var current = (byte)((value >> shift) & 0xFF);
            if (index < byteCount)
            {
                output.Add(current);
            }
        }
    }

    private static byte[] RunLength(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var position = 0;
        while (position < data.Length)
        {
            var length = data[position++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - position);
                for (var index = 0; index < count; index++)
                {
                    output.Add(data[position + index]);
                }

                position += count;
            }
            else if (position < data.Length)
            {
                var value = data[position++];
                for (var index = 0; index < 257 - length; index++)
                {
                    output.Add(value);
                }

                ReaderGuard.CheckDecodedLength(output.Count, "PDF-Stream (RunLengthDecode)");
            }
        }

        return output.ToArray();
    }

    private static byte[] Lzw(byte[] data, bool earlyChange)
    {
        var output = new List<byte>(data.Length * 2);
        var table = new List<byte[]>(4096);
        ResetTable(table);

        var codeWidth = 9;
        var reader = new BitReader(data);
        byte[]? previous = null;

        while (true)
        {
            var code = reader.Read(codeWidth);
            if (code < 0)
            {
                break;
            }

            if (code == 256)
            {
                ResetTable(table);
                codeWidth = 9;
                previous = null;
                continue;
            }

            if (code == 257)
            {
                break;
            }

            byte[] entry;
            if (code < table.Count && table[code].Length > 0)
            {
                entry = table[code];
            }
            else if (code == table.Count && previous is not null)
            {
                entry = [.. previous, previous[0]];
            }
            else
            {
                throw new DocumentValidationException("LZW code is outside the table.");
            }

            output.AddRange(entry);
            ReaderGuard.CheckDecodedLength(output.Count, "PDF-Stream (LZWDecode)");

            if (previous is not null && table.Count < 4096)
            {
                table.Add([.. previous, entry[0]]);
            }

            previous = entry;
            if (table.Count + (earlyChange ? 1 : 0) == (1 << codeWidth) && codeWidth < 12)
            {
                codeWidth++;
            }
        }

        return output.ToArray();
    }

    private static void ResetTable(List<byte[]> table)
    {
        table.Clear();
        for (var index = 0; index < 256; index++)
        {
            table.Add([(byte)index]);
        }

        table.Add([]); // 256 Clear
        table.Add([]); // 257 EOD
    }

    private static bool EarlyChange(PdfDictionary? parms) => Int(parms, "EarlyChange") is not 0;

    private static byte[] Predictor(byte[] data, PdfDictionary? parms)
    {
        var predictor = Int(parms, "Predictor") ?? 1;
        if (predictor <= 1)
        {
            return data;
        }

        var colors = Int(parms, "Colors") ?? 1;
        var bitsPerComponent = Int(parms, "BitsPerComponent") ?? 8;
        var columns = Int(parms, "Columns") ?? 1;
        if (colors is < 1 or > 32
            || bitsPerComponent is not (1 or 2 or 4 or 8 or 16)
            || columns < 1)
        {
            throw new DocumentValidationException("PDF /DecodeParms contains invalid predictor values.");
        }

        var rowBits = (long)colors * bitsPerComponent * columns;
        if (rowBits > (long)ReaderGuard.MaxDecodedBytes * 8)
        {
            throw new DocumentValidationException("PDF /DecodeParms describes a row that is too long.");
        }

        var bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        var rowLength = (int)((rowBits + 7) / 8);

        if (predictor == 2)
        {
            return TiffPredictor(data, colors, bitsPerComponent, rowLength);
        }

        if (predictor is >= 10 and <= 15)
        {
            return PngPredictor(data, bytesPerPixel, rowLength);
        }

        throw new UnsupportedFeatureException($"PDF predictor {predictor} is not supported.");
    }

    private static byte[] PngPredictor(byte[] data, int bytesPerPixel, int rowLength)
    {
        var rowSize = rowLength + 1;
        var rows = data.Length / rowSize;
        var result = new byte[rows * rowLength];
        var previous = new byte[rowLength];
        var position = 0;

        for (var row = 0; row < rows; row++)
        {
            var filter = data[position++];
            var target = row * rowLength;
            for (var index = 0; index < rowLength; index++)
            {
                var raw = data[position + index];
                var left = index >= bytesPerPixel ? result[target + index - bytesPerPixel] : (byte)0;
                var up = previous[index];
                var upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : (byte)0;
                result[target + index] = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) >> 1)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => throw new DocumentValidationException($"Unknown PNG predictor filter {filter}."),
                };
            }

            position += rowLength;
            previous = result[target..(target + rowLength)];
        }

        return result;
    }

    private static byte[] TiffPredictor(byte[] data, int colors, int bitsPerComponent, int rowLength)
    {
        if (bitsPerComponent is not (8 or 16))
        {
            throw new UnsupportedFeatureException($"TIFF predictor with {bitsPerComponent} bits is not supported.");
        }

        var rows = data.Length / rowLength;
        var result = (byte[])data.Clone();
        var sampleSize = bitsPerComponent / 8;

        for (var row = 0; row < rows; row++)
        {
            var start = row * rowLength;
            for (var column = colors * sampleSize; column + sampleSize <= rowLength; column += sampleSize)
            {
                for (var part = 0; part < sampleSize; part++)
                {
                    result[start + column + part] = (byte)(result[start + column + part] + result[start + column - (colors * sampleSize) + part]);
                }
            }
        }

        return result;
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static int? Int(PdfDictionary? dictionary, string key)
        => dictionary?.Get(key) switch
        {
            PdfInteger integer when integer.Value is >= int.MinValue and <= int.MaxValue => (int)integer.Value,
            PdfReal real when Math.Abs(real.Value % 1) < 1e-9 && real.Value is >= int.MinValue and <= int.MaxValue => (int)real.Value,
            PdfInteger or PdfReal => throw new DocumentValidationException($"PDF value /{key} is outside the valid range."),
            _ => null,
        };

    private sealed class BitReader(byte[] data)
    {
        private int _position;

        public int Read(int width)
        {
            var value = 0;
            for (var index = 0; index < width; index++)
            {
                var byteIndex = _position >> 3;
                if (byteIndex >= data.Length)
                {
                    return -1;
                }

                var bit = (data[byteIndex] >> (7 - (_position & 7))) & 1;
                value = (value << 1) | bit;
                _position++;
            }

            return value;
        }
    }
}
