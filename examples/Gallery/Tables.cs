using Reglet;

namespace Gallery;

/// <summary>Tables: from a quick grid to merged cells, shading and a table that runs over several pages.</summary>
public static class Tables
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("tables", "simple-table", "A header row and three data rows in three lines of code.")
        {
            Document = () => PortableDocument.Create()
                .Metadata(title: "Simple table")
                .Heading("Opening hours")
                .Table(t => t.Header("Day", "Opens", "Closes").Row("Monday to Friday", "08:00", "18:00").Row("Saturday", "09:00", "14:00").Row("Sunday", "closed", ""))
                .Build(),
        };
        yield return new Example("tables", "table-formatting", "Column widths, a shaded header, merged cells, alignment and borders.")
        {
            Document = Formatting,
        };
        yield return new Example("tables", "long-table", "A price list over several pages; the header row repeats on every page.")
        {
            Document = LongTable,
        };
    }

    private static PortableDocument Formatting()
    {
        TableCell Head(string text) => new(new Paragraph(new TextRun(text) { Bold = true, Color = Color.White })) { Background = Assets.Petrol };
        TableCell Number(string text) => new(text) { Alignment = TextAlignment.Right };

        var rows = new[]
        {
            new TableRow(Head("Region"), Head("Quarter"), Head("Units"), Head("Revenue")) { IsHeader = true },
            new TableRow(new TableCell("North") { RowSpan = 2, VerticalAlignment = TableCellVerticalAlignment.Center }, new TableCell("Q1"), Number("1,250"), Number("€ 31,250")),
            new TableRow(new TableCell("Q2"), Number("1,410"), Number("€ 35,250")),
            new TableRow(new TableCell("South") { RowSpan = 2, VerticalAlignment = TableCellVerticalAlignment.Center }, new TableCell("Q1"), Number("980"), Number("€ 24,500")),
            new TableRow(new TableCell("Q2"), Number("1,105"), Number("€ 27,625")),
            new TableRow(
                new TableCell(new Paragraph(new TextRun("Total") { Bold = true })) { ColumnSpan = 2, Background = Assets.Tint },
                new TableCell(new Paragraph(new TextRun("4,745") { Bold = true })) { Alignment = TextAlignment.Right, Background = Assets.Tint },
                new TableCell(new Paragraph(new TextRun("€ 118,625") { Bold = true })) { Alignment = TextAlignment.Right, Background = Assets.Tint }),
        };

        var table = new TableBlock(rows)
        {
            ColumnWidths = [Length.FromCentimeters(4), Length.FromCentimeters(3), Length.FromCentimeters(3.5), Length.FromCentimeters(4.5)],
            Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(0.75), Assets.Petrol),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Table formatting" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Sales by region")),
            table,
        }));
    }

    private static PortableDocument LongTable()
    {
        var rows = new List<TableRow> { new(new TableCell("Article"), new TableCell("Description"), new TableCell("Price")) { IsHeader = true } };
        for (var index = 1; index <= 120; index++)
        {
            rows.Add(new TableRow(new TableCell($"A-{index:D4}"), new TableCell($"{Words.Paragraph(index, 4)}"), new TableCell($"€ {index * 3.75:0.00}") { Alignment = TextAlignment.Right }));
        }

        return new PortableDocument(new DocumentMetadata { Title = "Price list" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Price list")),
            new TableBlock(rows) { ColumnWidths = [Length.FromCentimeters(3), Length.FromCentimeters(9.5), Length.FromCentimeters(3.5)] },
        }));
    }
}
