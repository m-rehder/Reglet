namespace Reglet;

/// <summary>A placed image in the PDF (resource name and display size in points).</summary>
internal sealed record PdfImageHandle(string ResourceName, double Width, double Height);

/// <summary>
/// embeds images as XObjects: JPEG directly via DCTDecode, PNG decoded via zlib (with a soft
/// mask for transparency).
/// unsupported formats are clearly rejected.
/// </summary>
internal sealed class PdfImageManager
{
    private readonly PdfFileWriter _file;
    private readonly PdfResourceManager _resources;
    private readonly ResourceLimits _limits;
    private readonly Dictionary<ImageBlock, PdfImageHandle> _images = new();

    public PdfImageManager(PdfFileWriter file, PdfResourceManager resources, ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(limits);
        _file = file;
        _resources = resources;
        _limits = limits;
    }

    public PdfImageHandle GetImage(ImageBlock image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (_images.TryGetValue(image, out var existing))
        {
            return existing;
        }

        var (width, height) = ImageLayout.Resolve(image);
        Check(image);
        var objectNumber = _file.ReserveObject();
        var data = image.RawData;

        if (image.MediaType == ImageMediaType.Jpeg)
        {
            WriteJpeg(objectNumber, data.Span);
        }
        else
        {
            WritePng(objectNumber, data.Span);
        }

        var resourceName = _resources.AddImage($"image:{_images.Count + 1}", objectNumber);
        var handle = new PdfImageHandle(resourceName, width.Points, height.Points);
        _images[image] = handle;
        return handle;
    }

    public void Check(ImageBlock image) => ImageGuard.Check(image, _limits);

    private void WriteJpeg(int objectNumber, ReadOnlySpan<byte> data)
    {
        var metadata = ImageMetadataReader.Read(data, ImageMediaType.Jpeg);
        var colorSpace = metadata.Components switch
        {
            1 => "/DeviceGray",
            3 => "/DeviceRGB",
            _ => throw new UnsupportedFeatureException(
                $"Only grayscale and RGB JPEG are supported (found: {metadata.Components} components)."),
        };

        _file.WriteStreamObject(
            objectNumber,
            data,
            $"/Type /XObject /Subtype /Image /Width {metadata.PixelWidth} /Height {metadata.PixelHeight} "
            + $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode");
    }

    private void WritePng(int objectNumber, ReadOnlySpan<byte> data)
    {
        var decoded = PdfPngDecoder.Decode(data);
        var compressed = PdfStreamCompressor.Compress(decoded.Samples);

        // Transparency becomes a soft mask (ISO 32000-1, 11.6.5.3) instead of being laid over
        // white, so that a logo keeps the background it is placed on.
        var softMask = string.Empty;
        if (decoded.Alpha is { } alpha)
        {
            var maskObject = _file.ReserveObject();
            _file.WriteStreamObject(
                maskObject,
                PdfStreamCompressor.Compress(alpha),
                $"/Type /XObject /Subtype /Image /Width {decoded.Width} /Height {decoded.Height} "
                + "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode");
            softMask = $" /SMask {maskObject} 0 R";
        }

        _file.WriteStreamObject(
            objectNumber,
            compressed,
            $"/Type /XObject /Subtype /Image /Width {decoded.Width} /Height {decoded.Height} "
            + $"/ColorSpace {decoded.ColorSpace} /BitsPerComponent 8 /Filter /FlateDecode{softMask}");
    }
}
