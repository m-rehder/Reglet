using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// <see cref="Chart"/> as an ODF chart object (ADR-0025): <c>draw:object</c> in a frame, the chart itself is a
/// sub-document <c>Object N/content.xml</c> with <c>chart:chart</c> and a local table that holds the values.
/// </summary>
internal static class OdtCharts
{
    internal const string ChartNs = "urn:oasis:names:tc:opendocument:xmlns:chart:1.0";
    private const string ChartMediaType = "application/vnd.oasis.opendocument.chart";

    /// <summary>The charts of the body in document order (also in tables, lists and text boxes) with their object names.</summary>
    internal static Dictionary<Chart, string> Collect(PortableDocument document)
    {
        var ids = new Dictionary<Chart, string>(ReferenceEqualityComparer.Instance);
        foreach (var chart in document.Sections.SelectMany(section => Enumerate<Chart>(section.Blocks)))
        {
            ids[chart] = "Object " + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
        }

        return ids;
    }

    /// <summary>All blocks of type <typeparamref name="T"/> in document order (also in tables, lists, text boxes and diagrams).</summary>
    internal static IEnumerable<T> Enumerate<T>(IEnumerable<Block> blocks)
        where T : Block
        => BlockScan.Enumerate<T>(blocks, block => block is Diagram diagram
            ? OdtDiagrams.Shapes(diagram).SelectMany(placed => placed.Shape.Content ?? []).ToList()
            : null);

    internal static bool HasChart(IEnumerable<Block> blocks) => Enumerate<Chart>(blocks).Any();

    internal static void WriteParts(OdtPackageWriter package, IReadOnlyDictionary<Chart, string> charts, InvalidXmlCharacterBehavior xmlBehavior)
    {
        foreach (var (chart, name) in charts)
        {
            package.AddDirectoryEntry(name, ChartMediaType);
            package.AddXmlPart(name + "/content.xml", writer => WriteContent(writer, chart, xmlBehavior));
        }
    }

    private static string Cm(Length length) => (length.Points / 72 * 2.54).ToString("0.####", CultureInfo.InvariantCulture) + "cm";

