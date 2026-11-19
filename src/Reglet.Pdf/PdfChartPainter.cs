using System.Globalization;

namespace Reglet;

/// <summary>
/// Draws a <see cref="Chart"/> as vectors (ADR-0025). The layout follows fixed rules: title on top, legend
/// at the bottom or right, a value axis with ticks from a 1-2-5 scale, light grid lines, the series in the
/// palette. Text uses the font of the document.
/// </summary>
internal sealed class PdfChartPainter
{
    private const double Margin = 8;
    private const double TitleSize = 12;
    private const double TextSize = 8;
    private static readonly Color GridColor = Color.FromHex("#D9D9D9");
    private static readonly Color AxisColor = Color.FromHex("#808080");
    private static readonly Color TextColor = Color.FromHex("#404040");

    private readonly PdfContentWriter _content;
    private readonly Chart _chart;
    private readonly PdfFontHandle _font;
    private readonly PdfFontHandle _bold;
    private readonly Action<string> _report;
    private readonly double _width;
    private readonly double _height;
    private readonly double _originX;
    private readonly double _originY;
    private bool _reported;

    private PdfChartPainter(
        PdfContentWriter content, Chart chart, PdfFontHandle font, PdfFontHandle bold, Action<string> report,
        double width, double height, double x, double y)
    {
        _content = content;
        _chart = chart;
        _font = font;
        _bold = bold;
        _report = report;
        _width = width;
        _height = height;
        _originX = x;
        _originY = y;
    }

    /// <summary>Draws the chart into the frame whose bottom left corner is at x/y.</summary>
    internal static void Paint(
        PdfContentWriter content, Chart chart, PdfFontHandle font, PdfFontHandle bold, Action<string> report,
        double width, double height, double x, double y)
        => new PdfChartPainter(content, chart, font, bold, report, width, height, x, y).Paint();

    /// <summary>A 1-2-5 scale over the range: lowest tick, highest tick and the step between ticks.</summary>
    internal static (double Min, double Max, double Step) NiceScale(double min, double max, int targetTicks = 5)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        if (max - min < 1e-12)
        {
            var pad = max == 0 ? 1 : Math.Abs(max) * 0.1;
            min -= pad;
            max += pad;
        }

