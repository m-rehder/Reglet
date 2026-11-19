namespace Reglet;

/// <summary>
/// tolerance-based image comparison for the visual regression (K11). Decodes both PNGs
/// via the own <c>PdfPngDecoder</c> and returns the fraction of clearly deviating
/// channel values.
/// </summary>
internal static class ImageDiff
{
    public static double DifferenceRatio(byte[] baselinePng, byte[] actualPng, int tolerance = 8)
    {
        ArgumentNullException.ThrowIfNull(baselinePng);
        ArgumentNullException.ThrowIfNull(actualPng);

        var baseline = PdfPngDecoder.Decode(baselinePng);
        var actual = PdfPngDecoder.Decode(actualPng);

        if (baseline.Width != actual.Width || baseline.Height != actual.Height)
        {
            return 1.0;
        }

        var samples = Math.Min(baseline.Samples.Length, actual.Samples.Length);
        if (samples == 0)
        {
            return 0.0;
        }

        // Only what is drawn counts: on a mostly white page even another font stayed below any
        // tolerance measured against the whole image.
        var differing = 0;
        var inked = 0;
        for (var index = 0; index < samples; index++)
        {
            if (baseline.Samples[index] < 250 || actual.Samples[index] < 250)
            {
                inked++;
            }

            if (Math.Abs(baseline.Samples[index] - actual.Samples[index]) > tolerance)
            {
                differing++;
            }
        }

        if (inked == 0)
        {
            return 0.0;
        }

        return (double)differing / inked;
    }
}
