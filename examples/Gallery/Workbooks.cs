using System.Globalization;
using System.Text;
using Reglet;

namespace Gallery;

/// <summary>Excel workbooks (package Reglet.Xlsx): a calculation with formulas and print setup, and reading one back.</summary>
public static class Workbooks
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("workbooks", "calculation", "A quotation as an Excel workbook: formulas, number formats, borders, a logo and fit to one page wide.")
        {
            Run = CalculationAsync,
        };
        yield return new Example("workbooks", "read-a-workbook", "Reads a workbook back and lists every cell as Excel shows it.")
        {
            Run = ReadAsync,
        };
    }

    private static Workbook Calculation()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddWorksheet("Quotation");

        sheet.Cell("A1").Value = "Quotation Q-2026-117";
        sheet.Cell("A1").Style = new CellStyle { Font = new CellFont { Size = 16, Bold = true, Color = Assets.Petrol } };
        sheet.Merge("A1:E1");
        sheet.Images.Add(new WorksheetImage(Assets.SolidPng(96, 32, 0x2B, 0xA3, 0x9A), 1, 6) { WidthPixels = 96, HeightPixels = 32, AlternativeText = "Logo" });
        sheet.Cell("A2").Value = DateTime.Today;

        string[] headers = ["Item", "Description", "Quantity", "Unit price", "Total"];
        for (var column = 0; column < headers.Length; column++)
        {
            sheet.Cell(4, column + 1).Value = headers[column];
        }

        sheet.SetStyle("A4:E4", style => style with
        {
            Font = new CellFont { Bold = true, Color = Color.White },
            Fill = Assets.Petrol,
            HorizontalAlignment = CellHorizontalAlignment.Center,
        });

        (string Item, string Description, int Quantity, decimal Price)[] lines =
        [
            ("A-100", "Monitor arm, double", 4, 89.90m),
            ("A-210", "Docking station with three displays and power delivery", 4, 219.00m),
            ("C-007", "Installation (hours)", 6, 75.00m),
        ];
        for (var index = 0; index < lines.Length; index++)
        {
            var row = 5 + index;
            sheet.Cell(row, 1).Value = lines[index].Item;
            sheet.Cell(row, 2).Value = lines[index].Description;
            sheet.Cell(row, 3).Value = lines[index].Quantity;
            sheet.Cell(row, 4).Value = lines[index].Price;
            sheet.Cell(row, 5).Formula = $"PRODUCT(C{row},D{row})";
        }

        var last = 4 + lines.Length;
        sheet.SetStyle($"B5:B{last}", style => style with { WrapText = true, VerticalAlignment = CellVerticalAlignment.Top });
        sheet.SetStyle($"C5:C{last}", style => style with { NumberFormat = "0" });
        sheet.SetStyle($"D5:E{last + 3}", style => style with { NumberFormat = "#,##0.00" });

        sheet.Cell(last + 1, 4).Value = "Net";
        sheet.Cell(last + 1, 5).Formula = $"SUM(E5:E{last})";
        sheet.Cell(last + 2, 4).Value = "VAT 19 %";
        sheet.Cell(last + 2, 5).Formula = $"E{last + 1}*0.19";
        sheet.Cell(last + 3, 4).Value = new RichText(new RichTextRun("Total", new CellFont { Bold = true }), new RichTextRun(" (EUR)"));
        sheet.Cell(last + 3, 5).Formula = $"E{last + 1}+E{last + 2}";
        sheet.SetStyle($"D{last + 3}:E{last + 3}", style => style with
        {
            Font = style.Font with { Bold = true },
            Border = style.Border with { Top = new CellBorderLine(CellBorderStyle.Medium) },
        });

        sheet.SetOutsideBorder($"A4:E{last}", new CellBorderLine(CellBorderStyle.Thin, Color.FromHex("808080")));
        sheet.ColumnWidths[1] = 10;
        sheet.ColumnWidths[2] = 40;
        sheet.ColumnWidths[3] = 10;
        sheet.ColumnWidths[4] = 12;
        sheet.ColumnWidths[5] = 14;

        sheet.PageSetup = new SheetPageSetup
        {
            Orientation = PageOrientation.Portrait,
            PaperSize = SheetPaperSize.A4,
            FitToPagesWide = 1,
            FitToPagesTall = 0,
            PrintArea = $"A1:F{last + 3}",
            RepeatRows = (4, 4),
        };
        return workbook;
    }

    private static async Task<IReadOnlyList<string>> CalculationAsync(string output)
    {
        var path = Path.Combine(output, "workbooks-calculation.xlsx");
        await using var file = File.Create(path);

        // Excel and LibreOffice calculate the formulas when they open the file.
        await new XlsxWorkbookWriter().WriteAsync(Calculation(), file);
        return [path];
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(string output)
    {
        using var buffer = new MemoryStream();
        await new XlsxWorkbookWriter().WriteAsync(Calculation(), buffer);
        buffer.Position = 0;

        var workbook = new XlsxWorkbookReader().Read(buffer);
        var text = new StringBuilder();
        var culture = CultureInfo.GetCultureInfo("en-US");
        foreach (var sheet in workbook.Worksheets)
        {
            text.AppendLine($"Sheet '{sheet.Name}', {sheet.Images.Count} image(s), print area {sheet.PageSetup.PrintArea}");
            foreach (var cell in sheet.Cells.Where(cell => cell.Value is not null || cell.Formula is not null))
            {
                // A formula written by Reglet has no cached value until Excel saves the file.
                var shown = cell.Formula is { } formula ? "=" + formula : cell.GetFormattedText(culture);
                text.AppendLine($"  {cell.Reference,-4} {shown}");
            }
        }

        var path = Path.Combine(output, "workbooks-read-a-workbook.txt");
        await File.WriteAllTextAsync(path, text.ToString());
        return [path];
    }
}
