using System.Globalization;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Reads a DrawingML chart part (<c>c:chartSpace</c>) into a <see cref="Chart"/> (ADR-0025). Reads what the writer
/// writes and the common parts of what Word writes (cached or literal values); other plot kinds, combinations, 3-D
/// charts and formatting beyond series colors are reported and left out.
/// </summary>
internal static class DocxChartReader
{
    private static readonly XNamespace C = DocxCharts.ChartNamespace;
    private static readonly XNamespace A = DocxConstants.DrawingMainNamespace;

    /// <returns>The chart, or null after a warning when it cannot be mapped.</returns>
    internal static Chart? Read(XElement part, Length? width, Length? height, string? altText, Action<string> warn)
    {
        if (part.Element(C + "chart") is not { } chart || chart.Element(C + "plotArea") is not { } plotArea)
        {
            warn("DOCX: a chart without a plot area is not mapped when reading.");
            return null;
        }

        var plots = plotArea.Elements().Where(e => e.Name.Namespace == C && e.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal)).ToList();
        if (plots.Count == 0)
        {
            warn("DOCX: a chart without a plot is not mapped when reading.");
            return null;
        }

        var plot = plots[0];
        if (plots.Count > 1)
        {
            warn("DOCX: a chart with several plot types is read with its first plot only.");
        }

        ChartKind kind;
        var stacked = false;
        switch (plot.Name.LocalName)
        {
            case "barChart":
                kind = (string?)plot.Element(C + "barDir")?.Attribute("val") == "bar" ? ChartKind.Bar : ChartKind.Column;
                var barGrouping = (string?)plot.Element(C + "grouping")?.Attribute("val");
                stacked = barGrouping is "stacked" or "percentStacked";
                if (barGrouping == "percentStacked")
                {
                    warn("DOCX: a percent-stacked chart is read as stacked.");
                }

                break;
            case "lineChart":
                kind = ChartKind.Line;
                break;
            case "areaChart":
                kind = ChartKind.Area;
                stacked = (string?)plot.Element(C + "grouping")?.Attribute("val") is "stacked" or "percentStacked";
                break;
            case "pieChart" or "doughnutChart":
                kind = ChartKind.Pie;
                break;
            case "scatterChart":
                kind = ChartKind.Scatter;
                break;
            default:
                warn($"DOCX: the chart type 'c:{plot.Name.LocalName}' is not mapped when reading.");
                return null;
        }

        var series = new List<ChartSeries>();
        List<string>? categories = null;
        var index = 0;
        foreach (var ser in plot.Elements(C + "ser"))
        {
            var thisIndex = index++;
            var name = Points(ser.Element(C + "tx")).FirstOrDefault() ?? string.Empty;
            var values = Numbers(ser.Element(C + (kind == ChartKind.Scatter ? "yVal" : "val")));
            if (values.Count == 0)
            {
                continue;
            }

            var xValues = kind == ChartKind.Scatter ? Numbers(ser.Element(C + "xVal")) : null;
            if (categories is null && kind != ChartKind.Scatter && ser.Element(C + "cat") is { } cat)
            {
                categories = Points(cat).Select(p => p ?? string.Empty).ToList();
            }

            Color? color = SeriesColor(ser, kind == ChartKind.Line);
            if (color == Chart.DefaultPalette[thisIndex % Chart.DefaultPalette.Count])
            {
                color = null;
            }

            try
            {
                series.Add(new ChartSeries(name, values, xValues is not null && xValues.Count == values.Count ? xValues : null) { Color = color });
            }
            catch (ArgumentException)
            {
                warn("DOCX: a chart series with invalid values is not mapped when reading.");
                continue;
            }

            if (kind == ChartKind.Pie)
            {
                break;
            }
        }

        if (series.Count == 0)
        {
            warn("DOCX: a chart without values is not mapped when reading.");
            return null;
        }

        if (kind == ChartKind.Scatter && series.Any(s => s.XValues is null))
        {
            warn("DOCX: a scatter series without x values is not mapped when reading.");
            return null;
        }

