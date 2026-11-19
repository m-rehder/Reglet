using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Reglet;

/// <summary>
/// Decoded image data for a PDF image XObject: 8-bit color samples and, when the image has
/// transparency, an 8-bit alpha channel for a soft mask (<c>/SMask</c>).
/// </summary>
internal readonly record struct PdfImageData(int Width, int Height, string ColorSpace, byte[] Samples, byte[]? Alpha = null);

/// <summary>
/// decodes PNG images with BCL means for PDF embedding: all color types and bit depths of
/// ISO/IEC 15948 (1, 2, 4, 8 and 16 bit), with and without Adam7 interlacing. Palettes are
/// expanded to RGB, 16-bit samples are reduced to 8 bit. Transparency (alpha channel or tRNS)
/// becomes a separate alpha channel that the PDF writes as a soft mask.
/// </summary>
internal static class PdfPngDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Adam7 passes: start column, start row, column step, row step.</summary>
    private static readonly (int X, int Y, int DX, int DY)[] Adam7Passes =
    [
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    ];

    public static PdfImageData Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data.Slice(0, 8).SequenceEqual(Signature))
        {
            throw new ImageException("Not a valid PNG file.");
        }

        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        var offset = 8;
        while (offset + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            if (length < 0 || offset + 12L + length > data.Length)
            {
                break;
            }

            var type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var chunk = data.Slice(offset + 8, length);

            switch (type)
            {
                case "IHDR":
                    // Only an IHDR as the first chunk is valid. ImageGuard checks the dimensions
                    // at exactly this position; a later IHDR could bypass the check.
                    if (offset != 8)
                    {
                        throw new ImageException("PNG contains an IHDR chunk at an invalid position.");
                    }

                    if (length < 13)
                    {
                        throw new ImageException("PNG IHDR chunk is too short.");
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(0, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(4, 4));
                    bitDepth = chunk[8];
                    colorType = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE":
                    palette = chunk.ToArray();
                    break;
                case "tRNS":
                    transparency = chunk.ToArray();
                    break;
                case "IDAT":
                    idat.Write(chunk);
                    break;
            }

            if (type == "IEND")
            {
                break;
            }

            offset += 12 + length;
        }

        if (width == 0 && height == 0 && bitDepth == 0)
        {
            throw new ImageException("PNG does not start with an IHDR chunk.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ImageException("PNG has no valid dimensions.");
        }

        if (interlace is not (0 or 1))
        {
            throw new ImageException($"PNG interlace method {interlace} is invalid.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new ImageException($"PNG color type {colorType} is invalid."),
        };

        // Allowed combinations of color type and bit depth (ISO/IEC 15948, table 11.1).
        var validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth)
        {
            throw new ImageException($"PNG bit depth {bitDepth} is invalid for color type {colorType}.");
        }

        if (colorType == 3 && palette is null)
        {
            throw new ImageException("PNG palette is missing.");
        }

        var bitsPerPixel = channels * bitDepth;

        // Decompress only as many bytes as the image dimensions allow. Without this limit
        // a small PNG (zlib bomb) could occupy arbitrarily much memory.
        (int X, int Y, int DX, int DY)[] passes = interlace == 1 ? Adam7Passes : [(0, 0, 1, 1)];
        var expectedLength = 0L;
        foreach (var (passX, passY, stepX, stepY) in passes)
        {
            var passWidth = PassExtent(width, passX, stepX);
            var passHeight = PassExtent(height, passY, stepY);
            if (passWidth > 0 && passHeight > 0)
            {
                expectedLength += passHeight * (RowBytes(passWidth, bitsPerPixel) + 1);
            }
        }

        var outputChannels = colorType is 2 or 3 or 6 ? 3 : 1;
        if (expectedLength > Array.MaxLength || (long)width * height * outputChannels > Array.MaxLength)
        {
            throw new ImageException("PNG is too large to decode.");
        }

        idat.Position = 0;
        var raw = new byte[expectedLength];
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        {
            try
            {
                zlib.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: true);
            }
            catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
            {
                throw new ImageException("PNG data is incomplete or corrupted.", exception);
            }
        }

        var target = new PixelTarget(width, height, colorType, bitDepth, palette, transparency);
        var position = 0;
        foreach (var (passX, passY, stepX, stepY) in passes)
        {
            var passWidth = PassExtent(width, passX, stepX);
            var passHeight = PassExtent(height, passY, stepY);
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            var rowBytes = (int)RowBytes(passWidth, bitsPerPixel);
            var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
            var previous = new byte[rowBytes];
            var current = new byte[rowBytes];
            for (var row = 0; row < passHeight; row++)
            {
                var filter = raw[position];
                Unfilter(filter, raw.AsSpan(position + 1, rowBytes), current, previous, bytesPerPixel);
                position += rowBytes + 1;
                target.WriteRow(current, passWidth, passY + row * stepY, passX, stepX);
                (previous, current) = (current, previous);
            }
        }

        return target.ToImageData();
    }

    /// <summary>Number of pixels of a pass along one axis.</summary>
    private static int PassExtent(int size, int start, int step)
        => size <= start ? 0 : (size - start + step - 1) / step;

    private static long RowBytes(int pixels, int bitsPerPixel)
        => ((long)pixels * bitsPerPixel + 7) / 8;

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, byte[] result, byte[] previous, int bytesPerPixel)
    {
        for (var x = 0; x < source.Length; x++)
        {
            var value = source[x];
            var left = x >= bytesPerPixel ? result[x - bytesPerPixel] : (byte)0;
            var up = previous[x];
            var upLeft = x >= bytesPerPixel ? previous[x - bytesPerPixel] : (byte)0;

            result[x] = filter switch
            {
                0 => value,
                1 => (byte)(value + left),
                2 => (byte)(value + up),
                3 => (byte)(value + ((left + up) >> 1)),
                4 => (byte)(value + Paeth(left, up, upLeft)),
                _ => throw new ImageException($"Unknown PNG filter {filter}."),
            };
        }
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

    /// <summary>
    /// Collects the decoded pixels as 8-bit color samples and an alpha channel. The alpha
    /// channel is dropped when every pixel is opaque.
    /// </summary>
    private sealed class PixelTarget
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _colorType;
        private readonly int _bitDepth;
        private readonly byte[]? _palette;
        private readonly byte[]? _transparency;
        private readonly int _channels;
        private readonly int _outputChannels;
        private readonly byte[] _samples;
        private readonly byte[] _alpha;
        private readonly int[]? _colorKey;
        private bool _hasTransparency;

        public PixelTarget(int width, int height, int colorType, int bitDepth, byte[]? palette, byte[]? transparency)
        {
            _width = width;
            _height = height;
            _colorType = colorType;
            _bitDepth = bitDepth;
            _palette = palette;
            _transparency = transparency;
            _channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            _outputChannels = colorType is 2 or 3 or 6 ? 3 : 1;
            _samples = new byte[width * height * _outputChannels];
            _alpha = new byte[width * height];

            // tRNS for grayscale/RGB: one 16-bit key value per channel in the image's bit depth.
            var keyChannels = colorType switch { 0 => 1, 2 => 3, _ => 0 };
            if (keyChannels > 0 && transparency is not null && transparency.Length >= keyChannels * 2)
            {
                _colorKey = new int[keyChannels];
                for (var channel = 0; channel < keyChannels; channel++)
                {
                    _colorKey[channel] = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(channel * 2, 2));
                }
            }
        }

        public void WriteRow(byte[] row, int pixels, int y, int startX, int stepX)
        {
            Span<int> values = stackalloc int[4];
            for (var index = 0; index < pixels; index++)
            {
                for (var channel = 0; channel < _channels; channel++)
                {
                    values[channel] = ReadSample(row, index * _channels + channel);
                }

                var pixel = y * _width + startX + index * stepX;
                var output = pixel * _outputChannels;
                int alpha;
                switch (_colorType)
                {
                    case 0:
                        _samples[output] = Scale(values[0]);
                        alpha = _colorKey is not null && values[0] == _colorKey[0] ? 0 : 255;
                        break;
                    case 2:
                        _samples[output] = Scale(values[0]);
                        _samples[output + 1] = Scale(values[1]);
                        _samples[output + 2] = Scale(values[2]);
                        alpha = _colorKey is not null && values[0] == _colorKey[0] && values[1] == _colorKey[1] && values[2] == _colorKey[2]
                            ? 0
                            : 255;
                        break;
                    case 3:
                    {
                        var entry = values[0] * 3;
                        if (entry + 2 >= _palette!.Length)
                        {
                            throw new ImageException("PNG palette index is outside the palette.");
                        }

                        _samples[output] = _palette[entry];
                        _samples[output + 1] = _palette[entry + 1];
                        _samples[output + 2] = _palette[entry + 2];

                        // tRNS for palette images: alpha per palette entry (missing = opaque).
                        alpha = _transparency is not null && values[0] < _transparency.Length ? _transparency[values[0]] : 255;
                        break;
                    }

                    case 4:
                        _samples[output] = Scale(values[0]);
                        alpha = Scale(values[1]);
                        break;
                    default:
                        _samples[output] = Scale(values[0]);
                        _samples[output + 1] = Scale(values[1]);
                        _samples[output + 2] = Scale(values[2]);
                        alpha = Scale(values[3]);
                        break;
                }

                _alpha[pixel] = (byte)alpha;
                if (alpha != 255)
                {
                    _hasTransparency = true;
                }
            }
        }

        public PdfImageData ToImageData()
            => new(
                _width,
                _height,
                _outputChannels == 3 ? "/DeviceRGB" : "/DeviceGray",
                _samples,
                _hasTransparency ? _alpha : null);

        /// <summary>Reads the sample with the given index of a row in the image's bit depth.</summary>
        private int ReadSample(byte[] row, int sampleIndex)
        {
            switch (_bitDepth)
            {
                case 8:
                    return row[sampleIndex];
                case 16:
                    return (row[sampleIndex * 2] << 8) | row[sampleIndex * 2 + 1];
                default:
                {
                    var bit = sampleIndex * _bitDepth;
                    var shift = 8 - _bitDepth - (bit & 7);
                    return (row[bit >> 3] >> shift) & ((1 << _bitDepth) - 1);
                }
            }
        }

        /// <summary>Scales a gray, color or alpha sample to 8 bit.</summary>
        private byte Scale(int value) => _bitDepth switch
        {
            8 => (byte)value,
            16 => (byte)(value >> 8),
            _ => (byte)(value * 255 / ((1 << _bitDepth) - 1)),
        };
    }
}
