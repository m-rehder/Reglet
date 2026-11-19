using System.Text;
using Reglet;

/// <summary>
/// PNG decoding for the PDF writer: all bit depths (1, 2, 4, 8, 16), Adam7 interlacing and
/// transparency as a soft mask instead of a white background.
/// </summary>
internal static class PngDecoderChecks
{
    internal static int Run()
    {
        try
        {
            DecodesLowBitDepthGray();
            DecodesLowBitDepthPalettes();
            DecodesSixteenBitImages();
            DecodesInterlacedImages();
            KeepsAlphaAsSoftMask();
            TurnsColorKeyIntoSoftMask();
            OmitsSoftMaskForOpaqueImages();
            RejectsInvalidDepthCombinations();
            WritesInterlacedPaletteImageInWarnMode();
            Console.WriteLine("PNG decoder: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PNG decoder: " + exception);
            return 1;
        }
    }

    private static void DecodesLowBitDepthGray()
    {
        foreach (var depth in new[] { 1, 2, 4 })
        {
            var max = (1 << depth) - 1;
            var png = TestImages.Png(11, 3, colorType: 0, depth, (x, y) => [(x + y) % (max + 1)]);
            var decoded = PdfPngDecoder.Decode(png);
            Require(decoded.ColorSpace == "/DeviceGray", $"{depth}-bit gray stays gray.");
            Require(decoded.Samples.Length == 11 * 3, $"{depth}-bit gray: one sample per pixel.");
            for (var y = 0; y < 3; y++)
            {
                for (var x = 0; x < 11; x++)
                {
                    var expected = (x + y) % (max + 1) * 255 / max;
                    Require(decoded.Samples[y * 11 + x] == expected, $"{depth}-bit gray: pixel ({x},{y}) scaled to 8 bit.");
                }
            }

            Require(decoded.Alpha is null, $"{depth}-bit gray without tRNS has no soft mask.");
        }
    }