    private static string Hex(Color color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The paragraph with the frame and the object.</summary>
    internal static void WritePlacement(XmlWriter writer, Chart chart, string objectName, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteStartElement("draw", "frame", OdtNamespaces.Draw);
        writer.WriteAttributeString("draw", "name", OdtNamespaces.Draw, "Chart " + objectName["Object ".Length..]);
        writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, "as-char");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Cm(chart.Width));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Cm(chart.Height));
        writer.WriteStartElement("draw", "object", OdtNamespaces.Draw);
        writer.WriteAttributeString("xlink", "href", OdtNamespaces.XLink, "./" + objectName);
        writer.WriteAttributeString("xlink", "type", OdtNamespaces.XLink, "simple");
        writer.WriteAttributeString("xlink", "show", OdtNamespaces.XLink, "embed");
        writer.WriteAttributeString("xlink", "actuate", OdtNamespaces.XLink, "onLoad");
        writer.WriteEndElement();
        if ((chart.AltText ?? chart.Title) is { Length: > 0 } alt)
        {
            writer.WriteElementString("svg", "title", OdtNamespaces.Svg, XmlText.Clean(alt, xmlBehavior));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    // A … Z, AA … AZ, BA …: the reader takes up to three letters.
    private static string Column(int index) => (index < 26 ? string.Empty : Column((index / 26) - 1)) + (char)('A' + (index % 26));

    // The address form of ODF: absolute, the table name only before the first cell ("local-table.$B$2:.$B$5").
    private static string Range(int firstColumn, int firstRow, int lastColumn, int lastRow)
        => $"local-table.${Column(firstColumn)}${firstRow}:.${Column(lastColumn)}${lastRow}";

    private static string Cell(int column, int row) => $"local-table.${Column(column)}${row}";

    private static void WriteContent(XmlWriter writer, Chart chart, InvalidXmlCharacterBehavior xmlBehavior)
    {
        string Clean(string text) => XmlText.Clean(text, xmlBehavior);
        var scatter = chart.Kind == ChartKind.Scatter;
        var pie = chart.Kind == ChartKind.Pie;
        var count = chart.Series.Max(series => series.Values.Count);
        var seriesCount = chart.Series.Count;
        var categories = Enumerable.Range(0, count)
            .Select(i => chart.Categories is { } names && i < names.Count ? names[i] : (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();

        // Local table: scatter has an x and a y column per series, the others one category column and one per series.
        var columns = scatter ? 2 * seriesCount : 1 + seriesCount;

        writer.WriteStartElement("office", "document-content", OdtNamespaces.Office);

        writer.WriteAttributeString("office", "version", OdtNamespaces.Office, "1.3");

        // Automatic styles: the chart, the axes and one per series (the pie colors its slices).
        writer.WriteStartElement("office", "automatic-styles", OdtNamespaces.Office);
        Style(writer, "chart", "chart", chart: w =>
        {
            w.WriteAttributeString("chart", "stacked", ChartNs, chart.Stacked && chart.Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Area ? "true" : "false");
            w.WriteAttributeString("chart", "vertical", ChartNs, chart.Kind == ChartKind.Bar ? "true" : "false");
            w.WriteAttributeString("chart", "symbol-type", ChartNs, chart.Kind is ChartKind.Line or ChartKind.Scatter ? "automatic" : "none");
            w.WriteAttributeString("chart", "include-hidden-cells", ChartNs, "false");
            w.WriteAttributeString("chart", "series-source", ChartNs, "columns");
        });
        for (var i = 0; i < seriesCount; i++)
        {
            var color = Hex(chart.SeriesColor(i));
            Style(writer, "ser" + i, "chart",
                graphic: w =>
                {
                    if (chart.Kind is ChartKind.Line)
                    {
                        w.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "solid");
                        w.WriteAttributeString("svg", "stroke-width", OdtNamespaces.Svg, "0.07cm");
                        w.WriteAttributeString("svg", "stroke-color", OdtNamespaces.Svg, color);
                    }
                    else if (scatter)
                    {
                        w.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "none");
                        w.WriteAttributeString("draw", "fill-color", OdtNamespaces.Draw, color);
                    }
                    else
                    {
                        w.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "none");
                        w.WriteAttributeString("draw", "fill", OdtNamespaces.Draw, "solid");
                        w.WriteAttributeString("draw", "fill-color", OdtNamespaces.Draw, color);
                    }
                },
                chart: w =>
                {
                    if (chart.Kind is ChartKind.Line or ChartKind.Scatter)
                    {
                        w.WriteAttributeString("chart", "symbol-type", ChartNs, "automatic");
                    }
                });
        }

        if (pie)
        {
            for (var i = 0; i < count; i++)
            {
                var color = Hex(Chart.DefaultPalette[i % Chart.DefaultPalette.Count]);
                Style(writer, "pt" + i, "chart", graphic: w =>
                {
                    w.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "none");
                    w.WriteAttributeString("draw", "fill", OdtNamespaces.Draw, "solid");
                    w.WriteAttributeString("draw", "fill-color", OdtNamespaces.Draw, color);
                }, chart: w => w.WriteAttributeString("chart", "data-label-number", ChartNs, "percentage"));
            }
        }

        Style(writer, "wall", "chart", graphic: w =>
        {
            w.WriteAttributeString("draw", "stroke", OdtNamespaces.Draw, "none");
            w.WriteAttributeString("draw", "fill", OdtNamespaces.Draw, "none");
        });
        Style(writer, "axisX", "chart", chart: w =>
        {
            w.WriteAttributeString("chart", "display-label", ChartNs, "true");
            if (chart.Kind == ChartKind.Bar)
            {
                w.WriteAttributeString("chart", "reverse-direction", ChartNs, "true");
            }

        });
        Style(writer, "axisY", "chart", chart: w =>
        {
            w.WriteAttributeString("chart", "display-label", ChartNs, "true");
            if (chart.Minimum is { } min)
            {
                w.WriteAttributeString("chart", "minimum", ChartNs, Num(min));
            }

            if (chart.Maximum is { } max)
            {
                w.WriteAttributeString("chart", "maximum", ChartNs, Num(max));
            }
        });
        writer.WriteEndElement();

        writer.WriteStartElement("office", "body", OdtNamespaces.Office);
        writer.WriteStartElement("office", "chart", OdtNamespaces.Office);
        writer.WriteStartElement("chart", "chart", ChartNs);
        writer.WriteAttributeString("chart", "class", ChartNs, "chart:" + chart.Kind switch
        {
            ChartKind.Column or ChartKind.Bar => "bar",
            ChartKind.Line => "line",
            ChartKind.Area => "area",
            ChartKind.Pie => "circle",
            _ => "scatter",
        });
        writer.WriteAttributeString("chart", "style-name", ChartNs, "chart");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, Cm(chart.Width));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, Cm(chart.Height));

        if (!string.IsNullOrEmpty(chart.Title))
        {
            writer.WriteStartElement("chart", "title", ChartNs);
            writer.WriteStartElement("text", "p", OdtNamespaces.Text);
            writer.WriteString(Clean(chart.Title));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var hasLegend = chart.Legend != ChartLegendPosition.None && (pie || seriesCount > 1 || !string.IsNullOrEmpty(chart.Series[0].Name));
        if (hasLegend)
        {
            writer.WriteStartElement("chart", "legend", ChartNs);
            writer.WriteAttributeString("chart", "legend-position", ChartNs, chart.Legend == ChartLegendPosition.Right ? "end" : "bottom");
            writer.WriteEndElement();
        }

        writer.WriteStartElement("chart", "plot-area", ChartNs);
        writer.WriteAttributeString("chart", "style-name", ChartNs, "chart");
        writer.WriteAttributeString("table", "cell-range-address", OdtNamespaces.Table, Range(0, 1, columns - 1, count + 1));
        writer.WriteAttributeString("chart", "data-source-has-labels", ChartNs, scatter ? "both" : "both");

        if (!pie)
        {
            Axis(writer, "x", "axisX", chart.CategoryAxisTitle, scatter ? null : Range(0, 2, 0, count + 1), Clean);
            Axis(writer, "y", "axisY", chart.ValueAxisTitle, null, Clean, grid: true);
        }
        else
        {
            // The slice names come from the categories of the first column; the schema keeps them in an x axis
            // (before the series), as LibreOffice writes a pie chart.
            writer.WriteStartElement("chart", "axis", ChartNs);
            writer.WriteAttributeString("chart", "dimension", ChartNs, "x");
            writer.WriteAttributeString("chart", "name", ChartNs, "primary-x");
            writer.WriteStartElement("chart", "categories", ChartNs);
            writer.WriteAttributeString("table", "cell-range-address", OdtNamespaces.Table, Range(0, 2, 0, count + 1));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        for (var s = 0; s < seriesCount; s++)
        {
            var valueColumn = scatter ? 2 * s + 1 : 1 + s;
            var points = chart.Series[s].Values.Count;
            writer.WriteStartElement("chart", "series", ChartNs);
            writer.WriteAttributeString("chart", "style-name", ChartNs, "ser" + s);
            writer.WriteAttributeString("chart", "values-cell-range-address", ChartNs, Range(valueColumn, 2, valueColumn, points + 1));
            writer.WriteAttributeString("chart", "label-cell-address", ChartNs, Cell(valueColumn, 1));
            writer.WriteAttributeString("chart", "attached-axis", ChartNs, "primary-y");
            if (pie)
            {
                writer.WriteAttributeString("chart", "class", ChartNs, "chart:circle");
            }

            if (pie)
            {
                for (var i = 0; i < count; i++)
                {
                    writer.WriteStartElement("chart", "data-point", ChartNs);
                    writer.WriteAttributeString("chart", "style-name", ChartNs, "pt" + i);
                    writer.WriteEndElement();
                }
            }

            if (scatter)
            {
                writer.WriteStartElement("chart", "domain", ChartNs);
                writer.WriteAttributeString("table", "cell-range-address", OdtNamespaces.Table, Range(2 * s, 2, 2 * s, points + 1));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        if (!pie)
        {
            writer.WriteStartElement("chart", "wall", ChartNs);
            writer.WriteAttributeString("chart", "style-name", ChartNs, "wall");
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // plot-area

        // The values themselves.
        writer.WriteStartElement("table", "table", OdtNamespaces.Table);
        writer.WriteAttributeString("table", "name", OdtNamespaces.Table, "local-table");
        writer.WriteStartElement("table", "table-header-columns", OdtNamespaces.Table);
        writer.WriteElementString("table", "table-column", OdtNamespaces.Table, null);
        writer.WriteEndElement();
        writer.WriteStartElement("table", "table-columns", OdtNamespaces.Table);
        writer.WriteStartElement("table", "table-column", OdtNamespaces.Table);
        writer.WriteAttributeString("table", "number-columns-repeated", OdtNamespaces.Table, Math.Max(1, columns - 1).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("table", "table-header-rows", OdtNamespaces.Table);
        writer.WriteStartElement("table", "table-row", OdtNamespaces.Table);
        if (scatter)
        {
            foreach (var series in chart.Series)
            {
                StringCell(writer, "x", Clean);
                StringCell(writer, series.Name, Clean);
            }
        }
        else
        {
            writer.WriteElementString("table", "table-cell", OdtNamespaces.Table, null);
            foreach (var series in chart.Series)
            {
                StringCell(writer, series.Name, Clean);
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("table", "table-rows", OdtNamespaces.Table);
        for (var i = 0; i < count; i++)
        {
            writer.WriteStartElement("table", "table-row", OdtNamespaces.Table);
            if (scatter)
            {
                foreach (var series in chart.Series)
                {
                    NumberCell(writer, i < series.Values.Count ? series.XValues![i] : null);
                    NumberCell(writer, i < series.Values.Count ? series.Values[i] : null);
                }
            }
            else
            {
                StringCell(writer, categories[i], Clean);
                foreach (var series in chart.Series)
                {
                    NumberCell(writer, i < series.Values.Count ? (pie ? Math.Max(0, series.Values[i]) : series.Values[i]) : null);
                }
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement(); // table
        writer.WriteEndElement(); // chart:chart
        writer.WriteEndElement(); // office:chart
        writer.WriteEndElement(); // office:body
        writer.WriteEndElement(); // document-content
    }

    private static void Style(XmlWriter writer, string name, string family, Action<XmlWriter>? graphic = null, Action<XmlWriter>? chart = null)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, family);
        if (chart is not null)
        {
            writer.WriteStartElement("style", "chart-properties", OdtNamespaces.Style);
            chart(writer);
            writer.WriteEndElement();
        }

        if (graphic is not null)
        {
            writer.WriteStartElement("style", "graphic-properties", OdtNamespaces.Style);
            graphic(writer);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void Axis(XmlWriter writer, string dimension, string style, string? title, string? categories, Func<string, string> clean, bool grid = false)
    {
        writer.WriteStartElement("chart", "axis", ChartNs);
        writer.WriteAttributeString("chart", "dimension", ChartNs, dimension);
        writer.WriteAttributeString("chart", "name", ChartNs, "primary-" + dimension);
        writer.WriteAttributeString("chart", "style-name", ChartNs, style);
        if (!string.IsNullOrEmpty(title))
        {
            writer.WriteStartElement("chart", "title", ChartNs);
            writer.WriteStartElement("text", "p", OdtNamespaces.Text);
            writer.WriteString(clean(title));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        if (categories is not null)
        {
            writer.WriteStartElement("chart", "categories", ChartNs);
            writer.WriteAttributeString("table", "cell-range-address", OdtNamespaces.Table, categories);
            writer.WriteEndElement();
        }

        if (grid)
        {
            writer.WriteStartElement("chart", "grid", ChartNs);
            writer.WriteAttributeString("chart", "class", ChartNs, "major");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void StringCell(XmlWriter writer, string text, Func<string, string> clean)
    {
        writer.WriteStartElement("table", "table-cell", OdtNamespaces.Table);
        writer.WriteAttributeString("office", "value-type", OdtNamespaces.Office, "string");
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteString(clean(text));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void NumberCell(XmlWriter writer, double? value)
    {
        writer.WriteStartElement("table", "table-cell", OdtNamespaces.Table);
        if (value is { } number)
        {
            writer.WriteAttributeString("office", "value-type", OdtNamespaces.Office, "float");
            writer.WriteAttributeString("office", "value", OdtNamespaces.Office, Num(number));
            writer.WriteStartElement("text", "p", OdtNamespaces.Text);
            writer.WriteString(Num(number));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }
}