        var raw = (max - min) / Math.Max(1, targetTicks);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
        return (Math.Round(Math.Floor(min / step + 1e-9) * step, 12), Math.Round(Math.Ceiling(max / step - 1e-9) * step, 12), step);
    }

    private static string Number(double value, double step)
    {
        var decimals = step >= 1 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step) - 1e-9), 0, 8);
        var text = Math.Round(value, 10).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static Color Mix(Color color, double share)
        => new((byte)(color.Red * share + 255 * (1 - share)), (byte)(color.Green * share + 255 * (1 - share)), (byte)(color.Blue * share + 255 * (1 - share)));

    // Top-down coordinates (y from the top edge) to PDF coordinates.
    private double X(double x) => _originX + x;

    private double Y(double y) => _originY + _height - y;

    private string Safe(string text)
    {
        if (_font.CanEncode(text))
        {
            return text;
        }

        if (!_reported)
        {
            _report("PDF: a chart text has characters the font cannot show; they are replaced by a question mark.");
            _reported = true;
        }

        return string.Concat(text.Select(c => _font.CanEncode(c.ToString()) ? c : '?'));
    }

    private double Width(string text, double size, bool bold = false) => (bold ? _bold : _font).Measure(Safe(text), size);

    /// <summary>Draws text with its baseline at y (top-down); align 0 left, 0.5 centered, 1 right of x.</summary>
    private void Text(string text, double size, Color color, double x, double y, double align = 0, bool bold = false)
    {
        text = Safe(text);
        if (text.Length == 0)
        {
            return;
        }

        var font = bold ? _bold : _font;
        PdfPageRenderer.WriteLineTo(_content, font, text, size, color, X(x - align * font.Measure(text, size)), Y(y), 0, 100, null);
    }

    private string Shorten(string text, double available, double size)
    {
        if (Width(text, size) <= available)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "…";
            if (Width(candidate, size) <= available)
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private void Paint()
    {
        _content.SaveState();
        _content.FillRectangle(_originX, _originY, _width, _height, Color.White);

        var top = Margin;
        if (!string.IsNullOrEmpty(_chart.Title))
        {
            Text(_chart.Title, TitleSize, Color.Black, _width / 2, top + _font.Metrics.AscentEm * TitleSize, 0.5, bold: true);
            top += _font.Metrics.LineHeightEm * TitleSize + 6;
        }

        var legend = LegendItems();
        double bottom = _height - Margin;
        double right = _width - Margin;
        if (legend.Count > 0 && _chart.Legend == ChartLegendPosition.Bottom)
        {
            bottom -= DrawBottomLegend(legend, bottom);
        }
        else if (legend.Count > 0 && _chart.Legend == ChartLegendPosition.Right)
        {
            right -= DrawRightLegend(legend, top, bottom, right);
        }

        if (_chart.Kind == ChartKind.Pie)
        {
            DrawPie(top, bottom, Margin, right);
        }
        else
        {
            DrawAxesChart(top, bottom, Margin, right);
        }

        _content.RestoreState();
    }

    private List<(string Name, Color Color)> LegendItems()
    {
        var items = new List<(string, Color)>();
        if (_chart.Legend == ChartLegendPosition.None)
        {
            return items;
        }

        if (_chart.Kind == ChartKind.Pie)
        {
            for (var i = 0; i < _chart.Series[0].Values.Count; i++)
            {
                items.Add((_chart.Categories?[i] ?? (i + 1).ToString(CultureInfo.InvariantCulture), Chart.DefaultPalette[i % Chart.DefaultPalette.Count]));
            }

            return items;
        }

        if (_chart.Series.Count == 1 && string.IsNullOrEmpty(_chart.Series[0].Name))
        {
            return items;
        }

        for (var i = 0; i < _chart.Series.Count; i++)
        {
            items.Add((_chart.Series[i].Name, _chart.SeriesColor(i)));
        }

        return items;
    }

    private double DrawBottomLegend(List<(string Name, Color Color)> items, double bottom)
    {
        const double Swatch = 8;
        const double Gap = 14;
        var rows = new List<List<(string Name, Color Color, double Width)>> { new() };
        var rowWidth = 0.0;
        foreach (var (name, color) in items)
        {
            var width = Swatch + 4 + Width(name, TextSize);
            if (rows[^1].Count > 0 && rowWidth + Gap + width > _width - 2 * Margin)
            {
                rows.Add(new());
                rowWidth = 0;
            }

            rowWidth += (rows[^1].Count > 0 ? Gap : 0) + width;
            rows[^1].Add((name, color, width));
        }

        var lineHeight = 13.0;
        var total = rows.Count * lineHeight + 4;
        var y = bottom - total + 4;
        foreach (var row in rows)
        {
            var x = (_width - (row.Sum(item => item.Width) + Gap * (row.Count - 1))) / 2;
            foreach (var (name, color, width) in row)
            {
                _content.FillRectangle(X(x), Y(y + lineHeight / 2 + Swatch / 2 - 1), Swatch, Swatch, color);
                Text(name, TextSize, TextColor, x + Swatch + 4, y + lineHeight / 2 + TextSize * 0.35);
                x += width + Gap;
            }

            y += lineHeight;
        }

        return total;
    }

    private double DrawRightLegend(List<(string Name, Color Color)> items, double top, double bottom, double right)
    {
        const double Swatch = 8;
        var widest = items.Max(item => Width(item.Name, TextSize));
        var legendWidth = Swatch + 4 + widest + 8;
        var x = right - legendWidth + 8;
        var total = items.Count * 13.0;
        var y = top + Math.Max(0, (bottom - top - total) / 2);
        foreach (var (name, color) in items)
        {
            _content.FillRectangle(X(x), Y(y + 6.5 + Swatch / 2 - 1), Swatch, Swatch, color);
            Text(name, TextSize, TextColor, x + Swatch + 4, y + 6.5 + TextSize * 0.35);
            y += 13;
        }

        return legendWidth;
    }

    private (double Min, double Max, double Step) ValueScale()
    {
        double low, high;
        if (_chart.Kind == ChartKind.Scatter)
        {
            low = _chart.Series.Min(series => series.Values.Min());
            high = _chart.Series.Max(series => series.Values.Max());
        }
        else if (_chart.Stacked && _chart.Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Area)
        {
            var count = _chart.Series.Max(series => series.Values.Count);
            low = 0;
            high = 0;
            for (var i = 0; i < count; i++)
            {
                var positive = _chart.Series.Sum(series => i < series.Values.Count && series.Values[i] > 0 ? series.Values[i] : 0);
                var negative = _chart.Series.Sum(series => i < series.Values.Count && series.Values[i] < 0 ? series.Values[i] : 0);
                high = Math.Max(high, positive);
                low = Math.Min(low, negative);
            }
        }
        else
        {
            low = _chart.Series.Min(series => series.Values.Min());
            high = _chart.Series.Max(series => series.Values.Max());
        }

        if (_chart.Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Area)
        {
            low = Math.Min(low, 0);
            high = Math.Max(high, 0);
        }

        var scale = NiceScale(_chart.Minimum ?? low, _chart.Maximum ?? high);
        if (_chart.Minimum is not null || _chart.Maximum is not null)
        {
            var min = _chart.Minimum ?? scale.Min;
            var max = _chart.Maximum ?? scale.Max;
            if (max <= min)
            {
                max = min + scale.Step;
            }

            return (min, max, scale.Step);
        }

        return scale;
    }

    private void DrawAxesChart(double top, double bottom, double left, double right)
    {
        var horizontal = _chart.Kind == ChartKind.Bar;
        var scatter = _chart.Kind == ChartKind.Scatter;
        var (min, max, step) = ValueScale();
        var ticks = new List<double>();
        for (var tick = min; tick <= max + step * 1e-6; tick = Math.Round(tick + step, 12))
        {
            ticks.Add(tick);
        }

        // The x scale of a scatter is a value scale as well.
        var xScale = scatter
            ? NiceScale(_chart.Series.Min(s => s.XValues!.Min()), _chart.Series.Max(s => s.XValues!.Max()))
            : (Min: 0.0, Max: 0.0, Step: 1.0);
        var xTicks = new List<double>();
        if (scatter)
        {
            for (var tick = xScale.Min; tick <= xScale.Max + xScale.Step * 1e-6; tick = Math.Round(tick + xScale.Step, 12))
            {
                xTicks.Add(tick);
            }
        }

        var categories = scatter ? 0 : Math.Max(_chart.Series.Max(series => series.Values.Count), 1);
        var categoryLabels = scatter ? [] : Enumerable.Range(0, categories)
            .Select(i => _chart.Categories is { } names && i < names.Count ? names[i] : (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();

        // Room for the axis labels and titles.
        var valueLabelWidth = ticks.Max(tick => Width(Number(tick, step), TextSize));
        var categoryLabelWidth = horizontal ? categoryLabels.Max(label => Width(label, TextSize)) : 0;
        var leftSpace = left + (horizontal ? categoryLabelWidth + 6 : valueLabelWidth + 6)
            + ((horizontal ? _chart.CategoryAxisTitle : _chart.ValueAxisTitle) is { Length: > 0 } ? TextSize + 6 : 0);
        var bottomSpace = (TextSize + 6)
            + ((horizontal ? _chart.ValueAxisTitle : _chart.CategoryAxisTitle) is { Length: > 0 } ? TextSize + 6 : 0);
        var plotLeft = leftSpace;
        var plotRight = right - 6;
        var plotTop = top + 4;
        var plotBottom = bottom - bottomSpace;
        if (plotRight - plotLeft < 20 || plotBottom - plotTop < 20)
        {
            return;
        }

        double ValueToPosition(double value) => horizontal
            ? plotLeft + (value - min) / (max - min) * (plotRight - plotLeft)
            : plotBottom - (value - min) / (max - min) * (plotBottom - plotTop);
        double XToPosition(double value) => plotLeft + (value - xScale.Min) / (xScale.Max - xScale.Min) * (plotRight - plotLeft);

        // Grid lines and value axis labels.
        foreach (var tick in ticks)
        {
            var position = ValueToPosition(tick);
            if (horizontal)
            {
                Line(position, plotTop, position, plotBottom, GridColor, 0.5);
                Text(Number(tick, step), TextSize, TextColor, position, plotBottom + 4 + TextSize, 0.5);
            }
            else
            {
                Line(plotLeft, position, plotRight, position, GridColor, 0.5);
                Text(Number(tick, step), TextSize, TextColor, plotLeft - 4, position + TextSize * 0.35, 1);
            }
        }

        if (scatter)
        {
            foreach (var tick in xTicks)
            {
                var position = XToPosition(tick);
                Line(position, plotTop, position, plotBottom, GridColor, 0.5);
                Text(Number(tick, xScale.Step), TextSize, TextColor, position, plotBottom + 4 + TextSize, 0.5);
            }
        }

        // Bars and areas are cut at the axis range; lines and markers are only when the range is fixed
        // (a marker on the axis would otherwise be cut in half).
        _content.SaveState();
        if (_chart.Kind is ChartKind.Column or ChartKind.Bar or ChartKind.Area || _chart.Minimum is not null || _chart.Maximum is not null)
        {
            _content.Rectangle(X(plotLeft), Y(plotBottom), plotRight - plotLeft, plotBottom - plotTop).Clip();
        }

        var span = horizontal ? plotBottom - plotTop : plotRight - plotLeft;
        var band = span / Math.Max(1, categories);
        switch (_chart.Kind)
        {
            case ChartKind.Column:
            case ChartKind.Bar:
                DrawBars(horizontal, categories, band, plotLeft, plotTop, plotBottom, ValueToPosition, Math.Clamp(0, min, max));
                break;
            case ChartKind.Line:
            case ChartKind.Area:
                DrawLines(_chart.Kind == ChartKind.Area, categories, band, plotLeft, plotBottom, ValueToPosition, Math.Clamp(0, min, max));
                break;
            case ChartKind.Scatter:
                for (var s = 0; s < _chart.Series.Count; s++)
                {
                    var series = _chart.Series[s];
                    for (var i = 0; i < series.Values.Count; i++)
                    {
                        Marker(XToPosition(series.XValues![i]), ValueToPosition(series.Values[i]), 3.5, _chart.SeriesColor(s));
                    }
                }

                break;
        }

        _content.RestoreState();

        // Axes on top of the data and the category labels.
        var zero = ValueToPosition(Math.Clamp(0, min, max));
        if (horizontal)
        {
            Line(zero, plotTop, zero, plotBottom, AxisColor, 1);
            Line(plotLeft, plotBottom, plotRight, plotBottom, AxisColor, 1);
        }
        else
        {
            Line(plotLeft, plotTop, plotLeft, plotBottom, AxisColor, 1);
            Line(plotLeft, scatter ? plotBottom : zero, plotRight, scatter ? plotBottom : zero, AxisColor, 1);
        }

        if (!scatter)
        {
            for (var i = 0; i < categories; i++)
            {
                if (horizontal)
                {
                    var label = Shorten(categoryLabels[i], categoryLabelWidth + 2, TextSize);
                    Text(label, TextSize, TextColor, plotLeft - 4, plotTop + (i + 0.5) * band + TextSize * 0.35, 1);
                }
                else
                {
                    var label = Shorten(categoryLabels[i], band - 2, TextSize);
                    Text(label, TextSize, TextColor, plotLeft + (i + 0.5) * band, plotBottom + 4 + TextSize, 0.5);
                }
            }
        }

        DrawAxisTitles(plotLeft, plotRight, plotTop, plotBottom, horizontal);
    }

    private void DrawAxisTitles(double plotLeft, double plotRight, double plotTop, double plotBottom, bool horizontal)
    {
        var bottomTitle = horizontal ? _chart.ValueAxisTitle : _chart.CategoryAxisTitle;
        var leftTitle = horizontal ? _chart.CategoryAxisTitle : _chart.ValueAxisTitle;
        if (!string.IsNullOrEmpty(bottomTitle))
        {
            Text(bottomTitle, TextSize, Color.Black, (plotLeft + plotRight) / 2, plotBottom + 4 + 2 * TextSize + 6, 0.5, bold: true);
        }

        if (!string.IsNullOrEmpty(leftTitle))
        {
            // Rotated a quarter turn counter-clockwise, centered on the plot height.
            _content.SaveState();
            var text = Safe(leftTitle);
            var width = _bold.Measure(text, TextSize);
            _content.ConcatMatrix(0, 1, -1, 0, X(Margin + TextSize), Y((plotTop + plotBottom) / 2 + width / 2));
            PdfPageRenderer.WriteLineTo(_content, _bold, text, TextSize, Color.Black, 0, 0, 0, 100, null);
            _content.RestoreState();
        }
    }

    private void DrawBars(
        bool horizontal, int categories, double band, double plotLeft, double plotTop, double plotBottom,
        Func<double, double> position, double baseline)
    {
        var seriesCount = _chart.Series.Count;
        var groupShare = band * 0.8;
        var barSize = _chart.Stacked ? groupShare : groupShare / seriesCount;
        for (var i = 0; i < categories; i++)
        {
            var positive = 0.0;
            var negative = 0.0;
            for (var s = 0; s < seriesCount; s++)
            {
                var values = _chart.Series[s].Values;
                if (i >= values.Count)
                {
                    continue;
                }

                double from = baseline, to = values[i];
                if (_chart.Stacked)
                {
                    from = to >= 0 ? positive : negative;
                    to += from;
                    if (values[i] >= 0)
                    {
                        positive = to;
                    }
                    else
                    {
                        negative = to;
                    }
                }

                var offset = (_chart.Stacked ? 0 : s * barSize) + (band - groupShare) / 2;
                var (a, b) = (position(from), position(to));
                var color = _chart.SeriesColor(s);
                if (horizontal)
                {
                    var y = plotTop + i * band + offset;
                    _content.FillRectangle(X(Math.Min(a, b)), Y(y + barSize), Math.Abs(b - a), barSize, color);
                }
                else
                {
                    var x = plotLeft + i * band + offset;
                    _content.FillRectangle(X(x), Y(Math.Max(a, b)), barSize, Math.Abs(b - a), color);
                }
            }
        }
    }

    private void DrawLines(
        bool area, int categories, double band, double plotLeft, double plotBottom, Func<double, double> position, double baseline)
    {
        var cumulative = new double[categories];
        var rendered = new List<(int Index, List<(double X, double Y)> Top, List<(double X, double Y)> Bottom)>();
        foreach (var s in Enumerable.Range(0, _chart.Series.Count))
        {
            var values = _chart.Series[s].Values;
            var top = new List<(double, double)>();
            var low = new List<(double, double)>();
            for (var i = 0; i < Math.Min(values.Count, categories); i++)
            {
                var before = area && _chart.Stacked ? cumulative[i] : baseline;
                var after = area && _chart.Stacked ? cumulative[i] + values[i] : values[i];
                cumulative[i] = area && _chart.Stacked ? after : 0;
                var x = plotLeft + (i + 0.5) * band;
                top.Add((x, position(after)));
                low.Add((x, position(before)));
            }

            rendered.Add((s, top, low));
        }

        // A stacked area is drawn from the last series to the first so the lower ones stay visible.
        foreach (var (s, top, low) in area && _chart.Stacked ? rendered.AsEnumerable().Reverse() : rendered)
        {
            if (top.Count == 0)
            {
                continue;
            }

            var color = _chart.SeriesColor(s);
            if (area)
            {
                _content.SaveState().SetFillColor(Mix(color, 0.55));
                _content.MoveTo(X(top[0].X), Y(low[0].Y));
                foreach (var (x, y) in top)
                {
                    _content.LineTo(X(x), Y(y));
                }

                for (var i = low.Count - 1; i >= 0; i--)
                {
                    _content.LineTo(X(low[i].X), Y(low[i].Y));
                }

                _content.ClosePath().Fill().RestoreState();
            }

            _content.SaveState().SetStrokeColor(color).SetLineWidth(2);
            _content.MoveTo(X(top[0].X), Y(top[0].Y));
            foreach (var (x, y) in top.Skip(1))
            {
                _content.LineTo(X(x), Y(y));
            }

            _content.Stroke().RestoreState();
            if (!area)
            {
                foreach (var (x, y) in top)
                {
                    Marker(x, y, 3, color);
                }
            }
        }

    }

    private void Marker(double x, double y, double radius, Color color)
    {
        _content.SaveState().SetFillColor(color);
        Circle(X(x), Y(y), radius);
        _content.Fill().RestoreState();
    }

    private void Circle(double cx, double cy, double r)
    {
        const double Kappa = 0.5522847498;
        var k = r * Kappa;
        _content.MoveTo(cx + r, cy)
            .CurveTo(cx + r, cy + k, cx + k, cy + r, cx, cy + r)
            .CurveTo(cx - k, cy + r, cx - r, cy + k, cx - r, cy)
            .CurveTo(cx - r, cy - k, cx - k, cy - r, cx, cy - r)
            .CurveTo(cx + k, cy - r, cx + r, cy - k, cx + r, cy)
            .ClosePath();
    }

    private void Line(double x1, double y1, double x2, double y2, Color color, double width)
    {
        _content.SaveState().SetStrokeColor(color).SetLineWidth(width);
        _content.MoveTo(X(x1), Y(y1)).LineTo(X(x2), Y(y2)).Stroke().RestoreState();
    }

    private void DrawPie(double top, double bottom, double left, double right)
    {
        var values = _chart.Series[0].Values;
        var negative = values.Any(value => value < 0);
        if (negative)
        {
            _report("PDF: a negative value of a pie is drawn as zero.");
        }

        var slices = values.Select(value => Math.Max(0, value)).ToList();
        var total = slices.Sum();
        var radius = Math.Min(right - left, bottom - top) / 2 - 6;
        if (total <= 0 || radius < 10)
        {
            return;
        }

        var (cx, cy) = ((left + right) / 2, (top + bottom) / 2);
        var start = Math.PI / 2;
        for (var i = 0; i < slices.Count; i++)
        {
            var sweep = slices[i] / total * 2 * Math.PI;
            if (sweep <= 0)
            {
                continue;
            }

            var color = Chart.DefaultPalette[i % Chart.DefaultPalette.Count];
            _content.SaveState().SetFillColor(color).SetStrokeColor(Color.White).SetLineWidth(1);
            if (slices.Count(value => value > 0) == 1)
            {
                Circle(X(cx), Y(cy), radius);
            }
            else
            {
                _content.MoveTo(X(cx), Y(cy));
                Arc(cx, cy, radius, start, -sweep);
                _content.ClosePath();
            }

            _content.FillAndStroke().RestoreState();
            if (sweep / (2 * Math.PI) >= 0.05)
            {
                var middle = start - sweep / 2;
                var luminance = (0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue) / 255;
                var share = (sweep / (2 * Math.PI) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                Text(share, TextSize + 1, luminance > 0.6 ? Color.Black : Color.White,
                    cx + Math.Cos(middle) * radius * 0.62, cy - Math.Sin(middle) * radius * 0.62 + TextSize * 0.35, 0.5, bold: true);
            }

            start -= sweep;
        }
    }

    // An arc from the angle by the sweep (radians, mathematical direction; negative = clockwise) as Bézier segments.
    private void Arc(double cx, double cy, double radius, double start, double sweep)
    {
        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        var each = sweep / segments;
        var k = 4.0 / 3 * Math.Tan(each / 4);
        var angle = start;
        _content.LineTo(X(cx + radius * Math.Cos(angle)), Y(cy - radius * Math.Sin(angle)));
        for (var i = 0; i < segments; i++)
        {
            var next = angle + each;
            double x1 = Math.Cos(angle) - k * Math.Sin(angle), y1 = Math.Sin(angle) + k * Math.Cos(angle);
            double x2 = Math.Cos(next) + k * Math.Sin(next), y2 = Math.Sin(next) - k * Math.Cos(next);
            _content.CurveTo(
                X(cx + radius * x1), Y(cy - radius * y1), X(cx + radius * x2), Y(cy - radius * y2),
                X(cx + radius * Math.Cos(next)), Y(cy - radius * Math.Sin(next)));
            angle = next;
        }
    }
}
