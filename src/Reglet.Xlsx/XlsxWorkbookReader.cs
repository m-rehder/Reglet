using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Reads an XLSX workbook into a <see cref="Workbook"/> (ADR-0043), narrow on purpose: sheets, cells with value,
/// formula (and the value Excel cached for it), number format and style, merged ranges, column widths, row heights and
/// the page setup with print area and repeated rows. Use <see cref="WorksheetCell.GetFormattedText"/> for the text Excel
/// shows. shortcut: images, theme and indexed colours, conditional formats, comments and the cells of a shared formula
/// other than its first (they keep their cached value) are not read; extend when a workbook needs them.
/// </summary>
public sealed class XlsxWorkbookReader
{
    private static readonly XNamespace S = XlsxConstants.Main;
    private static readonly XNamespace R = XlsxConstants.Relationships;

    /// <summary>Reads a workbook from the stream; the stream stays open.</summary>
    /// <exception cref="DocumentValidationException">The stream is not a readable XLSX package.</exception>
    public Workbook Read(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReaderGuard.Run("XLSX", () =>
        {
            var bytes = ReaderGuard.ReadAllBounded(input, "XLSX");
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var workbookPart = Relationships(zip, string.Empty).Values
                .FirstOrDefault(relationship => relationship.Type == XlsxConstants.OfficeDocumentType).Target ?? "xl/workbook.xml";
            var workbookXml = Load(zip, workbookPart) ?? throw new DocumentValidationException("XLSX: the package has no workbook.");
            var relationships = Relationships(zip, workbookPart);

            var strings = relationships.Values.Where(relationship => relationship.Type == XlsxConstants.SharedStringsType)
                .Select(relationship => Load(zip, relationship.Target)).FirstOrDefault();
            var styles = new Styles(relationships.Values.Where(relationship => relationship.Type == XlsxConstants.StylesType)
                .Select(relationship => Load(zip, relationship.Target)).FirstOrDefault());
            var sharedStrings = strings?.Elements(S + "si").Select(ReadString).ToList() ?? [];

            var workbook = new Workbook();
            var sheets = workbookXml.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? [];
            foreach (var sheetElement in sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheet = workbook.AddWorksheet((string?)sheetElement.Attribute("name") ?? string.Empty);
                var id = (string?)sheetElement.Attribute(R + "id");
                if (id is not null && relationships.TryGetValue(id, out var target) && Load(zip, target.Target) is { } sheetXml)
                {
                    ReadSheet(sheet, sheetXml, styles, sharedStrings);
                }
            }

            ReadDefinedNames(workbook, workbookXml);
            return workbook;
        });
    }

    private static void ReadSheet(Worksheet sheet, XElement xml, Styles styles, List<object> sharedStrings)
    {
        foreach (var column in xml.Element(S + "cols")?.Elements(S + "col") ?? [])
        {
            var min = Int(column, "min");
            var max = Int(column, "max");
            // A col element for "all remaining columns" carries no width a caller set column by column.
            if (min is >= 1 and <= CellReference.MaxColumn && max >= min && max - min < 1000 && Double(column, "width") is { } width
                && ((string?)column.Attribute("customWidth") is "1" or "true"))
            {
                for (var index = min.Value; index <= max; index++)
                {
                    sheet.ColumnWidths[index] = width;
                }
            }
        }

        var rowNumber = 0;
        foreach (var row in xml.Element(S + "sheetData")?.Elements(S + "row") ?? [])
        {
            rowNumber = Int(row, "r") ?? rowNumber + 1;
            if ((string?)row.Attribute("customHeight") is "1" or "true" && Double(row, "ht") is { } height)
            {
                sheet.RowHeights[rowNumber] = height;
            }

            var columnNumber = 0;
            foreach (var element in row.Elements(S + "c"))
            {
                var (cellRow, cellColumn) = CellReference.TryParse((string?)element.Attribute("r"), out var parsedRow, out var parsedColumn)
                    ? (parsedRow, parsedColumn)
                    : (rowNumber, columnNumber + 1);
                columnNumber = cellColumn;
                ReadCell(sheet.Cell(cellRow, cellColumn), element, styles, sharedStrings);
            }
        }

        foreach (var merge in xml.Element(S + "mergeCells")?.Elements(S + "mergeCell") ?? [])
        {
            // An overlapping or malformed merge in a foreign file is skipped rather than failing the whole read.
            if ((string?)merge.Attribute("ref") is { } reference && reference.Contains(':')
                && !sheet.MergedRanges.Any(existing => TryParseRange(reference) is not { } range || existing.Overlaps(range))
                && TryParseRange(reference) is { } parsed && parsed.First != parsed.Last)
            {
                sheet.Merge(reference);
            }
        }

        var setup = new SheetPageSetup();
        if (xml.Element(S + "pageMargins") is { } margins)
        {
            Length Inches(string name, double fallback) => Length.FromInches(Double(margins, name) is { } value && value >= 0 ? value : fallback);
            setup = setup with { Margins = new PageMargins(Inches("top", 0.75), Inches("right", 0.7), Inches("bottom", 0.75), Inches("left", 0.7)) };
        }

        if (xml.Element(S + "pageSetup") is { } pageSetup)
        {
            setup = setup with
            {
                Orientation = (string?)pageSetup.Attribute("orientation") == "landscape" ? PageOrientation.Landscape : PageOrientation.Portrait,
                PaperSize = (string?)pageSetup.Attribute("paperSize") == "1" ? SheetPaperSize.Letter : SheetPaperSize.A4,
            };
            if ((string?)xml.Element(S + "sheetPr")?.Element(S + "pageSetUpPr")?.Attribute("fitToPage") is "1" or "true")
            {
                setup = setup with
                {
                    FitToPagesWide = Math.Max(0, Int(pageSetup, "fitToWidth") ?? 1),
                    FitToPagesTall = Math.Max(0, Int(pageSetup, "fitToHeight") ?? 1),
                };
            }
        }

        sheet.PageSetup = setup;
    }

