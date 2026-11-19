namespace Reglet;

/// <summary>
/// a picture watermark that is placed behind the page content (OF-B09). It is the
/// picture counterpart of the text <see cref="Watermark"/>.
/// </summary>
public sealed record PictureWatermark
{
    private readonly byte[] _data;
    private double _opacity = 0.1;
    private Length? _width;
    private Length? _height;

    /// <summary>Creates a picture watermark from image bytes and a media type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The media type is unknown.</exception>
    /// <exception cref="ArgumentException">The image data is empty.</exception>
    public PictureWatermark(ReadOnlyMemory<byte> data, ImageMediaType mediaType)
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

    /// <summary>The image bytes without a copy for the writers.</summary>
    internal ReadOnlyMemory<byte> RawData => _data;

    /// <summary>The media type of the image bytes.</summary>
    public ImageMediaType MediaType { get; }

    /// <summary>
    /// Display width. Null means: half the page width. The aspect ratio of the image
    /// is kept if only one of the two sizes is given.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public Length? Width
    {
        get => _width;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Width), "The width of the watermark must be positive.");
            }

            _width = value;
        }
    }

    /// <summary>
    /// Display height. Null means: the height that follows from the width and the
    /// aspect ratio of the image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    public Length? Height
    {
        get => _height;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Height), "The height of the watermark must be positive.");
            }

            _height = value;
        }
    }

    /// <summary>Opacity between 0 (invisible) and 1 (opaque). Default is 0.1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The opacity is outside 0–1.</exception>
    public double Opacity
    {
        get => _opacity;
        init
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(Opacity), value, "Opacity must be between 0 and 1.");
            }

            _opacity = value;
        }
    }

    /// <summary>Horizontal offset from the center of the page. Default is 0.</summary>
    public Length? OffsetX { get; init; }

    /// <summary>Vertical offset from the center of the page. Default is 0.</summary>
    public Length? OffsetY { get; init; }
}
