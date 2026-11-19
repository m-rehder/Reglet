using System.Globalization;

namespace Reglet;

/// <summary>The symbology of a barcode created with <see cref="Barcode"/> (ADR-0023).</summary>
public enum BarcodeSymbology
{
    /// <summary>Code 128 (ISO/IEC 15417), character codes 0–127.</summary>
    Code128,

    /// <summary>EAN-13: 12 digits plus check digit (the check digit may be omitted).</summary>
    Ean13,

    /// <summary>EAN-8: 7 digits plus check digit (the check digit may be omitted).</summary>
    Ean8,

    /// <summary>QR code (ISO/IEC 18004): any text, versions 1-40, square.</summary>
    QrCode,
}

/// <summary>
/// Creates barcodes as vector shapes (ADR-0023): a <see cref="DrawingShape"/> of kind freeform whose
/// path is the dark modules. The shape is transparent around the code; put it on a light surface.
/// </summary>
public static class Barcode
{
    internal static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    internal static readonly string[] EanL =
    {
        "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011",
    };

    internal static readonly string[] EanG =
    {
        "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111",
    };

    internal static readonly string[] EanParity =
    {
        "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL",
    };

    /// <summary>Creates a barcode shape.</summary>
    /// <param name="symbology">The symbology.</param>
    /// <param name="content">The content (EAN: digits only).</param>
    /// <param name="moduleWidth">The width of the narrowest bar; 0.33 mm when null.</param>
    /// <param name="height">The height of the bars; 15 mm when null; ignored by a QR code, which is square.</param>
    /// <param name="color">The color of the bars; black when null.</param>
    /// <param name="quietZoneModules">The empty width around the code in modules; 10 left and right for a 1D code (no margin above and below), 4 on all sides for a QR code, when null.</param>
    /// <param name="errorCorrection">The error correction level of a QR code; ignored by the other symbologies.</param>
    /// <returns>A shape whose alt text is the content.</returns>
    /// <exception cref="ArgumentException">The content cannot be encoded in the symbology.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or the symbology is unknown.</exception>
    public static DrawingShape Create(
        BarcodeSymbology symbology, string content, Length? moduleWidth = null, Length? height = null,
        Color? color = null, int? quietZoneModules = null,
        QrErrorCorrection errorCorrection = QrErrorCorrection.Medium)
    {
        ArgumentNullException.ThrowIfNull(content);
        var module = moduleWidth ?? Length.FromMillimeters(0.33);
        var barHeight = height ?? Length.FromMillimeters(15);
        var quiet = quietZoneModules ?? (symbology == BarcodeSymbology.QrCode ? 4 : 10);
        if (module.Points <= 0 || barHeight.Points <= 0 || quiet < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleWidth), "Sizes must be positive.");
        }

        if (symbology == BarcodeSymbology.QrCode)
        {
            if (content.Length == 0 || !Enum.IsDefined(errorCorrection))
            {
                throw new ArgumentException("A QR code needs content and a known error correction level.", nameof(content));
            }

            return Shape(QrRows(QrEncoder.Encode(content, errorCorrection)), module, module, quiet, color, content);
        }

        var modules = symbology switch
        {
            BarcodeSymbology.Code128 => EncodeCode128(content),
            BarcodeSymbology.Ean13 => EncodeEan(content, 13),
            BarcodeSymbology.Ean8 => EncodeEan(content, 8),
            _ => throw new ArgumentOutOfRangeException(nameof(symbology), symbology, "Unknown symbology."),
        };

        // A 1D code is one row; its quiet zone is only on the sides.
        return Shape(new[] { modules }, module, barHeight, quiet, color, content, quietVertical: false);
    }

    private static bool[][] QrRows(bool[,] grid)
    {
        var rows = new bool[grid.GetLength(0)][];
        for (var y = 0; y < rows.Length; y++)
        {
            rows[y] = new bool[grid.GetLength(1)];
            for (var x = 0; x < rows[y].Length; x++)
            {
                rows[y][x] = grid[y, x];
            }
        }

        return rows;
    }

    // Dark runs of every row become rectangles of one path (one fill, so no seams between rows).
    private static DrawingShape Shape(
        bool[][] rows, Length module, Length rowHeight, int quiet, Color? color, string content,
        bool quietVertical = true)
    {
        var width = rows[0].Length;
        var path = new List<ShapePathStep>();
        var top = quietVertical ? quiet * module.Points : 0;
        for (var r = 0; r < rows.Length; r++)
        {
            var y0 = Length.FromPoints(top + r * rowHeight.Points);
            var y1 = Length.FromPoints(top + (r + 1) * rowHeight.Points);
            for (var i = 0; i < width;)
            {
                if (!rows[r][i])
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < width && rows[r][i])
                {
                    i++;
                }

                var x0 = Length.FromPoints((quiet + start) * module.Points);
                var x1 = Length.FromPoints((quiet + i) * module.Points);
                path.Add(new ShapePathStep(ShapePathCommand.MoveTo, x0, y0));
                path.Add(new ShapePathStep(ShapePathCommand.LineTo, x1, y0));
                path.Add(new ShapePathStep(ShapePathCommand.LineTo, x1, y1));
                path.Add(new ShapePathStep(ShapePathCommand.LineTo, x0, y1));
                path.Add(new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero));
            }
        }

        var height = Length.FromPoints(2 * top + rows.Length * rowHeight.Points);
        return new DrawingShape(ShapeKind.Freeform, Length.FromPoints((width + 2 * quiet) * module.Points), height, path: path)
        {
            Fill = color ?? Color.Black,
            AltText = content,
        };
    }

    /// <summary>The check digit of an EAN-13 or EAN-8 number without its check digit.</summary>
    /// <param name="digits">The digits (7 or 12).</param>
    /// <returns>The check digit 0–9.</returns>
    /// <exception cref="ArgumentException">The text is not 7 or 12 digits.</exception>
    public static int EanCheckDigit(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length is not (7 or 12) || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Expected 7 or 12 digits.", nameof(digits));
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            // The digit next to the check digit weighs 3, then 1, 3, 1 ...
            sum += (digits[digits.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10;
    }

    /// <summary>The Code 128 symbol values of the content, including start, check and stop (for tests).</summary>
    internal static IReadOnlyList<int> Code128Values(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Any(c => c > 127))
        {
            throw new ArgumentException("Code 128 needs at least one character with code 0-127.", nameof(content));
        }

        var values = new List<int>();
        var set = '\0';
        for (var i = 0; i < content.Length;)
        {
            var run = 0;
            while (i + run < content.Length && char.IsAsciiDigit(content[i + run]))
            {
                run++;
            }

            if (run >= 4 || (i == 0 && run == content.Length && run >= 2))
            {
                if (run % 2 == 1)
                {
                    // An odd run: its first digit goes into the current set so the rest pairs up.
                    if (set == '\0')
                    {
                        set = 'B';
                        values.Add(104);
                    }

                    values.Add(content[i] - 32);
                    i++;
                    run--;
                }

                values.Add(set == '\0' ? 105 : 99);
                set = 'C';
                for (var end = i + run; i < end; i += 2)
                {
                    values.Add((content[i] - '0') * 10 + content[i + 1] - '0');
                }

                continue;
            }

            var c = content[i++];
            if (set is '\0' or 'C')
            {
                var start = set == '\0';
                set = c < 32 ? 'A' : 'B';
                values.Add(start ? (set == 'A' ? 103 : 104) : (set == 'A' ? 101 : 100));
            }

            if (set == 'A' ? c >= 96 : c < 32)
            {
                values.Add(98); // SHIFT: one character from the other set
                values.Add(set == 'A' ? c - 32 : c + 64);
            }
            else
            {
                values.Add(set == 'A' && c < 32 ? c + 64 : c - 32);
            }
        }

        var sum = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            sum += i * values[i];
        }

        values.Add(sum % 103);
        values.Add(106);
        return values;
    }

    private static bool[] EncodeCode128(string content)
    {
        var modules = new List<bool>();
        foreach (var value in Code128Values(content))
        {
            var dark = true;
            foreach (var width in Code128Patterns[value])
            {
                for (var k = 0; k < width - '0'; k++)
                {
                    modules.Add(dark);
                }

                dark = !dark;
            }
        }

        return modules.ToArray();
    }

    private static bool[] EncodeEan(string content, int length)
    {
        if (!content.All(char.IsAsciiDigit) || content.Length != length && content.Length != length - 1)
        {
            throw new ArgumentException($"EAN-{length} needs {length - 1} or {length} digits.", nameof(content));
        }

        var body = content[..(length - 1)];
        var check = EanCheckDigit(body);
        if (content.Length == length && content[length - 1] - '0' != check)
        {
            throw new ArgumentException("The check digit is wrong; expected " + check.ToString(CultureInfo.InvariantCulture) + ".", nameof(content));
        }

        var digits = body + (char)('0' + check);
        var bits = new System.Text.StringBuilder("101");
        var half = length / 2;
        var skip = length == 13 ? 1 : 0;
        var parity = length == 13 ? EanParity[digits[0] - '0'] : "LLLL";
        for (var i = 0; i < half; i++)
        {
            var d = digits[skip + i] - '0';
            bits.Append(parity[i] == 'L' ? EanL[d] : EanG[d]);
        }

        bits.Append("01010");
        for (var i = 0; i < half; i++)
        {
            var d = digits[skip + half + i] - '0';
            bits.Append(EanL[d].Select(b => b == '0' ? '1' : '0').ToArray());
        }

        bits.Append("101");
        return bits.ToString().Select(b => b == '1').ToArray();
    }
}
