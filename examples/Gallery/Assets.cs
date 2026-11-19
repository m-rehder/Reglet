using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Gallery;

/// <summary>
/// Shared assets of the examples. The pictures are generated, so the gallery needs no binary files.
/// </summary>
public static class Assets
{
    /// <summary>The password of the protected examples.</summary>
    public const string Password = "reglet";

    /// <summary>Petrol, the colour of the Reglet logo.</summary>
    public static readonly Reglet.Color Petrol = Reglet.Color.FromHex("2BA39A");

    /// <summary>The orange accent of the logo.</summary>
    public static readonly Reglet.Color Accent = Reglet.Color.FromHex("F28C38");

    /// <summary>The first of the named font files found in the usual font folders of Windows, macOS and Linux; null if none.</summary>
    public static string? FindFont(params string[] names)
    {
        string[] folders =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "/System/Library/Fonts/Supplemental", "/Library/Fonts",
            "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/TTF", "/usr/share/fonts/dejavu",
        ];
        return names.SelectMany(name => folders.Where(folder => folder.Length > 0).Select(folder => Path.Combine(folder, name))).FirstOrDefault(File.Exists);
    }

    /// <summary>A light tint for table headers and boxes.</summary>
    public static readonly Reglet.Color Tint = Reglet.Color.FromHex("E3F4F2");

    /// <summary>A picture that runs from petrol at the top to the light tint at the bottom, with an orange stripe.</summary>
    public static byte[] GradientPng(int width, int height)
        => Png(width, height, (x, y) =>
        {
            var t = (double)y / Math.Max(1, height - 1);
            if (y > height * 0.62 && y < height * 0.70 && x > width * 0.12 && x < width * 0.88)
            {
                return (0xF2, 0x8C, 0x38);
            }

            return ((byte)(0x2B + (0xE3 - 0x2B) * t), (byte)(0xA3 + (0xF4 - 0xA3) * t), (byte)(0x9A + (0xF2 - 0x9A) * t));
        });

    /// <summary>Creates a valid, single-color RGB PNG with IDAT data.</summary>
    public static byte[] SolidPng(int width, int height, byte red, byte green, byte blue)
        => Png(width, height, (_, _) => (red, green, blue));

    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var raw = new byte[height * (1 + width * 3)];
        var position = 0;
        for (var y = 0; y < height; y++)
        {
            raw[position++] = 0;
            for (var x = 0; x < width; x++)
            {
                var (red, green, blue) = pixel(x, y);
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

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        var chunk = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        target.AddRange(chunk);

        // Word refuses a PNG with a wrong CRC ("The picture cannot be displayed").
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(chunk));
        target.AddRange(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
