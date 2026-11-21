using System.Globalization;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Reads an ODF chart sub-document (<c>office:chart</c> with <c>chart:chart</c> and its local table) into a
/// <see cref="Chart"/> (ADR-0025). Values come from the local table through the cell ranges of the series; cell
/// addresses are read in the forms ODF and LibreOffice write (<c>local-table.$B$2:.$B$5</c>, <c>B2:B5</c>, <c>.B2:.B5</c>).
/// </summary>
internal static class OdtChartReader
{
    private static readonly XNamespace ChartNs = OdtCharts.ChartNs;
    private static readonly XNamespace TableNs = OdtNamespaces.Table;
    private static readonly XNamespace OfficeNs = OdtNamespaces.Office;
    private static readonly XNamespace StyleNs = OdtNamespaces.Style;
    private static readonly XNamespace DrawNs = OdtNamespaces.Draw;
    private static readonly XNamespace SvgNs = OdtNamespaces.Svg;
    private static readonly XNamespace TextNs = OdtNamespaces.Text;

    private readonly record struct Cell(string? Text, double? Number);

    internal static bool IsChart(XElement content) => content.Element(OfficeNs + "body")?.Element(OfficeNs + "chart") is not null;

    internal static Chart? Read(XElement content, Length? width, Length? height, string? altText, ReaderGuard.ChartBudget budget, Action<string> warn)
    {
        try
        {
            return ReadChart(content, width, height, altText, budget, warn);
        }
        catch (DocumentValidationException)
        {
            warn("ODT: a chart above the data point limit of the document is not mapped when reading.");
            return null;
        }
    }

