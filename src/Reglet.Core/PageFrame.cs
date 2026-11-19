namespace Reglet;

/// <summary>
/// a frame at the paper edge (phase S). The frame lies <see cref="Inset"/> from the paper edge
/// removed and is drawn with <see cref="Width"/>.
/// </summary>
public sealed record PageFrame
{
    private Length _width = Length.FromPoints(1);
    private Length _inset = Length.FromPoints(18);

    /// <summary>Line width of the border. Default is 1 point.</summary>
    public Length Width
    {
        get => _width;
        init
        {
            if (value.Points <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Width), "Border width must be positive.");
            }

            _width = value;
        }
    }

    /// <summary>Distance of the frame from the paper edge. Default is 18 points.</summary>
    public Length Inset
    {
        get => _inset;
        init
        {
            if (value.Points < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(Inset), "Border spacing must not be negative.");
            }

            _inset = value;
        }
    }

    /// <summary>Border color. Default is black.</summary>
    public Color Color { get; init; } = Color.Black;
}
