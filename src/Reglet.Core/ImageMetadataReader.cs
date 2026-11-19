using System.Buffers.Binary;

namespace Reglet;

/// <summary>Pixel dimensions and resolution of an image.</summary>
internal readonly record struct ImageMetadata(
    int PixelWidth,
    int PixelHeight,
    double DpiX,
    double DpiY,
    int Components = 0);

/// <summary>
/// reads pixel dimensions and DPI from PNG (IHDR/pHYs) and JPEG headers (SOFn/APP0).
/// missing DPI values are replaced by the caller with the default value.
/// </summary>
internal static class ImageMetadataReader
{
    /// <summary>Documented default when the image specifies no resolution.</summary>
    public const double DefaultDpi = 96.0;

    private const double PixelsPerMeterPerDpi = 39.37007874015748;

    public static ImageMetadata Read(ReadOnlySpan<byte> data, ImageMediaType mediaType) => mediaType switch
    {
        ImageMediaType.Png => ReadPng(data),
        ImageMediaType.Jpeg => ReadJpeg(data),
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unknown image media type."),
    };

    private static ImageMetadata ReadPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24
            || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47)
        {
            throw new ImageException("Not a valid PNG file.");
        }

        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new ImageException("PNG does not start with an IHDR chunk.");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        if (width <= 0 || height <= 0)
        {
            throw new ImageException("PNG has no valid dimensions.");
        }

        var dpiX = 0.0;
        var dpiY = 0.0;

        var offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            if (length < 0 || offset + 12L + length > data.Length)
            {
                break;
            }

            var type = data.Slice(offset + 4, 4);

            if (type.SequenceEqual("pHYs"u8) && length >= 9)
            {
                var pixelsPerMeterX = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));
                var pixelsPerMeterY = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 12, 4));
                if (data[offset + 16] == 1)
                {
                    dpiX = pixelsPerMeterX / PixelsPerMeterPerDpi;
                    dpiY = pixelsPerMeterY / PixelsPerMeterPerDpi;
                }

                break;
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        return new ImageMetadata(width, height, dpiX, dpiY);
    }

    private static ImageMetadata ReadJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new ImageException("Not a valid JPEG file.");
        }

        var width = 0;
        var height = 0;
        var components = 0;
        var dpiX = 0.0;
        var dpiY = 0.0;
        var offset = 2;

        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = data[offset + 1];
            offset += 2;

            if (marker is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || offset + length > data.Length)
            {
                break;
            }

            var segment = data.Slice(offset + 2, length - 2);

            if (marker == 0xE0
                && segment.Length >= 12
                && segment[0] == 'J' && segment[1] == 'F' && segment[2] == 'I' && segment[3] == 'F' && segment[4] == 0)
            {
                var units = segment[7];
                var xDensity = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(8, 2));
                var yDensity = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(10, 2));

                if (units == 1)
                {
                    dpiX = xDensity;
                    dpiY = yDensity;
                }
                else if (units == 2)
                {
                    dpiX = xDensity * 2.54;
                    dpiY = yDensity * 2.54;
                }
            }
            else if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segment.Length >= 6)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3, 2));
                    components = segment[5];
                }

                break;
            }

            offset += length;
        }

        if (width <= 0 || height <= 0)
        {
            throw new ImageException("JPEG dimensions could not be read.");
        }

        return new ImageMetadata(width, height, dpiX, dpiY, components);
    }
}
