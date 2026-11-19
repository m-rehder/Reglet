namespace Reglet;

/// <summary>Checks image data against the configured <see cref="ResourceLimits"/>.</summary>
internal static class ImageGuard
{
    public static void Check(ImageBlock image, ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(limits);

        var data = image.RawData;
        var byteCount = data.Length;
        if (byteCount > limits.MaxImageBytes)
        {
            throw new ImageException(
                $"The image is {byteCount} bytes and exceeds the limit of {limits.MaxImageBytes} bytes.");
        }

        if (image.MediaType == ImageMediaType.Svg)
        {
            // SVG has no pixel metadata; the size limits are checked on the bytes only.
            return;
        }

        var metadata = ImageMetadataReader.Read(data.Span, image.MediaType);
        var pixels = (long)metadata.PixelWidth * metadata.PixelHeight;
        if (pixels > limits.MaxImagePixels)
        {
            throw new ImageException(
                $"The image has {pixels} pixels and exceeds the limit of {limits.MaxImagePixels} pixels.");
        }
    }
}
