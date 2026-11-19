using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

/// <summary>Creates valid test images (PNG) for the writer tests.</summary>
internal static class TestImages
{
    /// <summary>Creates a valid RGB PNG with single-color pixels (for the PDF writer).</summary>
    public static byte[] RgbPng(int width, int height, byte red, byte green, byte blue)
    {
        var raw = new byte[height * (1 + width * 3)];
        var position = 0;
        for (var y = 0; y < height; y++)
        {
            raw[position++] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[position++] = red;
                raw[position++] = green;
                raw[position++] = blue;
            }
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(png, "IHDR", ihdr);
        AppendChunk(png, "IDAT", compressed);
        AppendChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>
    /// Creates a PNG of any color type and bit depth, optionally Adam7-interlaced. <paramref name="pixel"/>
    /// returns the raw channel values (in the bit depth) of a pixel; every row uses the Sub
    /// filter so that the unfiltering is exercised as well.
    /// </summary>
    public static byte[] Png(
        int width,
        int height,
        int colorType,
        int bitDepth,
        Func<int, int, int[]> pixel,
        bool interlaced = false,
        byte[]? palette = null,
        byte[]? transparency = null)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * bitDepth;
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        (int X, int Y, int DX, int DY)[] passes = interlaced
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];

        var raw = new List<byte>();
        foreach (var (startX, startY, stepX, stepY) in passes)
        {
            var passWidth = width <= startX ? 0 : (width - startX + stepX - 1) / stepX;
            var passHeight = height <= startY ? 0 : (height - startY + stepY - 1) / stepY;
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            var rowBytes = (passWidth * bitsPerPixel + 7) / 8;
            for (var row = 0; row < passHeight; row++)
            {
                var line = new byte[rowBytes];
                var bit = 0;
                for (var column = 0; column < passWidth; column++)
                {
                    foreach (var value in pixel(startX + column * stepX, startY + row * stepY))
                    {
                        if (bitDepth == 16)
                        {
                            line[bit / 8] = (byte)(value >> 8);
                            line[bit / 8 + 1] = (byte)value;
                        }
                        else if (bitDepth == 8)
                        {
                            line[bit / 8] = (byte)value;
                        }
                        else
                        {
                            line[bit / 8] |= (byte)(value << (8 - bitDepth - bit % 8));
                        }

                        bit += bitDepth;
                    }
                }

                raw.Add(1);
                for (var index = 0; index < rowBytes; index++)
                {
                    var left = index >= bytesPerPixel ? line[index - bytesPerPixel] : 0;
                    raw.Add((byte)(line[index] - left));
                }
            }
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw.ToArray());
            }

            compressed = output.ToArray();
        }

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = (byte)(interlaced ? 1 : 0);
        AppendChunk(png, "IHDR", ihdr);
        if (palette is not null)
        {
            AppendChunk(png, "PLTE", palette);
        }

        if (transparency is not null)
        {
            AppendChunk(png, "tRNS", transparency);
        }

        AppendChunk(png, "IDAT", compressed);
        AppendChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(Encoding.ASCII.GetBytes(type));
        target.AddRange(data);

        // CRC-32 over type and data, so that libpng-based decoders (LibreOffice, Word) accept the file.
        var crc = Crc32([.. Encoding.ASCII.GetBytes(type), .. data]);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        target.AddRange(checksum.ToArray());
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
