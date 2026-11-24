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
            ReadsExcelFeatures();
            ReadsForeignWorkbooksTolerantly();
            WidthsAndIndentsAsExcelShowsThem();
            EvaluatesFormulas();
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
        sheet.ColumnWidths[1] = 40;
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
            Require(sheet.Contains("<col min=\"1\" max=\"1\" width=\"40.7109375\" customWidth=\"1\" />", StringComparison.Ordinal),
                "A shown width of 40 is stored with Excel's 5 px padding (ECMA-376 18.3.1.13).");
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
        Require(first.MergedRanges.Single().ToString() == "A8:C8" && first.ColumnWidths[1] == 40 && first.RowHeights[8] == 40, "Geometry is read back.");
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
            (150.0, "[>=100]\"high\";[<0]\"neg\";\"low\"", "high", us),
            (-5.0, "[>=100]\"high\";[<0]\"neg\";\"low\"", "neg", us),
            (5.0, "[>=100]\"high\";[<0]\"neg\";\"low\"", "low", us),
            (-5.0, "[Red][<=0]0.00;0.00", "5.00", us),
            (1234.5, "[>1000]#,##0,\"K\";0", "1K", us),
            (999.0, "[>1000]#,##0,\"K\";0", "999", us),
            (5.0, "[>100]0", "########", us),
            (12345.678, "0.00E+00", "1.23E+04", us),
            (0.000123, "0.00E+00", "1.23E-04", us),
            (0.0, "0.00E+00", "0.00E+00", us),
            (-12345.0, "0.0E+0", "-1.2E+4", us),
            (99999.0, "0.0E+0", "1.0E+5", us),
            (12345.0, "##0.0E+0", "12.3E+3", us),
            (12345.0, "0.0e-0", "1,2e4", de),
            (1.5, "# ?/?", "1 1/2", us),
            (0.75, "# ?/?", " 3/4", us),
            (3.14159, "# ??/??", "3 14/99", us),
            (2.5, "?/?", "5/2", us),
            (1.3, "# ?/8", "1 2/8", us),
            (5.0, "# ?/?", "5    ", us),
            (-1.5, "# ?/?", "-1 1/2", us),
            (1.0625, "[h]:mm", "25:30", us),
            (1.0625, "[mm]:ss", "1530:00", us),
            (1.0625, "[ss]", "91800", us),
            (0.5 + (0.25 / 86400), "hh:mm:ss.00", "12:00:00.25", us),
            (0.5 + (0.25 / 86400), "hh:mm:ss.0", "12:00:00,3", de),
            (0.5 + (0.6 / 86400), "hh:mm:ss", "12:00:01", us),
            (123456789.0, "000-00-0000", "123-45-6789", us),
            (12.0, "000-00-0000", "000-00-0012", us),
            (4155551234.0, "(000) 000-0000", "(415) 555-1234", us),
            (9876543210.0, "00-0000", "987654-3210", us),
            (12.345, "0.0 0", "12.3 5", us),
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

    /// <summary>A package as Excel saves it: theme and indexed colours with tint, a shared formula and a two-cell picture.</summary>
    private static void ReadsExcelFeatures()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        var parts = new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{pkg}\">"
                + $"<Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{rel}/styles\" Target=\"styles.xml\"/>"
                + $"<Relationship Id=\"rId3\" Type=\"{rel}/theme\" Target=\"theme/theme1.xml\"/></Relationships>",
            ["xl/theme/theme1.xml"] = $"<a:theme xmlns:a=\"{a}\"><a:themeElements><a:clrScheme name=\"Office\">"
                + "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
                + "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1>"
                + "</a:clrScheme></a:themeElements></a:theme>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{main}\">"
                + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color theme=\"4\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor theme=\"0\" tint=\"-0.249977111117893\"/></patternFill></fill></fills>"
                + "<borders count=\"2\"><border/><border><bottom style=\"thin\"><color indexed=\"10\"/></bottom></border></borders>"
                + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\"/></cellXfs></styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheetData>"
                + "<row r=\"1\"><c r=\"A1\" s=\"1\"><v>1</v></c><c r=\"B1\"><v>2</v></c><c r=\"C1\"><f t=\"shared\" ref=\"C1:C3\" si=\"0\">A1*B1+$A$1+SUM(A1:B1)&amp;\"A1\"</f><v>5</v></c></row>"
                + "<row r=\"2\"><c r=\"C2\"><f t=\"shared\" si=\"0\"/><v>7</v></c></row>"
                + "<row r=\"3\"><c r=\"C3\"><f t=\"shared\" si=\"0\"/></c><c r=\"D3\"><f t=\"shared\" si=\"0\"/></c></row>"
                + "</sheetData><drawing r:id=\"rId1\"/></worksheet>",
            ["xl/worksheets/_rels/sheet1.xml.rels"] = $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>",
            ["xl/drawings/drawing1.xml"] = $"<xdr:wsDr xmlns:xdr=\"{xdr}\" xmlns:a=\"{a}\" xmlns:r=\"{rel}\"><xdr:twoCellAnchor>"
                + "<xdr:from><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>4</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>"
                + "<xdr:to><xdr:col>5</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>8</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>"
                + "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\" descr=\"Chart\"/><xdr:cNvPicPr/></xdr:nvPicPr>"
                + "<xdr:blipFill><a:blip r:embed=\"rId1\"/></xdr:blipFill><xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"952500\" cy=\"476250\"/></a:xfrm></xdr:spPr></xdr:pic>"
                + "<xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>",
            ["xl/drawings/_rels/drawing1.xml.rels"] = $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/image\" Target=\"../media/image1.png\"/></Relationships>",
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(xml);
            }

            using var image = zip.CreateEntry("xl/media/image1.png").Open();
            image.Write(Png);
        }

        buffer.Position = 0;
        var sheet = new XlsxWorkbookReader().Read(buffer).Worksheets[0];
        var style = sheet.Cell("A1").Style;
        Require(style.Font.Color == Color.FromHex("4472C4") && style.Font.Bold, "A theme font colour (accent 1) is resolved.");
        Require(style.Fill == Color.FromHex("BFBFBF"), $"Background 1 darker 25 % is BFBFBF, got {style.Fill}.");
        Require(style.Border.Bottom?.Color == Color.FromHex("FF0000"), "An indexed colour (10 = red) is resolved.");
        Require(sheet.Cell("C1").Formula == "A1*B1+$A$1+SUM(A1:B1)&\"A1\"", "The shared formula's first cell keeps its text.");
        Require(sheet.Cell("C2").Formula == "A2*B2+$A$1+SUM(A2:B2)&\"A1\"" && sheet.Cell("C2").Value is 7.0,
            "A dependent cell gets the formula moved one row, absolute references and text stay; its cached value is kept.");
        Require(sheet.Cell("D3").Formula == "B3*C3+$A$1+SUM(B3:C3)&\"A1\"", "A dependent cell to the right moves the columns too.");
        var picture = sheet.Images.Single();
        Require(picture.Row == 5 && picture.Column == 4 && picture.WidthPixels == 100 && picture.HeightPixels == 50 && picture.AlternativeText == "Chart"
            && picture.Data.Span.SequenceEqual(Png), "A two-cell picture is read with its anchor, size and bytes.");

        var written = new Workbook();
        var imageSheet = written.AddWorksheet("I");
        imageSheet.Images.Add(new WorksheetImage(Png, 3, 2) { WidthPixels = 40 });
        var reread = new XlsxWorkbookReader().Read(new MemoryStream(Write(written, out _))).Worksheets[0].Images.Single();
        Require(reread.Row == 3 && reread.Column == 2 && reread.WidthPixels == 40 && reread.HeightPixels == 1, "A one-cell picture survives the round trip.");
        Require(XlsxFormula.Shift("'My Sheet'!A1+Sheet2!B$2+A1048576", 1, 0) == "'My Sheet'!A2+Sheet2!B$2+#REF!", "Sheet names stay; a row past the end is #REF!.");
        var lines = XlsxFormula.Shift("SUM(A:A)+SUM($B:C)+SUM(1:3)+SUM($2:$2)+SUM(Sheet2!D:D)+LOG10(A1)", 1, 1);
        Require(lines == "SUM(B:B)+SUM($B:D)+SUM(2:4)+SUM($2:$2)+SUM(Sheet2!E:E)+LOG10(B2)", $"Whole columns and rows move along their axis, got {lines}.");
        Require(XlsxFormula.Shift("SUM(XFD:XFD)", 0, 1) == "SUM(#REF!)", "A column past the last is #REF!.");
    }

    /// <summary>Audit 2026-11-24: names and texts beyond Excel's limits are kept on reading; many merges stay fast.</summary>
    private static void ReadsForeignWorkbooksTolerantly()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        var longName = new string('n', 40);
        var longText = new string('t', 40_000);
        var merges = string.Concat(Enumerable.Range(1, 20_000).Select(row => $"<mergeCell ref=\"A{row}:B{row}\"/>"));
        var parts = new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"{longName}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{longText}</t></is></c></row></sheetData><mergeCells>{merges}</mergeCells></worksheet>",
        };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(xml);
            }
        }

        buffer.Position = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var workbook = new XlsxWorkbookReader().Read(buffer);
        Require(watch.Elapsed < TimeSpan.FromSeconds(10), $"20,000 merges read in bounded time ({watch.Elapsed}).");
        var sheet = workbook.Worksheets.Single();
        Require(sheet.Name == longName && sheet.Cell("A1").Value is string { Length: 40_000 } && sheet.MergedRanges.Count == 10_000,
            "A foreign name and text beyond Excel's limits are kept; merges stop at 10,000.");
        Throws<DocumentValidationException>(() => Write(workbook, out _), "Writing a name Excel refuses is an error.");


        // The continued fraction finds the same fraction as trying every denominator.
        var random = new Random(7);
        for (var index = 0; index < 500; index++)
        {
            var value = random.NextDouble();
            foreach (var limit in new long[] { 9, 99, 999 })
            {
                var (numerator, denominator) = CellFormatter.Approximate(value, limit);
                var best = double.MaxValue;
                for (long candidate = 1; candidate <= limit; candidate++)
                {
                    best = Math.Min(best, Math.Abs(value - (Math.Round(value * candidate) / candidate)));
                }

                Require(denominator <= limit && Math.Abs(Math.Abs(value - ((double)numerator / denominator)) - best) < 1e-12,
                    $"{value} with denominators up to {limit}: {numerator}/{denominator} is the closest fraction.");
            }
        }
    }

    /// <summary>ADR-0046: widths as Excel shows them, and an indented General cell aligned left.</summary>
    private static void WidthsAndIndentsAsExcelShowsThem()
    {
        foreach (var (shown, stored, back) in new[] { (8.43, 9.140625, 8.43), (40.0, 40.7109375, 40.0), (10.5, 11.2109375, 10.43), (0.0, 0.0, 0.0), (1.0, 1.7109375, 1.0) })
        {
            Require(XlsxConstants.StoredWidth(shown) == stored && XlsxConstants.DisplayedWidth(stored) == back,
                $"A shown width of {shown} is stored as {stored} and shows as {back} (got {XlsxConstants.StoredWidth(shown)}, {XlsxConstants.DisplayedWidth(XlsxConstants.StoredWidth(shown))}).");
        }

        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("S");
        sheet.Cell("A1").Value = "indented";
        sheet.Cell("A1").Style = new CellStyle { Indent = 2 };
        sheet.Cell("A2").Value = "right";
        sheet.Cell("A2").Style = new CellStyle { Indent = 1, HorizontalAlignment = CellHorizontalAlignment.Right };
        using var zip = new ZipArchive(new MemoryStream(Write(workbook, out _)));
        var styles = new StreamReader(zip.GetEntry("xl/styles.xml")!.Open()).ReadToEnd();
        Require(styles.Contains("<alignment horizontal=\"left\" indent=\"2\" />", StringComparison.Ordinal)
            && styles.Contains("<alignment horizontal=\"right\" indent=\"1\" />", StringComparison.Ordinal),
            "An indented General cell is aligned left, as Excel needs it; a right aligned one stays right.");
    }

    /// <summary>ADR-0046: the formulas a test needs, calculated; anything else is a clear error.</summary>
    private static void EvaluatesFormulas()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("Calc");
        var other = workbook.AddWorksheet("Other sheet");
        other.Cell("A1").Value = 10;
        sheet.Cell("B2").Value = 4;
        sheet.Cell("C2").Value = 89.9;
        sheet.Cell("D2").Formula = "PRODUCT(B2,C2)";
        sheet.Cell("B3").Value = 2;
        sheet.Cell("C3").Value = "5";
        sheet.Cell("D3").Formula = "B3*C3";
        sheet.Cell("D4").Value = "a note";
        sheet.Cell("D5").Formula = "SUM(D2:D4)";
        sheet.Cell("D6").Formula = "=D5*(1-10%)+'Other sheet'!$A$1";
        sheet.Cell("D7").Formula = "SUM(D2, D3, Calc!D4, 1.5e1) - PRODUCT(B2:B3)";
        sheet.Cell("A1").Value = new DateTime(2026, 11, 24);
        sheet.Cell("A2").Formula = "A1+7";

        void Equal(string reference, double expected)
        {
            var actual = sheet.Cell(reference).Evaluate();
            Require(Math.Abs(actual - expected) < 1e-9, $"{reference} is {expected}, got {actual}.");
        }

        Equal("D2", 4 * 89.9);
        Equal("D3", 10);
        Equal("D5", (4 * 89.9) + 10);
        Equal("D6", (((4 * 89.9) + 10) * 0.9) + 10);
        Equal("D7", (4 * 89.9) + 10 + 15 - 8);
        Equal("C2", 89.9);
        Equal("Z99", 0);
        Equal("A2", new DateTime(2026, 12, 1).ToOADate());

        double Calc(string formula)
        {
            var cell = sheet.Cell("H1");
            cell.Formula = formula;
            return cell.Evaluate();
        }

        foreach (var (formula, expected) in new[] { ("2+3*4", 14.0), ("(2+3)*4", 20.0), ("-2^2", 4.0), ("2^3^2", 64.0), ("10/4", 2.5), ("+5", 5.0), ("50%", 0.5), ("SUM()", 0.0), ("PRODUCT(Z1:Z9)", 0.0), ("sum(B2:B3)", 6.0), ("SUM(D4)", 0.0) })
        {
            Require(Math.Abs(Calc(formula) - expected) < 1e-9, $"={formula} is {expected}.");
        }

        sheet.Cell("E1").Formula = "E2+1";
        sheet.Cell("E2").Formula = "E1+1";
        foreach (var (formula, expected) in new[]
        {
            ("1/0", "#DIV/0!"), ("D4*2", "#VALUE!"), ("AVERAGE(B2:B3)", "AVERAGE is not supported"), ("A:A", "not a cell reference"),
            ("B2:B3", "outside SUM"), ("E1", "circular"), ("\"x\"", "text in a formula"), ("(1+2", "not closed"), ("1+", "ends too early"),
            ("Nope!A1", "no sheet 'Nope'"), ("1 2", "unexpected"), (new string('(', 300) + "1" + new string(')', 300), "nested deeper"),
        })
        {
            try
            {
                Calc(formula);
                throw new InvalidOperationException($"={formula} is not refused.");
            }
            catch (FormulaException exception)
            {
                Require(exception.Message.Contains(expected, StringComparison.Ordinal), $"={formula} is refused with '{expected}': {exception.Message}");
            }
        }

        // A chain of formula cells deeper than the limit stops with a message instead of exhausting the stack.
        var chain = workbook.AddWorksheet("Chain");
        chain.Cell(1, 1).Value = 1;
        for (var row = 2; row <= 600; row++)
        {
            chain.Cell(row, 1).Formula = $"A{row - 1}+1";
        }

        Throws<FormulaException>(() => chain.Cell(600, 1).Evaluate(), "A 600-cell chain stops at the depth limit.");
        Require(chain.Cell(400, 1).Evaluate() == 400, "A 400-cell chain is calculated.");

        // Cells that share their inputs are calculated once each: 60 doubling rows would be 2^60 steps otherwise.
        var diamond = workbook.AddWorksheet("Diamond");
        diamond.Cell(1, 1).Value = 1;
        for (var row = 2; row <= 60; row++)
        {
            diamond.Cell(row, 1).Formula = $"A{row - 1}+A{row - 1}";
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Require(diamond.Cell(60, 1).Evaluate() == Math.Pow(2, 59) && watch.Elapsed < TimeSpan.FromSeconds(1), $"Shared inputs are calculated once ({watch.Elapsed}).");

        // A workbook read back calculates the same.
        var read = new XlsxWorkbookReader().Read(new MemoryStream(Write(workbook, out _)));
        Require(Math.Abs(read["Calc"].Cell("D6").Evaluate() - ((((4 * 89.9) + 10) * 0.9) + 10)) < 1e-9, "A read workbook calculates the same.");
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
