using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// Writes a <see cref="Workbook"/> as an XLSX package (SpreadsheetML, ADR-0043): sheets, shared strings, styles, merged
/// ranges, column widths, row heights, images, page setup, print area and repeated rows. Formulas are written without a
/// cached value and Excel recalculates them on load.
/// </summary>
public sealed class XlsxWorkbookWriter
{
    /// <summary>Writes the workbook to the stream.</summary>
    /// <param name="workbook">The workbook; it needs at least one sheet.</param>
    /// <param name="output">The target stream; it stays open.</param>
    /// <param name="options">Writer options; null uses <see cref="XlsxWriteOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="DocumentValidationException">The workbook has no sheet.</exception>
    public Task<WriteResult> WriteAsync(
        Workbook workbook,
        Stream output,
        XlsxWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (workbook.Worksheets.Count == 0)
        {
            throw new DocumentValidationException("XLSX: a workbook needs at least one sheet.");
        }

        // A workbook read from a file may carry names Excel refuses; Excel would repair (rename) them.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Worksheets)
        {
            if (!Workbook.IsValidSheetName(sheet.Name) || !names.Add(sheet.Name))
            {
                throw new DocumentValidationException($"XLSX: '{sheet.Name}' is not a valid or unique sheet name; Excel would refuse it.");
            }
        }

        var effective = options ?? XlsxWriteOptions.Default;
        var warnings = new List<WriteWarning>();
        return WriterOutput.Write(
            output,
            stream =>
            {
                WritePackage(workbook, stream, effective, warnings, cancellationToken);
                return null;
            },
            warnings,
            effective,
            cancellationToken);
    }

    private static void WritePackage(Workbook workbook, Stream stream, XlsxWriteOptions options, List<WriteWarning> warnings, CancellationToken cancellationToken)
    {
        using var package = new OpcPackageWriter(stream, options.InvalidXmlCharacters, "XLSX");
        package.AddDefault("png", "image/png");
        package.AddDefault("jpeg", "image/jpeg");
        package.AddRelationship(string.Empty, new OpcRelationship("rId1", XlsxConstants.OfficeDocumentType, "xl/workbook.xml"));
        package.AddOverride("xl/workbook.xml", XlsxConstants.WorkbookContentType);

        var styles = new StyleTable();
        var strings = new SharedStrings();
        var imageIndex = 0;
        for (var index = 0; index < workbook.Worksheets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = workbook.Worksheets[index];
            var part = $"xl/worksheets/sheet{index + 1}.xml";
            package.AddOverride(part, XlsxConstants.WorksheetContentType);
            package.AddRelationship("xl/workbook.xml", new OpcRelationship($"rId{index + 1}", XlsxConstants.WorksheetType, $"worksheets/sheet{index + 1}.xml"));

            string? drawingRelationship = null;
            if (sheet.Images.Count > 0)
            {
                var drawing = $"xl/drawings/drawing{index + 1}.xml";
                drawingRelationship = "rId1";
                package.AddOverride(drawing, XlsxConstants.DrawingContentType);
                package.AddRelationship(part, new OpcRelationship(drawingRelationship, XlsxConstants.DrawingType, $"../drawings/drawing{index + 1}.xml"));
                var images = new List<string>();
                foreach (var image in sheet.Images)
                {
                    imageIndex++;
                    var extension = image.Info.MediaType == ImageMediaType.Png ? "png" : "jpeg";
                    package.AddBinaryPart($"xl/media/image{imageIndex}.{extension}", image.Data);
                    var id = $"rId{images.Count + 1}";
                    package.AddRelationship(drawing, new OpcRelationship(id, XlsxConstants.ImageType, $"../media/image{imageIndex}.{extension}"));
                    images.Add(id);
                }

                package.WriteXmlPart(drawing, writer => WriteDrawing(writer, sheet, images));
            }

            package.WriteXmlPart(part, writer => WriteWorksheet(writer, sheet, index == 0, styles, strings, drawingRelationship, warnings));
        }

        var count = workbook.Worksheets.Count;
        package.AddRelationship("xl/workbook.xml", new OpcRelationship($"rId{count + 1}", XlsxConstants.StylesType, "styles.xml"));
        package.AddOverride("xl/styles.xml", XlsxConstants.StylesContentType);
        package.WriteXmlPart("xl/styles.xml", styles.Write);
        if (strings.Count > 0)
        {
            package.AddRelationship("xl/workbook.xml", new OpcRelationship($"rId{count + 2}", XlsxConstants.SharedStringsType, "sharedStrings.xml"));
            package.AddOverride("xl/sharedStrings.xml", XlsxConstants.SharedStringsContentType);
            package.WriteXmlPart("xl/sharedStrings.xml", strings.Write);
        }

        package.WriteXmlPart("xl/workbook.xml", writer => WriteWorkbook(writer, workbook));
    }

