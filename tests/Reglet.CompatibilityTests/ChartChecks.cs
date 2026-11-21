using System.Text;
using Reglet;

/// <summary>Charts (ADR-0025): model rules, the axis scale and the PDF rendering.</summary>
internal static class ChartChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidCharts();
            NiceScale();
            WritesPdf();
            WritesDocx();
            WritesOdt();
            ReadsCharts();
            ManySeriesRoundTrip();
            KeepsChartsWhenChanged();
            ReportsAChartItCannotMap();
            Console.WriteLine("Charts: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Charts: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static ChartSeries Series(string name = "s") => new(name, new double[] { 1, 2, 3 });

    private static void RejectsInvalidCharts()
    {
        foreach (var make in new Func<object>[]
        {
            () => new ChartSeries("s", Array.Empty<double>()),
            () => new ChartSeries("s", new[] { 1.0, double.NaN }),
            () => new ChartSeries("s", new[] { 1.0, 2.0 }, new[] { 1.0 }),
            () => new Chart(ChartKind.Column, Array.Empty<ChartSeries>()),
            () => new Chart(ChartKind.Pie, new[] { Series(), Series() }),
            () => new Chart(ChartKind.Scatter, new[] { Series() }),
            () => new Chart(ChartKind.Column, new[] { new ChartSeries("s", new[] { 1.0 }, new[] { 1.0 }) }),
            () => new Chart(ChartKind.Column, new[] { Series() }, new[] { "a", "b" }),
            () => new Chart(ChartKind.Column, new[] { Series() }, width: Length.Zero),
            () => new Chart((ChartKind)9, new[] { Series() }),
            () => new Chart(ChartKind.Column, new[] { Series() }) { Minimum = double.PositiveInfinity },
        })
        {
            var threw = false;
            try
            {
                make();
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Require(threw, "An invalid chart is rejected.");
        }

        var chart = new Chart(ChartKind.Column, new[] { Series(), new ChartSeries("t", new[] { 1.0 }) { Color = Color.FromHex("#112233") } });
        Require(chart.Width.Points == 400 && chart.Height.Points == 250, "Default size.");
        Require(chart.SeriesColor(0) == Chart.DefaultPalette[0] && chart.SeriesColor(1) == Color.FromHex("#112233"), "Palette and own color.");
        Require(new Chart(ChartKind.Scatter, new[] { new ChartSeries("s", new[] { 1.0 }, new[] { 2.0 }) }).Categories is null, "A scatter needs no categories.");
    }

    private static void NiceScale()
    {
        foreach (var (low, high, min, max, step) in new[]
        {
            (0.0, 19.0, 0.0, 20.0, 5.0), (0.0, 45.0, 0.0, 50.0, 10.0), (-3.0, 19.0, -5.0, 20.0, 5.0),
            (0.12, 0.97, 0.0, 1.0, 0.2), (0.0, 1000.0, 0.0, 1000.0, 200.0), (7.0, 7.0, 6.0, 8.0, 0.5),
        })
        {
            var scale = PdfChartPainter.NiceScale(low, high);
            Require(Math.Abs(scale.Min - min) < 1e-9 && Math.Abs(scale.Max - max) < 1e-9 && Math.Abs(scale.Step - step) < 1e-9,
                $"Scale of {low}..{high}: {scale}");
            Require(scale.Min <= low && scale.Max >= high, $"The scale of {low}..{high} covers the data.");
        }
    }

    private static void WritesDocx()
    {
        var categories = new[] { "A", "B", "C" };
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Chart(ChartKind.Column, new[] { Series("x"), Series("y") }, categories) { Title = "Title & <more>", AltText = "Column chart", ValueAxisTitle = "Value", Minimum = 0, Maximum = 10 },
            new Chart(ChartKind.Bar, new[] { Series() }, categories) { Stacked = true, Legend = ChartLegendPosition.Right },
            new Chart(ChartKind.Line, new[] { Series() }, categories),
            new Chart(ChartKind.Area, new[] { Series("a"), Series("b") }, categories) { Stacked = true },
            new Chart(ChartKind.Pie, new[] { new ChartSeries("p", new[] { 1.0, 2.0, 3.0 }) }, categories),
            new Chart(ChartKind.Scatter, new[] { new ChartSeries("s", new[] { 1.0, 2.0 }, new[] { 0.0, 5.0 }) }) { CategoryAxisTitle = "x" },
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new Chart(ChartKind.Column, new[] { Series() }, categories, Length.FromPoints(400), Length.FromPoints(200)) })) }),
        })
        {
            Header = new HeaderFooter(new Block[] { new Chart(ChartKind.Pie, new[] { Series() }, categories) }),
        });
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        Require(result.Warnings.Count == 1 && result.Warnings[0].Message.Contains("chart in a header"), "Only the chart in the header is reported: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(buffer.ToArray()));
        string Part(string name)
        {
            using var reader = new StreamReader((zip.GetEntry(name) ?? throw new InvalidOperationException("Part missing: " + name)).Open());
            return reader.ReadToEnd();
        }

        for (var i = 1; i <= 7; i++)
        {
            System.Xml.Linq.XDocument.Parse(Part($"word/charts/chart{i}.xml"));
        }

        Require(zip.GetEntry("word/charts/chart8.xml") is null, "Seven chart parts.");
        var types = Part("[Content_Types].xml");
        var rels = Part("word/_rels/document.xml.rels");
        Require(Count(types, "drawingml.chart+xml") == 7 && Count(rels, "relationships/chart\"") == 7, "Seven content types and relationships.");
        var body = Part("word/document.xml");
        Require(Count(body, "<c:chart ") == 7 && body.Contains("descr=\"Column chart\""), "Seven inline charts, alt text.");
        var first = Part("word/charts/chart1.xml");
        Require(first.Contains("<c:barDir val=\"col\"") && first.Contains("clustered") && first.Contains("<c:max val=\"10\"") && first.Contains("Title &amp; &lt;more&gt;") && first.Contains("<c:numLit>"), "Column chart part with literals and an escaped title.");
        Require(!first.Contains("externalData"), "No embedded workbook.");
        Require(Part("word/charts/chart2.xml").Contains("<c:barDir val=\"bar\"") && Part("word/charts/chart2.xml").Contains("stacked") && Part("word/charts/chart2.xml").Contains("legendPos val=\"r\""), "Stacked bar with the legend on the right.");
        Require(Part("word/charts/chart5.xml").Contains("<c:pieChart>") && Part("word/charts/chart5.xml").Contains("showPercent"), "Pie with percent labels.");
        Require(Part("word/charts/chart6.xml").Contains("<c:scatterChart>") && Part("word/charts/chart6.xml").Contains("<c:xVal>"), "Scatter with x values.");
        Require(Part("word/charts/chart3.xml").Contains("<c:legend>"), "A named series has a legend.");

        using var plain = new MemoryStream();
        DocumentWriter.WriteAsync(
            new PortableDocument(new DocumentMetadata(), new Section(new Chart(ChartKind.Column, new[] { new ChartSeries("", new[] { 1.0 }) }))), plain, DocumentFormat.Docx).GetAwaiter().GetResult();
        using var plainZip = new System.IO.Compression.ZipArchive(new MemoryStream(plain.ToArray()));
        using var plainReader = new StreamReader(plainZip.GetEntry("word/charts/chart1.xml")!.Open());
        Require(!plainReader.ReadToEnd().Contains("<c:legend>"), "A single series without a name has no legend.");
    }

    private static void WritesOdt()
    {
        var categories = new[] { "A", "B", "C" };
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Chart(ChartKind.Column, new[] { Series("x"), Series("y") }, categories) { Title = "Title & <more>", AltText = "Column chart", ValueAxisTitle = "Value", Minimum = 0, Maximum = 10 },
            new Chart(ChartKind.Pie, new[] { new ChartSeries("p", new[] { 1.0, 2.0, 3.0 }) }, categories),
            new Chart(ChartKind.Scatter, new[] { new ChartSeries("s", new[] { 1.0, 2.0 }, new[] { 0.0, 5.0 }) }),
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new Chart(ChartKind.Line, new[] { Series() }, categories) })) }),
        })
        {
            Footer = new HeaderFooter(new Block[] { new Chart(ChartKind.Pie, new[] { Series() }, categories) }),
        });
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(result.Warnings.Count == 1 && result.Warnings[0].Message.Contains("chart in the footer"), "Only the chart in the footer is reported: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(buffer.ToArray()));
        string Part(string name)
        {
            using var reader = new StreamReader((zip.GetEntry(name) ?? throw new InvalidOperationException("Part missing: " + name)).Open());
            return reader.ReadToEnd();
        }

        for (var i = 1; i <= 4; i++)
        {
            System.Xml.Linq.XDocument.Parse(Part($"Object {i}/content.xml"));
        }

        Require(zip.GetEntry("Object 5/content.xml") is null, "Four chart objects.");
        var manifest = Part("META-INF/manifest.xml");
        Require(Count(manifest, "application/vnd.oasis.opendocument.chart") == 4, "Four chart directories in the manifest.");
        var body = Part("content.xml");
        Require(Count(body, "<draw:object ") == 4 && body.Contains("Column chart"), "Four objects in the text, alt text.");
        var first = Part("Object 1/content.xml");
        Require(first.Contains("chart:class=\"chart:bar\"") && first.Contains("$B$2:.$B$4") && first.Contains("Title &amp; &lt;more&gt;") && first.Contains("chart:maximum=\"10\""), "Column chart with absolute cell ranges and an escaped title.");
        Require(Part("Object 2/content.xml").Contains("chart:class=\"chart:circle\""), "Pie.");
        Require(Part("Object 3/content.xml").Contains("<chart:domain"), "Scatter with a domain.");
    }

    private static void WritesPdf()
    {
        var categories = new[] { "A", "B", "C" };
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Chart(ChartKind.Column, new[] { Series("x"), Series("y") }, categories) { Title = "Title", AltText = "Column chart" },
            new Chart(ChartKind.Bar, new[] { Series() }, categories) { Stacked = true },
            new Chart(ChartKind.Line, new[] { Series() }, categories),
            new Chart(ChartKind.Area, new[] { Series("a"), Series("b") }, categories) { Stacked = true },
            new Chart(ChartKind.Pie, new[] { new ChartSeries("p", new[] { 1.0, 2.0, 3.0 }) }, categories),
            new Chart(ChartKind.Scatter, new[] { new ChartSeries("s", new[] { 1.0, 2.0 }, new[] { 0.0, 5.0 }) }),
            new TableBlock(new[] { new TableRow(new TableCell(new Block[] { new Chart(ChartKind.Column, new[] { Series() }, categories, Length.FromPoints(150), Length.FromPoints(100)) })) }),
        }));
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions()).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "No warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var pdf = Encoding.Latin1.GetString(buffer.ToArray());
        Require(pdf.Contains("/Figure") && pdf.Contains("Column chart"), "The chart is a figure with its alt text.");

        // A negative pie value is reported; a text the font cannot show too.
        using var second = new MemoryStream();
        var reported = new PdfDocumentWriter().WriteAsync(
            new PortableDocument(new DocumentMetadata(), new Section(new Block[]
            {
                new Chart(ChartKind.Pie, new[] { new ChartSeries("p", new[] { 1.0, -2.0 }) }),
            })), second, new PdfWriteOptions()).GetAwaiter().GetResult();
        Require(reported.Warnings.Any(w => w.Message.Contains("negative value of a pie")), "A negative pie value is reported.");
    }

    private static Chart[] SampleCharts()
    {
        var categories = new[] { "Q1", "Q2", "Q3" };
        return
        [
            new Chart(ChartKind.Column, [new ChartSeries("x", new[] { 1, 2.5, 3 }), new ChartSeries("y", new double[] { 3, 1, 2 }) { Color = Color.FromHex("#112233") }], categories, Length.FromPoints(300), Length.FromPoints(200))
            { Title = "Title & <more>", CategoryAxisTitle = "Cat", ValueAxisTitle = "Val", Minimum = 0, Maximum = 10, AltText = "Alt" },
            new Chart(ChartKind.Bar, [new ChartSeries("", new double[] { 1, 2, 3 })], categories) { Legend = ChartLegendPosition.Right, Stacked = true },
            new Chart(ChartKind.Line, [new ChartSeries("l", new double[] { 1, 2, 3 })], categories),
            new Chart(ChartKind.Area, [new ChartSeries("a", new double[] { 1, 2, 3 }), new ChartSeries("b", new double[] { 2, 2, 2 })], categories) { Stacked = true },
            new Chart(ChartKind.Pie, [new ChartSeries("p", new double[] { 1, 2, 3 })], categories) { Title = "Pie" },
            new Chart(ChartKind.Scatter, [new ChartSeries("s", new double[] { 1, 4, 9 }, new double[] { 1, 2, 3 })]) { CategoryAxisTitle = "x", ValueAxisTitle = "y" },
        ];
    }

    private static void RequireSame(Chart read, Chart original, string format)
    {
        var label = format + " " + original.Kind;
        Require(read.Kind == original.Kind, label + ": kind");
        Require(read.Series.Select(x => x.Name).SequenceEqual(original.Series.Select(x => x.Name)), label + ": names");
        for (var i = 0; i < original.Series.Count; i++)
        {
            Require(read.Series[i].Values.SequenceEqual(original.Series[i].Values), label + ": values");
            Require((read.Series[i].XValues is null && original.Series[i].XValues is null)
                || (read.Series[i].XValues is not null && original.Series[i].XValues is not null && read.Series[i].XValues!.SequenceEqual(original.Series[i].XValues!)), label + ": x values");
            Require(read.SeriesColor(i) == original.SeriesColor(i), label + ": color");
        }

        Require((read.Categories is null && original.Categories is null) || (read.Categories is not null && original.Categories is not null && read.Categories.SequenceEqual(original.Categories)), label + ": categories");
        Require(read.Title == original.Title && read.CategoryAxisTitle == original.CategoryAxisTitle && read.ValueAxisTitle == original.ValueAxisTitle, label + ": titles");
        Require(read.Minimum == original.Minimum && read.Maximum == original.Maximum && read.Stacked == original.Stacked && read.AltText == original.AltText, label + ": limits, stacking, alt text");
        Require(Math.Abs(read.Width.Points - original.Width.Points) < 0.5 && Math.Abs(read.Height.Points - original.Height.Points) < 0.5, label + ": size");
    }

    private static void ManySeriesRoundTrip()
    {
        // ODT column letters ran from A to Z and then on to '[': series 26 and later (scatter: 13) had no valid address.
        foreach (var kind in new[] { ChartKind.Column, ChartKind.Scatter })
        {
            var series = Enumerable.Range(0, 30).Select(i => new ChartSeries("s" + i, [i, i + 0.5], kind == ChartKind.Scatter ? [1.0, 2.0] : null)).ToList();
            var chart = new Chart(kind, series, kind == ChartKind.Scatter ? null : ["a", "b"]);
            foreach (var format in new[] { DocumentFormat.Odt, DocumentFormat.Docx })
            {
                var read = Read(Write(new PortableDocument(new DocumentMetadata(), new Section(chart)), format, out _), format);
                var back = read.Document.Sections[0].Blocks.OfType<Chart>().Single();
                Require(back.Series.Count == 30 && back.Series[29].Name == "s29" && back.Series[29].Values.SequenceEqual([29, 29.5]),
                    $"{format} {kind}: thirty series round-trip.");
            }
        }
    }

    private static byte[] Write(PortableDocument document, DocumentFormat format, out WriteResult result)
    {
        using var buffer = new MemoryStream();
        result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static ReadResult Read(byte[] bytes, DocumentFormat format) => DocumentReader.Read(new MemoryStream(bytes), format);

    /// <summary>Charts are read into the model (DOCX chart part, ODT chart object) and written again.</summary>
    private static void ReadsCharts()
    {
        var charts = SampleCharts();
        var document = new PortableDocument(new DocumentMetadata(), new Section(charts));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var read = Read(Write(document, format, out _), format);
            Require(read.Warnings.Count == 0, format + ": no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
            var found = read.Document.Sections[0].Blocks.OfType<Chart>().ToList();
            Require(found.Count == charts.Length, format + ": all charts are read");
            for (var i = 0; i < charts.Length; i++)
            {
                RequireSame(found[i], charts[i], format.ToString());
            }
        }
    }

    /// <summary>A changed document keeps its charts, because they are part of the model.</summary>
    private static void KeepsChartsWhenChanged()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[] { new Paragraph([new TextRun("head")]) }.Concat(SampleCharts()).ToArray()));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var read = Read(Write(document, format, out _), format);
            var changed = new PortableDocument(read.Document.Metadata, new Section(read.Document.Sections[0].Blocks.Append(new Paragraph([new TextRun("new")])).ToArray()));
            var again = Read(Write(changed, format, out var result), format);
            Require(result.Warnings.Count == 0, format + ": no warning for a changed document: " + string.Join("|", result.Warnings.Select(w => w.Message)));
            Require(again.Document.Sections[0].Blocks.OfType<Chart>().Count() == 6, format + ": six charts after the change");
        }
    }

    /// <summary>A chart the reader cannot map is reported on reading.</summary>
    private static void ReportsAChartItCannotMap()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Chart(ChartKind.Column, new[] { Series() }, new[] { "a", "b", "c" })));
        using var package = new MemoryStream(Write(document, DocumentFormat.Docx, out _));
        using var output = new MemoryStream();
        using (var source = new System.IO.Compression.ZipArchive(package))
        using (var target = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                if (entry.FullName == "word/charts/chart1.xml")
                {
                    text = text.Replace("c:barChart", "c:bar3DChart", StringComparison.Ordinal);
                }

                using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open());
                writer.Write(text);
            }
        }

        var read = Read(output.ToArray(), DocumentFormat.Docx);
        Require(read.Warnings.Any(w => w.Message.Contains("c:bar3DChart")), "The unknown plot type is reported: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        Require(!read.Document.Sections[0].Blocks.OfType<Chart>().Any(), "No chart is mapped.");
    }
}
