using System.IO.Compression;

namespace Reglet;

/// <summary>Turns a PDF image XObject into a JPEG or PNG image block (ADR-0033); anything else is reported.</summary>
internal static class PdfImageRecovery
{
    internal static ImageBlock? Decode(PdfFile file, PdfStreamValue stream, List<ReadWarning> warnings)
    {
        try
        {
            var dictionary = stream.Dictionary;
            var filters = FilterNames(file, dictionary.Get("Filter"));
            if (filters is ["DCTDecode"] or ["DCT"])
            {
                var meta = ImageMetadataReader.Read(stream.Raw, ImageMediaType.Jpeg);
                if (meta.Components is 1 or 3)
                {
                    return new ImageBlock(stream.Raw, ImageMediaType.Jpeg);
                }

                return Skip(warnings, "a JPEG image that is neither gray nor RGB");
            }

            if (file.Number(dictionary.Get("BitsPerComponent")) != 8
                || file.Number(dictionary.Get("Width")) is not { } width
                || file.Number(dictionary.Get("Height")) is not { } height
                || width < 1 || height < 1 || width * height > 50_000_000)
            {
                return Skip(warnings, "an image that is not 8 bits per component");
            }

            var components = Components(file, dictionary.Get("ColorSpace"));
            if (components is null || filters.Any(name => name is "DCTDecode" or "DCT" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode"))
            {
                return Skip(warnings, "an image with an unsupported color space or filter");
            }

            var samples = file.DecodeStream(stream);
            var w = (int)width;
            var h = (int)height;
            if (samples.Length < w * h * components.Value)
            {
                return Skip(warnings, "an image whose data is shorter than its size");
            }

            byte[]? alpha = null;
            if (file.GetStream(dictionary.Get("SMask")) is { } mask
                && file.Number(mask.Dictionary.Get("Width")) == width && file.Number(mask.Dictionary.Get("Height")) == height
                && file.Number(mask.Dictionary.Get("BitsPerComponent")) == 8)
            {
                alpha = file.DecodeStream(mask);
                if (alpha.Length < w * h)
                {
                    alpha = null;
                }
            }

            return new ImageBlock(PngEncode(w, h, components.Value, samples, alpha), ImageMediaType.Png);
        }
        catch (Exception exception) when (exception is UnsupportedFeatureException or DocumentValidationException or ArgumentException or InvalidDataException)
        {
            return Skip(warnings, "an image that cannot be decoded");
        }
    }

    private static ImageBlock? Skip(List<ReadWarning> warnings, string what)
    {
        var message = $"PDF: {what} is not recovered.";
        if (!warnings.Any(warning => warning.Message == message))
        {
            warnings.Add(new ReadWarning(message));
        }

        return null;
    }

    private static List<string> FilterNames(PdfFile file, PdfValue? value)
    {
        var names = new List<string>();
        if (file.Name(value) is { } single)
        {
            names.Add(single);
        }
        else if (file.Array(value) is { } array)
        {
            names.AddRange(array.Items.Select(item => file.Name(item)).OfType<string>());
        }

        return names;
    }

    private static int? Components(PdfFile file, PdfValue? colorSpace)
    {
        if (file.Name(colorSpace) is { } name)
        {
            return name switch { "DeviceGray" or "G" or "CalGray" => 1, "DeviceRGB" or "RGB" or "CalRGB" => 3, _ => null };
        }

        if (file.Array(colorSpace) is { Items.Count: >= 2 } array && file.Name(array.Items[0]) == "ICCBased"
            && file.GetStream(array.Items[1]) is { } profile && file.Number(profile.Dictionary.Get("N")) is { } n && n is 1 or 3)
        {
            return (int)n;
        }

        return null;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        output.Write(typed);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typed)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xFFFFFFFFu);
        output.Write(length);
    }

    private static byte[] PngEncode(int width, int height, int components, byte[] samples, byte[]? alpha)
    {
        var channels = components + (alpha is null ? 0 : 1);
        var raw = new byte[height * (1 + width * channels)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0;
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < components; c++)
                {
                    raw[o++] = samples[(y * width + x) * components + c];
                }

                if (alpha is not null)
                {
                    raw[o++] = alpha[y * width + x];
                }
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = (byte)(components == 1 ? (alpha is null ? 0 : 4) : (alpha is null ? 2 : 6));
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }
}
