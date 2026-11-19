namespace Reglet;

/// <summary>
/// a text-based watermark that is placed behind the page content (phase S).
/// </summary>
public sealed record Watermark
{
    private double _opacity = 0.1;
    private double _rotation = 45;
    private Length _fontSize = Length.FromPoints(48);

    /// <summary>Creates a watermark with the specified text.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">The text is empty.</exception>
    public Watermark(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
    }

    /// <summary>The displayed text.</summary>
    public string Text { get; }

    /// <summary>Opacity between 0 (invisible) and 1 (opaque). Default is 0.1.</summary>
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

    /// <summary>Rotation in degrees (counterclockwise), e.g. 45 for a diagonal. Default is 45.</summary>
    public double Rotation
    {
        get => _rotation;
        init
        {
            if (!double.IsFinite(value) || value is < -360 or > 360)
            {
                throw new ArgumentOutOfRangeException(nameof(Rotation), value, "Rotation must be between -360 and 360 degrees.");
            }

            _rotation = value;
        }
    }

    /// <summary>Font size of the watermark. Default is 48 points.</summary>
    public Length FontSize
    {
        get => _fontSize;
        init
        {
            if (value.Points <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(FontSize), "Font size must be positive.");
            }

            _fontSize = value;
        }
    }

    /// <summary>Watermark color. Default is a medium gray.</summary>
    public Color Color { get; init; } = Color.FromRgb(0x80, 0x80, 0x80);
}
