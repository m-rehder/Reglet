using System.IO.Compression;
using Reglet;

/// <summary>
/// mail merge (phase AB): binds a template with data rows (AB01/AB03) and writes one document
/// per row; placeholders can also bind images and fields (AB04).
/// </summary>
internal static class MailMergeChecks
{
    internal static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reglet-merge-" + Guid.NewGuid().ToString("N"));
        try
        {
            BindsDataRow();
            MergesToFiles(directory);
            HandlesConditionals();
            ResolvesNestedAndUnclosedConditions();
            LargeTemplatesStayFast();
            LimitsBoundTemplateSize();
            CleansRepeatedValues();
            MergeSkipsColumnsThatCannotBePlaceholders(directory);
            ComparesRepeatedFieldNamesOrdinally();
            BindsImageAndField();
            ValidatesTypedPlaceholders();
            RepeatRowValuesStayLiteral();
            RepeatsOnlyTheContainingRow();
            RepeatsNestedRowsAtBothLevels();
            RepeatsBlockRegions();
            RepeatsRowRegions();
            ReportsUnsafeRepeatRegions();
            RepeatsImageRegions();
            RepeatsSelfContainedRegions();
            RepeatsOdtNoteRegion();
            RepeatsImageRowsSafely();
            RepeatsBookmarkRegions();
            EnforcesRepeatRegionLimits();
            KeepsRepeatBindingsIndependent();
            RemovesDuplicateWordParagraphIds();
            RepeatsPerParentData();
            RepeatsPerParentTableRows();
            RepeatsThreeDataLevels();
            ValidatesNestedRegionData();
            ScansPlainRegionSiblingsLinearly();
            RejectsInvalidValues(directory);
            Console.WriteLine("Mail merge (AB): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Mail merge (AB): " + exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] TemplateBytes(DocumentFormat format)
    {
        var document = PortableDocument.Create()
            .Paragraph("Hello {{Name}} from {{City}}.")
            .Build();

        using var buffer = new MemoryStream();
        if (format == DocumentFormat.Docx)
        {
            new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        }
        else
        {
            new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        }

        return buffer.ToArray();
    }

    private static void BindsDataRow()
    {
        var template = DocumentTemplate.FromBytes(TemplateBytes(DocumentFormat.Docx))
            .Bind(new Dictionary<string, string?> { ["Name"] = "Ada", ["City"] = "London", ["Missing"] = null });

        Require(template.Placeholders.ContainsKey("Name"), "Bind: Name bound.");
        Require(template.Placeholders.ContainsKey("City"), "Bind: City bound.");
        Require(!template.Placeholders.ContainsKey("Missing"), "Bind: null value skipped.");
    }

    private static void MergesToFiles(string directory)
    {
        var rows = new[]
        {
            new Dictionary<string, string?> { ["Name"] = "Ada", ["City"] = "London" },
            new Dictionary<string, string?> { ["Name"] = "Grace", ["City"] = "Paris" },
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var template = DocumentTemplate.FromBytes(TemplateBytes(format));
            var target = Path.Combine(directory, format.ToString());
            var paths = MailMerge.MergeAsync(template, rows, target, format).GetAwaiter().GetResult();

            Require(paths.Count == 2, $"{format}: two files.");
            var first = ReadDocumentText(paths[0], format);
            var second = ReadDocumentText(paths[1], format);
            Require(first.Contains("Ada", StringComparison.Ordinal) && first.Contains("London", StringComparison.Ordinal), $"{format}: first row bound.");
            Require(second.Contains("Grace", StringComparison.Ordinal) && second.Contains("Paris", StringComparison.Ordinal), $"{format}: second row bound.");
        }
    }

    private static byte[] Template(DocumentFormat format, params Paragraph[] paragraphs)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(paragraphs)), buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static Paragraph[] Lines(params string[] texts) => texts.Select(text => new Paragraph(new TextRun(text))).ToArray();

    private static Stream WriteStream(DocumentTemplate template, DocumentFormat format, ResourceLimits? limits = null, InvalidXmlCharacterBehavior? xml = null)
    {
        var output = new MemoryStream();
        var document = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
        WriteOptions options = format == DocumentFormat.Docx
            ? new DocxWriteOptions { Template = template, Limits = limits ?? ResourceLimits.Default, InvalidXmlCharacters = xml ?? InvalidXmlCharacterBehavior.Replace }
            : new OdtWriteOptions { Template = template, Limits = limits ?? ResourceLimits.Default, InvalidXmlCharacters = xml ?? InvalidXmlCharacterBehavior.Replace };
        DocumentWriter.WriteAsync(document, output, format, options).GetAwaiter().GetResult();
        output.Position = 0;
        return output;
    }

