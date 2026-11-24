using System.Globalization;
using System.IO.Compression;
using Reglet;

/// <summary>
/// ADR-0043: workbooks. Sheet names, the XLSX writer (cells, formulas, styles, geometry, images, page setup), the reader's
/// round trip, determinism and Excel's number formats as text.
/// </summary>
internal static class WorkbookChecks
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAEUlEQVR4nGP4z8DwHwQZGBgAJPIF+08CpZgAAAAASUVORK5CYII=");

    internal static int Run()
    {
        try
        {
            ChecksSheetNames();
            WritesAndReadsWorkbook();
            WritesDeterministically();
            FormatsNumbers();
            RejectsBrokenInput();
            Console.WriteLine("Workbooks (XLSX): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Workbooks (XLSX): " + exception);
            return 1;
        }
    }

    private static void ChecksSheetNames()
    {
        var workbook = new Workbook();
        foreach (var name in new[] { "", new string('x', 32), "a[b", "a]b", "a:b", "a*b", "a?b", "a/b", "a\\b", "'quoted", "quoted'" })
        {
            Require(!Workbook.IsValidSheetName(name), $"'{name}' is rejected.");
            Throws<ArgumentException>(() => workbook.AddWorksheet(name), $"AddWorksheet rejects '{name}'.");
        }

        Require(Workbook.IsValidSheetName(new string('x', 31)) && Workbook.IsValidSheetName("Room 1.02 (East)"), "31 characters and punctuation are fine.");
        workbook.AddWorksheet("Costs");
        Throws<ArgumentException>(() => workbook.AddWorksheet("COSTS"), "A name used ignoring case is rejected.");
        Require(workbook["costs"].Name == "Costs", "The indexer ignores case.");
    }

    private static Workbook Sample()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("Calc 'A' 1");
        sheet.Cell("A1").Value = "Item";
        sheet.Cell("B1").Value = " padded ";
        sheet.Cell(2, 1).Value = 2;
        sheet.Cell(2, 2).Value = 12.5m;
        sheet.Cell("C2").Formula = "=PRODUCT(A2,B2)";
        sheet.Cell("C3").Formula = "SUM(C2:C2)";
        sheet.Cell("A4").Value = new DateTime(2026, 11, 24);
        sheet.Cell("B4").Value = true;
        sheet.Cell("A5").Value = new RichText(new RichTextRun("Terms: ", new CellFont { Bold = true }), new RichTextRun("apply"));
        sheet.Cell("A6").Value = 19.0;
        sheet.Cell("A6").Style = new CellStyle { NumberFormat = @"#,##0.00\ [$%-1]_);" };
        sheet.Cell("B6").Value = "text";
        sheet.Cell("B6").Style = new CellStyle { NumberFormat = "@" };
        sheet.SetStyle("A1:B1", style => style with
        {
            Font = new CellFont { Name = "Arial", Size = 12, Bold = true, Color = Color.FromHex("FF0000") },
            Fill = Color.FromHex("D9D9D9"),
            HorizontalAlignment = CellHorizontalAlignment.Center,
            VerticalAlignment = CellVerticalAlignment.Top,
            WrapText = true,
            Indent = 2,
        });
        sheet.SetOutsideBorder("A1:B2", new CellBorderLine(CellBorderStyle.Thick, Color.FromHex("0000FF")));
        sheet.Cell("C3").Style = sheet.Cell("C3").Style with { Border = new CellBorders { Top = new CellBorderLine(CellBorderStyle.Medium) } };
        sheet.Merge("A8:C8");
        sheet.ColumnWidths[1] = 30.5;
        sheet.RowHeights[8] = 40;
        sheet.RowHeights[20] = 12;
        sheet.Images.Add(new WorksheetImage(Png, 10, 2) { WidthPixels = 80, HeightPixels = 40, AlternativeText = "Logo" });
        sheet.PageSetup = new SheetPageSetup
        {
            Orientation = PageOrientation.Landscape,
            PaperSize = SheetPaperSize.Letter,
            FitToPagesWide = 1,
            FitToPagesTall = 0,
            Margins = new PageMargins(Length.FromInches(0.5)),
            PrintArea = "C10:A1",
            RepeatRows = (1, 2),
        };
        workbook.AddWorksheet("Second").Cell("A1").Value = "x";
        return workbook;
    }

    private static byte[] Write(Workbook workbook, out WriteResult result)
    {
        using var buffer = new MemoryStream();
        result = new XlsxWorkbookWriter().WriteAsync(workbook, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void WritesAndReadsWorkbook()
    {
        var bytes = Write(Sample(), out var result);
        Require(!result.HasWarnings, "A clean workbook writes without warnings.");

        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            string Part(string name) => new StreamReader(zip.GetEntry(name)!.Open()).ReadToEnd();
            var book = Part("xl/workbook.xml");
            Require(book.Contains("fullCalcOnLoad=\"1\"", StringComparison.Ordinal), "Excel is told to calculate on load.");
            Require(book.Contains("<definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">'Calc ''A'' 1'!$A$1:$C$10</definedName>", StringComparison.Ordinal),
                "The print area is a defined name with a quoted sheet name.");
            Require(book.Contains("'Calc ''A'' 1'!$1:$2", StringComparison.Ordinal), "The repeated rows are a defined name.");
            Require(book.IndexOf("definedNames", StringComparison.Ordinal) < book.IndexOf("calcPr", StringComparison.Ordinal), "definedNames precede calcPr.");

            var sheet = Part("xl/worksheets/sheet1.xml");
            Require(sheet.Contains("<c r=\"C2\"><f>PRODUCT(A2,B2)</f></c>", StringComparison.Ordinal), "A formula is written without '=' and without a cached value.");
            Require(sheet.Contains("<pageSetUpPr fitToPage=\"1\" />", StringComparison.Ordinal)
                && sheet.Contains("<pageSetup paperSize=\"1\" fitToWidth=\"1\" fitToHeight=\"0\" orientation=\"landscape\" />", StringComparison.Ordinal),
                "Fit to one page wide, Letter, landscape.");
            Require(sheet.Contains("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.5\" bottom=\"0.5\"", StringComparison.Ordinal), "Margins in inches.");
            Require(sheet.Contains("<col min=\"1\" max=\"1\" width=\"30.5\" customWidth=\"1\" />", StringComparison.Ordinal), "Column width in characters.");
            Require(sheet.Contains("<row r=\"20\" ht=\"12\" customHeight=\"1\" />", StringComparison.Ordinal), "An empty row keeps its height.");
            Require(sheet.Contains("<mergeCell ref=\"A8:C8\" />", StringComparison.Ordinal), "Merged range.");
            Require(sheet.IndexOf("mergeCells", StringComparison.Ordinal) < sheet.IndexOf("pageMargins", StringComparison.Ordinal)
                && sheet.IndexOf("pageSetup ", StringComparison.Ordinal) < sheet.IndexOf("<drawing", StringComparison.Ordinal), "Worksheet children in schema order.");

            var drawing = Part("xl/drawings/drawing1.xml");
            Require(drawing.Contains("<xdr:col>1</xdr:col>", StringComparison.Ordinal) && drawing.Contains("<xdr:row>9</xdr:row>", StringComparison.Ordinal)
                && drawing.Contains($"cx=\"{80 * 9525}\" cy=\"{40 * 9525}\"", StringComparison.Ordinal) && drawing.Contains("descr=\"Logo\"", StringComparison.Ordinal),
                "The image is anchored at B10 with its pixel size.");
            Require(zip.GetEntry("xl/media/image1.png") is not null, "The image bytes are in the package.");

            var styles = Part("xl/styles.xml");
            Require(styles.Contains("formatCode=\"#,##0.00\\ [$%-1]_);\"", StringComparison.Ordinal) && styles.Contains("formatCode=\"@\"", StringComparison.Ordinal),
                "Number formats are custom codes.");
            Require(styles.Contains("<b /><sz val=\"12\" /><color rgb=\"FFFF0000\" /><name val=\"Arial\" />", StringComparison.Ordinal), "Font in schema order.");
            Require(styles.Contains("<patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\" />", StringComparison.Ordinal), "Solid fill.");
            Require(styles.Contains("<alignment horizontal=\"center\" vertical=\"top\" wrapText=\"1\" indent=\"2\" />", StringComparison.Ordinal), "Alignment.");
            Require(Part("xl/sharedStrings.xml").Contains("<t xml:space=\"preserve\"> padded </t>", StringComparison.Ordinal), "Spaces are preserved.");
        }

        var read = new XlsxWorkbookReader().Read(new MemoryStream(bytes));
        Require(read.Worksheets.Select(sheet => sheet.Name).SequenceEqual(["Calc 'A' 1", "Second"]), "Sheets and their order are read back.");
        var first = read.Worksheets[0];
        Require(first.Cell("A1").Value is "Item" && first.Cell("B1").Value is " padded " && first.Cell("A2").Value is 2.0 && first.Cell("B2").Value is 12.5,
            "Text and numbers are read back.");
        Require(first.Cell("C2").Formula == "PRODUCT(A2,B2)" && first.Cell("C2").Value is null, "A formula is read back without a value.");
        Require(first.Cell("A4").Value is DateTime date && date == new DateTime(2026, 11, 24) && first.Cell("A4").Style.NumberFormat == "m/d/yyyy",
            "A date gets the short date format and is read back as a date.");
        Require(first.Cell("B4").Value is true, "A boolean is read back.");
        Require(first.Cell("A5").Value is RichText { Runs: [{ Text: "Terms: ", Font.Bold: true }, { Text: "apply", Font: null }] }, "Rich text is read back.");
        var header = first.Cell("A1").Style;
        Require(header.Font == new CellFont { Name = "Arial", Size = 12, Bold = true, Color = Color.FromHex("FF0000") }
            && header.Fill == Color.FromHex("D9D9D9") && header.HorizontalAlignment == CellHorizontalAlignment.Center
            && header.VerticalAlignment == CellVerticalAlignment.Top && header.WrapText && header.Indent == 2,
            "Font, fill and alignment are read back.");
        Require(header.Border.Top == new CellBorderLine(CellBorderStyle.Thick, Color.FromHex("0000FF")) && header.Border.Left is not null && header.Border.Right is null
            && first.Cell("B2").Style.Border is { Right: not null, Bottom: not null, Top: null }, "The outside border reaches the edge cells only.");
        Require(first.Cell("C3").Style.Border.Top?.Style == CellBorderStyle.Medium, "A single side.");
        Require(first.MergedRanges.Single().ToString() == "A8:C8" && first.ColumnWidths[1] == 30.5 && first.RowHeights[8] == 40, "Geometry is read back.");
        var setup = first.PageSetup;
        Require(setup.Orientation == PageOrientation.Landscape && setup.PaperSize == SheetPaperSize.Letter && setup.FitToPagesWide == 1
            && setup.FitToPagesTall == 0 && setup.PrintArea == "A1:C10" && setup.RepeatRows == (1, 2)
            && Math.Abs(setup.Margins.Left.Points - 36) < 0.01, "The page setup is read back.");

        var us = CultureInfo.GetCultureInfo("en-US");
        Require(first.Cell("A6").GetFormattedText(us) == "19.00 % " && first.Cell("A4").GetFormattedText(us) == "11/24/2026"
            && first.Cell("B2").GetFormattedText(us) == "12.5" && first.Cell("B6").GetFormattedText(us) == "text"
            && first.Cell("A5").GetFormattedText(us) == "Terms: apply", "Formatted text as Excel shows it.");
    }

    private static void WritesDeterministically()
    {
        var first = Write(Sample(), out _);
        var second = Write(Sample(), out _);
        Require(first.SequenceEqual(second), "Two writes of the same workbook are byte-identical.");

        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("M");
        sheet.Merge("A1:B1");
        sheet.Cell("B1").Value = "hidden";
        Write(workbook, out var result);
        Require(result.Warnings.Any(warning => warning.Message.Contains("B1", StringComparison.Ordinal)), "A value hidden by a merge is reported.");
        Throws<DocumentValidationException>(() => Write(new Workbook(), out _), "A workbook without a sheet is rejected.");
        Throws<ArgumentException>(() => sheet.Merge("B1:C1"), "Overlapping merges are rejected.");
    }

    private static void FormatsNumbers()
    {
        var us = CultureInfo.GetCultureInfo("en-US");
        var de = CultureInfo.GetCultureInfo("de-DE");
        (object Value, string? Code, string Expected, CultureInfo Culture)[] cases =
        [
            (1234.5, "#,##0.00", "1,234.50", us),
            (1234.5, "#,##0.00", "1.234,50", de),
            (1234.5, "0.00", "1234.50", us),
            (1234.5, "0", "1235", us),
            (2.5, "0", "3", us),
            (1234.5, "$#,##0.00", "$1,234.50", us),
            (-5.0, "$#,##0.00", "-$5.00", us),
            (-5.0, "0.00;(0.00)", "(5.00)", us),
            (0.0, "0;-0;\"zero\"", "zero", us),
            ("abc", "0;-0;0;\"<\"@\">\"", "<abc>", us),
            (0.125, "0%", "13%", us),
            (1234567.0, "#,##0,", "1,235", us),
            (1.5, "0.0#", "1.5", us),
            (1.25, "0.0#", "1.25", us),
            (0.3, null, "0.3", us),
            (0.1 + 0.2, "General", "0.3", us),
            (12345678901234.0, null, "1.23457E+13", us),
            (19.0, @"#,##0.00\ [$%-1]_);", "19.00 % ", us),
            (-19.0, @"#,##0.00\ [$%-1]_);", "", us),
            (5.0, "@", "5", us),
            (new DateTime(2026, 3, 7, 14, 5, 9), "m/d/yyyy", "3/7/2026", us),
            (new DateTime(2026, 3, 7, 14, 5, 9), "d-mmm-yy", "7-Mar-26", us),
            (new DateTime(2026, 3, 7, 14, 5, 9), "h:mm AM/PM", "2:05 PM", us),
            (new DateTime(2026, 3, 7, 14, 5, 9), "mm:ss", "05:09", us),
            (new DateTime(2026, 3, 7, 14, 5, 9), "dddd, mmmm d", "Saturday, March 7", us),
            (new DateTime(2026, 3, 7), "dd.mm.yyyy", "07.03.2026", de),
            (true, null, "TRUE", us),
        ];
        foreach (var (value, code, expected, culture) in cases)
        {
            var cell = new Workbook().AddWorksheet("F").Cell("A1");
            cell.Value = value;
            cell.Style = new CellStyle { NumberFormat = code };
            var text = cell.GetFormattedText(culture);
            Require(text == expected, $"{value} with '{code}' shows '{expected}' ({culture.Name}), got '{text}'.");
        }
    }

    private static void RejectsBrokenInput()
    {
        Throws<DocumentValidationException>(() => new XlsxWorkbookReader().Read(new MemoryStream([1, 2, 3])), "A non-ZIP input is reported as invalid.");
        Throws<ArgumentException>(() => new Workbook().AddWorksheet("S").Cell("A0"), "Row 0 is not a cell.");
        Throws<ArgumentException>(() => new Workbook().AddWorksheet("S").Cell("A1").Value = new object(), "An unsupported value type is rejected.");
        Throws<ArgumentException>(() => new Workbook().AddWorksheet("S").Cell("A1").Value = double.NaN, "NaN is rejected.");
    }

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
