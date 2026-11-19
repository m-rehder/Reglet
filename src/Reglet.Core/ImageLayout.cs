namespace Reglet;

/// <summary>
/// computes the display size of an image from pixels, DPI and scaling mode.
/// Missing DPI values use the documented default.
/// </summary>
internal static class ImageLayout
{
    public static (Length Width, Length Height) Resolve(ImageBlock image)
    {
        if (image.MediaType == ImageMediaType.Svg)
        {
            // An SVG carries its own size; an explicit size in the model wins.
            if (image.Width is { } svgWidth && image.Height is { } svgHeight)
            {
                return (svgWidth, svgHeight);
            }

            if (SvgMetadata.Read(image.RawData.Span) is { } intrinsic)
            {
                var svgAspect = intrinsic.Height / intrinsic.Width;
                if (image.Width is { } onlyWidth)
                {
                    return (onlyWidth, Length.FromPoints(onlyWidth.Points * svgAspect));
                }

                if (image.Height is { } onlyHeight)
                {
                    return (Length.FromPoints(onlyHeight.Points / svgAspect), onlyHeight);
                }

                return (Length.FromPoints(intrinsic.Width), Length.FromPoints(intrinsic.Height));
            }

            throw new ImageException("SVG images need an explicit width and height.");
        }

        var metadata = ImageMetadataReader.Read(image.RawData.Span, image.MediaType);

        var dpiX = metadata.DpiX > 0 ? metadata.DpiX : ImageMetadataReader.DefaultDpi;
        var dpiY = metadata.DpiY > 0 ? metadata.DpiY : ImageMetadataReader.DefaultDpi;

        var pixelWidth = Length.FromInches(metadata.PixelWidth / dpiX);
        var pixelHeight = Length.FromInches(metadata.PixelHeight / dpiY);

        if (pixelWidth.Points <= 0 || pixelHeight.Points <= 0)
        {
            throw new ImageException("Image has no valid pixel dimensions.");
        }

        var aspect = pixelHeight.Points / pixelWidth.Points;

        switch (image.ScaleMode)
        {
            case ImageScaleMode.FitWidth:
            {
                var width = image.Width ?? pixelWidth;
                return (width, Length.FromPoints(width.Points * aspect));
            }

            case ImageScaleMode.Contain:
            {
                var boxWidth = image.Width ?? pixelWidth;
                var boxHeight = image.Height ?? pixelHeight;
                var scale = Math.Min(boxWidth.Points / pixelWidth.Points, boxHeight.Points / pixelHeight.Points);
                return (Length.FromPoints(pixelWidth.Points * scale), Length.FromPoints(pixelHeight.Points * scale));
            }

            default:
                if (image.Width is { } explicitWidth && image.Height is { } explicitHeight)
                {
                    return (explicitWidth, explicitHeight);
                }

                if (image.Width is { } widthOnly)
                {
                    return (widthOnly, Length.FromPoints(widthOnly.Points * aspect));
                }

                if (image.Height is { } heightOnly)
                {
                    return (Length.FromPoints(heightOnly.Points / aspect), heightOnly);
                }

                return (pixelWidth, pixelHeight);
        }
    }
}
