namespace Reglet;

/// <summary>
/// The visible outline of an image as horizontal extents per scan line, normalized to the image box
/// (0 is the left or top edge, 1 the right or bottom one). A tight text wrap uses it to reserve only
/// the space the visible image needs (OF-B06).
/// </summary>
internal sealed class ImageContour
{
    private readonly double[] _left;
    private readonly double[] _right;

    private ImageContour(double[] left, double[] right)
    {
        _left = left;
        _right = right;
    }

    /// <summary>The number of sampled bands (vertical resolution of the contour).</summary>
    public int Bands => _left.Length;

    /// <summary>
    /// The contour of a PNG image, or null when the image is fully opaque (or not a PNG, so the box
    /// itself is the outline).
    /// </summary>
    public static ImageContour? From(byte[] data, ImageMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (mediaType != ImageMediaType.Png)
        {
            return null;
        }

        PdfImageData decoded;
        try
        {
            decoded = PdfPngDecoder.Decode(data);
        }
        catch (ImageException)
        {
            return null;
        }

        if (decoded.Alpha is not { } alpha || decoded.Width <= 0 || decoded.Height <= 0
            || alpha.Length < decoded.Width * decoded.Height)
        {
            return null;
        }

        // At most 64 bands keep the scan cheap and the outline smooth enough for a wrap.
        var bands = Math.Min(64, decoded.Height);
        var left = new double[bands];
        var right = new double[bands];
        var visible = false;
        for (var band = 0; band < bands; band++)
        {
            var from = (int)((long)band * decoded.Height / bands);
            var to = Math.Max(from + 1, (int)((long)(band + 1) * decoded.Height / bands));
            var min = int.MaxValue;
            var max = -1;
            for (var row = from; row < to && row < decoded.Height; row++)
            {
                var offset = row * decoded.Width;
                for (var column = 0; column < decoded.Width; column++)
                {
                    if (alpha[offset + column] < 32)
                    {
                        continue;
                    }

                    min = Math.Min(min, column);
                    max = Math.Max(max, column);
                }
            }

            // An empty band is no obstacle: its extent is inverted and ignored by Extents.
            left[band] = max < 0 ? 1 : (double)min / decoded.Width;
            right[band] = max < 0 ? 0 : (double)(max + 1) / decoded.Width;
            visible |= max >= 0;
        }

        return visible ? new ImageContour(left, right) : null;
    }

    /// <summary>
    /// The extents of the visible image in the vertical band [<paramref name="top"/>,
    /// <paramref name="bottom"/>] (both 0 is the top edge, 1 the bottom one). The left value is the
    /// smallest and the right value the largest visible column of the band, so text keeps clear of
    /// the whole visible shape below and above the line.
    /// </summary>
    public (double Left, double Right) Extents(double top, double bottom)
    {
        if (_left.Length == 0)
        {
            return (0, 1);
        }

        var first = Math.Clamp((int)Math.Floor(top * _left.Length), 0, _left.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling(bottom * _left.Length) - 1, first, _left.Length - 1);
        var min = 1.0;
        var max = 0.0;
        for (var band = first; band <= last; band++)
        {
            if (_right[band] <= _left[band])
            {
                continue; // empty band
            }

            min = Math.Min(min, _left[band]);
            max = Math.Max(max, _right[band]);
        }

        return (min, max);
    }
}
