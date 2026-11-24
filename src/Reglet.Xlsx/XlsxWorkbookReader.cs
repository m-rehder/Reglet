using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Reads an XLSX workbook into a <see cref="Workbook"/> (ADR-0043), narrow on purpose: sheets, cells with value,
/// formula (and the value Excel cached for it), number format and style, merged ranges, column widths, row heights and
/// the page setup with print area and repeated rows, PNG and JPEG images, theme and indexed colours (with tint) and the
/// cells of shared formulas. Use <see cref="WorksheetCell.GetFormattedText"/> for the text Excel shows. Conditional
/// formats, comments and the other features ADR-0043 leaves out are not read.
/// </summary>
public sealed class XlsxWorkbookReader
{
    private static readonly XNamespace S = XlsxConstants.Main;
    private static readonly XNamespace R = XlsxConstants.Relationships;

    /// <summary>The most merged ranges read per sheet (Excel files rarely have more than a few hundred).</summary>
    private const int MaxMerges = 10_000;

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

            XElement? Part(string type) => relationships.Values.Where(relationship => relationship.Type == type)
                .Select(relationship => Load(zip, relationship.Target)).FirstOrDefault();
            var stylesXml = Part(XlsxConstants.StylesType);
            var palette = new Palette(Part(XlsxConstants.ThemeType), stylesXml);
            var styles = new Styles(stylesXml, palette);
            var sharedStrings = Part(XlsxConstants.SharedStringsType)?.Elements(S + "si").Select(item => ReadString(item, palette)).ToList() ?? [];
            var mediaBudget = ReaderGuard.MaxMediaBytesPerDocument;

            var workbook = new Workbook();
            var sheets = workbookXml.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? [];
            foreach (var sheetElement in sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheet = workbook.AddReadWorksheet((string?)sheetElement.Attribute("name") ?? string.Empty);
                var id = (string?)sheetElement.Attribute(R + "id");
                if (id is not null && relationships.TryGetValue(id, out var target) && Load(zip, target.Target) is { } sheetXml)
                {
                    ReadSheet(sheet, sheetXml, styles, sharedStrings);
                    ReadImages(zip, target.Target, sheetXml, sheet, ref mediaBudget);
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

        var sharedFormulas = new Dictionary<string, (string Formula, int Row, int Column)>(StringComparer.Ordinal);
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
                ReadCell(sheet.Cell(cellRow, cellColumn), element, styles, sharedStrings, sharedFormulas);
            }
        }