    private static Chart? ReadChart(XElement content, Length? width, Length? height, string? altText, ReaderGuard.ChartBudget budget, Action<string> warn)
    {
        if (content.Element(OfficeNs + "body")?.Element(OfficeNs + "chart") is not { } body
            || body.Element(ChartNs + "chart") is not { } root || root.Element(ChartNs + "plot-area") is not { } plot)
        {
            warn("ODT: a chart object without a plot area is not mapped when reading.");
            return null;
        }

        var styles = AutomaticStyles(content);
        var grid = ReadTable(budget, root.Descendants(TableNs + "table").FirstOrDefault());
        var chartClass = (string?)root.Attribute(ChartNs + "class");
        XElement? Properties(string? name) => name is not null && styles.TryGetValue(name, out var style) ? style.Chart : null;
        var plotProperties = Properties((string?)plot.Attribute(ChartNs + "style-name") ?? (string?)root.Attribute(ChartNs + "style-name"));

        ChartKind kind;
        switch (chartClass)
        {
            case "chart:bar":
                kind = (string?)plotProperties?.Attribute(ChartNs + "vertical") == "true" ? ChartKind.Bar : ChartKind.Column;
                break;
            case "chart:line":
                kind = ChartKind.Line;
                break;
            case "chart:area":
                kind = ChartKind.Area;
                break;
            case "chart:circle":
                kind = ChartKind.Pie;
                break;
            case "chart:scatter":
                kind = ChartKind.Scatter;
                break;
            default:
                warn($"ODT: the chart class '{chartClass}' is not mapped when reading.");
                return null;
        }

        var stacked = (string?)plotProperties?.Attribute(ChartNs + "stacked") == "true";
        var series = new List<ChartSeries>();
        var index = 0;
        foreach (var element in plot.Elements(ChartNs + "series"))
        {
            var thisIndex = index++;
            if ((string?)element.Attribute(ChartNs + "values-cell-range-address") is not { } valuesRange
                || Cells(budget, grid, valuesRange) is not { Count: > 0 } valueCells)
            {
                continue;
            }

            var values = valueCells.Select(c => c.Number ?? 0).ToList();
            var name = (string?)element.Attribute(ChartNs + "label-cell-address") is { } label ? Cells(budget, grid, label)?.FirstOrDefault().Text ?? string.Empty : string.Empty;
            List<double>? xValues = null;
            if (kind == ChartKind.Scatter)
            {
                if ((string?)element.Element(ChartNs + "domain")?.Attribute(TableNs + "cell-range-address") is { } domain
                    && Cells(budget, grid, domain) is { } xs && xs.Count == values.Count)
                {
                    xValues = xs.Select(c => c.Number ?? 0).ToList();
                }
                else
                {
                    continue;
                }
            }

            XElement? graphic = null;
            if ((string?)element.Attribute(ChartNs + "style-name") is { } styleName && styles.TryGetValue(styleName, out var found))
            {
                graphic = found.Graphic;
            }

            var colorText = (kind == ChartKind.Line ? (string?)graphic?.Attribute(SvgNs + "stroke-color") : null)
                ?? (string?)graphic?.Attribute(DrawNs + "fill-color") ?? (string?)graphic?.Attribute(SvgNs + "stroke-color");
            Color? color = Color.TryParseHex(colorText, out var parsed) && colorText!.StartsWith('#') ? parsed : null;
            if (color == Chart.DefaultPalette[thisIndex % Chart.DefaultPalette.Count])
            {
                color = null;
            }

            try
            {
                series.Add(new ChartSeries(name, values, xValues) { Color = color });
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (kind == ChartKind.Pie)
            {
                break;
            }
        }

        if (series.Count == 0)
        {
            warn("ODT: a chart without values is not mapped when reading.");
            return null;
        }

        // Categories: the categories element of the x axis, else of the plot area (a pie).
        List<string>? categories = null;
        var axes = plot.Elements(ChartNs + "axis").ToList();
        var xAxis = axes.FirstOrDefault(a => (string?)a.Attribute(ChartNs + "dimension") == "x");
        var categoryRange = (string?)(xAxis?.Element(ChartNs + "categories") ?? plot.Element(ChartNs + "categories"))?.Attribute(TableNs + "cell-range-address");
        if (kind != ChartKind.Scatter && categoryRange is not null && Cells(budget, grid, categoryRange) is { } found2)
        {
            categories = found2.Select(c => c.Text ?? string.Empty).ToList();
        }

        if (categories is not null && categories.Count < series.Max(s => s.Values.Count))
        {
            categories = null;
        }

        static string? Title(XElement? element)
        {
            var value = element?.Element(ChartNs + "title") is { } title
                ? string.Join(" ", title.Elements(TextNs + "p").Select(p => p.Value)) : null;
            return string.IsNullOrEmpty(value) ? null : value;
        }

        var yAxis = axes.FirstOrDefault(a => (string?)a.Attribute(ChartNs + "dimension") == "y");
        var yProperties = Properties((string?)yAxis?.Attribute(ChartNs + "style-name"));
        double? Limit(string name) => double.TryParse((string?)yProperties?.Attribute(ChartNs + name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

        var legend = ChartLegendPosition.None;
        if (root.Element(ChartNs + "legend") is { } legendElement)
        {
            legend = ((string?)legendElement.Attribute(ChartNs + "legend-position") ?? "end") is "end" or "right"
                ? ChartLegendPosition.Right
                : ChartLegendPosition.Bottom;
        }

        var chartTitle = Title(root);
        try
        {
            return new Chart(kind, series, kind == ChartKind.Scatter ? null : categories, width, height)
            {
                Title = chartTitle,
                Legend = legend,
                CategoryAxisTitle = Title(xAxis),
                ValueAxisTitle = Title(yAxis),
                Minimum = Limit("minimum"),
                Maximum = Limit("maximum"),
                Stacked = stacked,
                AltText = altText == chartTitle ? null : altText,
            };
        }
        catch (ArgumentException)
        {
            warn("ODT: a chart with invalid data is not mapped when reading.");
            return null;
        }
    }

    private sealed record StyleProperties(XElement? Chart, XElement? Graphic);

    private static Dictionary<string, StyleProperties> AutomaticStyles(XElement content)
    {
        var result = new Dictionary<string, StyleProperties>(StringComparer.Ordinal);
        foreach (var container in content.Elements(OfficeNs + "automatic-styles"))
        {
            foreach (var style in container.Elements(StyleNs + "style"))
            {
                if ((string?)style.Attribute(StyleNs + "name") is { } name)
                {
                    result[name] = new StyleProperties(style.Element(StyleNs + "chart-properties"), style.Element(StyleNs + "graphic-properties"));
                }
            }
        }

        return result;
    }

    /// <summary>The local table as rows of cells (repeated cells and rows expanded, bounded).</summary>
    private static List<List<Cell>> ReadTable(ReaderGuard.ChartBudget budget, XElement? table)
    {
        var rows = new List<List<Cell>>();
        if (table is null)
        {
            return rows;
        }

        foreach (var row in table.Descendants(TableNs + "table-row"))
        {
            var cells = new List<Cell>();
            foreach (var cell in row.Elements().Where(e => e.Name == TableNs + "table-cell" || e.Name == TableNs + "covered-table-cell"))
            {
                var repeats = int.TryParse((string?)cell.Attribute(TableNs + "number-columns-repeated"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                    ? Math.Min(r, 256) : 1;
                var text = string.Join("\n", cell.Elements(TextNs + "p").Select(p => p.Value));
                double? number = double.TryParse((string?)cell.Attribute(OfficeNs + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
                budget.Take(Math.Max(repeats, 1));
                for (var i = 0; i < Math.Max(repeats, 1); i++)
                {
                    cells.Add(new Cell(text.Length == 0 ? null : text, number));
                }
            }

            var rowRepeats = int.TryParse((string?)row.Attribute(TableNs + "number-rows-repeated"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rr)
                ? Math.Min(rr, 256) : 1;
            for (var i = 0; i < Math.Max(rowRepeats, 1) && rows.Count < 100_000; i++)
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    /// <summary>"local-table.$B$2:.$B$5", "B2:B5", ".B2:.B5", "local-table.B2" → the cells in column order.</summary>
    private static List<Cell>? Cells(ReaderGuard.ChartBudget budget, List<List<Cell>> grid, string address)
    {
        static (int Column, int Row)? Parse(string part)
        {
            var text = part.Replace("$", string.Empty, StringComparison.Ordinal);
            var dot = text.LastIndexOf('.');
            if (dot >= 0)
            {
                text = text[(dot + 1)..];
            }

            var letters = new string(text.TakeWhile(char.IsAsciiLetter).ToArray());
            if (letters.Length is 0 or > 3 || !int.TryParse(text[letters.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) || row < 1)
            {
                return null;
            }

            var column = 0;
            foreach (var letter in letters.ToUpperInvariant())
            {
                column = column * 26 + letter - 64;
            }

            return (column - 1, row - 1);
        }

        var parts = address.Split(':');
        if (Parse(parts[0]) is not { } first || (parts.Length > 1 ? Parse(parts[1]) : first) is not { } last)
        {
            return null;
        }

        var result = new List<Cell>();
        for (var column = Math.Min(first.Column, last.Column); column <= Math.Max(first.Column, last.Column); column++)
        {
            for (var row = Math.Min(first.Row, last.Row); row <= Math.Max(first.Row, last.Row); row++)
            {
                if (result.Count == 100_000)
                {
                    return null;
                }

                result.Add(row < grid.Count && column < grid[row].Count ? grid[row][column] : default);
            }
        }

        budget.Take(result.Count);
        return result;
    }
}