    private static void WriteWorkbook(XmlWriter writer, Workbook workbook)
    {
        writer.WriteStartElement("workbook", XlsxConstants.Main);
        writer.WriteAttributeString("xmlns", "r", null, XlsxConstants.Relationships);
        writer.WriteStartElement("bookViews", XlsxConstants.Main);
        writer.WriteStartElement("workbookView", XlsxConstants.Main);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheets", XlsxConstants.Main);
        for (var index = 0; index < workbook.Worksheets.Count; index++)
        {
            writer.WriteStartElement("sheet", XlsxConstants.Main);
            writer.WriteAttributeString("name", workbook.Worksheets[index].Name);
            writer.WriteAttributeString("sheetId", Number(index + 1));
            writer.WriteAttributeString("r", "id", XlsxConstants.Relationships, $"rId{index + 1}");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        var names = new List<(string Name, int Sheet, string Value)>();
        for (var index = 0; index < workbook.Worksheets.Count; index++)
        {
            var sheet = workbook.Worksheets[index];
            var quoted = "'" + sheet.Name.Replace("'", "''", StringComparison.Ordinal) + "'!";
            if (sheet.PageSetup.PrintArea is { } area)
            {
                var range = CellRange.Parse(area);
                names.Add(("_xlnm.Print_Area", index, quoted + Absolute(range.First) + ":" + Absolute(range.Last)));
            }

            if (sheet.PageSetup.RepeatRows is { } rows)
            {
                names.Add(("_xlnm.Print_Titles", index, $"{quoted}${Number(rows.First)}:${Number(rows.Last)}"));
            }
        }

        if (names.Count > 0)
        {
            writer.WriteStartElement("definedNames", XlsxConstants.Main);
            foreach (var (name, sheet, value) in names)
            {
                writer.WriteStartElement("definedName", XlsxConstants.Main);
                writer.WriteAttributeString("name", name);
                writer.WriteAttributeString("localSheetId", Number(sheet));
                writer.WriteString(value);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        // No cached formula results are written, so Excel must calculate everything when it opens the file.
        writer.WriteStartElement("calcPr", XlsxConstants.Main);
        writer.WriteAttributeString("calcId", "191029");
        writer.WriteAttributeString("fullCalcOnLoad", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string Absolute((int Row, int Column) cell) => "$" + CellReference.ColumnName(cell.Column) + "$" + Number(cell.Row);

    private static void WriteWorksheet(
        XmlWriter writer,
        Worksheet sheet,
        bool selected,
        StyleTable styles,
        SharedStrings strings,
        string? drawingRelationship,
        List<WriteWarning> warnings)
    {
        writer.WriteStartElement("worksheet", XlsxConstants.Main);
        writer.WriteAttributeString("xmlns", "r", null, XlsxConstants.Relationships);

        var setup = sheet.PageSetup;
        var fit = setup.FitToPagesWide is not null || setup.FitToPagesTall is not null;
        if (fit)
        {
            writer.WriteStartElement("sheetPr", XlsxConstants.Main);
            writer.WriteStartElement("pageSetUpPr", XlsxConstants.Main);
            writer.WriteAttributeString("fitToPage", "1");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var cells = sheet.Cells.ToList();
        writer.WriteStartElement("dimension", XlsxConstants.Main);
        writer.WriteAttributeString("ref", cells.Count == 0
            ? "A1"
            : new CellRange((cells.Min(cell => cell.Row), cells.Min(cell => cell.Column)), (cells.Max(cell => cell.Row), cells.Max(cell => cell.Column))).ToString());
        writer.WriteEndElement();

        writer.WriteStartElement("sheetViews", XlsxConstants.Main);
        writer.WriteStartElement("sheetView", XlsxConstants.Main);
        if (selected)
        {
            writer.WriteAttributeString("tabSelected", "1");
        }

        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("sheetFormatPr", XlsxConstants.Main);
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();

        if (sheet.ColumnWidths.Count > 0)
        {
            writer.WriteStartElement("cols", XlsxConstants.Main);
            foreach (var (column, width) in sheet.ColumnWidths)
            {
                if (column is < 1 or > CellReference.MaxColumn || !double.IsFinite(width) || width is < 0 or > 255)
                {
                    throw new DocumentValidationException($"XLSX: column {column} of '{sheet.Name}' needs a width of 0–255 characters.");
                }

                writer.WriteStartElement("col", XlsxConstants.Main);
                writer.WriteAttributeString("min", Number(column));
                writer.WriteAttributeString("max", Number(column));
                writer.WriteAttributeString("width", Number(XlsxConstants.StoredWidth(width)));
                writer.WriteAttributeString("customWidth", "1");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("sheetData", XlsxConstants.Main);
        var rows = cells.Select(cell => cell.Row).Concat(sheet.RowHeights.Keys).Distinct().Order();
        var byRow = cells.ToLookup(cell => cell.Row);
        foreach (var row in rows)
        {
            if (row is < 1 or > CellReference.MaxRow)
            {
                throw new DocumentValidationException($"XLSX: row {row} of '{sheet.Name}' is outside 1–{CellReference.MaxRow}.");
            }

            writer.WriteStartElement("row", XlsxConstants.Main);
            writer.WriteAttributeString("r", Number(row));
            if (sheet.RowHeights.TryGetValue(row, out var height))
            {
                if (!double.IsFinite(height) || height is < 0 or > 409)
                {
                    throw new DocumentValidationException($"XLSX: row {row} of '{sheet.Name}' needs a height of 0–409 points.");
                }

                writer.WriteAttributeString("ht", Number(height));
                writer.WriteAttributeString("customHeight", "1");
            }

            foreach (var cell in byRow[row])
            {
                if (cell.IsTooLong)
                {
                    warnings.Add(new WriteWarning($"XLSX: the text of {sheet.Name}!{cell.Reference} is longer than Excel's 32,767 characters; Excel cuts it."));
                }

                WriteCell(writer, cell, styles, strings);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        if (sheet.MergedRanges.Count > 0)
        {
            writer.WriteStartElement("mergeCells", XlsxConstants.Main);
            writer.WriteAttributeString("count", Number(sheet.MergedRanges.Count));
            foreach (var range in sheet.MergedRanges)
            {
                writer.WriteStartElement("mergeCell", XlsxConstants.Main);
                writer.WriteAttributeString("ref", range.ToString());
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        var margins = setup.Margins;
        writer.WriteStartElement("pageMargins", XlsxConstants.Main);
        writer.WriteAttributeString("left", Inches(margins.Left));
        writer.WriteAttributeString("right", Inches(margins.Right));
        writer.WriteAttributeString("top", Inches(margins.Top));
        writer.WriteAttributeString("bottom", Inches(margins.Bottom));
        writer.WriteAttributeString("header", "0.3");
        writer.WriteAttributeString("footer", "0.3");
        writer.WriteEndElement();

        writer.WriteStartElement("pageSetup", XlsxConstants.Main);
        writer.WriteAttributeString("paperSize", setup.PaperSize == SheetPaperSize.Letter ? "1" : "9");
        if (fit)
        {
            writer.WriteAttributeString("fitToWidth", Number(setup.FitToPagesWide ?? 1));
            writer.WriteAttributeString("fitToHeight", Number(setup.FitToPagesTall ?? 1));
        }

        writer.WriteAttributeString("orientation", setup.Orientation == PageOrientation.Landscape ? "landscape" : "portrait");
        writer.WriteEndElement();

        if (drawingRelationship is not null)
        {
            writer.WriteStartElement("drawing", XlsxConstants.Main);
            writer.WriteAttributeString("r", "id", XlsxConstants.Relationships, drawingRelationship);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        var hidden = sheet.MergedRanges
            .SelectMany(range => cells.Where(cell => cell.Value is not null || cell.Formula is not null)
                .Where(cell => cell.Row >= range.First.Row && cell.Row <= range.Last.Row && cell.Column >= range.First.Column && cell.Column <= range.Last.Column
                    && (cell.Row, cell.Column) != range.First)
                .Select(cell => cell.Reference))
            .ToList();
        if (hidden.Count > 0)
        {
            warnings.Add(new WriteWarning($"XLSX: in '{sheet.Name}' the cells {string.Join(", ", hidden)} lie inside a merged range; Excel shows only its top-left cell."));
        }
    }

    private static void WriteCell(XmlWriter writer, WorksheetCell cell, StyleTable styles, SharedStrings strings)
    {
        writer.WriteStartElement("c", XlsxConstants.Main);
        writer.WriteAttributeString("r", cell.Reference);
        var style = styles.Index(cell.Style, cell.Value);
        if (style != 0)
        {
            writer.WriteAttributeString("s", Number(style));
        }

        if (cell.Formula is { } formula)
        {
            writer.WriteStartElement("f", XlsxConstants.Main);
            writer.WriteString(formula);
            writer.WriteEndElement();
            writer.WriteEndElement();
            return;
        }

        switch (cell.Value)
        {
            case string or RichText:
                writer.WriteAttributeString("t", "s");
                writer.WriteElementString("v", XlsxConstants.Main, Number(strings.Index(cell.Value)));
                break;
            case bool flag:
                writer.WriteAttributeString("t", "b");
                writer.WriteElementString("v", XlsxConstants.Main, flag ? "1" : "0");
                break;
            case double number:
                writer.WriteElementString("v", XlsxConstants.Main, Number(number));
                break;
            case DateTime date:
                writer.WriteElementString("v", XlsxConstants.Main, Number(date.ToOADate()));
                break;
        }

        writer.WriteEndElement();
    }

    private static void WriteDrawing(XmlWriter writer, Worksheet sheet, List<string> relationships)
    {
        const string xdr = XlsxConstants.Drawing;
        const string a = XlsxConstants.DrawingMain;
        writer.WriteStartElement("xdr", "wsDr", xdr);
        writer.WriteAttributeString("xmlns", "a", null, a);
        writer.WriteAttributeString("xmlns", "r", null, XlsxConstants.Relationships);
        for (var index = 0; index < sheet.Images.Count; index++)
        {
            var image = sheet.Images[index];
            var width = (image.WidthPixels ?? image.Info.PixelWidth) * XlsxConstants.EmuPerPixel;
            var height = (image.HeightPixels ?? image.Info.PixelHeight) * XlsxConstants.EmuPerPixel;
            writer.WriteStartElement("xdr", "oneCellAnchor", xdr);
            writer.WriteStartElement("xdr", "from", xdr);
            writer.WriteElementString("xdr", "col", xdr, Number(image.Column - 1));
            writer.WriteElementString("xdr", "colOff", xdr, "0");
            writer.WriteElementString("xdr", "row", xdr, Number(image.Row - 1));
            writer.WriteElementString("xdr", "rowOff", xdr, "0");
            writer.WriteEndElement();
            writer.WriteStartElement("xdr", "ext", xdr);
            writer.WriteAttributeString("cx", Number(width));
            writer.WriteAttributeString("cy", Number(height));
            writer.WriteEndElement();

            writer.WriteStartElement("xdr", "pic", xdr);
            writer.WriteStartElement("xdr", "nvPicPr", xdr);
            writer.WriteStartElement("xdr", "cNvPr", xdr);
            writer.WriteAttributeString("id", Number(index + 2));
            writer.WriteAttributeString("name", $"Picture {index + 1}");
            if (image.AlternativeText is { } alternative)
            {
                writer.WriteAttributeString("descr", alternative);
            }

            writer.WriteEndElement();
            writer.WriteStartElement("xdr", "cNvPicPr", xdr);
            writer.WriteStartElement("a", "picLocks", a);
            writer.WriteAttributeString("noChangeAspect", "1");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("xdr", "blipFill", xdr);
            writer.WriteStartElement("a", "blip", a);
            writer.WriteAttributeString("r", "embed", XlsxConstants.Relationships, relationships[index]);
            writer.WriteEndElement();
            writer.WriteStartElement("a", "stretch", a);
            writer.WriteStartElement("a", "fillRect", a);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("xdr", "spPr", xdr);
            writer.WriteStartElement("a", "xfrm", a);
            writer.WriteStartElement("a", "off", a);
            writer.WriteAttributeString("x", "0");
            writer.WriteAttributeString("y", "0");
            writer.WriteEndElement();
            writer.WriteStartElement("a", "ext", a);
            writer.WriteAttributeString("cx", Number(width));
            writer.WriteAttributeString("cy", Number(height));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("a", "prstGeom", a);
            writer.WriteAttributeString("prst", "rect");
            writer.WriteStartElement("a", "avLst", a);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("xdr", "clientData", xdr);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Inches(Length length) => Number(Math.Round(length.To(LengthUnit.Inches), 4));

    /// <summary>The shared string table: each distinct text or rich text once.</summary>
    private sealed class SharedStrings
    {
        private readonly Dictionary<string, int> _plain = new(StringComparer.Ordinal);
        private readonly Dictionary<RichText, int> _rich = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _items = [];

        public int Count => _items.Count;

        public int Index(object? value)
        {
            if (value is RichText rich)
            {
                if (!_rich.TryGetValue(rich, out var richIndex))
                {
                    richIndex = _items.Count;
                    _rich.Add(rich, richIndex);
                    _items.Add(rich);
                }

                return richIndex;
            }

            var text = (string)value!;
            if (!_plain.TryGetValue(text, out var index))
            {
                index = _items.Count;
                _plain.Add(text, index);
                _items.Add(text);
            }

            return index;
        }

        public void Write(XmlWriter writer)
        {
            writer.WriteStartElement("sst", XlsxConstants.Main);
            writer.WriteAttributeString("count", Number(_items.Count));
            writer.WriteAttributeString("uniqueCount", Number(_items.Count));
            foreach (var item in _items)
            {
                writer.WriteStartElement("si", XlsxConstants.Main);
                if (item is RichText rich)
                {
                    foreach (var run in rich.Runs)
                    {
                        writer.WriteStartElement("r", XlsxConstants.Main);
                        if (run.Font is { } font)
                        {
                            writer.WriteStartElement("rPr", XlsxConstants.Main);
                            // CT_RPrElt order: rFont, b, i, color, sz.
                            if (font.Name is { } name)
                            {
                                WriteVal(writer, "rFont", name);
                            }

                            if (font.Bold)
                            {
                                writer.WriteElementString("b", XlsxConstants.Main, null);
                            }

                            if (font.Italic)
                            {
                                writer.WriteElementString("i", XlsxConstants.Main, null);
                            }

                            if (font.Color is { } color)
                            {
                                WriteColor(writer, "color", color);
                            }

                            if (font.Size is { } size)
                            {
                                WriteVal(writer, "sz", Number(size));
                            }

                            writer.WriteEndElement();
                        }

                        WriteText(writer, run.Text);
                        writer.WriteEndElement();
                    }
                }
                else
                {
                    WriteText(writer, (string)item);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        private static void WriteText(XmlWriter writer, string text)
        {
            writer.WriteStartElement("t", XlsxConstants.Main);
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n')))
            {
                writer.WriteAttributeString("xml", "space", null, "preserve");
            }

            writer.WriteString(text);
            writer.WriteEndElement();
        }
    }

    internal static void WriteVal(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name, XlsxConstants.Main);
        writer.WriteAttributeString("val", value);
        writer.WriteEndElement();
    }

    internal static void WriteColor(XmlWriter writer, string name, Color color)
    {
        writer.WriteStartElement(name, XlsxConstants.Main);
        writer.WriteAttributeString("rgb", "FF" + color.ToString().TrimStart('#'));
        writer.WriteEndElement();
    }

    /// <summary>
    /// The style sheet: number formats, fonts, fills, borders and cell formats, each distinct one once. Index 0 of each
    /// is the default Excel expects (fills 0 and 1 are reserved).
    /// </summary>
    private sealed class StyleTable
    {
        private readonly List<string> _formats = [];
        private readonly List<CellFont> _fonts = [CellFont.Default];
        private readonly List<Color?> _fills = [null, null];
        private readonly List<CellBorders> _borders = [CellBorders.None];
        private readonly List<(int Format, int Font, int Fill, int Border, CellStyle Style)> _cells = [(0, 0, 0, 0, CellStyle.Default)];
        private readonly Dictionary<(CellStyle Style, int Format), int> _index = [];

        public int Index(CellStyle style, object? value)
        {
            var format = style.NumberFormat is { } code && !code.Equals("General", StringComparison.OrdinalIgnoreCase)
                ? FormatId(code)
                : value is DateTime date ? (date.TimeOfDay == TimeSpan.Zero ? 14 : 22) : 0;
            if (format == 0 && style == CellStyle.Default)
            {
                return 0;
            }

            if (_index.TryGetValue((style, format), out var index))
            {
                return index;
            }

            var fill = style.Fill is { } color ? Add(_fills, color, 2) : 0;
            index = _cells.Count;
            _cells.Add((format, Add(_fonts, style.Font, 0), fill, Add(_borders, style.Border, 0), style));
            _index.Add((style, format), index);
            return index;
        }

        private int FormatId(string code)
        {
            var index = _formats.IndexOf(code);
            if (index < 0)
            {
                index = _formats.Count;
                _formats.Add(code);
            }

            return 164 + index;
        }

        private static int Add<T>(List<T> list, T item, int start)
        {
            var index = list.FindIndex(start, existing => EqualityComparer<T>.Default.Equals(existing, item));
            if (index < 0)
            {
                index = list.Count;
                list.Add(item);
            }

            return index;
        }

        public void Write(XmlWriter writer)
        {
            const string main = XlsxConstants.Main;
            writer.WriteStartElement("styleSheet", main);
            if (_formats.Count > 0)
            {
                writer.WriteStartElement("numFmts", main);
                writer.WriteAttributeString("count", Number(_formats.Count));
                for (var index = 0; index < _formats.Count; index++)
                {
                    writer.WriteStartElement("numFmt", main);
                    writer.WriteAttributeString("numFmtId", Number(164 + index));
                    writer.WriteAttributeString("formatCode", _formats[index]);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteStartElement("fonts", main);
            writer.WriteAttributeString("count", Number(_fonts.Count));
            foreach (var font in _fonts)
            {
                // CT_Font order: b, i, sz, color, name, family.
                writer.WriteStartElement("font", main);
                if (font.Bold)
                {
                    writer.WriteElementString("b", main, null);
                }

                if (font.Italic)
                {
                    writer.WriteElementString("i", main, null);
                }

                WriteVal(writer, "sz", Number(font.Size ?? XlsxConstants.DefaultFontSize));
                if (font.Color is { } color)
                {
                    WriteColor(writer, "color", color);
                }

                WriteVal(writer, "name", font.Name ?? XlsxConstants.DefaultFontName);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("fills", main);
            writer.WriteAttributeString("count", Number(_fills.Count));
            for (var index = 0; index < _fills.Count; index++)
            {
                writer.WriteStartElement("fill", main);
                writer.WriteStartElement("patternFill", main);
                if (index == 0)
                {
                    writer.WriteAttributeString("patternType", "none");
                }
                else if (index == 1)
                {
                    writer.WriteAttributeString("patternType", "gray125");
                }
                else
                {
                    writer.WriteAttributeString("patternType", "solid");
                    WriteColor(writer, "fgColor", _fills[index]!.Value);
                    writer.WriteStartElement("bgColor", main);
                    writer.WriteAttributeString("indexed", "64");
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("borders", main);
            writer.WriteAttributeString("count", Number(_borders.Count));
            foreach (var border in _borders)
            {
                writer.WriteStartElement("border", main);
                WriteSide(writer, "left", border.Left);
                WriteSide(writer, "right", border.Right);
                WriteSide(writer, "top", border.Top);
                WriteSide(writer, "bottom", border.Bottom);
                WriteSide(writer, "diagonal", null);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("cellStyleXfs", main);
            writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("xf", main);
            writer.WriteAttributeString("numFmtId", "0");
            writer.WriteAttributeString("fontId", "0");
            writer.WriteAttributeString("fillId", "0");
            writer.WriteAttributeString("borderId", "0");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("cellXfs", main);
            writer.WriteAttributeString("count", Number(_cells.Count));
            foreach (var (format, font, fill, border, style) in _cells)
            {
                writer.WriteStartElement("xf", main);
                writer.WriteAttributeString("numFmtId", Number(format));
                writer.WriteAttributeString("fontId", Number(font));
                writer.WriteAttributeString("fillId", Number(fill));
                writer.WriteAttributeString("borderId", Number(border));
                writer.WriteAttributeString("xfId", "0");
                if (format != 0) writer.WriteAttributeString("applyNumberFormat", "1");
                if (font != 0) writer.WriteAttributeString("applyFont", "1");
                if (fill != 0) writer.WriteAttributeString("applyFill", "1");
                if (border != 0) writer.WriteAttributeString("applyBorder", "1");
                var aligned = style.HorizontalAlignment != CellHorizontalAlignment.General
                    || style.VerticalAlignment != CellVerticalAlignment.Bottom || style.WrapText || style.Indent > 0;
                if (aligned)
                {
                    writer.WriteAttributeString("applyAlignment", "1");
                    writer.WriteStartElement("alignment", main);
                    // Excel indents only left or right aligned cells; an indented General cell is aligned left, as Excel's own
                    // dialog does when an indent is set.
                    var horizontal = style.HorizontalAlignment == CellHorizontalAlignment.General && style.Indent > 0
                        ? CellHorizontalAlignment.Left
                        : style.HorizontalAlignment;
                    if (horizontal != CellHorizontalAlignment.General)
                    {
                        writer.WriteAttributeString("horizontal", horizontal.ToString().ToLowerInvariant());
                    }

                    if (style.VerticalAlignment != CellVerticalAlignment.Bottom)
                    {
                        writer.WriteAttributeString("vertical", style.VerticalAlignment.ToString().ToLowerInvariant());
                    }

                    if (style.WrapText)
                    {
                        writer.WriteAttributeString("wrapText", "1");
                    }

                    if (style.Indent > 0)
                    {
                        writer.WriteAttributeString("indent", Number(style.Indent));
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();

            writer.WriteStartElement("cellStyles", main);
            writer.WriteAttributeString("count", "1");
            writer.WriteStartElement("cellStyle", main);
            writer.WriteAttributeString("name", "Normal");
            writer.WriteAttributeString("xfId", "0");
            writer.WriteAttributeString("builtinId", "0");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static void WriteSide(XmlWriter writer, string side, CellBorderLine? line)
        {
            writer.WriteStartElement(side, XlsxConstants.Main);
            if (line is not null)
            {
                writer.WriteAttributeString("style", line.Style.ToString().ToLowerInvariant());
                if (line.Color is { } color)
                {
                    WriteColor(writer, "color", color);
                }
                else
                {
                    writer.WriteStartElement("color", XlsxConstants.Main);
                    writer.WriteAttributeString("indexed", "64");
                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement();
        }
    }
}