        // An overlapping or malformed merge in a foreign file is skipped rather than failing the whole read. Each merge
        // is checked against the earlier ones, so a hostile file with very many merges is cut off at MaxMerges.
        foreach (var merge in (xml.Element(S + "mergeCells")?.Elements(S + "mergeCell") ?? []).Take(MaxMerges))
        {
            if ((string?)merge.Attribute("ref") is { } reference && reference.Contains(':')
                && TryParseRange(reference) is { } parsed && parsed.First != parsed.Last
                && !sheet.MergedRanges.Any(existing => existing.Overlaps(parsed)))
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

    private static void ReadCell(
        WorksheetCell cell,
        XElement element,
        Styles styles,
        List<object> sharedStrings,
        Dictionary<string, (string Formula, int Row, int Column)> sharedFormulas)
    {
        cell.Style = styles.Cell(Int(element, "s") ?? 0);
        var type = (string?)element.Attribute("t");
        var raw = (string?)element.Element(S + "v");
        object? value = type switch
        {
            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                ? sharedStrings[index]
                : null,
            "inlineStr" => element.Element(S + "is") is { } inline ? ReadString(inline, styles.Palette) : null,
            "str" or "e" => raw,
            "b" => raw is "1" or "true",
            _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                ? CellFormatter.IsDateFormat(cell.Style.NumberFormat) && number is >= -657434 and < 2958466 ? DateTime.FromOADate(number) : number
                : null,
        };

        var formula = element.Element(S + "f");
        var text = formula?.Value;
        if (formula is not null && (string?)formula.Attribute("t") == "shared" && (string?)formula.Attribute("si") is { } group)
        {
            if (!string.IsNullOrEmpty(text))
            {
                // The first cell of a shared formula carries its text; the others take it shifted by their offset.
                sharedFormulas[group] = (text, cell.Row, cell.Column);
            }
            else if (sharedFormulas.TryGetValue(group, out var master))
            {
                text = XlsxFormula.Shift(master.Formula, cell.Row - master.Row, cell.Column - master.Column);
            }
        }

        if (!string.IsNullOrEmpty(text))
        {
            cell.SetFormulaResult(text, value);
        }
        else if (value is not null)
        {
            cell.SetReadValue(value);
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
    private static object ReadString(XElement item, Palette palette)
    {
        var runs = item.Elements(S + "r").ToList();
        if (runs.Count == 0)
        {
            return string.Concat(item.Elements(S + "t").Select(text => text.Value));
        }

        return new RichText(runs.Select(run => new RichTextRun(
            string.Concat(run.Elements(S + "t").Select(text => text.Value)),
            run.Element(S + "rPr") is { } properties ? ReadFont(properties, "rFont", palette) : null)));
    }

    private static CellFont ReadFont(XElement font, string nameElement, Palette palette) => new()
    {
        Name = (string?)font.Element(S + nameElement)?.Attribute("val"),
        Size = Double(font.Element(S + "sz"), "val") is { } size and >= 1 and <= 409 ? size : null,
        Bold = font.Element(S + "b") is { } bold && (string?)bold.Attribute("val") is null or "1" or "true",
        Italic = font.Element(S + "i") is { } italic && (string?)italic.Attribute("val") is null or "1" or "true",
        Color = palette.Resolve(font.Element(S + "color")),
    };

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

    /// <summary>
    /// The pictures of a sheet's drawing: PNG or JPEG anchored at one cell (size from the anchor) or two cells (size from
    /// the picture's own extent). Other media, shapes and charts are skipped.
    /// </summary>
    private static void ReadImages(ZipArchive zip, string sheetPart, XElement sheetXml, Worksheet sheet, ref long budget)
    {
        XNamespace xdr = XlsxConstants.Drawing;
        XNamespace a = XlsxConstants.DrawingMain;
        var sheetRelationships = Relationships(zip, sheetPart);
        foreach (var drawingElement in sheetXml.Elements(S + "drawing"))
        {
            if ((string?)drawingElement.Attribute(R + "id") is not { } id || !sheetRelationships.TryGetValue(id, out var drawing)
                || Load(zip, drawing.Target) is not { } drawingXml)
            {
                continue;
            }

            var drawingRelationships = Relationships(zip, drawing.Target);
            foreach (var anchor in drawingXml.Elements().Where(element => element.Name == xdr + "oneCellAnchor" || element.Name == xdr + "twoCellAnchor"))
            {
                var picture = anchor.Element(xdr + "pic");
                var from = anchor.Element(xdr + "from");
                if (picture?.Element(xdr + "blipFill")?.Element(a + "blip")?.Attribute(R + "embed")?.Value is not { } embed
                    || !drawingRelationships.TryGetValue(embed, out var media) || zip.GetEntry(media.Target) is not { } entry
                    || !int.TryParse(from?.Element(xdr + "col")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var column)
                    || !int.TryParse(from?.Element(xdr + "row")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
                    || column is < 0 or >= CellReference.MaxColumn || row is < 0 or >= CellReference.MaxRow)
                {
                    continue;
                }

                var extent = anchor.Name == xdr + "oneCellAnchor"
                    ? anchor.Element(xdr + "ext")
                    : picture.Element(xdr + "spPr")?.Element(a + "xfrm")?.Element(a + "ext");
                var bytes = ReaderGuard.ReadBoundedBytes(entry, budget);
                budget -= bytes.Length;
                if (budget < 0)
                {
                    throw new DocumentValidationException("XLSX: the images exceed the media limit of a workbook.");
                }

                try
                {
                    sheet.Images.Add(new WorksheetImage(bytes, row + 1, column + 1)
                    {
                        WidthPixels = Pixels(extent, "cx"),
                        HeightPixels = Pixels(extent, "cy"),
                        AlternativeText = (string?)picture.Element(xdr + "nvPicPr")?.Element(xdr + "cNvPr")?.Attribute("descr"),
                    });
                }
                catch (ImageException)
                {
                    // Neither PNG nor JPEG (EMF, GIF, ...): the model has no place for it.
                }
            }
        }

        static int? Pixels(XElement? extent, string name)
            => long.TryParse((string?)extent?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var emu) && emu > 0
                ? (int)Math.Clamp(Math.Round(emu / (double)XlsxConstants.EmuPerPixel), 1, int.MaxValue)
                : null;
    }

    /// <summary>The cell formats of styles.xml as <see cref="CellStyle"/>s, by index.</summary>
    private sealed class Styles
    {
        private readonly List<CellStyle> _cells = [];

        public Styles(XElement? xml, Palette palette)
        {
            Palette = palette;
            var formats = xml?.Element(S + "numFmts")?.Elements(S + "numFmt")
                .Where(format => Int(format, "numFmtId") is not null && format.Attribute("formatCode") is not null)
                .GroupBy(format => Int(format, "numFmtId")!.Value)
                .ToDictionary(group => group.Key, group => (string)group.First().Attribute("formatCode")!) ?? [];
            var fonts = xml?.Element(S + "fonts")?.Elements(S + "font").Select((font, index) => index == 0 ? CellFont.Default : ReadFont(font, "name", palette)).ToList() ?? [];
            var fills = xml?.Element(S + "fills")?.Elements(S + "fill")
                .Select(fill => fill.Element(S + "patternFill") is { } pattern && (string?)pattern.Attribute("patternType") == "solid"
                    ? palette.Resolve(pattern.Element(S + "fgColor"))
                    : null)
                .ToList() ?? [];
            var borders = xml?.Element(S + "borders")?.Elements(S + "border").Select(border => new CellBorders
            {
                Left = Side(border.Element(S + "left"), palette),
                Right = Side(border.Element(S + "right"), palette),
                Top = Side(border.Element(S + "top"), palette),
                Bottom = Side(border.Element(S + "bottom"), palette),
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

        public Palette Palette { get; }

        public CellStyle Cell(int index) => index >= 0 && index < _cells.Count ? _cells[index] : CellStyle.Default;

        private static T? At<T>(List<T> list, int? index) => index is { } at && at >= 0 && at < list.Count ? list[at] : default;

        private static CellBorderLine? Side(XElement? side, Palette palette) => (string?)side?.Attribute("style") switch
        {
            null or "none" => null,
            "medium" or "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => new CellBorderLine(CellBorderStyle.Medium, palette.Resolve(side!.Element(S + "color"))),
            "thick" or "double" => new CellBorderLine(CellBorderStyle.Thick, palette.Resolve(side!.Element(S + "color"))),
            _ => new CellBorderLine(CellBorderStyle.Thin, palette.Resolve(side!.Element(S + "color"))),
        };
    }
}
