namespace Reglet;

/// <summary>
/// The intrinsic size of a PNG or JPEG image, read from its header without decoding the pixels.
/// </summary>
/// <param name="MediaType">PNG or JPEG, recognised from the file signature.</param>
/// <param name="PixelWidth">Width in pixels.</param>
/// <param name="PixelHeight">Height in pixels.</param>
/// <param name="DpiX">Horizontal resolution (PNG <c>pHYs</c>, JPEG JFIF density); null when the file carries none.</param>
/// <param name="DpiY">Vertical resolution; null when the file carries none.</param>
public sealed record ImageInfo(ImageMediaType MediaType, int PixelWidth, int PixelHeight, double? DpiX, double? DpiY)
{
    /// <summary>Reads the header of a PNG or JPEG image.</summary>
    /// <exception cref="ImageException">The bytes are neither a valid PNG nor a valid JPEG.</exception>
    public static ImageInfo Read(ReadOnlySpan<byte> data)
    {
        var mediaType = data switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => ImageMediaType.Png,
            [0xFF, 0xD8, ..] => ImageMediaType.Jpeg,
            _ => throw new ImageException("The image is neither PNG nor JPEG."),
        };
        var metadata = ImageMetadataReader.Read(data, mediaType);
        return new ImageInfo(
            mediaType,
            metadata.PixelWidth,
            metadata.PixelHeight,
            metadata.DpiX > 0 ? metadata.DpiX : null,
            metadata.DpiY > 0 ? metadata.DpiY : null);
    }
}
