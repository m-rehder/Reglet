using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// <see cref="Chart"/> as a native DrawingML chart (ADR-0025): an inline <c>c:chart</c> in the body and a
/// chart part <c>word/charts/chartN.xml</c> that holds the values as literals. There is no embedded workbook,
/// so Word shows the chart but cannot edit its data.
/// </summary>
internal static class DocxCharts
{
    internal const string ChartNamespace = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    internal const string ChartRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart";
    internal const string ChartContentType = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";
    private const string DrawingMain = DocxConstants.DrawingMainNamespace;
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>The paragraph with the inline chart that points at the relationship.</summary>
    internal static void WriteInline(XmlWriter writer, Chart chart, string relationshipId, int drawingId, InvalidXmlCharacterBehavior xmlBehavior, long availableWidth)
    {
        // A chart wider than the text area (a table cell) is scaled down with its proportions.
        var scale = Math.Min(1, availableWidth / 20.0 / chart.Width.Points);
        var (width, height) = (Length.FromPoints(chart.Width.Points * scale), Length.FromPoints(chart.Height.Points * scale));
        const string Wml = DocxConstants.WordprocessingML;
        const string Wp = DocxConstants.DrawingWordprocessingNamespace;
        writer.WriteStartElement("w", "p", Wml);
        writer.WriteStartElement("w", "r", Wml);
        writer.WriteStartElement("w", "drawing", Wml);
        writer.WriteStartElement("wp", "inline", Wp);
        foreach (var distance in new[] { "distT", "distB", "distL", "distR" })
        {
            writer.WriteAttributeString(distance, "0");
        }

        writer.WriteStartElement("wp", "extent", Wp);
        writer.WriteAttributeString("cx", Emu(width).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("cy", Emu(height).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("wp", "effectExtent", Wp);
        foreach (var side in new[] { "l", "t", "r", "b" })
        {
            writer.WriteAttributeString(side, "0");
        }

        writer.WriteEndElement();
        writer.WriteStartElement("wp", "docPr", Wp);
        writer.WriteAttributeString("id", drawingId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"Chart {drawingId}");
        if ((chart.AltText ?? chart.Title) is { Length: > 0 } alt)
        {
            writer.WriteAttributeString("descr", XmlText.Clean(alt, xmlBehavior));
        }

        writer.WriteEndElement();
        writer.WriteElementString("wp", "cNvGraphicFramePr", Wp, null);
        writer.WriteStartElement("a", "graphic", DrawingMain);
        writer.WriteStartElement("a", "graphicData", DrawingMain);
        writer.WriteAttributeString("uri", ChartNamespace);
        writer.WriteStartElement("c", "chart", ChartNamespace);
        writer.WriteAttributeString("r", "id", RelationshipsNamespace, relationshipId);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement(); // inline
        writer.WriteEndElement(); // drawing
        writer.WriteEndElement(); // r
        writer.WriteEndElement(); // p
    }

    private static long Emu(Length length) => DocxUnits.Emu(length.Points);

    /// <summary>Writes the chart part.</summary>
    internal static void WritePart(XmlWriter writer, Chart chart, InvalidXmlCharacterBehavior xmlBehavior)
    {
        var part = new PartWriter(writer, chart, xmlBehavior);
        part.Write();
    }

    private sealed class PartWriter(XmlWriter writer, Chart chart, InvalidXmlCharacterBehavior xmlBehavior)
    {
        private const string C = ChartNamespace;
        private const string A = DrawingMain;

        private static string Hex(Color color) => $"{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

        private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private string Clean(string text) => XmlText.Clean(text, xmlBehavior);

        private void Val(string element, string value)
        {
            writer.WriteStartElement("c", element, C);
            writer.WriteAttributeString("val", value);
            writer.WriteEndElement();
        }

        internal void Write()
        {
            writer.WriteStartElement("c", "chartSpace", C);
            writer.WriteAttributeString("xmlns", "a", null, A);
            writer.WriteAttributeString("xmlns", "r", null, RelationshipsNamespace);
            Val("roundedCorners", "0");
            writer.WriteStartElement("c", "chart", C);
            if (!string.IsNullOrEmpty(chart.Title))
            {
                Title(chart.Title, main: true);
            }

            Val("autoTitleDeleted", string.IsNullOrEmpty(chart.Title) ? "1" : "0");
            writer.WriteStartElement("c", "plotArea", C);
            writer.WriteElementString("c", "layout", C, null);
            switch (chart.Kind)
            {
                case ChartKind.Column or ChartKind.Bar:
                    Bars();
                    break;
                case ChartKind.Line:
                    Lines();
                    break;
                case ChartKind.Area:
                    Areas();
                    break;
                case ChartKind.Pie:
                    Pie();
                    break;
                default:
                    Scatter();
                    break;
            }

            if (chart.Kind != ChartKind.Pie)
            {
                Axes();
            }

            writer.WriteEndElement(); // plotArea
            if (HasLegend())
            {
                writer.WriteStartElement("c", "legend", C);
                Val("legendPos", chart.Legend == ChartLegendPosition.Right ? "r" : "b");
                Val("overlay", "0");
                writer.WriteEndElement();
            }

            Val("plotVisOnly", "1");
            Val("dispBlanksAs", "gap");
            writer.WriteEndElement(); // chart

            // The text of the axes and the legend is 9 pt like in the PDF.
            writer.WriteStartElement("c", "txPr", C);
            writer.WriteElementString("a", "bodyPr", A, null);
            writer.WriteElementString("a", "lstStyle", A, null);
            writer.WriteStartElement("a", "p", A);
            writer.WriteStartElement("a", "pPr", A);
            writer.WriteStartElement("a", "defRPr", A);
            writer.WriteAttributeString("sz", "900");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("a", "endParaRPr", A);
            writer.WriteAttributeString("lang", "en-US");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement(); // chartSpace
        }

        // As in the PDF: a single unnamed series needs no legend, a pie always names its slices.
        private bool HasLegend()
            => chart.Legend != ChartLegendPosition.None
                && (chart.Kind == ChartKind.Pie || chart.Series.Count > 1 || !string.IsNullOrEmpty(chart.Series[0].Name));

        private void Title(string text, bool main = false)
        {
            writer.WriteStartElement("c", "title", C);
            writer.WriteStartElement("c", "tx", C);
            writer.WriteStartElement("c", "rich", C);
            writer.WriteElementString("a", "bodyPr", A, null);
            writer.WriteStartElement("a", "p", A);
            writer.WriteStartElement("a", "r", A);
            writer.WriteStartElement("a", "rPr", A);
            writer.WriteAttributeString("lang", "en-US");
            writer.WriteAttributeString("sz", main ? "1200" : "900");
            writer.WriteAttributeString("b", "1");
            writer.WriteEndElement();
            writer.WriteElementString("a", "t", A, Clean(text));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            Val("overlay", "0");
            writer.WriteEndElement();
        }

        private void SeriesHeader(int index)
        {
            Val("idx", index.ToString(CultureInfo.InvariantCulture));
            Val("order", index.ToString(CultureInfo.InvariantCulture));
            writer.WriteStartElement("c", "tx", C);
            writer.WriteElementString("c", "v", C, Clean(chart.Series[index].Name));
            writer.WriteEndElement();
        }

        private void Fill(Color color, bool line = false)
        {
            writer.WriteStartElement("c", "spPr", C);
            if (line)
            {
                writer.WriteStartElement("a", "ln", A);
                writer.WriteAttributeString("w", "28575");
                SolidFill(color);
                writer.WriteEndElement();
            }
            else
            {
                SolidFill(color);
            }

            writer.WriteEndElement();
        }

        private void SolidFill(Color color)
        {
            writer.WriteStartElement("a", "solidFill", A);
            writer.WriteStartElement("a", "srgbClr", A);
            writer.WriteAttributeString("val", Hex(color));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private int CategoryCount() => chart.Series.Max(series => series.Values.Count);

        private void Categories()
        {
            var count = CategoryCount();
            writer.WriteStartElement("c", "cat", C);
            writer.WriteStartElement("c", "strLit", C);
            Val("ptCount", count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
            {
                Point(i, Clean(chart.Categories is { } names && i < names.Count ? names[i] : (i + 1).ToString(CultureInfo.InvariantCulture)));
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private void Point(int index, string value)
        {
            writer.WriteStartElement("c", "pt", C);
            writer.WriteAttributeString("idx", index.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("c", "v", C, value);
            writer.WriteEndElement();
        }

        private void Numbers(string element, IReadOnlyList<double> values, int count)
        {
            writer.WriteStartElement("c", element, C);
            writer.WriteStartElement("c", "numLit", C);
            writer.WriteElementString("c", "formatCode", C, "General");
            Val("ptCount", count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < values.Count; i++)
            {
                Point(i, Num(values[i]));
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private void Bars()
        {
            writer.WriteStartElement("c", "barChart", C);
            Val("barDir", chart.Kind == ChartKind.Bar ? "bar" : "col");
            Val("grouping", chart.Stacked ? "stacked" : "clustered");
            Val("varyColors", "0");
            for (var i = 0; i < chart.Series.Count; i++)
            {
                writer.WriteStartElement("c", "ser", C);
                SeriesHeader(i);
                Fill(chart.SeriesColor(i));
                Val("invertIfNegative", "0");
                Categories();
                Numbers("val", chart.Series[i].Values, chart.Series[i].Values.Count);
                writer.WriteEndElement();
            }

            Val("gapWidth", "50");
            if (chart.Stacked)
            {
                Val("overlap", "100");
            }

            Val("axId", "111111111");
            Val("axId", "222222222");
            writer.WriteEndElement();
        }

        private void Lines()
        {
            writer.WriteStartElement("c", "lineChart", C);
            Val("grouping", "standard");
            Val("varyColors", "0");
            for (var i = 0; i < chart.Series.Count; i++)
            {
                writer.WriteStartElement("c", "ser", C);
                SeriesHeader(i);
                Fill(chart.SeriesColor(i), line: true);
                Marker(chart.SeriesColor(i), "circle");
                Categories();
                Numbers("val", chart.Series[i].Values, chart.Series[i].Values.Count);
                Val("smooth", "0");
                writer.WriteEndElement();
            }

            Val("marker", "1");
            Val("axId", "111111111");
            Val("axId", "222222222");
            writer.WriteEndElement();
        }

        private void Marker(Color color, string symbol)
        {
            writer.WriteStartElement("c", "marker", C);
            Val("symbol", symbol);
            Val("size", "6");
            Fill(color);
            writer.WriteEndElement();
        }

        private void Areas()
        {
            writer.WriteStartElement("c", "areaChart", C);
            Val("grouping", chart.Stacked ? "stacked" : "standard");
            Val("varyColors", "0");
            for (var i = 0; i < chart.Series.Count; i++)
            {
                writer.WriteStartElement("c", "ser", C);
                SeriesHeader(i);
                Fill(chart.SeriesColor(i));
                Categories();
                Numbers("val", chart.Series[i].Values, chart.Series[i].Values.Count);
                writer.WriteEndElement();
            }

            Val("axId", "111111111");
            Val("axId", "222222222");
            writer.WriteEndElement();
        }

        private void Pie()
        {
            writer.WriteStartElement("c", "pieChart", C);
            Val("varyColors", "1");
            writer.WriteStartElement("c", "ser", C);
            SeriesHeader(0);
            for (var i = 0; i < chart.Series[0].Values.Count; i++)
            {
                writer.WriteStartElement("c", "dPt", C);
                Val("idx", i.ToString(CultureInfo.InvariantCulture));
                Val("bubble3D", "0");
                Fill(Chart.DefaultPalette[i % Chart.DefaultPalette.Count]);
                writer.WriteEndElement();
            }

            writer.WriteStartElement("c", "dLbls", C);
            foreach (var (name, value) in new[]
            {
                ("showLegendKey", "0"), ("showVal", "0"), ("showCatName", "0"), ("showSerName", "0"), ("showPercent", "1"), ("showBubbleSize", "0"),
            })
            {
                Val(name, value);
            }

            writer.WriteEndElement();
            Categories();
            Numbers("val", chart.Series[0].Values.Select(value => Math.Max(0, value)).ToList(), chart.Series[0].Values.Count);
            writer.WriteEndElement();
            Val("firstSliceAng", "0");
            writer.WriteEndElement();
        }

        private void Scatter()
        {
            writer.WriteStartElement("c", "scatterChart", C);
            Val("scatterStyle", "lineMarker");
            Val("varyColors", "0");
            for (var i = 0; i < chart.Series.Count; i++)
            {
                writer.WriteStartElement("c", "ser", C);
                SeriesHeader(i);

                // Markers only: the line is switched off.
                writer.WriteStartElement("c", "spPr", C);
                writer.WriteStartElement("a", "ln", A);
                writer.WriteAttributeString("w", "19050");
                writer.WriteElementString("a", "noFill", A, null);
                writer.WriteEndElement();
                writer.WriteEndElement();
                Marker(chart.SeriesColor(i), "circle");
                Numbers("xVal", chart.Series[i].XValues!, chart.Series[i].XValues!.Count);
                Numbers("yVal", chart.Series[i].Values, chart.Series[i].Values.Count);
                Val("smooth", "0");
                writer.WriteEndElement();
            }

            Val("axId", "111111111");
            Val("axId", "222222222");
            writer.WriteEndElement();
        }

        private void Axes()
        {
            var horizontal = chart.Kind == ChartKind.Bar;
            var scatter = chart.Kind == ChartKind.Scatter;
            if (scatter)
            {
                ValueAxis("111111111", "222222222", "b", chart.CategoryAxisTitle, null, null, crossesMax: false);
                ValueAxis("222222222", "111111111", "l", chart.ValueAxisTitle, chart.Minimum, chart.Maximum, crossesMax: false);
                return;
            }

            writer.WriteStartElement("c", "catAx", C);
            Val("axId", "111111111");
            writer.WriteStartElement("c", "scaling", C);
            Val("orientation", horizontal ? "maxMin" : "minMax");
            writer.WriteEndElement();
            Val("delete", "0");
            Val("axPos", horizontal ? "l" : "b");
            if (!string.IsNullOrEmpty(chart.CategoryAxisTitle))
            {
                Title(chart.CategoryAxisTitle);
            }

            Val("majorTickMark", "out");
            Val("minorTickMark", "none");
            Val("tickLblPos", "low");
            Val("crossAx", "222222222");
            Val("crosses", "autoZero");
            Val("auto", "1");
            Val("lblAlgn", "ctr");
            Val("lblOffset", "100");
            writer.WriteEndElement();
            ValueAxis("222222222", "111111111", horizontal ? "b" : "l", chart.ValueAxisTitle, chart.Minimum, chart.Maximum, crossesMax: horizontal);
        }

        private void ValueAxis(string id, string cross, string position, string? title, double? minimum, double? maximum, bool crossesMax)
        {
            writer.WriteStartElement("c", "valAx", C);
            Val("axId", id);
            writer.WriteStartElement("c", "scaling", C);
            Val("orientation", "minMax");
            if (maximum is { } max)
            {
                Val("max", Num(max));
            }

            if (minimum is { } min)
            {
                Val("min", Num(min));
            }

            writer.WriteEndElement();
            Val("delete", "0");
            Val("axPos", position);
            writer.WriteElementString("c", "majorGridlines", C, null);
            if (!string.IsNullOrEmpty(title))
            {
                Title(title);
            }

            writer.WriteStartElement("c", "numFmt", C);
            writer.WriteAttributeString("formatCode", "General");
            writer.WriteAttributeString("sourceLinked", "0");
            writer.WriteEndElement();
            Val("majorTickMark", "out");
            Val("minorTickMark", "none");
            Val("tickLblPos", "nextTo");
            Val("crossAx", cross);
            Val("crosses", crossesMax ? "max" : "autoZero");
            Val("crossBetween", chart.Kind == ChartKind.Scatter ? "midCat" : "between");
            writer.WriteEndElement();
        }
    }
}
