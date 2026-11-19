namespace Reglet;

/// <summary>
/// page-wide design elements from <see cref="PageTemplate"/> (phase S, S04): the
/// template itself, the resource names of the watermark graphics states, the picture
/// watermark (OF-B09) and the reporter for unsupported content.
/// </summary>
internal sealed record PdfPageDecoration(
    PageTemplate? Template,
    string? WatermarkExtGState,
    string? PictureWatermarkExtGState,
    PdfPictureWatermark? PictureWatermark,
    UnsupportedFeatureHandler Handler);

/// <summary>
/// the embedded image of a picture watermark (OF-B09): the resource name, the natural
/// size of the image in points and the requested display size.
/// </summary>
internal sealed record PdfPictureWatermark(
    string ResourceName,
    double NaturalWidth,
    double NaturalHeight,
    Length? Width,
    Length? Height,
    double OffsetX,
    double OffsetY);