        var valueAxes = plotArea.Elements(C + "valAx").ToList();
        var categoryAxis = plotArea.Elements(C + "catAx").FirstOrDefault() ?? plotArea.Elements(C + "dateAx").FirstOrDefault();
        string? categoryTitle;
        string? valueTitle;
        XElement? valueAxis;
        if (kind == ChartKind.Scatter)
        {
            var x = valueAxes.FirstOrDefault(a => (string?)a.Element(C + "axPos")?.Attribute("val") == "b") ?? valueAxes.FirstOrDefault();
            var y = valueAxes.FirstOrDefault(a => a != x);
            categoryTitle = Title(x);
            valueTitle = Title(y);
            valueAxis = y;
        }
        else
        {
            categoryTitle = Title(categoryAxis);
            valueAxis = valueAxes.FirstOrDefault();
            valueTitle = Title(valueAxis);
        }

        var scaling = valueAxis?.Element(C + "scaling");
        double? minimum = Number((string?)scaling?.Element(C + "min")?.Attribute("val"));
        double? maximum = Number((string?)scaling?.Element(C + "max")?.Attribute("val"));

        var legend = ChartLegendPosition.None;
        if (chart.Element(C + "legend") is { } legendElement)
        {
            legend = ((string?)legendElement.Element(C + "legendPos")?.Attribute("val") ?? "r") is "r" or "tr"
                ? ChartLegendPosition.Right
                : ChartLegendPosition.Bottom;
        }

        var titleDeleted = (string?)chart.Element(C + "autoTitleDeleted")?.Attribute("val") == "1";
        var chartTitle = titleDeleted ? null : Title(chart);

        // The categories must cover the longest series; otherwise they are numbered.
        if (categories is not null && categories.Count < series.Max(s => s.Values.Count))
        {
            categories = null;
        }

        try
        {
            return new Chart(kind, series, kind == ChartKind.Scatter ? null : categories, width, height)
            {
                Title = chartTitle,
                Legend = legend,
                CategoryAxisTitle = categoryTitle,
                ValueAxisTitle = valueTitle,
                Minimum = minimum,
                Maximum = maximum,
                Stacked = stacked,
                AltText = altText == chartTitle ? null : altText,
            };
        }
        catch (ArgumentException)
        {
            warn("DOCX: a chart with invalid data is not mapped when reading.");
            return null;
        }
    }

    private static double? Number(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>The text of a <c>c:title</c> below the element (all runs, paragraphs joined by a space).</summary>
    private static string? Title(XElement? element)
    {
        var rich = element?.Element(C + "title")?.Element(C + "tx")?.Element(C + "rich");
        if (rich is null)
        {
            return null;
        }

        var text = string.Join(" ", rich.Elements(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))));
        return text.Length == 0 ? null : text;
    }

    /// <summary>The points of a literal or cached string/number list in index order; a gap is null.</summary>
    private static List<string?> Points(XElement? element)
    {
        var result = new List<string?>();
        if (element is null)
        {
            return result;
        }

        if (element.Name.LocalName == "tx" && element.Element(C + "v") is { } direct)
        {
            return [direct.Value];
        }

        var list = element.Descendants().FirstOrDefault(e => e.Name.Namespace == C
            && e.Name.LocalName is "strCache" or "strLit" or "numCache" or "numLit");
        if (list is null)
        {
            return result;
        }

        var count = int.TryParse((string?)list.Element(C + "ptCount")?.Attribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared)
            ? Math.Clamp(declared, 0, 100_000) : 0;
        for (var i = 0; i < count; i++)
        {
            result.Add(null);
        }

        foreach (var point in list.Elements(C + "pt"))
        {
            if (!int.TryParse((string?)point.Attribute("idx"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || at < 0 || at >= 100_000)
            {
                continue;
            }

            while (result.Count <= at)
            {
                result.Add(null);
            }

            result[at] = point.Element(C + "v")?.Value;
        }

        return result;
    }

    private static List<double> Numbers(XElement? element)
        => Points(element).Select(p => Number(p) ?? 0).ToList();

    /// <summary>The solid color of a series: the line for a line chart, else the fill, else the marker fill.</summary>
    private static Color? SeriesColor(XElement series, bool line)
    {
        static Color? Hex(XElement? properties, bool useLine)
        {
            var parent = useLine ? properties?.Element(A + "ln") : properties;
            return Color.TryParseHex((string?)parent?.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val"), out var color)
                ? color : null;
        }

        var properties = series.Element(C + "spPr");
        var marker = series.Element(C + "marker")?.Element(C + "spPr");
        return line ? Hex(properties, true) ?? Hex(marker, false) : Hex(properties, false) ?? Hex(marker, false);
    }
}
