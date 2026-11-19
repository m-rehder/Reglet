namespace Reglet;

/// <summary>
/// cropping of an image (phase AA, AA02): the fraction cut off on each side (0 to 0.5 of the
/// image size).
/// </summary>
public readonly record struct ImageCrop(double Left, double Top, double Right, double Bottom)
{
    /// <summary>No cropping.</summary>
    public static ImageCrop None => default;

    /// <summary>Whether nothing is cropped.</summary>
    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}
