namespace Reglet;

/// <summary>An embedded image.</summary>
public sealed class ImageBlock : Block
{
    private readonly byte[] _data;
    private Length? _width;
    private Length? _height;
    private ImageScaleMode _scaleMode = ImageScaleMode.Original;
    private ImageCrop? _crop;
    private Length? _offsetX;
    private Length? _offsetY;
    private ImageBlock? _rasterFallback;

    /// <summary>Creates an image from bytes and media type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The media type is unknown.</exception>
    /// <exception cref="ArgumentException">The image data is empty.</exception>
    public ImageBlock(ReadOnlyMemory<byte> data, ImageMediaType mediaType)
    {
        if (!Enum.IsDefined(mediaType))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unknown image media type.");
        }

        if (data.IsEmpty)
        {
            throw new ArgumentException("Image data must not be empty.", nameof(data));
        }

        _data = data.ToArray();
        MediaType = mediaType;
    }

    /// <summary>The image bytes as a defensive copy.</summary>
    public ReadOnlyMemory<byte> Data => _data.ToArray();

    /// <summary>
    /// The image bytes without a copy for the writers. The caller must not modify the returned
    /// memory view; the bytes remain immutable.
    /// </summary>
    internal ReadOnlyMemory<byte> RawData => _data;

    /// <summary>The media type of the image bytes.</summary>
    public ImageMediaType MediaType { get; }

    /// <summary>Optional PNG or JPEG used by DOCX readers that do not support SVG.</summary>
    /// <exception cref="ArgumentException">The image is not SVG, or the fallback is not raster.</exception>
    public ImageBlock? RasterFallback
    {
        get => _rasterFallback;
        init
        {
            if (value is not null && (MediaType != ImageMediaType.Svg || value.MediaType == ImageMediaType.Svg))
                throw new ArgumentException("A raster fallback is only supported for SVG images and must be PNG or JPEG.");
            _rasterFallback = value;
        }
    }

    /// <summary>Alternative text for accessibility.</summary>
    public string? AltText { get; init; }

    /// <summary>Display width; null means automatic.</summary>
    public Length? Width
    {
        get => _width;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Width), "Image width must be positive.");
            }

            _width = value;
        }
    }

    /// <summary>Display height; null means automatic.</summary>
    public Length? Height
    {
        get => _height;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Height), "Image height must be positive.");
            }

            _height = value;
        }
    }

    /// <summary>Scaling mode.</summary>
    public ImageScaleMode ScaleMode
    {
        get => _scaleMode;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(ScaleMode), value, "Unknown scaling mode.");
            }

            _scaleMode = value;
        }
    }

    /// <summary>
    /// cropping cut off on each side (phase AA, AA02); null means no cropping.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A fraction is outside 0–0.5 or the sum exceeds 1.</exception>
    public ImageCrop? Crop
    {
        get => _crop;
        init
        {
            if (value is { } crop)
            {
                if (crop.Left is < 0 or > 0.5 || crop.Top is < 0 or > 0.5
                    || crop.Right is < 0 or > 0.5 || crop.Bottom is < 0 or > 0.5)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "A crop fraction must be between 0 and 0.5.");
                }

                if (crop.Left + crop.Right >= 1 || crop.Top + crop.Bottom >= 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "The crop fractions must leave part of the image.");
                }
            }

            _crop = value;
        }
    }

    /// <summary>
    /// wrapping of the text around a floating image (phase AA, AA01); <see cref="ImageWrap.None"/> is inline.
    /// </summary>
    public ImageWrap Wrap { get; init; } = ImageWrap.None;

    /// <summary>Horizontal offset of a floating image; null means 0.</summary>
    public Length? OffsetX
    {
        get => _offsetX;
        init
        {
            if (value is { } offset && !double.IsFinite(offset.Points))
            {
                throw new ArgumentOutOfRangeException(nameof(OffsetX), "The offset must be finite.");
            }

            _offsetX = value;
        }
    }

    /// <summary>Vertical offset of a floating image; null means 0.</summary>
    public Length? OffsetY
    {
        get => _offsetY;
        init
        {
            if (value is { } offset && !double.IsFinite(offset.Points))
            {
                throw new ArgumentOutOfRangeException(nameof(OffsetY), "The offset must be finite.");
            }

            _offsetY = value;
        }
    }
}