    private static void DecodesLowBitDepthPalettes()
    {
        foreach (var depth in new[] { 1, 2, 4, 8 })
        {
            var entries = 1 << depth;
            var palette = new byte[entries * 3];
            for (var index = 0; index < entries; index++)
            {
                palette[index * 3] = (byte)(index * 7);
                palette[index * 3 + 1] = (byte)(255 - index);
                palette[index * 3 + 2] = (byte)(index * 3);
            }

            var png = TestImages.Png(13, 2, colorType: 3, depth, (x, y) => [(x * 3 + y) % entries], palette: palette);
            var decoded = PdfPngDecoder.Decode(png);
            Require(decoded.ColorSpace == "/DeviceRGB", $"{depth}-bit palette is expanded to RGB.");
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 13; x++)
                {
                    var index = (x * 3 + y) % entries;
                    var offset = (y * 13 + x) * 3;
                    Require(
                        decoded.Samples[offset] == palette[index * 3]
                        && decoded.Samples[offset + 1] == palette[index * 3 + 1]
                        && decoded.Samples[offset + 2] == palette[index * 3 + 2],
                        $"{depth}-bit palette: pixel ({x},{y}) has its palette color.");
                }
            }
        }
    }

    private static void DecodesSixteenBitImages()
    {
        var rgb = TestImages.Png(5, 4, colorType: 2, bitDepth: 16, (x, y) => [x * 10000, y * 15000, 0xABCD]);
        var decoded = PdfPngDecoder.Decode(rgb);
        Require(decoded.Samples.Length == 5 * 4 * 3, "16-bit RGB: three 8-bit samples per pixel.");
        Require(decoded.Samples[(2 * 5 + 3) * 3] == (3 * 10000) >> 8, "16-bit RGB: red reduced to the high byte.");
        Require(decoded.Samples[(2 * 5 + 3) * 3 + 1] == (2 * 15000) >> 8, "16-bit RGB: green reduced to the high byte.");
        Require(decoded.Samples[(2 * 5 + 3) * 3 + 2] == 0xAB, "16-bit RGB: blue reduced to the high byte.");

        var grayAlpha = TestImages.Png(3, 3, colorType: 4, bitDepth: 16, (x, y) => [0x8000, x == 1 ? 0 : 0xFFFF]);
        var gray = PdfPngDecoder.Decode(grayAlpha);
        Require(gray.ColorSpace == "/DeviceGray" && gray.Samples.All(sample => sample == 0x80), "16-bit gray+alpha: gray samples.");
        Require(gray.Alpha is { } alpha && alpha[1] == 0 && alpha[0] == 255, "16-bit gray+alpha: alpha channel kept.");
    }

    private static void DecodesInterlacedImages()
    {
        // Odd sizes leave some Adam7 passes empty; every pixel must still land in its place.
        foreach (var (width, height) in new[] { (1, 1), (3, 2), (9, 9), (17, 5) })
        {
            int[] Color(int x, int y) => [(x * 31) & 0xFF, (y * 17) & 0xFF, (x ^ y) & 0xFF];
            var progressive = PdfPngDecoder.Decode(TestImages.Png(width, height, colorType: 2, bitDepth: 8, Color));
            var interlaced = PdfPngDecoder.Decode(TestImages.Png(width, height, colorType: 2, bitDepth: 8, Color, interlaced: true));
            Require(interlaced.Samples.SequenceEqual(progressive.Samples), $"Interlaced {width}x{height} RGB matches the progressive image.");
        }

        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };
        var fourBit = PdfPngDecoder.Decode(TestImages.Png(10, 7, colorType: 3, bitDepth: 2, (x, y) => [(x + 2 * y) % 4], interlaced: true, palette: palette));
        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var index = (x + 2 * y) % 4;
                Require(fourBit.Samples[(y * 10 + x) * 3 + 1] == palette[index * 3 + 1], $"Interlaced 2-bit palette: pixel ({x},{y}).");
            }
        }
    }

    private static void KeepsAlphaAsSoftMask()
    {
        var png = TestImages.Png(4, 1, colorType: 6, bitDepth: 8, (x, _) => [200, 100, 50, x * 85]);
        var decoded = PdfPngDecoder.Decode(png);
        Require(decoded.Samples[0] == 200 && decoded.Samples[1] == 100 && decoded.Samples[2] == 50, "RGBA: color is not laid over white.");
        Require(decoded.Alpha is { } alpha && alpha.SequenceEqual(new byte[] { 0, 85, 170, 255 }), "RGBA: alpha becomes the soft mask.");

        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var text = Encoding.Latin1.GetString(buffer.ToArray());
        var mask = System.Text.RegularExpressions.Regex.Match(text, @"/SMask (\d+) 0 R");
        Require(mask.Success, "PDF: the image references a soft mask.");
        var maskObject = System.Text.RegularExpressions.Regex.Match(
            text, "\n" + mask.Groups[1].Value + @" 0 obj\n<<[^>]*/ColorSpace /DeviceGray /BitsPerComponent 8");
        Require(maskObject.Success, "PDF: the soft mask is a gray image.");

        // Palette entries with partial alpha (tRNS) are a soft mask as well.
        var palette = PdfPngDecoder.Decode(TestImages.Png(
            2, 1, colorType: 3, bitDepth: 4, (x, _) => [x], palette: [10, 20, 30, 40, 50, 60], transparency: [0]));
        Require(palette.Alpha is { } paletteAlpha && paletteAlpha[0] == 0 && paletteAlpha[1] == 255, "Palette tRNS: alpha per entry.");
        Require(palette.Samples[0] == 10, "Palette tRNS: the transparent entry keeps its color.");
    }

    private static void TurnsColorKeyIntoSoftMask()
    {
        // 16-bit color key: only the exact 16-bit value is transparent (not just the high byte).
        var png = TestImages.Png(
            3, 1, colorType: 0, bitDepth: 16, (x, _) => [x == 0 ? 0x1234 : x == 1 ? 0x1235 : 0xFFFF], transparency: [0x12, 0x34]);
        var decoded = PdfPngDecoder.Decode(png);
        Require(decoded.Alpha is { } alpha && alpha[0] == 0 && alpha[1] == 255 && alpha[2] == 255, "16-bit gray key compares all 16 bits.");

        var rgb = PdfPngDecoder.Decode(TestImages.Png(
            2, 1, colorType: 2, bitDepth: 8, (x, _) => x == 0 ? [1, 2, 3] : [1, 2, 4], transparency: [0, 1, 0, 2, 0, 3]));
        Require(rgb.Alpha is { } rgbAlpha && rgbAlpha[0] == 0 && rgbAlpha[1] == 255, "RGB key: the key color is transparent.");

        var lowDepth = PdfPngDecoder.Decode(TestImages.Png(
            4, 1, colorType: 0, bitDepth: 2, (x, _) => [x], transparency: [0, 2]));
        Require(lowDepth.Alpha is { } lowAlpha && lowAlpha[2] == 0 && lowAlpha.Count(value => value == 0) == 1, "2-bit gray key uses the native sample value.");
    }

    private static void OmitsSoftMaskForOpaqueImages()
    {
        var decoded = PdfPngDecoder.Decode(TestImages.Png(3, 3, colorType: 6, bitDepth: 8, (_, _) => [1, 2, 3, 255]));
        Require(decoded.Alpha is null, "Fully opaque RGBA has no soft mask.");

        var document = PortableDocument.Create().Image(TestImages.RgbPng(4, 2, 255, 0, 0), ImageMediaType.Png).Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!Encoding.Latin1.GetString(buffer.ToArray()).Contains("/SMask", StringComparison.Ordinal), "Opaque PNG: no /SMask in the PDF.");
    }

    private static void RejectsInvalidDepthCombinations()
    {
        foreach (var (colorType, depth) in new[] { (2, 4), (3, 16), (6, 1), (4, 2), (0, 3) })
        {
            var png = TestImages.Png(1, 1, colorType, 8, (_, _) => new int[colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 }], palette: colorType == 3 ? [0, 0, 0] : null);
            png[24] = (byte)depth;
            try
            {
                PdfPngDecoder.Decode(png);
                throw new InvalidOperationException($"Color type {colorType} with {depth} bit must be rejected.");
            }
            catch (ImageException)
            {
            }
        }
    }

    private static void WritesInterlacedPaletteImageInWarnMode()
    {
        // Previously the whole PDF failed on such an image, even in warn mode.
        var png = TestImages.Png(20, 20, colorType: 3, bitDepth: 4, (x, y) => [(x / 5 + y / 5) % 3], interlaced: true, palette: [255, 0, 0, 0, 128, 0, 0, 0, 255]);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png).Build();
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "Interlaced 4-bit palette PNG is written without warnings.");
        Require(PdfStreams.Decompress(buffer.ToArray()).Contains("/Im1 Do", StringComparison.Ordinal), "Interlaced 4-bit palette PNG is drawn.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