    private static void ReadCell(WorksheetCell cell, XElement element, Styles styles, List<object> sharedStrings)
    {
        cell.Style = styles.Cell(Int(element, "s") ?? 0);
        var type = (string?)element.Attribute("t");
        var raw = (string?)element.Element(S + "v");
        object? value = type switch
        {
            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : null,
            "inlineStr" => element.Element(S + "is") is { } inline ? ReadString(inline) : null,
            "str" or "e" => raw,
            "b" => raw is "1" or "true",
            _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? CellFormatter.IsDateFormat(cell.Style.NumberFormat) && number is >= -657434 and < 2958466 ? DateTime.FromOADate(number) : number
                : null,
        };

        if (element.Element(S + "f") is { } formula && formula.Value.Length > 0)
        {
            cell.SetFormulaResult(formula.Value, value);
        }
        else if (value is not null)
        {
            cell.Value = value;
        }
    }

    private static void ReadDefinedNames(Workbook workbook, XElement workbookXml)
    {
        foreach (var name in workbookXml.Element(S + "definedNames")?.Elements(S + "definedName") ?? [])
        {
            if (Int(name, "localSheetId") is not { } index || index < 0 || index >= workbook.Worksheets.Count)
            {
                continue;
            }

            var sheet = workbook.Worksheets[index];
            // 'Sheet'!$A$1:$F$40 (a second area after a comma is not modelled).
            var areas = name.Value.Split(',').Select(area => area[(area.LastIndexOf('!') + 1)..].Replace("$", string.Empty, StringComparison.Ordinal)).ToList();
            switch ((string?)name.Attribute("name"))
            {
                case "_xlnm.Print_Area" when areas.Count > 0 && TryRange(areas[0], out var range):
                    sheet.PageSetup = sheet.PageSetup with { PrintArea = range };
                    break;
                case "_xlnm.Print_Titles":
                    foreach (var area in areas)
                    {
                        var parts = area.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                            && first >= 1 && first <= last && last <= CellReference.MaxRow)
                        {
                            sheet.PageSetup = sheet.PageSetup with { RepeatRows = (first, last) };
                        }
                    }

                    break;
            }
        }
    }

    private static CellRange? TryParseRange(string text) => TryRange(text, out var range) ? CellRange.Parse(range) : null;

    private static bool TryRange(string text, out string range)
    {
        try
        {
            range = CellRange.Parse(text).ToString();
            return true;
        }
        catch (ArgumentException)
        {
            range = string.Empty;
            return false;
        }
    }

    /// <summary>A shared or inline string: plain text, or rich text when it has runs.</summary>
    private static object ReadString(XElement item)
    {
        var runs = item.Elements(S + "r").ToList();
        if (runs.Count == 0)
        {
            return string.Concat(item.Elements(S + "t").Select(text => text.Value));
        }

        return new RichText(runs.Select(run => new RichTextRun(
            string.Concat(run.Elements(S + "t").Select(text => text.Value)),
            run.Element(S + "rPr") is { } properties ? ReadFont(properties, "rFont") : null)));
    }

    private static CellFont ReadFont(XElement font, string nameElement) => new()
    {
        Name = (string?)font.Element(S + nameElement)?.Attribute("val"),
        Size = Double(font.Element(S + "sz"), "val") is { } size and >= 1 and <= 409 ? size : null,
        Bold = font.Element(S + "b") is { } bold && (string?)bold.Attribute("val") is null or "1" or "true",
        Italic = font.Element(S + "i") is { } italic && (string?)italic.Attribute("val") is null or "1" or "true",
        Color = ReadColor(font.Element(S + "color")),
    };

    /// <summary>An ARGB colour; theme and indexed colours are not resolved.</summary>
    private static Color? ReadColor(XElement? color)
        => (string?)color?.Attribute("rgb") is { Length: 8 } argb && Color.TryParseHex(argb[2..], out var parsed) ? parsed : null;

    private static int? Int(XElement? element, string attribute)
        => int.TryParse((string?)element?.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Double(XElement? element, string attribute)
        => double.TryParse((string?)element?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static XElement? Load(ZipArchive zip, string part)
    {
        if (zip.GetEntry(part) is not { } entry)
        {
            return null;
        }

        using var stream = ReaderGuard.OpenBounded(entry);
        return ReaderGuard.LoadXml(stream).Root;
    }

    private static Dictionary<string, (string Type, string Target)> Relationships(ZipArchive zip, string part)
    {
        var result = new Dictionary<string, (string Type, string Target)>(StringComparer.Ordinal);
        foreach (var relationship in Load(zip, OpcPackageWriter.RelsPartName(part))?.Elements() ?? [])
        {
            if ((string?)relationship.Attribute("Id") is { } id && (string?)relationship.Attribute("Target") is { } target
                && (string?)relationship.Attribute("TargetMode") != "External")
            {
                result[id] = ((string?)relationship.Attribute("Type") ?? string.Empty, OpcPackageWriter.ResolveTarget(part, target));
            }
        }

        return result;
    }

    /// <summary>The cell formats of styles.xml as <see cref="CellStyle"/>s, by index.</summary>
    private sealed class Styles
    {
        private readonly List<CellStyle> _cells = [];

        public Styles(XElement? xml)
        {
            var formats = xml?.Element(S + "numFmts")?.Elements(S + "numFmt")
                .Where(format => Int(format, "numFmtId") is not null && format.Attribute("formatCode") is not null)
                .GroupBy(format => Int(format, "numFmtId")!.Value)
                .ToDictionary(group => group.Key, group => (string)group.First().Attribute("formatCode")!) ?? [];
            var fonts = xml?.Element(S + "fonts")?.Elements(S + "font").Select((font, index) => index == 0 ? CellFont.Default : ReadFont(font, "name")).ToList() ?? [];
            var fills = xml?.Element(S + "fills")?.Elements(S + "fill")
                .Select(fill => fill.Element(S + "patternFill") is { } pattern && (string?)pattern.Attribute("patternType") == "solid"
                    ? ReadColor(pattern.Element(S + "fgColor"))
                    : null)
                .ToList() ?? [];
            var borders = xml?.Element(S + "borders")?.Elements(S + "border").Select(border => new CellBorders
            {
                Left = Side(border.Element(S + "left")),
                Right = Side(border.Element(S + "right")),
                Top = Side(border.Element(S + "top")),
                Bottom = Side(border.Element(S + "bottom")),
            }).ToList() ?? [];

            foreach (var xf in xml?.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
            {
                var formatId = Int(xf, "numFmtId") ?? 0;
                var alignment = xf.Element(S + "alignment");
                _cells.Add(new CellStyle
                {
                    NumberFormat = formats.TryGetValue(formatId, out var code) ? code : XlsxConstants.BuiltInFormats.GetValueOrDefault(formatId),
                    Font = At(fonts, Int(xf, "fontId")) ?? CellFont.Default,
                    Fill = At(fills, Int(xf, "fillId")),
                    Border = At(borders, Int(xf, "borderId")) ?? CellBorders.None,
                    HorizontalAlignment = (string?)alignment?.Attribute("horizontal") switch
                    {
                        "left" => CellHorizontalAlignment.Left,
                        "center" or "centerContinuous" => CellHorizontalAlignment.Center,
                        "right" => CellHorizontalAlignment.Right,
                        _ => CellHorizontalAlignment.General,
                    },
                    VerticalAlignment = (string?)alignment?.Attribute("vertical") switch
                    {
                        "top" => CellVerticalAlignment.Top,
                        "center" => CellVerticalAlignment.Center,
                        _ => CellVerticalAlignment.Bottom,
                    },
                    WrapText = (string?)alignment?.Attribute("wrapText") is "1" or "true",
                    Indent = Math.Clamp(Int(alignment, "indent") ?? 0, 0, 250),
                });
            }
        }

        public CellStyle Cell(int index) => index >= 0 && index < _cells.Count ? _cells[index] : CellStyle.Default;

        private static T? At<T>(List<T> list, int? index) => index is { } at && at >= 0 && at < list.Count ? list[at] : default;

        private static CellBorderLine? Side(XElement? side) => (string?)side?.Attribute("style") switch
        {
            null or "none" => null,
            "medium" or "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => new CellBorderLine(CellBorderStyle.Medium, ReadColor(side!.Element(S + "color"))),
            "thick" or "double" => new CellBorderLine(CellBorderStyle.Thick, ReadColor(side!.Element(S + "color"))),
            _ => new CellBorderLine(CellBorderStyle.Thin, ReadColor(side!.Element(S + "color"))),
        };
    }
}