    private static string ReadBack(Stream package, DocumentFormat format)
        => string.Join("|", DocumentReader.Read(package, format).Document.Sections[0].Blocks.OfType<Paragraph>()
            .Select(paragraph => string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text))));

    private static void ResolvesNestedAndUnclosedConditions()
    {
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var bytes = Template(format, Lines("{{If:yes}}", "A", "{{If:no}}", "B", "{{EndIf}}", "C", "{{EndIf}}", "{{EndIf}}", "{{If:open}}", "D"));
            var text = ReadBack(WriteStream(DocumentTemplate.FromBytes(bytes).Condition("yes", true).Condition("no", false), format), format);
            Require(text == "A|C|D", $"{format}: nested, stray and unclosed conditions gave '{text}'.");
        }
    }

    private static void LargeTemplatesStayFast()
    {
        var row = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = "v" } };
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var cases = new (string Name, Func<DocumentTemplate> Build)[]
            {
                ("plain", () => DocumentTemplate.FromBytes(Template(format, Lines(Enumerable.Repeat("Text", 50_000).ToArray())))),
                ("unclosed", () => DocumentTemplate.FromBytes(Template(format, Lines(Enumerable.Repeat("{{If:a}}", 20_000).ToArray())))),
                ("regions", () => DocumentTemplate.FromBytes(Template(format, Lines(Enumerable.Repeat(new[] { "{{Repeat:R}}", "x", "{{EndRepeat}}" }, 10_000).SelectMany(line => line).ToArray()))).RepeatRegion("R", row)),
                ("one text", () => DocumentTemplate.FromBytes(Template(format, Lines(string.Concat(Enumerable.Repeat("{{If:a}}", 200_000)) + string.Concat(Enumerable.Repeat("{{x}}", 200_000))))).Condition("a", true)),
                ("many runs", () => DocumentTemplate.FromBytes(Template(format, new Paragraph(Enumerable.Repeat<Inline[]>([new TextRun("{{a") { Bold = true }, new TextRun("}}")], 50_000).SelectMany(run => run)))).BindText("a", "v")),
            };

            foreach (var (name, build) in cases)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                WriteStream(build(), format);
                Require(timer.Elapsed < TimeSpan.FromSeconds(15), $"{format}: template '{name}' took {timer.ElapsedMilliseconds} ms.");
            }
        }
    }

    private static void LimitsBoundTemplateSize()
    {
        var limits = new ResourceLimits { MaxTemplateCharacters = 10_000 };
        var large = new string('V', 20_000);
        var rows = Enumerable.Repeat((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = large }, 1).ToArray();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var bound = DocumentTemplate.FromBytes(Template(format, Lines("{{a}}"))).BindText("a", large);
            Throws<DocumentValidationException>(() => WriteStream(bound, format, limits));
            Require(ReadBack(WriteStream(bound, format), format) == large, $"{format}: a value within the default limit is written.");

            var region = DocumentTemplate.FromBytes(Template(format, Lines("{{Repeat:R}}", "{{F}}", "{{EndRepeat}}"))).RepeatRegion("R", rows);
            Throws<DocumentValidationException>(() => WriteStream(region, format, limits));

            var manyRows = Enumerable.Repeat((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(), 5_000).ToArray();
            var clones = DocumentTemplate.FromBytes(Template(format, Lines("{{Repeat:R}}", "some text", "{{EndRepeat}}"))).RepeatRegion("R", manyRows);
            Throws<DocumentValidationException>(() => WriteStream(clones, format, limits));
        }
    }

    private static void CleansRepeatedValues()
    {
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            foreach (var value in new[] { "a\u0001b", "a\uD800b" })
            {
                var rows = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = value } };
                var template = DocumentTemplate.FromBytes(Template(format, Lines("{{Repeat:R}}", "{{F}}", "{{EndRepeat}}"))).RepeatRegion("R", rows);
                Require(ReadBack(WriteStream(template, format), format) == "a\uFFFDb", $"{format}: an invalid XML character is replaced.");
                Throws<DocumentValidationException>(() => WriteStream(template, format, xml: InvalidXmlCharacterBehavior.Fail));
            }
        }
    }

    private static void MergeSkipsColumnsThatCannotBePlaceholders(string directory)
    {
        var bytes = Template(DocumentFormat.Docx, Lines("{{Name}}"));
        var rows = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["Name"] = "Ada", ["{x}"] = "1", [""] = "2", [" "] = "3" } };
        var paths = MailMerge.MergeAsync(DocumentTemplate.FromBytes(bytes), rows, directory, DocumentFormat.Docx).GetAwaiter().GetResult();
        Require(ReadBack(File.OpenRead(paths[0]), DocumentFormat.Docx) == "Ada", "Merge: a column that cannot be a placeholder is skipped.");
    }

    private static void ComparesRepeatedFieldNamesOrdinally()
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "Ada" };
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var template = DocumentTemplate.FromBytes(Template(format, Lines("{{Repeat:R}}", "{{name}}", "{{Name}}", "{{EndRepeat}}"))).RepeatRegion("R", [row]);
            var text = ReadBack(WriteStream(template, format), format);
            Require(text == "{{name}}|Ada", $"{format}: field names are case-sensitive, got '{text}'.");
        }
    }

    private static byte[] TextTemplate(string text, DocumentFormat format)
    {
        var document = PortableDocument.Create().Paragraph(text).Build();
        using var buffer = new MemoryStream();
        if (format == DocumentFormat.Docx)
        {
            new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        }
        else
        {
            new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        }

        return buffer.ToArray();
    }

    private static string WriteTemplate(DocumentTemplate template, DocumentFormat format)
        => WriteTemplate(template, format, out _);

    private static string WriteTemplate(DocumentTemplate template, DocumentFormat format, out WriteResult result)
        => WriteTemplate(template, format, out result, out _);

    private static string WriteTemplate(DocumentTemplate template, DocumentFormat format, out WriteResult result, out byte[] package)
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
        using var buffer = new MemoryStream();
        if (format == DocumentFormat.Docx)
        {
            result = new DocxDocumentWriter().WriteAsync(document, buffer, new DocxWriteOptions { Template = template }).GetAwaiter().GetResult();
        }
        else
        {
            result = new OdtDocumentWriter().WriteAsync(document, buffer, new OdtWriteOptions { Template = template }).GetAwaiter().GetResult();
        }

        package = buffer.ToArray();
        return DocumentText(package, format);
    }

    private static string DocumentText(byte[] package, DocumentFormat format)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(format == DocumentFormat.Docx ? "word/document.xml" : "content.xml")
            ?? throw new InvalidOperationException("Part missing.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void HandlesConditionals()
    {
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var bytes = TextTemplate("A {{If:Show}}{{Name}}{{EndIf}} B", format);

            var hidden = DocumentTemplate.FromBytes(bytes).Condition("Show", false).BindText("Name", "Hidden");
            Require(!WriteTemplate(hidden, format).Contains("Hidden", StringComparison.Ordinal), $"{format}: false condition removes the region.");

            var shown = DocumentTemplate.FromBytes(bytes).Condition("Show", true).BindText("Name", "Shown");
            Require(WriteTemplate(shown, format).Contains("Shown", StringComparison.Ordinal), $"{format}: true condition keeps the region.");

            var empty = DocumentTemplate.FromBytes(TextTemplate("{{Optional}}", format)).BindEmpty("Optional");
            Require(!WriteTemplate(empty, format).Contains("Optional", StringComparison.Ordinal), $"{format}: empty binding removes the paragraph.");
        }
    }

    private static void BindsImageAndField()
    {
        var template = DocumentTemplate.FromBytes(TemplateBytes(DocumentFormat.Docx))
            .Bind("Name", new ImageBlock(TestImages.RgbPng(4, 2, 0, 0, 255), ImageMediaType.Png))
            .Bind("City", new DateField(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        Require(template.Placeholders.ContainsKey("Name"), "AB04: image bound.");
        Require(template.Placeholders.ContainsKey("City"), "AB04: field bound.");
    }

    private static void RejectsInvalidValues(string directory)
    {
        var template = DocumentTemplate.FromBytes(TemplateBytes(DocumentFormat.Docx));
        Throws<UnsupportedFeatureException>(
            () => MailMerge.MergeAsync(template, [], directory, DocumentFormat.Pdf).GetAwaiter().GetResult());
        Throws<ArgumentException>(
            () => MailMerge.MergeAsync(template, [], " ", DocumentFormat.Docx).GetAwaiter().GetResult());
    }

    private static void ValidatesTypedPlaceholders()
    {
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var template = DocumentTemplate.FromBytes(TextTemplate("{{Name}} {{Date}} {{Amount}}", format))
                .Declare("Name", TemplatePlaceholderType.Text)
                .Declare("Date", TemplatePlaceholderType.Date)
                .Declare("Amount", TemplatePlaceholderType.Number);
            var bound = template.Bind(new Dictionary<string, string?>
            {
                ["Name"] = "Ada", ["Date"] = "2026-11-04", ["Amount"] = "1234.50",
            });
            var output = WriteTemplate(bound, format);
            Require(output.Contains("Ada", StringComparison.Ordinal) && output.Contains("1234.50", StringComparison.Ordinal),
                $"{format}: valid typed values are written.");
            Require(template.PlaceholderTypes.Count == 3 && template.Placeholders.Count == 0,
                $"{format}: declarations and bindings are immutable.");
            Throws<ArgumentException>(() => template.BindText("Date", "2026-02-30"));
            Throws<ArgumentException>(() => template.BindText("Amount", "not a number"));
            Throws<ArgumentException>(() => template.Bind("Name", new PageNumberField()));
            Throws<ArgumentException>(() => template.BindText("Amount", "1,25"));
            Throws<ArgumentException>(() => template.BindText("Date", "not a date").Declare("Date", TemplatePlaceholderType.Date));

            var untyped = DocumentTemplate.FromBytes(TextTemplate("{{Date}}", format)).BindText("Date", "invalid");
            Throws<ArgumentException>(() => untyped.Declare("Date", TemplatePlaceholderType.Date));
            Throws<ArgumentException>(() => template.Body("Name"));
            Throws<ArgumentException>(() => DocumentTemplate.FromBytes(TextTemplate("{{Body}}", format)).Body("Body").Declare("Body", TemplatePlaceholderType.Text));

            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["amount"] = "12" };
            var repeat = template.RepeatRows("Rows", [row]);
            row["amount"] = "bad";
            Throws<ArgumentException>(() => WriteTemplate(repeat, format));
            Throws<ArgumentException>(() => template.RepeatRows("Rows", [row]));
            Throws<ArgumentException>(() => DocumentTemplate.FromBytes(TextTemplate("{{Rows}}", format))
                .RepeatRows("Rows", [row]).Declare("Amount", TemplatePlaceholderType.Number));
        }

        var basic = DocumentTemplate.FromBytes(TemplateBytes(DocumentFormat.Docx));
        var image = new ImageBlock(TestImages.RgbPng(4, 2, 0, 0, 255), ImageMediaType.Png);
        _ = basic.Declare("Image", TemplatePlaceholderType.Image).Bind("Image", image);
        _ = basic.Declare("Field", TemplatePlaceholderType.Field).Bind("Field", new PageNumberField());
        _ = basic.Declare("Date", TemplatePlaceholderType.Date).Bind("Date", new DateField(DateTimeOffset.UnixEpoch));
        Throws<ArgumentException>(() => basic.Declare("Image", TemplatePlaceholderType.Image).BindText("Image", "bad"));
        Throws<ArgumentException>(() => basic.Declare("Field", TemplatePlaceholderType.Field).BindText("Field", "bad"));
        Throws<ArgumentException>(() => basic.Declare("Date", TemplatePlaceholderType.Date).Bind("Date", new DateField()));
        Throws<ArgumentOutOfRangeException>(() => basic.Declare("Name", (TemplatePlaceholderType)999));

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var source = PortableDocument.Create().Paragraph("{{Image}}").Paragraph("{{Field}}").Build();
            using var sourceBuffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, sourceBuffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(sourceBuffer.ToArray())
                .Declare("Image", TemplatePlaceholderType.Image).Bind("Image", image)
                .Declare("Field", TemplatePlaceholderType.Field).Bind("Field", new PageNumberField());
            var output = WriteTemplate(template, format);
            Require(output.Contains(format == DocumentFormat.Docx ? "drawing" : "draw:frame", StringComparison.Ordinal),
                $"{format}: a typed image is written.");
            Require(output.Contains(format == DocumentFormat.Docx ? "PAGE" : "page-number", StringComparison.Ordinal),
                $"{format}: a typed field is written.");
        }
    }

    private static void RepeatRowValuesStayLiteral()
    {
        var source = PortableDocument.Create().Table(table => table.Row("{{Rows}}{{Name}}", "{{Qty}}")).Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray())
                .BindText("Qty", "wrong")
                .RepeatRows("Rows", [new Dictionary<string, string?> { ["Name"] = "{{Qty}}", ["Qty"] = "3" }]);
            var result = WriteTemplate(template, format);
            Require(result.Contains("{{Qty}}", StringComparison.Ordinal) && !result.Contains("wrong", StringComparison.Ordinal),
                $"{format}: marker-like row data stays literal.");

            var conditional = DocumentTemplate.FromBytes(buffer.ToArray())
                .Condition("Hidden", false)
                .RepeatRows("Rows", [new Dictionary<string, string?> { ["Name"] = "{{If:Hidden}}literal{{EndIf}}", ["Qty"] = "<&" }]);
            var conditionalResult = WriteTemplate(conditional, format);
            Require(conditionalResult.Contains("{{If:Hidden}}literal{{EndIf}}", StringComparison.Ordinal)
                && conditionalResult.Contains("&lt;&amp;", StringComparison.Ordinal),
                $"{format}: row data is not treated as a condition and XML characters are escaped.");
            _ = System.Xml.Linq.XDocument.Parse(conditionalResult);
        }
    }

    private static void RepeatsOnlyTheContainingRow()
    {
        var inner = new TableBlock([new TableRow(new TableCell("{{Rows}}{{Name}}"))]);
        var outer = new TableBlock([new TableRow(new TableCell(new Block[]
        {
            new Paragraph(new TextRun("Outer")), inner, new Paragraph(new TextRun("After")),
        }))]);
        var source = new PortableDocument(new DocumentMetadata(), new Section(outer));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray()).RepeatRows("Rows",
            [
                new Dictionary<string, string?> { ["Name"] = "Apple" },
                new Dictionary<string, string?> { ["Name"] = "Pear" },
            ]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format));
            var rows = xml.Descendants().Count(element => element.Name.LocalName == (format == DocumentFormat.Docx ? "tr" : "table-row"));
            Require(rows == 3, $"{format}: only the nested row repeats; got {rows} rows.");
            Require(xml.Root!.Value.Split("Outer", StringSplitOptions.None).Length == 2, $"{format}: the outer row is not duplicated.");
            Require(xml.Root.Value.Contains("Apple", StringComparison.Ordinal) && xml.Root.Value.Contains("Pear", StringComparison.Ordinal),
                $"{format}: both nested rows contain their own value.");
        }
    }

    private static void RepeatsNestedRowsAtBothLevels()
    {
        var inner = new TableBlock([new TableRow(new TableCell("{{Inner}}{{Name}}"))]);
        var outer = new TableBlock([new TableRow(new TableCell(new Block[]
        {
            new Paragraph(new TextRun("{{Outer}}{{Group}}")), inner,
        }))]);
        var source = new PortableDocument(new DocumentMetadata(), new Section(outer));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var outside = new[] { new Dictionary<string, string?> { ["Group"] = "A" }, new Dictionary<string, string?> { ["Group"] = "B" } };
            var inside = new[] { new Dictionary<string, string?> { ["Name"] = "Apple" }, new Dictionary<string, string?> { ["Name"] = "Pear" } };
            foreach (var innerFirst in new[] { false, true })
            {
                var template = DocumentTemplate.FromBytes(buffer.ToArray());
                template = innerFirst ? template.RepeatRows("Inner", inside).RepeatRows("Outer", outside)
                    : template.RepeatRows("Outer", outside).RepeatRows("Inner", inside);
                var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format));
                var rowCount = xml.Descendants().Count(element => element.Name.LocalName == (format == DocumentFormat.Docx ? "tr" : "table-row"));
                Require(rowCount == 6, $"{format}: two outer and four inner rows are produced.");
                Require(xml.Root!.Value.Split("Apple", StringSplitOptions.None).Length == 3
                    && xml.Root.Value.Split("Pear", StringSplitOptions.None).Length == 3,
                    $"{format}: both inner values appear once per outer row.");
                if (format == DocumentFormat.Odt)
                {
                    var names = xml.Descendants().Where(element => element.Name.LocalName == "table")
                        .Select(element => element.Attributes().Single(attribute => attribute.Name.LocalName == "name").Value).ToArray();
                    Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "ODT: repeated nested tables have unique names.");
                }
            }
        }
    }

    private static void RepeatsBlockRegions()
    {
        var source = PortableDocument.Create()
            .Paragraph("{{Repeat:Groups}}")
            .Paragraph("Group {{Group}}")
            .Paragraph("{{Repeat:Items}}")
            .Paragraph("Item {{Item}} in {{Group}}")
            .Paragraph("{{EndRepeat}}")
            .Paragraph("{{EndRepeat}}")
            .Paragraph("Tail")
            .Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray())
                .RepeatRegion("Groups", [new Dictionary<string, string?> { ["Group"] = "A" }, new Dictionary<string, string?> { ["Group"] = "B" }])
                .RepeatRegion("Items", [new Dictionary<string, string?> { ["Item"] = "Apple" }, new Dictionary<string, string?> { ["Item"] = "{{Group}}" }]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            var text = xml.Root!.Value;
            Require(!result.HasWarnings, $"{format}: valid repeated regions have no warnings.");
            var compactText = text.Replace(" ", string.Empty, StringComparison.Ordinal);
            Require(compactText.Contains("GroupA", StringComparison.Ordinal) && compactText.Contains("GroupB", StringComparison.Ordinal), $"{format}: outer rows are bound.");
            Require(text.Split("Apple", StringSplitOptions.None).Length == 3 && text.Split("{{Group}}", StringSplitOptions.None).Length == 3,
                $"{format}: nested rows repeat and literal markers remain literal.");
            Require(!text.Contains("{{Repeat:", StringComparison.Ordinal) && !text.Contains("{{EndRepeat}}", StringComparison.Ordinal),
                $"{format}: region markers are removed.");

            var empty = DocumentTemplate.FromBytes(buffer.ToArray()).RepeatRegion("Groups", []).RepeatRegion("Items", []);
            var emptyText = System.Xml.Linq.XDocument.Parse(WriteTemplate(empty, format)).Root!.Value;
            Require(!emptyText.Contains("Group", StringComparison.Ordinal) && emptyText.Contains("Tail", StringComparison.Ordinal),
                $"{format}: an empty repeat removes its region.");
        }
    }

    private static void RepeatsRowRegions()
    {
        var source = PortableDocument.Create().Table(table => table.Row("Header").Row("{{Repeat:Rows}}")
            .Row("{{Name}}").Row("second {{Name}}").Row("{{EndRepeat}}")).Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray()).RepeatRegion("Rows",
                [new Dictionary<string, string?> { ["Name"] = "Apple" }, new Dictionary<string, string?> { ["Name"] = "Pear" }]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            var rowCount = xml.Descendants().Count(element => element.Name.LocalName == (format == DocumentFormat.Docx ? "tr" : "table-row"));
            Require(!result.HasWarnings && rowCount == 5, $"{format}: a repeat region produces four rows and a header.");
        }
    }

    private static void ReportsUnsafeRepeatRegions()
    {
        // ODT cannot rename a cross-paragraph bookmark range per clone, so it reports the region;
        // DOCX now clones the range with fresh names (OF-D03).
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("{{Repeat:Rows}}")), new Paragraph(new TextRun("{{Name}}")),
            new Paragraph(new Inline[] { new Bookmark("Cross"), new TextRun("Target") }),
            new Paragraph(new Inline[] { new TextRun("More"), new BookmarkEnd("Cross") }),
            new Paragraph(new TextRun("{{EndRepeat}}")),
        }));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray()).BindText("Name", "changed").RepeatRegion("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var content = WriteTemplate(template, format, out var result);
            if (format == DocumentFormat.Docx)
            {
                Require(!result.HasWarnings, "DOCX: a cross-paragraph bookmark region is cloned.");
                Require(content.Contains("changed", StringComparison.Ordinal), "DOCX: the cloned region is bound.");
                Require(CountOccurrences(content, "bookmarkStart") == 2 && CountOccurrences(content, "bookmarkEnd") == 2,
                    "DOCX: both clones keep their bookmark start and end.");
                continue;
            }

            Require(result.Warnings.Any(warning => warning.Message.Contains("cannot safely be duplicated", StringComparison.Ordinal)),
                $"{format}: unsafe region content is reported.");
            Require(content.Contains("{{Name}}", StringComparison.Ordinal) && !content.Contains("changed", StringComparison.Ordinal),
                $"{format}: the unexpanded region is not partially bound.");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Self-contained content repeats: a complete field (DOCX) and a table (ODT, OF-D03).</summary>
    private static void RepeatsSelfContainedRegions()
    {
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("{{Repeat:Rows}}")),
            new Paragraph(new Inline[] { new TextRun("Page "), new Field("PAGE") }),
            new TableBlock([new TableRow(new TableCell("{{Name}}"))]),
            new Paragraph(new TextRun("{{EndRepeat}}")),
        }));
        var rows = new[]
        {
            new Dictionary<string, string?> { ["Name"] = "Apple" },
            new Dictionary<string, string?> { ["Name"] = "Pear" },
        };

        using var docxBuffer = new MemoryStream();
        DocumentWriter.WriteAsync(source, docxBuffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        var docxTemplate = DocumentTemplate.FromBytes(docxBuffer.ToArray()).RepeatRegion("Rows", rows);
        var docx = System.Xml.Linq.XDocument.Parse(WriteTemplate(docxTemplate, DocumentFormat.Docx, out var docxResult));
        Require(!docxResult.HasWarnings, "DOCX: a region with a complete field and a table repeats: " + string.Join("; ", docxResult.Warnings));
        var fields = docx.Descendants().Count(element => element.Name.LocalName == "fldChar"
            && (string?)element.Attribute(System.Xml.Linq.XName.Get("fldCharType", element.Name.NamespaceName)) == "begin");
        Require(fields == 2, $"DOCX: both clones carry a complete field, got {fields}.");

        using var odtBuffer = new MemoryStream();
        DocumentWriter.WriteAsync(source, odtBuffer, DocumentFormat.Odt).GetAwaiter().GetResult();
        var odtTemplate = DocumentTemplate.FromBytes(odtBuffer.ToArray()).RepeatRegion("Rows", rows);
        var odt = System.Xml.Linq.XDocument.Parse(WriteTemplate(odtTemplate, DocumentFormat.Odt, out var odtResult));
        Require(!odtResult.HasWarnings, "ODT: a region with a table repeats: " + string.Join("; ", odtResult.Warnings));
        var tables = odt.Descendants().Where(element => element.Name.LocalName == "table").ToList();
        Require(tables.Count == 2, $"ODT: both clones keep their table, got {tables.Count}.");
        var names = tables.Select(table => (string?)table.Attributes().First(attribute => attribute.Name.LocalName == "name")).ToList();
        Require(names.Distinct(StringComparer.Ordinal).Count() == names.Count, "ODT: copied tables have unique names.");
        Require(odt.Root!.Value.Contains("Apple", StringComparison.Ordinal) && odt.Root.Value.Contains("Pear", StringComparison.Ordinal),
            "ODT: both clones are bound.");
    }

    /// <summary>An ODT note carries its body, so a region with a note repeats there (OF-D03).</summary>
    private static void RepeatsOdtNoteRegion()
    {
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("{{Repeat:Rows}}")),
            new Paragraph(new Inline[] { new TextRun("{{Name}}"), new Footnote(new Paragraph(new TextRun("Note"))) }),
            new Paragraph(new TextRun("{{EndRepeat}}")),
        }));
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(source, buffer, DocumentFormat.Odt).GetAwaiter().GetResult();
        var template = DocumentTemplate.FromBytes(buffer.ToArray()).RepeatRegion("Rows",
            [new Dictionary<string, string?> { ["Name"] = "Apple" }, new Dictionary<string, string?> { ["Name"] = "Pear" }]);
        var content = WriteTemplate(template, DocumentFormat.Odt, out var result);
        Require(!result.HasWarnings, "ODT: a region with a note repeats: " + string.Join("; ", result.Warnings));
        var xml = System.Xml.Linq.XDocument.Parse(content);
        var notes = xml.Descendants().Where(element => element.Name.LocalName == "note").ToList();
        Require(notes.Count == 2, $"ODT: both clones keep their note, got {notes.Count}.");
        var ids = notes.Select(note => (string?)note.Attributes().First(attribute => attribute.Name.LocalName == "id")).ToList();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Count, "ODT: copied notes have unique ids: " + string.Join(", ", ids));
        Require((xml.Root!.Value.Split("Note", StringSplitOptions.None).Length - 1) == 2, "ODT: both note bodies are copied.");

        // A DOCX note is cloned with a fresh id and a duplicated note body in the notes part.
        using var docxBuffer = new MemoryStream();
        DocumentWriter.WriteAsync(source, docxBuffer, DocumentFormat.Docx).GetAwaiter().GetResult();
        var docxTemplate = DocumentTemplate.FromBytes(docxBuffer.ToArray()).RepeatRegion("Rows",
            [new Dictionary<string, string?> { ["Name"] = "Apple" }, new Dictionary<string, string?> { ["Name"] = "Pear" }]);
        var docxContent = WriteTemplate(docxTemplate, DocumentFormat.Docx, out var docxResult);
        Require(!docxResult.HasWarnings, "DOCX: a region with a note repeats: " + string.Join("; ", docxResult.Warnings));
        var docxXml = System.Xml.Linq.XDocument.Parse(docxContent);
        var references = docxXml.Descendants().Where(element => element.Name.LocalName == "footnoteReference").ToList();
        Require(references.Count == 2, $"DOCX: both clones keep their note reference, got {references.Count}.");
        var referenceIds = references.Select(reference => (string?)reference.Attributes().First(attribute => attribute.Name.LocalName == "id")).ToList();
        Require(referenceIds.Distinct(StringComparer.Ordinal).Count() == referenceIds.Count,
            "DOCX: copied note references have unique ids: " + string.Join(", ", referenceIds));
        Require(docxContent.Split("Apple", StringSplitOptions.None).Length == 2
                && docxContent.Split("Pear", StringSplitOptions.None).Length == 2,
            "DOCX: both clones are bound.");
    }

    private static void RepeatsImageRegions()
    {
        var image = new ImageBlock(TestImages.RgbPng(4, 2, 0, 0, 255), ImageMediaType.Png) { AltText = "A picture" };
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("{{Repeat:Rows}}")), image, new Paragraph(new TextRun("{{EndRepeat}}")),
        }));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(source, buffer, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(buffer.ToArray()).RepeatRegion("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result, out var package));
            Require(!result.HasWarnings, $"{format}: simple images in repeated regions are supported.");
            if (format == DocumentFormat.Docx)
            {
                System.Xml.Linq.XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
                var ids = xml.Descendants(wp + "docPr").Select(element => (string?)element.Attribute("id")).ToArray();
                Require(ids.Length == 2 && ids.Distinct(StringComparer.Ordinal).Count() == 2, "DOCX: repeated pictures have distinct drawing IDs.");
            }
            else
            {
                System.Xml.Linq.XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
                var frames = xml.Descendants(draw + "frame").ToArray();
                Require(frames.Length == 2 && frames.Count(frame => frame.Attribute(draw + "name") is not null) == 1,
                    "ODT: repeated images do not duplicate frame names.");
            }

            var read = DocumentReader.Read(new MemoryStream(package), format).Document;
            Require(read.Sections.SelectMany(section => section.Blocks).OfType<ImageBlock>().Count() == 2,
                $"{format}: both repeated images survive reading.");
        }
    }

    private static void RepeatsImageRowsSafely()
    {
        var image = new ImageBlock(TestImages.RgbPng(4, 2, 0, 0, 255), ImageMediaType.Png);
        var table = new TableBlock([new TableRow(new TableCell(new Block[] { new Paragraph(new TextRun("{{Rows}}")), image }))]);
        var source = new PortableDocument(new DocumentMetadata(), new Section(table));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray()).RepeatRows("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            Require(!result.HasWarnings, $"{format}: a row with a simple image is safely repeated.");
            if (format == DocumentFormat.Docx)
            {
                System.Xml.Linq.XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
                var ids = xml.Descendants(wp + "docPr").Select(element => (string?)element.Attribute("id")).ToArray();
                Require(ids.Length == 2 && ids.Distinct(StringComparer.Ordinal).Count() == 2, "DOCX: repeated row pictures have unique IDs.");
            }
            else
            {
                System.Xml.Linq.XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
                var frames = xml.Descendants(draw + "frame").ToArray();
                Require(frames.Length == 2 && frames.Count(frame => frame.Attribute(draw + "name") is not null) == 1,
                    "ODT: repeated row pictures do not duplicate frame names.");
            }

            // Content that is not self-contained stays reported: in ODT a bookmark range that spans
            // paragraphs (OF-D03). A DOCX note is cloned with a fresh id and body.
            var unsafeCell = format == DocumentFormat.Docx
                ? new TableCell(new Paragraph(new Inline[]
                {
                    new TextRun("{{Rows}}{{Name}}"), new Footnote(new Paragraph(new TextRun("Note"))),
                }))
                : new TableCell(new Block[]
                {
                    new Paragraph(new Inline[] { new TextRun("{{Rows}}{{Name}}"), new Bookmark("Cross") }),
                    new Paragraph(new Inline[] { new TextRun("more"), new BookmarkEnd("Cross") }),
                });
            var bookmarkTable = new TableBlock([new TableRow(unsafeCell)]);
            using var unsafeInput = new MemoryStream();
            DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(bookmarkTable)), unsafeInput, format).GetAwaiter().GetResult();
            var unsafeTemplate = DocumentTemplate.FromBytes(unsafeInput.ToArray()).BindText("Name", "changed").RepeatRows("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var unsafeOutput = WriteTemplate(unsafeTemplate, format, out var unsafeResult);
            if (format == DocumentFormat.Docx)
            {
                Require(!unsafeResult.HasWarnings, "DOCX: a repeated row with a note repeats safely: " + string.Join("; ", unsafeResult.Warnings));
                var unsafeXml = System.Xml.Linq.XDocument.Parse(unsafeOutput);
                Require(unsafeXml.Descendants().Count(element => element.Name.LocalName == "footnoteReference") == 2,
                    "DOCX: both repeated rows keep their own note reference.");
                Require(unsafeOutput.Contains("changed", StringComparison.Ordinal), "DOCX: the repeated row is bound.");
            }
            else
            {
                Require(unsafeResult.Warnings.Any(warning => warning.Message.Contains("cannot safely be duplicated", StringComparison.Ordinal))
                    && unsafeOutput.Contains("{{Name}}", StringComparison.Ordinal) && !unsafeOutput.Contains("changed", StringComparison.Ordinal),
                    "ODT: an unsafe repeated row is reported and remains unbound.");
            }
        }
    }

    private static void RepeatsBookmarkRegions()
    {
        var source = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("{{Repeat:Rows}}")),
            new Paragraph(new Inline[] { new Bookmark("Target"), new TextRun("destination") }),
            new Paragraph(new Hyperlink("#Target", "jump")),
            new Paragraph(new TextRun("{{EndRepeat}}")),
        }));
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray()).RepeatRegion("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            Require(!result.HasWarnings, $"{format}: local bookmarks can be repeated.");
            System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            System.Xml.Linq.XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
            System.Xml.Linq.XNamespace xlink = "http://www.w3.org/1999/xlink";
            var names = format == DocumentFormat.Docx
                ? xml.Descendants(w + "bookmarkStart").Select(node => (string?)node.Attribute(w + "name")).ToArray()
                : xml.Descendants(text + "bookmark").Select(node => (string?)node.Attribute(text + "name")).ToArray();
            var targets = format == DocumentFormat.Docx
                ? xml.Descendants(w + "hyperlink").Select(node => (string?)node.Attribute(w + "anchor")).ToArray()
                : xml.Descendants(text + "a").Select(node => ((string?)node.Attribute(xlink + "href"))?[1..]).ToArray();
            Require(names.Length == 2 && names[0] != names[1] && names.SequenceEqual(targets), $"{format}: each link points to its own copied bookmark.");
            if (format == DocumentFormat.Docx)
            {
                var starts = xml.Descendants(w + "bookmarkStart").Select(node => (string?)node.Attribute(w + "id")).ToArray();
                var ends = xml.Descendants(w + "bookmarkEnd").Select(node => (string?)node.Attribute(w + "id")).ToArray();
                Require(starts.Distinct().Count() == 2 && starts.SequenceEqual(ends), "DOCX: bookmark IDs are unique and paired.");
            }

            var markerSource = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
            {
                new Paragraph(new Inline[] { new Bookmark("Marker"), new TextRun("{{Repeat:Rows}}") }),
                new Paragraph(new TextRun("{{Name}}")), new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
            using var markerInput = new MemoryStream();
            DocumentWriter.WriteAsync(markerSource, markerInput, format).GetAwaiter().GetResult();
            var markerTemplate = DocumentTemplate.FromBytes(markerInput.ToArray()).BindText("Name", "changed").RepeatRegion("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var markerOutput = WriteTemplate(markerTemplate, format, out var markerResult);
            Require(markerResult.HasWarnings && markerOutput.Contains("{{Name}}", StringComparison.Ordinal)
                && markerOutput.Contains("Marker", StringComparison.Ordinal), $"{format}: bookmark-bearing markers are preserved.");

            var rowSource = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([
                new TableRow(new TableCell(new Paragraph(new Inline[]
                {
                    new TextRun("{{Rows}}"), new Bookmark("RowTarget"), new Hyperlink("#RowTarget", "jump"),
                }))),
            ])));
            using var rowInput = new MemoryStream();
            DocumentWriter.WriteAsync(rowSource, rowInput, format).GetAwaiter().GetResult();
            var rowTemplate = DocumentTemplate.FromBytes(rowInput.ToArray()).RepeatRows("Rows",
                [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
            var rowXml = System.Xml.Linq.XDocument.Parse(WriteTemplate(rowTemplate, format, out var rowResult));
            var rowNames = format == DocumentFormat.Docx
                ? rowXml.Descendants(w + "bookmarkStart").Select(node => (string?)node.Attribute(w + "name")).ToArray()
                : rowXml.Descendants(text + "bookmark").Select(node => (string?)node.Attribute(text + "name")).ToArray();
            var rowTargets = format == DocumentFormat.Docx
                ? rowXml.Descendants(w + "hyperlink").Select(node => (string?)node.Attribute(w + "anchor")).ToArray()
                : rowXml.Descendants(text + "a").Select(node => ((string?)node.Attribute(xlink + "href"))?[1..]).ToArray();
            Require(!rowResult.HasWarnings && rowNames.Length == 2 && rowNames[0] != rowNames[1] && rowNames.SequenceEqual(rowTargets),
                $"{format}: repeated rows preserve their own bookmark links.");

            if (format == DocumentFormat.Docx)
            {
                var content = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(input.ToArray(), "word/document.xml")));
                var start = content.Descendants(w + "bookmarkStart").Single();
                var end = content.Descendants(w + "bookmarkEnd").Single();
                var nextParagraph = start.Ancestors(w + "p").First().ElementsAfterSelf(w + "p").First();
                end.Remove();
                nextParagraph.Add(end);
                var package = TestPackages.ReplacePart(input.ToArray(), "word/document.xml", System.Text.Encoding.UTF8.GetBytes(content.ToString()));
                var crossParagraph = DocumentTemplate.FromBytes(package).RepeatRegion("Rows",
                    [new Dictionary<string, string?>(), new Dictionary<string, string?>()]);
                var preserved = WriteTemplate(crossParagraph, format, out var crossResult);
                Require(!crossResult.HasWarnings && !preserved.Contains("{{Repeat:Rows}}", StringComparison.Ordinal),
                    "DOCX: a cross-paragraph bookmark range is cloned.");
                var crossXml = System.Xml.Linq.XDocument.Parse(preserved);
                var crossNames = crossXml.Descendants(w + "bookmarkStart").Select(node => (string?)node.Attribute(w + "name")).ToArray();
                Require(crossNames.Length == 2 && crossNames[0] != crossNames[1]
                        && crossXml.Descendants(w + "bookmarkEnd").Count() == 2,
                    "DOCX: each clone of a cross-paragraph range has its own start and end.");
            }
        }
    }

    private static void EnforcesRepeatRegionLimits()
    {
        var source = PortableDocument.Create().Paragraph("{{Repeat:Rows}}").Paragraph("{{Name}}")
            .Paragraph("{{EndRepeat}}").Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray()).RepeatRegion("Rows",
                [new Dictionary<string, string?> { ["Name"] = "12345" }, new Dictionary<string, string?> { ["Name"] = "ok" }]);
            var document = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
            foreach (var limits in new[] { new ResourceLimits { MaxBlocks = 1 }, new ResourceLimits { MaxTextLength = 4 } })
            {
                using var output = new MemoryStream();
                WriteOptions options = format == DocumentFormat.Docx
                    ? new DocxWriteOptions { Template = template, Limits = limits }
                    : new OdtWriteOptions { Template = template, Limits = limits };
                Throws<DocumentValidationException>(() => DocumentWriter.WriteAsync(document, output, format, options).GetAwaiter().GetResult());
                Require(output.Length == 0, $"{format}: limit failure occurs before output is written.");
            }
        }
    }

    private static void KeepsRepeatBindingsIndependent()
    {
        var source = PortableDocument.Create().Paragraph("{{Repeat:Rows}}").Paragraph("Block {{Name}}")
            .Paragraph("{{EndRepeat}}").Table(table => table.Row("{{Rows}}{{Name}}"))
            .Paragraph("{{Repeat:Label}}").Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray())
                .RepeatRegion("Rows", [new Dictionary<string, string?> { ["Name"] = "region" }])
                .RepeatRows("Rows", [new Dictionary<string, string?> { ["Name"] = "row" }])
                .BindText("Repeat:Label", "ordinary placeholder");
            var output = WriteTemplate(template, format, out var result);
            var text = System.Xml.Linq.XDocument.Parse(output).Root!.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            Require(!result.HasWarnings && text.Contains("region", StringComparison.Ordinal)
                && text.Contains("row", StringComparison.Ordinal) && text.Contains("ordinaryplaceholder", StringComparison.Ordinal),
                $"{format}: row repeats, region repeats and ordinary placeholders stay independent.");
        }
    }

    private static void RemovesDuplicateWordParagraphIds()
    {
        var source = PortableDocument.Create().Paragraph("{{Repeat:Rows}}").Paragraph("{{Name}}")
            .Paragraph("{{EndRepeat}}").Build();
        var package = TestPackages.Write(source, DocumentFormat.Docx);
        var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        System.Xml.Linq.XNamespace w14 = "http://schemas.microsoft.com/office/word/2010/wordml";
        var paragraphs = xml.Descendants(w + "p").ToArray();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            paragraphs[index].SetAttributeValue(w14 + "paraId", (index + 1).ToString("X8"));
            paragraphs[index].SetAttributeValue(w14 + "textId", "00000001");
        }

        package = TestPackages.ReplacePart(package, "word/document.xml", System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
        var template = DocumentTemplate.FromBytes(package).RepeatRegion("Rows",
            [new Dictionary<string, string?> { ["Name"] = "one" }, new Dictionary<string, string?> { ["Name"] = "two" }]);
        var output = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, DocumentFormat.Docx, out var result));
        var ids = output.Descendants(w + "p").Select(p => (string?)p.Attribute(w14 + "paraId")).Where(id => id is not null).ToArray();
        Require(!result.HasWarnings && ids.Length == 1 && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
            "DOCX: repeated paragraphs do not duplicate Word paragraph IDs.");
        Require(output.Descendants(w + "p").All(p => p.Attribute(w14 + "textId") is null || p.Attribute(w14 + "paraId") is not null),
            "DOCX: text IDs do not remain without paragraph IDs.");
    }

    private static void RepeatsPerParentData()
    {
        static TemplateRegionRow Item(string value) => new(new Dictionary<string, string?> { ["Item"] = value });
        static TemplateRegionRow Group(string name, IReadOnlyList<TemplateRegionRow>? items)
            => new(new Dictionary<string, string?> { ["Group"] = name }, items is null ? null
                : new Dictionary<string, IReadOnlyList<TemplateRegionRow>> { ["Items"] = items });

        var groups = new[]
        {
            Group("A", [Item("Apple"), Item("{{Group}}")]),
            Group("B", [Item("Pear")]),
            Group("C", []),
            Group("D", null),
        };
        var source = PortableDocument.Create().Paragraph("{{Repeat:Groups}}")
            .Paragraph("Group={{Group}}")
            .Paragraph("{{Repeat:Items}}")
            .Paragraph("Item={{Item}};Group={{Group}}")
            .Paragraph("{{EndRepeat}}")
            .Paragraph("{{EndRepeat}}").Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray())
                .RepeatRegion("Items", [new Dictionary<string, string?> { ["Item"] = "Fallback" }])
                .RepeatNestedRegion("Groups", groups);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            var text = xml.Root!.Value;
            Require(!result.HasWarnings, $"{format}: valid nested data has no warnings.");
            Require(text.Contains("Item=Apple;Group=A", StringComparison.Ordinal)
                && text.Contains("Item={{Group}};Group=A", StringComparison.Ordinal)
                && text.Contains("Item=Pear;Group=B", StringComparison.Ordinal), $"{format}: child values use the correct parent.");
            Require(!text.Contains("Item=Pear;Group=A", StringComparison.Ordinal)
                && !text.Contains("Item=Apple;Group=B", StringComparison.Ordinal)
                && !text.Contains("Item=Fallback;Group=C", StringComparison.Ordinal)
                && text.Contains("Item=Fallback;Group=D", StringComparison.Ordinal),
                $"{format}: empty nested data stays empty and absent nested data falls back to the shared set.");

            var emptyDocument = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
            using var output = new MemoryStream();
            WriteOptions options = format == DocumentFormat.Docx
                ? new DocxWriteOptions { Template = template }
                : new OdtWriteOptions { Template = template };
            DocumentWriter.WriteAsync(emptyDocument, output, format, options).GetAwaiter().GetResult();
            output.Position = 0;
            var read = DocumentReader.Read(output, format).Document;
            Require(DocumentSearch.Find(read, new TextQuery("Apple")).Count == 1
                && DocumentSearch.Find(read, new TextQuery("Pear")).Count == 1
                && DocumentSearch.Find(read, new TextQuery("Fallback")).Count == 1,
                $"{format}: parent-specific data survives a write/read cycle.");
        }
    }

    private static void ValidatesNestedRegionData()
    {
        var childValues = new Dictionary<string, string?> { ["Qty"] = "3" };
        var child = new TemplateRegionRow(childValues);
        var parent = new TemplateRegionRow(new Dictionary<string, string?>(),
            new Dictionary<string, IReadOnlyList<TemplateRegionRow>> { ["Items"] = [child] });
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var bytes = TextTemplate("{{Repeat:Groups}}", format);
            var template = DocumentTemplate.FromBytes(bytes).Declare("Qty", TemplatePlaceholderType.Number)
                .RepeatNestedRegion("Groups", [parent]);
            childValues["Qty"] = "invalid";
            Throws<ArgumentException>(() => WriteTemplate(template, format));
            Throws<ArgumentException>(() => DocumentTemplate.FromBytes(bytes).RepeatNestedRegion("Groups", [parent])
                .Declare("Qty", TemplatePlaceholderType.Number));
            childValues["Qty"] = "3";
        }
    }

    private static void RepeatsPerParentTableRows()
    {
        static TemplateRegionRow Group(string name, params string[] items)
            => new(new Dictionary<string, string?> { ["Group"] = name },
                new Dictionary<string, IReadOnlyList<TemplateRegionRow>>
                {
                    ["Items"] = items.Select(item => new TemplateRegionRow(new Dictionary<string, string?> { ["Item"] = item })).ToArray(),
                });

        var source = PortableDocument.Create().Table(table => table
            .Row("{{Repeat:Groups}}")
            .Row("Group={{Group}}")
            .Row("{{Repeat:Items}}")
            .Row("Item={{Item}};Group={{Group}}")
            .Row("{{EndRepeat}}")
            .Row("{{EndRepeat}}")).Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray()).RepeatNestedRegion("Groups",
                [Group("A", "Apple", "Plum"), Group("B", "Pear")]);
            var xml = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result));
            var rows = xml.Descendants().Where(element => element.Name.LocalName == (format == DocumentFormat.Docx ? "tr" : "table-row"))
                .Select(element => element.Value).ToArray();
            Require(!result.HasWarnings && rows.Length == 5, $"{format}: parent-specific nested data creates five rows.");
            Require(rows[1].Contains("Item=Apple;Group=A", StringComparison.Ordinal)
                && rows[2].Contains("Item=Plum;Group=A", StringComparison.Ordinal)
                && rows[4].Contains("Item=Pear;Group=B", StringComparison.Ordinal), $"{format}: nested rows remain under the correct parent.");
        }
    }

    private static void RepeatsThreeDataLevels()
    {
        static TemplateRegionRow Row(string field, string value, string? childName = null, params TemplateRegionRow[] children)
            => new(new Dictionary<string, string?> { [field] = value }, childName is null ? null
                : new Dictionary<string, IReadOnlyList<TemplateRegionRow>> { [childName] = children });
        var data = new[]
        {
            Row("Group", "A", "Items", Row("Item", "X", "Options", Row("Option", "red"), Row("Option", "blue"))),
            Row("Group", "B", "Items", Row("Item", "Y", "Options", Row("Option", "small"))),
        };
        var source = PortableDocument.Create().Paragraph("{{Repeat:Groups}}")
            .Paragraph("{{Repeat:Items}}")
            .Paragraph("{{Repeat:Options}}")
            .Paragraph("{{Group}}/{{Item}}/{{Option}}")
            .Paragraph("{{EndRepeat}}")
            .Paragraph("{{EndRepeat}}")
            .Paragraph("{{EndRepeat}}").Build();
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var input = new MemoryStream();
            DocumentWriter.WriteAsync(source, input, format).GetAwaiter().GetResult();
            var template = DocumentTemplate.FromBytes(input.ToArray()).RepeatNestedRegion("Groups", data);
            var text = System.Xml.Linq.XDocument.Parse(WriteTemplate(template, format, out var result)).Root!.Value;
            Require(!result.HasWarnings && text.Contains("A/X/red", StringComparison.Ordinal)
                && text.Contains("A/X/blue", StringComparison.Ordinal) && text.Contains("B/Y/small", StringComparison.Ordinal)
                && !text.Contains("B/X/", StringComparison.Ordinal), $"{format}: three levels retain their own data scopes.");
        }
    }

    private static void ScansPlainRegionSiblingsLinearly()
    {
        var root = new System.Xml.Linq.XElement("root",
            Enumerable.Range(0, 3000).Select(_ => new System.Xml.Linq.XElement("p", "plain")));
        var calls = 0;
        bool Candidate(System.Xml.Linq.XElement element)
        {
            calls++;
            return element.Name.LocalName == "p";
        }

        var repeats = new Dictionary<string, IReadOnlyList<TemplateRegionRow>> { ["Unused"] = [] };
        var expander = new TemplateRegionExpander(repeats, new Dictionary<string, TemplateContent>(), Candidate, _ => false,
            element => element.DescendantNodes().OfType<System.Xml.Linq.XText>(),
            element => element.DescendantNodes().OfType<System.Xml.Linq.XText>(),
            (_, _) => false, _ => { }, value => value, value => value.Length,
            message => throw new InvalidOperationException(message), ResourceLimits.Default);
        expander.Expand(root);
        Require(calls <= 12000, $"Repeat region scanning visits plain siblings linearly; got {calls} checks.");
    }

    private static string ReadDocumentText(string path, DocumentFormat format)
    {
        using var zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
        var entry = zip.GetEntry(format == DocumentFormat.Docx ? "word/document.xml" : "content.xml")
            ?? throw new InvalidOperationException("Part missing.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
