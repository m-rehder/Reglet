using Reglet;

/// <summary>Barcodes (ADR-0023): symbol tables, check digits, module layout and writing in the three formats.</summary>
internal static class BarcodeChecks
{
    internal static int Run()
    {
        try
        {
            Code128Table();
            Code128Values();
            EanCheckDigits();
            EanLayout();
            QrCapacities();
            QrLayout();
            RejectsInvalidContent();
            WritesAllFormats();
            Console.WriteLine("Barcodes: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Barcodes: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // The dark modules of a barcode shape (one per module of the given width), without the quiet zone.
    private static string Bits(DrawingShape shape, double moduleWidth, int quiet)
    {
        var total = (int)Math.Round(shape.Width.Points / moduleWidth) - 2 * quiet;
        var bits = new char[total];
        Array.Fill(bits, '0');
        var steps = shape.Path!;
        for (var i = 0; i < steps.Count; i += 5)
        {
            var from = (int)Math.Round(steps[i].X.Points / moduleWidth) - quiet;
            var to = (int)Math.Round(steps[i + 1].X.Points / moduleWidth) - quiet;
            for (var x = from; x < to; x++)
            {
                bits[x] = '1';
            }
        }

        return new string(bits);
    }

    private static void Code128Table()
    {
        var table = Barcode.Code128Patterns;
        Require(table.Length == 107, "Code 128: 107 symbols.");
        for (var i = 0; i < 106; i++)
        {
            Require(table[i].Length == 6 && table[i].Sum(c => c - '0') == 11, $"Code 128: symbol {i} has 6 widths and 11 modules.");
        }

        Require(table[106] == "2331112" && table[106].Sum(c => c - '0') == 13, "Code 128: the stop symbol has 13 modules.");
        Require(table.Distinct().Count() == 107, "Code 128: all symbols differ.");
    }

    private static void Code128Values()
    {
        Check("ABC", new[] { 104, 33, 34, 35 });
        Check("12345678", new[] { 105, 12, 34, 56, 78 });
        Check("12345", new[] { 104, 17, 99, 23, 45 }); // odd run: first digit in B, rest in C
        Check("ab12", new[] { 104, 65, 66, 17, 18 }); // fewer than 4 digits stay in B
        Check("A\tb", new[] { 104, 33, 98, 73, 66 }); // SHIFT for the control character
        Check("\tA", new[] { 103, 73, 33 });
        Check("AB1234ab", new[] { 104, 33, 34, 99, 12, 34, 100, 65, 66 });

        static void Check(string text, int[] expected)
        {
            var values = Barcode.Code128Values(text);
            var body = values.Take(values.Count - 2).ToArray();
            Require(body.SequenceEqual(expected), $"Code 128 values of '{text}': {string.Join(',', body)}");
            var sum = body[0] + body.Skip(1).Select((v, i) => (i + 1) * v).Sum();
            Require(values[^2] == sum % 103 && values[^1] == 106, $"Code 128 check symbol and stop of '{text}'.");
        }
    }

    private static void EanCheckDigits()
    {
        Require(Barcode.EanCheckDigit("400638133393") == 1, "EAN-13 4006381333931.");
        Require(Barcode.EanCheckDigit("003600029145") == 2, "EAN-13 0036000291452 (UPC-A 036000291452).");
        Require(Barcode.EanCheckDigit("9638507") == 4, "EAN-8 96385074.");
    }

    private static void EanLayout()
    {
        var module = 1.0;
        foreach (var (symbology, text, length) in new[]
        {
            (BarcodeSymbology.Ean13, "4006381333931", 13), (BarcodeSymbology.Ean13, "400638133393", 13),
            (BarcodeSymbology.Ean8, "96385074", 8), (BarcodeSymbology.Ean8, "9638507", 8),
        })
        {
            var shape = Barcode.Create(symbology, text, Length.FromPoints(module), Length.FromPoints(30), quietZoneModules: 9);
            var bits = Bits(shape, module, 9);
            var half = length / 2;
            Require(bits.Length == (length == 13 ? 95 : 67), "EAN length " + length);
            Require(bits.StartsWith("101") && bits.EndsWith("101") && bits.Substring(3 + 7 * half, 5) == "01010", "EAN guard patterns " + length);
            var full = text.Length == length ? text : text + Barcode.EanCheckDigit(text);
            var parity = length == 13 ? Barcode.EanParity[full[0] - '0'] : "LLLL";
            var digits = length == 13 ? full[1..] : full;
            for (var i = 0; i < length - (length == 13 ? 1 : 0); i++)
            {
                var at = i < half ? 3 + 7 * i : 3 + 7 * half + 5 + 7 * (i - half);
                var group = bits.Substring(at, 7);
                var d = digits[i] - '0';
                var expected = i >= half ? new string(Barcode.EanL[d].Select(c => c == '0' ? '1' : '0').ToArray())
                    : parity[i] == 'L' ? Barcode.EanL[d] : Barcode.EanG[d];
                Require(group == expected, $"EAN-{length} digit {i}.");
            }

            Require(shape.AltText == text && shape.Kind == ShapeKind.Freeform, "Shape kind and alt text.");
        }

        var code = Barcode.Create(BarcodeSymbology.Code128, "ABC", Length.FromPoints(1), Length.FromPoints(10));
        Require(Math.Abs(code.Width.Points - (11 * 5 + 13 + 20)) < 1e-9, "Code 128 width: symbols plus quiet zone of 10 modules per side.");
        Require(Math.Abs(code.Height.Points - 10) < 1e-9 && code.Fill == Color.Black, "Code 128 height and color.");
    }

    private static void QrCapacities()
    {
        // Byte capacity (data codewords minus mode and count bits) of known versions, ISO/IEC 18004 table 7.
        foreach (var (version, expected) in new[]
        {
            (1, new[] { 17, 14, 11, 7 }), (10, new[] { 271, 213, 151, 119 }), (40, new[] { 2953, 2331, 1663, 1273 }),
        })
        {
            for (var level = 0; level < 4; level++)
            {
                var bits = QrEncoder.DataCodewords(version, (QrErrorCorrection)level) * 8 - 4 - (version < 10 ? 8 : 16);
                Require(bits / 8 == expected[level], $"QR version {version} level {level}: byte capacity {bits / 8}.");
            }
        }

        foreach (var (length, level, size) in new[]
        {
            (17, QrErrorCorrection.Low, 21), (18, QrErrorCorrection.Low, 25), (14, QrErrorCorrection.Medium, 21), (15, QrErrorCorrection.Medium, 25),
            (2953, QrErrorCorrection.Low, 177), (1273, QrErrorCorrection.High, 177),
        })
        {
            Require(QrEncoder.Encode(new string('x', length), level).GetLength(0) == size, $"QR size for {length} bytes at {level}.");
        }

        Require(QrEncoder.AlignmentPositions(1).Count == 0, "QR version 1 has no alignment pattern.");
        foreach (var (version, expected) in new[]
        {
            (2, "6,18"), (7, "6,22,38"), (14, "6,26,46,66"), (32, "6,34,60,86,112,138"), (40, "6,30,58,86,114,142,170"),
        })
        {
            Require(string.Join(',', QrEncoder.AlignmentPositions(version)) == expected, "QR alignment positions of version " + version);
        }

        Require(QrEncoder.Encode("0123456789", QrErrorCorrection.Low).GetLength(0) == 21, "QR numeric text of 10 digits fits version 1.");
        Require(QrEncoder.Encode("HELLO WORLD", QrErrorCorrection.Quartile).GetLength(0) == 21, "QR alphanumeric HELLO WORLD at Q is version 1.");
        Require(QrEncoder.Encode("héllo €", QrErrorCorrection.Low).GetLength(0) == 21, "QR UTF-8 text.");
    }

    private static void QrLayout()
    {
        var grid = QrEncoder.Encode("Reglet", QrErrorCorrection.Medium);
        var size = grid.GetLength(0);
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Require(grid[cy + dy, cx + dx] == (ring != 2), "QR finder pattern at " + cx + "," + cy);
                }
            }
        }

        for (var i = 8; i < size - 8; i++)
        {
            Require(grid[6, i] == (i % 2 == 0) && grid[i, 6] == (i % 2 == 0), "QR timing pattern.");
        }

        Require(grid[size - 8, 8], "QR dark module.");

        var shape = Barcode.Create(BarcodeSymbology.QrCode, "Reglet", Length.FromPoints(2));
        Require(Math.Abs(shape.Width.Points - (size + 8) * 2) < 1e-9 && Math.Abs(shape.Height.Points - shape.Width.Points) < 1e-9, "QR shape is square with a quiet zone of 4 modules.");
        Require(shape.AltText == "Reglet", "QR alt text.");
        Require(Barcode.Create(BarcodeSymbology.QrCode, "x", Length.FromPoints(2), quietZoneModules: 0).Width.Points == 42, "QR without quiet zone.");
    }

