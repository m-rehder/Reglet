using Reglet;
using System.Text;
using System.Xml.Linq;

/// <summary>
/// import of the DOCX tables (phase AJ, AJ01): the style, the borders, the cell padding, the
/// width in percent, the column widths, the row height and the cell properties.
/// </summary>
internal static class TableImportChecks
{
    internal static int Run()
    {
        try
        {
            ReadsTableProperties();
            ReadsTableStyle();
            ReadsCellProperties();
            ReadsRowSpans();
            ReadsOdtTableProperties();
            ReadsInheritedOdtCellStyles();
            ReadsOdtRowHeights();
            ReadsOdtTableWidth();
            ReadsInheritedOdtColumnWidths();
            ReadsRelativeOdtColumnWidths();
            PreservesExplicitlyHiddenCellBorders();
            ReadsRepeatedOdtTableParts();
            Console.WriteLine("Table import (AJ01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Table import (AJ01): " + exception);
            return 1;
        }
    }

    private static void ReadsTableProperties()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
        })
        {
            Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Color.FromHex("#0000FF")),
            CellPadding = Length.FromPoints(4),
            WidthPercent = 80,
            ColumnWidths = new[] { Length.FromPoints(100), Length.FromPoints(120) },
        };

        var read = ReadTable(table);

        var border = read.Border;
        Require(border is not null, "DOCX: the table border is read.");
        Require(border!.Style == BorderStyle.Single, "DOCX: the border style is read.");
        Require(border.Width.Points == 1, "DOCX: the border width is read.");
        Require(border.Color.ToString() == Color.FromHex("#0000FF").ToString(), "DOCX: the border color is read.");
        Require(read.CellPadding?.Points == 4, "DOCX: the cell padding is read.");
        Require(read.WidthPercent == 80, "DOCX: the width in percent is read.");
        Require(read.ColumnWidths is { Count: 2 } widths, "DOCX: the column widths are read.");
        Require(
            read.ColumnWidths is { Count: 2 } values && values[0].Points == 100 && values[1].Points == 120,
            "DOCX: the column widths have the right values.");
    }

    private static void ReadsTableStyle()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B") }) { IsHeader = true },
            new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
        })
        {
            Style = new TableStyle("Grid")
            {
                HeaderBackground = Color.FromHex("#D9E1F2"),
                BandBackground = Color.FromHex("#F2F2F2"),
                HeaderRow = new TableStyleArea { Bold = true },
                BandRow = new TableStyleArea { TextColor = Color.FromHex("#808080") },
            },
        };

        var read = ReadTable(table);

        var style = read.Style;
        Require(style is not null, "DOCX: the table style is read.");
        Require(style!.Name == "Grid", "DOCX: the name of the table style is read.");
        Require(
            style.HeaderBackground?.ToString() == Color.FromHex("#D9E1F2").ToString(),
            "DOCX: the background of the header row is read.");
        Require(
            style.BandBackground?.ToString() == Color.FromHex("#F2F2F2").ToString(),
            "DOCX: the background of the banded rows is read.");
        Require(style.HeaderRow?.Bold == true, "DOCX: the bold text of the header row is read.");
        Require(
            style.BandRow?.TextColor?.ToString() == Color.FromHex("#808080").ToString(),
            "DOCX: the text color of the banded rows is read.");
    }

    private static void ReadsCellProperties()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[]
            {
                new TableCell("A")
                {
                    Background = Color.FromHex("#FF0000"),
                    VerticalAlignment = TableCellVerticalAlignment.Center,
                    Borders = ParagraphBorders.All(new ParagraphBorder(BorderStyle.Double)),
                },
            }),
        });

        var read = ReadTable(table);
        var cell = read.Rows[0].Cells[0];

        Require(
            cell.Background?.ToString() == Color.FromHex("#FF0000").ToString(),
            "DOCX: the background of the cell is read.");
        Require(
            cell.VerticalAlignment == TableCellVerticalAlignment.Center,
            "DOCX: the vertical alignment of the cell is read.");
        Require(cell.Borders?.Top?.Style == BorderStyle.Double, "DOCX: the border of the cell is read.");
    }

    private static void ReadsRowSpans()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
            new TableRow(new[] { new TableCell("1") { RowSpan = 2 }, new TableCell("2") }),
            new TableRow(new[] { new TableCell("3") }),
        });

        var read = ReadTable(table);

        Require(read.Rows.Count == 3, "DOCX: the rows are read.");
        Require(read.Rows[1].Cells[0].RowSpan == 2, "DOCX: the row span is read.");
        Require(read.Rows[1].Cells.Count == 2, "DOCX: the cells of the row are read.");
        Require(read.Rows[2].Cells.Count == 1, "DOCX: the covered cell is omitted.");
    }

    private static void ReadsOdtTableProperties()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[]
            {
                new TableCell("A") { Background = Color.FromHex("#FF0000"), VerticalAlignment = TableCellVerticalAlignment.Center,
                    Borders = new ParagraphBorders { Top = new ParagraphBorder(BorderStyle.Dashed, Length.FromPoints(1), Color.FromHex("#123456")) } },
                new TableCell("B"),
            }),
        })
        {
            ColumnWidths = new[] { Length.FromPoints(100), Length.FromPoints(120) },
            Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(0.5), Color.FromHex("#000000")),
            CellPadding = Length.FromPoints(5),
        };

        var document = new PortableDocument(new DocumentMetadata(), new Section(table));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var readTable = (TableBlock)read.Sections[0].Blocks[0];

        Require(
            readTable.Rows[0].Cells[0].Background?.ToString() == Color.FromHex("#FF0000").ToString(),
            "ODT: the background of the cell is read.");
        Require(readTable.Rows[0].Cells[0].VerticalAlignment == TableCellVerticalAlignment.Center, "ODT: vertical cell alignment is read.");
        var top = readTable.Rows[0].Cells[0].Borders?.Top;
        Require(top?.Style == BorderStyle.Dashed && top.Color.ToString() == "#123456" && Math.Abs(top.Width.Points - 1) < 0.02,
            "ODT: cell border style, color and width are read.");
        Require(readTable.ColumnWidths is { Count: 2 }, "ODT: the column widths are read.");
        Require(
            readTable.ColumnWidths is { Count: 2 } widths
                && Math.Abs(widths[0].Points - 100) < 0.2
                && Math.Abs(widths[1].Points - 120) < 0.2,
            "ODT: the column widths have the right values.");
        Require(readTable.Border is { } tableBorder && tableBorder.Style == BorderStyle.Single
            && Math.Abs(tableBorder.Width.Points - 0.5) < 0.02,
            "ODT: the table border is read from the table style.");
        Require(readTable.CellPadding is { } padding && Math.Abs(padding.Points - 5) < 0.1,
            "ODT: the table cell padding is read from the table style.");

        var converted = (TableBlock)TestPackages.Read(
            TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(readTable)), DocumentFormat.Docx),
            DocumentFormat.Docx).Sections[0].Blocks[0];
        Require(converted.CellPadding is { } convertedPadding && Math.Abs(convertedPadding.Points - 5) < 0.1
            && converted.Border is { Style: BorderStyle.Single },
            "ODT: table border and padding survive DOCX conversion.");
    }

    private static void ReadsRepeatedOdtTableParts()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        var document = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock(
            [new TableRow([new TableCell("A")])]) { ColumnWidths = [Length.FromPoints(100)] }));
        var package = TestPackages.Write(document, DocumentFormat.Odt);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var row = xml.Descendants(table + "table-row").First();
        var cell = row.Element(table + "table-cell")!;
        var column = xml.Descendants(table + "table-column").First();
        row.SetAttributeValue(table + "number-rows-repeated", "2");
        cell.SetAttributeValue(table + "number-columns-repeated", "3");
        column.SetAttributeValue(table + "number-columns-repeated", "3");
        byte[] Edited() => TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(xml.ToString()));
        var read = (TableBlock)TestPackages.Read(Edited(), DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.Rows.Count == 2 && read.Rows.All(item => item.Cells.Count == 3), "ODT: repeated rows and cells expand.");
        Require(read.ColumnWidths is { Count: 3 } && read.Rows[1].Cells.All(item =>
            ((TextRun)((Paragraph)item.Blocks[0]).Inlines[0]).Text == "A"), "ODT: repeated column widths and cell contents survive.");
        var regenerated = new PortableDocument(new DocumentMetadata(), new Section(read));
        var written = TestPackages.Write(regenerated, DocumentFormat.Odt);
        var reread = (TableBlock)TestPackages.Read(written, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(reread.Rows.Count == 2 && reread.Rows.All(item => item.Cells.Count == 3),
            "ODT: expanded rows and cells survive regeneration.");

        row.SetAttributeValue(table + "number-rows-repeated", "1001");
        cell.SetAttributeValue(table + "number-columns-repeated", "1000");
        var rejected = false;
        try { TestPackages.Read(Edited(), DocumentFormat.Odt); }
        catch (DocumentValidationException) { rejected = true; }
        Require(rejected, "ODT: nested repetition exceeding the total cell limit is rejected.");
    }

    private static void ReadsOdtRowHeights()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        var document = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock(
        [
            new TableRow(new TableCell("fixed")) { Height = Length.FromPoints(20), HeightRule = TableRowHeightRule.Exactly },
            new TableRow(new TableCell("minimum")) { Height = Length.FromPoints(30), HeightRule = TableRowHeightRule.AtLeast },
        ])));
        var package = TestPackages.Write(document, DocumentFormat.Odt);
        var content = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var rows = content.Descendants(table + "table-row").ToArray();
        var styleName = (string)rows[1].Attribute(table + "style-name")!;
        var minStyle = content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == styleName);
        var properties = minStyle.Element(style + "table-row-properties")!;
        // 30 pt for the content plus the 0.5 pt default grid above the row (ODF counts the whole row).
        Require((string?)properties.Attribute(style + "min-row-height") == "30.5pt", "ODT: minimum row height uses the standard attribute.");
        var read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.Rows[0].Height?.Points == 20 && read.Rows[0].HeightRule == TableRowHeightRule.Exactly
            && read.Rows[1].Height?.Points == 30 && read.Rows[1].HeightRule == TableRowHeightRule.AtLeast,
            "ODT: fixed and minimum row heights survive reading.");

        properties.SetAttributeValue(style + "min-row-height", null);
        properties.SetAttributeValue(style + "row-height", "30pt");
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.Rows[1].HeightRule == TableRowHeightRule.AtLeast, "ODT: legacy optimal row-height remains a minimum.");

        minStyle.SetAttributeValue(style + "parent-style-name", "ParentRow");
        properties.Remove();
        var styles = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml")));
        styles.Root!.Element(office + "styles")!.Add(new XElement(style + "style", new XAttribute(style + "name", "ParentRow"),
            new XAttribute(style + "family", "table-row"), new XElement(style + "table-row-properties", new XAttribute(style + "min-row-height", "25pt"))));
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        package = TestPackages.ReplacePart(package, "styles.xml", Encoding.UTF8.GetBytes(styles.ToString()));
        read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        // ODF's 25 pt are the whole row; the model keeps the content minimum, without the 0.5 pt grid above.
        Require(read.Rows[1].Height?.Points == 24.5 && read.Rows[1].HeightRule == TableRowHeightRule.AtLeast,
            "ODT: a minimum row height is inherited from a named style.");
        var regenerated = new PortableDocument(new DocumentMetadata(), new Section(read));
        var docx = (TableBlock)TestPackages.Read(TestPackages.Write(regenerated, DocumentFormat.Docx), DocumentFormat.Docx).Sections[0].Blocks[0];
        Require(docx.Rows[0].HeightRule == TableRowHeightRule.Exactly && docx.Rows[1].HeightRule == TableRowHeightRule.AtLeast
            && docx.Rows[1].Height?.Points == 24.5, "ODT row heights survive conversion to DOCX.");
        minStyle.Add(new XElement(style + "table-row-properties", new XAttribute(style + "row-height", "40pt"),
            new XAttribute(style + "use-optimal-row-height", "false")));
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.Rows[1].Height?.Points == 40 && read.Rows[1].HeightRule == TableRowHeightRule.Exactly,
            "ODT: a child fixed height overrides its parent's minimum.");
    }

    private static void ReadsOdtTableWidth()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        var original = new TableBlock([new TableRow([new TableCell("A"), new TableCell("B")])]) { WidthPercent = 80 };
        var package = TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(original)), DocumentFormat.Odt);
        var read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.WidthPercent == 80, "ODT: relative table width is read.");
        var regenerated = new PortableDocument(new DocumentMetadata(), new Section(read));
        foreach (var format in new[] { DocumentFormat.Odt, DocumentFormat.Docx })
        {
            var roundTrip = (TableBlock)TestPackages.Read(TestPackages.Write(regenerated, format), format).Sections[0].Blocks[0];
            Require(roundTrip.WidthPercent == 80, $"{format}: relative table width survives conversion.");
        }
        var withHeader = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("body")))
        {
            Header = new HeaderFooter(original),
        });
        var band = TestPackages.Read(TestPackages.Write(withHeader, DocumentFormat.Odt), DocumentFormat.Odt).Sections[0].Header;
        Require(band?.Blocks.OfType<TableBlock>().Single().WidthPercent == 80, "ODT: header table width is read from styles.xml.");

        var content = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var name = (string)content.Descendants(table + "table").First().Attribute(table + "style-name")!;
        var child = content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == name);
        child.SetAttributeValue(style + "parent-style-name", "TableParent");
        child.Element(style + "table-properties")!.SetAttributeValue(style + "rel-width", null);
        var styles = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml")));
        styles.Root!.Element(office + "styles")!.Add(new XElement(style + "style", new XAttribute(style + "name", "TableParent"),
            new XAttribute(style + "family", "table"), new XElement(style + "table-properties", new XAttribute(style + "rel-width", "62.5%"))));
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        package = TestPackages.ReplacePart(package, "styles.xml", Encoding.UTF8.GetBytes(styles.ToString()));
        read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.WidthPercent == 62.5, "ODT: relative table width is inherited from a named style.");

        child.Element(style + "table-properties")!.SetAttributeValue(style + "rel-width", "700%");
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        var unsupported = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt);
        Require(((TableBlock)unsupported.Document.Sections[0].Blocks[0]).WidthPercent is null
            && unsupported.Warnings.Any(warning => warning.Message.Contains("table width", StringComparison.Ordinal)),
            "ODT: a relative width outside the model's range is reported, not clamped.");
    }

    private static void ReadsInheritedOdtColumnWidths()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        var original = new TableBlock([new TableRow([new TableCell("A"), new TableCell("B")])])
        {
            ColumnWidths = [Length.FromPoints(100), Length.FromPoints(120)],
        };
        var package = TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(original)), DocumentFormat.Odt);
        var content = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var columns = content.Descendants(table + "table-column").ToArray();
        var firstStyle = (string)columns[0].Attribute(table + "style-name")!;
        var child = content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == firstStyle);
        child.SetAttributeValue(style + "parent-style-name", "ColumnParent");
        child.Element(style + "table-column-properties")!.SetAttributeValue(style + "column-width", null);
        var styles = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml")));
        styles.Root!.Element(office + "styles")!.Add(new XElement(style + "style", new XAttribute(style + "name", "ColumnParent"),
            new XAttribute(style + "family", "table-column"), new XElement(style + "table-column-properties", new XAttribute(style + "column-width", "75pt"))));
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        package = TestPackages.ReplacePart(package, "styles.xml", Encoding.UTF8.GetBytes(styles.ToString()));
        var read = (TableBlock)TestPackages.Read(package, DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.ColumnWidths is { Count: 2 } widths && Math.Abs(widths[0].Points - 75) < 0.1 && Math.Abs(widths[1].Points - 120) < 0.2,
            "ODT: inherited column width stays on its own column.");
        var converted = new PortableDocument(new DocumentMetadata(), new Section(read));
        var docx = (TableBlock)TestPackages.Read(TestPackages.Write(converted, DocumentFormat.Docx), DocumentFormat.Docx).Sections[0].Blocks[0];
        Require(docx.ColumnWidths is { Count: 2 } convertedWidths && Math.Abs(convertedWidths[0].Points - 75) < 0.1,
            "ODT: inherited column widths survive DOCX conversion.");

        columns[0].SetAttributeValue(table + "style-name", "MissingColumn");
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        var incomplete = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt);
        var incompleteTable = (TableBlock)incomplete.Document.Sections[0].Blocks[0];
        Require(incompleteTable.ColumnWidths is null && incomplete.Warnings.Any(warning => warning.Message.Contains("column widths", StringComparison.Ordinal)),
            "ODT: a missing first column width is reported instead of assigning the second width to it.");
    }

    private static void ReadsRelativeOdtColumnWidths()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var original = new TableBlock([new TableRow([new TableCell("A"), new TableCell("B")])])
        {
            ColumnWidths = [Length.FromPoints(100), Length.FromPoints(120)], WidthPercent = 80,
        };
        var package = TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(original)), DocumentFormat.Odt);
        var content = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var columns = content.Descendants(table + "table-column").ToArray();
        var tableName = (string)content.Descendants(table + "table").First().Attribute(table + "style-name")!;
        var tableProperties = content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == tableName)
            .Element(style + "table-properties")!;
        tableProperties.SetAttributeValue(style + "rel-width", null);
        tableProperties.SetAttributeValue(style + "width", "300pt");
        var columnProperties = columns.Select(column =>
        {
            var name = (string)column.Attribute(table + "style-name")!;
            return content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == name)
                .Element(style + "table-column-properties")!;
        }).ToArray();
        for (var index = 0; index < columnProperties.Length; index++)
        {
            columnProperties[index].SetAttributeValue(style + "column-width", null);
            columnProperties[index].SetAttributeValue(style + "rel-column-width", (index + 1) + "*");
        }
        byte[] Edited() => TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        var read = (TableBlock)TestPackages.Read(Edited(), DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.ColumnWidths is { Count: 2 } widths && widths[0].Points == 100 && widths[1].Points == 200,
            "ODT: relative widths use the fixed table width and preserve proportions.");
        var docx = (TableBlock)TestPackages.Read(TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(read)), DocumentFormat.Docx), DocumentFormat.Docx).Sections[0].Blocks[0];
        Require(docx.ColumnWidths is { Count: 2 } docxWidths && docxWidths[0].Points == 100 && docxWidths[1].Points == 200,
            "ODT: resolved relative widths survive DOCX conversion.");
        columnProperties[0].SetAttributeValue(style + "column-width", "60pt");
        read = (TableBlock)TestPackages.Read(Edited(), DocumentFormat.Odt).Sections[0].Blocks[0];
        Require(read.ColumnWidths is { Count: 2 } mixed && mixed[0].Points == 60 && mixed[1].Points == 240,
            "ODT: relative columns share the width left after fixed columns.");
        columnProperties[0].SetAttributeValue(style + "column-width", "300pt");
        var exhausted = DocumentReader.Read(new MemoryStream(Edited()), DocumentFormat.Odt);
        Require(((TableBlock)exhausted.Document.Sections[0].Blocks[0]).ColumnWidths is null
            && exhausted.Warnings.Any(warning => warning.Message.Contains("column widths", StringComparison.Ordinal)),
            "ODT: no remaining space for relative columns is reported.");
        columnProperties[0].SetAttributeValue(style + "column-width", "60pt");
        tableProperties.SetAttributeValue(style + "width", null);
        var relativeToPage = TestPackages.Read(Edited(), DocumentFormat.Odt);
        var pageWidth = PageSize.A4.Width.Points - PageMargins.Normal.Left.Points - PageMargins.Normal.Right.Points;
        var relativeTable = (TableBlock)relativeToPage.Sections[0].Blocks[0];
        Require(relativeTable.ColumnWidths is { Count: 2 } relativeWidths
            && Math.Abs(relativeWidths[0].Points - 60) < 0.2
            && Math.Abs(relativeWidths[1].Points - (pageWidth - 60)) < 0.5,
            "ODT: relative columns without an absolute table width use the text area.");

        // A relative table width scales the text area before the columns share it.
        tableProperties.SetAttributeValue(style + "rel-width", "50%");
        var percentToPage = TestPackages.Read(Edited(), DocumentFormat.Odt);
        var percentTable = (TableBlock)percentToPage.Sections[0].Blocks[0];
        Require(percentTable.ColumnWidths is { Count: 2 } percentWidths
            && Math.Abs(percentWidths[0].Points - 60) < 0.2
            && Math.Abs(percentWidths[1].Points - (pageWidth * 0.5 - 60)) < 0.5,
            "ODT: relative columns use the percent table width of the text area.");

        // All columns relative and no table width: the text area is shared by the weights.
        columnProperties[0].SetAttributeValue(style + "column-width", null);
        columnProperties[0].SetAttributeValue(style + "rel-column-width", "1*");
        tableProperties.SetAttributeValue(style + "rel-width", null);
        var fromTextArea = TestPackages.Read(Edited(), DocumentFormat.Odt);
        var areasTable = (TableBlock)fromTextArea.Sections[0].Blocks[0];
        Require(areasTable.ColumnWidths is { Count: 2 } areaWidths
            && Math.Abs(areaWidths[0].Points - pageWidth / 3) < 0.5
            && Math.Abs(areaWidths[1].Points - pageWidth * 2 / 3) < 0.5,
            "ODT: relative columns share the text area by their weights.");
    }

    private static void ReadsInheritedOdtCellStyles()
    {
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
        var document = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([new TableRow([new TableCell("A")])])));
        var package = TestPackages.Write(document, DocumentFormat.Odt);
        var content = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        var styleName = (string)content.Descendants(table + "table-cell").First().Attribute(table + "style-name")!;
        var child = content.Descendants(style + "style").First(node => (string?)node.Attribute(style + "name") == styleName);
        child.SetAttributeValue(style + "parent-style-name", "CellParent");
        child.Element(style + "table-cell-properties")!.ReplaceAttributes(
            new XAttribute(fo + "padding", "2pt"), new XAttribute(fo + "border", "0.5pt dashed #123456"),
            new XAttribute(fo + "border-right", "none"), new XAttribute(style + "vertical-align", "bottom"));
        var styles = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml")));
        styles.Root!.Element(office + "styles")!.Add(new XElement(style + "style",
            new XAttribute(style + "name", "CellParent"), new XAttribute(style + "family", "table-cell"),
            new XElement(style + "table-cell-properties", new XAttribute(fo + "background-color", "#AABBCC"),
                new XAttribute(fo + "padding-left", "5pt"), new XAttribute(fo + "border-left", "1pt solid #000000"))));
        package = TestPackages.ReplacePart(package, "styles.xml", Encoding.UTF8.GetBytes(styles.ToString()));
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var cell = ((TableBlock)read.Sections[0].Blocks[0]).Rows[0].Cells[0];
        Require(cell.Background?.ToString() == "#AABBCC" && cell.Padding?.Points == 2
            && cell.VerticalAlignment == TableCellVerticalAlignment.Bottom, "ODT: cell style inherits parent properties and child padding wins.");
        Require(cell.Borders?.Left?.Style == BorderStyle.Dashed && cell.Borders.Right?.Style == BorderStyle.None,
            "ODT: child shorthand overrides parent sides and an explicit none is retained.");
        var regenerated = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([new TableRow([cell])])));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var roundTrip = TestPackages.Read(TestPackages.Write(regenerated, format), format);
            var writtenCell = ((TableBlock)roundTrip.Sections[0].Blocks[0]).Rows[0].Cells[0];
            Require(writtenCell.Borders?.Right?.Style == BorderStyle.None && writtenCell.Borders.Left?.Style == BorderStyle.Dashed,
                $"{format}: inherited borders survive regeneration.");
        }
        child.SetAttributeValue(style + "parent-style-name", "MissingParent");
        package = TestPackages.ReplacePart(package, "content.xml", Encoding.UTF8.GetBytes(content.ToString()));
        var incomplete = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Odt);
        Require(incomplete.Warnings.Any(warning => warning.Message.Contains("cell style", StringComparison.Ordinal)),
            "ODT: a missing parent cell style is reported.");
    }

    private static void PreservesExplicitlyHiddenCellBorders()
    {
        var border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Color.FromHex("#123456"));
        var cell = new TableCell("A") { Borders = new ParagraphBorders { Right = new ParagraphBorder(BorderStyle.None) } };
        var table = new TableBlock([new TableRow([cell]) { IsHeader = true }])
        {
            Border = border,
            Style = new TableStyle("Styled") { HeaderRow = new TableStyleArea { Border = border } },
        };
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var bytes = TestPackages.Write(document, format);
            var result = TestPackages.Read(bytes, format);
            var readCell = ((TableBlock)result.Sections[0].Blocks[0]).Rows[0].Cells[0];
            Require(readCell.Borders?.Right?.Style == BorderStyle.None, $"{format}: an explicit missing cell border survives writing and reading.");
            if (format == DocumentFormat.Docx)
            {
                var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(bytes, "word/document.xml")));
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Require((string?)xml.Descendants(w + "tcBorders").First().Element(w + "right")?.Attribute(w + "val") == "nil",
                    "DOCX: the absent edge is explicitly written as nil.");
            }
            else
            {
                XNamespace odtStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
                XNamespace odtTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
                XNamespace fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
                var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(bytes, "content.xml")));
                var styleName = (string?)xml.Descendants(odtTable + "table-cell").First().Attribute(odtTable + "style-name");
                var properties = xml.Descendants(odtStyle + "style").Single(node => (string?)node.Attribute(odtStyle + "name") == styleName)
                    .Element(odtStyle + "table-cell-properties")!;
                Require((string?)properties.Attribute(fo + "border-right") == "none" && properties.Attribute(fo + "border-top") is not null,
                    "ODT: an absent edge overrides the conditional border without dropping the other edges.");
            }
        }

        var withBorder = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([new TableRow([new TableCell("A")])]) { Border = border }));
        var withoutRight = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([new TableRow([cell])]) { Border = border }));
        var fullPdf = PdfStreams.Decompress(TestPackages.Write(withBorder, DocumentFormat.Pdf));
        var partialPdf = PdfStreams.Decompress(TestPackages.Write(withoutRight, DocumentFormat.Pdf));
        Require(fullPdf.Split(" l\n").Length == partialPdf.Split(" l\n").Length + 1,
            "PDF: exactly one explicit cell edge is suppressed.");
    }

    /// <summary>Writes the table as DOCX, reads it and returns the table of the result.</summary>
    private static TableBlock ReadTable(TableBlock table)
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(table));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        return (TableBlock)read.Sections[0].Blocks[0];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
