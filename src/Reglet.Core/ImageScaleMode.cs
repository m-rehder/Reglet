namespace Reglet;

/// <summary>Scaling mode of an image.</summary>
public enum ImageScaleMode
{
    /// <summary>Original size from pixels and resolution.</summary>
    Original,

    /// <summary>Scale to the available width (preserving the aspect ratio).</summary>
    FitWidth,

    /// <summary>Fit into the available area (preserving the aspect ratio).</summary>
    Contain,
}