    private static void RejectsInvalidContent()
    {
        foreach (var make in new Func<DrawingShape>[]
        {
            () => Barcode.Create(BarcodeSymbology.Ean13, "12345"),
            () => Barcode.Create(BarcodeSymbology.Ean13, "400638133393A"),
            () => Barcode.Create(BarcodeSymbology.Ean13, "4006381333932"),
            () => Barcode.Create(BarcodeSymbology.Ean8, "123456"),
            () => Barcode.Create(BarcodeSymbology.Code128, ""),
            () => Barcode.Create(BarcodeSymbology.Code128, "ä"),
            () => Barcode.Create(BarcodeSymbology.Code128, "A", Length.Zero),
            () => Barcode.Create((BarcodeSymbology)99, "A"),
            () => Barcode.Create(BarcodeSymbology.QrCode, ""),
            () => Barcode.Create(BarcodeSymbology.QrCode, new string('x', 2954), errorCorrection: QrErrorCorrection.Low),
            () => Barcode.Create(BarcodeSymbology.QrCode, "x", errorCorrection: (QrErrorCorrection)9),
        })
        {
            var threw = false;
            try
            {
                make();
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Require(threw, "Invalid barcode input is rejected.");
        }
    }

    private static void WritesAllFormats()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            Barcode.Create(BarcodeSymbology.Code128, "Reglet-0023"),
            Barcode.Create(BarcodeSymbology.Ean13, "4006381333931"),
            Barcode.Create(BarcodeSymbology.QrCode, "https://example.org/"),
            new Paragraph(new Inline[] { new TextRun("after") }),
        }));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            using var buffer = new MemoryStream();
            var result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            Require(!result.HasWarnings, format + ": no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
            Require(buffer.Length > 0, format + ": output.");
            if (format != DocumentFormat.Pdf)
            {
                buffer.Position = 0;
                var read = DocumentReader.Read(buffer, format);
                var shapes = read.Document.Sections[0].Blocks.OfType<DrawingShape>().ToList();
                Require(shapes.Count == 3 && shapes[0].AltText == "Reglet-0023", format + ": all barcodes come back as shapes with their alt text.");
            }
        }
    }
}
