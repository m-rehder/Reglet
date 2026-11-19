using System.IO.Compression;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// templates as Word and LibreOffice write them: placeholders inside sentences, numbers in local
/// notation, conditions inside cells and repeats without rows.
/// </summary>
internal static class TemplateFidelityChecks
{
    private const string TextNamespace = "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"";
    private const string TableNamespace = "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"";
    private static readonly IReadOnlyDictionary<string, string?>[] NoRows = [];

    internal static int Run()
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Action)[]
        {
            ("space before a placeholder", KeepsSpaceBeforePlaceholder),
            ("numbers", ReadsAndFormatsNumbers),
            ("template language", UsesTemplateLanguage),
            ("casing", AppliesCasing),
            ("condition in a cell", RemovesConditionInsideCell),
            ("rows", RemovesTableWithoutRows),
            ("cell end", KeepsParagraphInCell),
            ("line breaks", WritesLineBreaksAndTabs),
            ("native regions", ConvertsNativeRegionsInOneParagraph),
            ("unbalanced native region", KeepsUnbalancedNativeRegion),
            ("section break", KeepsSectionBreakInRemovedCondition),
            ("odt page width", ReadsTemplatePageWidth),
            ("odt note ids", GivesInsertedNotesFreeIds),
            ("odt table in list", MovesTableOutOfListItem),
            ("signatures", RemovesTemplateSignature),
            ("odt bookmarks", RenamesCollidingBookmark),
        })
        {
            try
            {
                check();
            }
            catch (Exception exception)
            {
                failures.Add(name + ": " + exception.Message);
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("Template fidelity: " + string.Join(" || ", failures));
            return 1;
        }

        Console.WriteLine("Template fidelity: all checks passed.");
        return 0;
    }

    private static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    private static string OP(string text) => $"<text:p {TextNamespace}>{text}</text:p>";

    private static string Cell(string content)
        => $"<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid><w:tr><w:tc>{content}</w:tc></w:tr></w:tbl>";

    private static byte[] OdtWithStyles(string inner, Func<string, string> styles)
    {
        var template = Template(DocumentFormat.Odt, inner);
        using var buffer = new MemoryStream();
        buffer.Write(template);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("styles.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("styles.xml").Open());
            writer.Write(styles(xml));
        }

        return buffer.ToArray();
    }

    private static TableBlock SmallTable(string text) => new([new TableRow(new TableCell(text))]);

    private static void ReadsTemplatePageWidth()
    {
        foreach (var value in new[] { "5", "x", "1e400cm", "NaNcm" })
        {
            Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(OdtWithStyles(OP("{{T}}"), xml => Regex.Replace(xml, "fo:page-width=\"[^\"]*\"", $"fo:page-width=\"{value}\""))).Bind("T", SmallTable("x")));
        }

        // A narrow layout before the one the page style uses must not squeeze the table.
        var narrow = OdtWithStyles(OP("{{T}}"), xml =>
        {
            var layout = Regex.Match(xml, "<style:page-layout style:name=\"pm1\".*?</style:page-layout>", RegexOptions.Singleline).Value;
            return xml.Replace(layout, Regex.Replace(layout.Replace("pm1", "narrow"), "fo:page-width=\"[^\"]*\"", "fo:page-width=\"5cm\"") + layout, StringComparison.Ordinal);
        });
        var width = Regex.Match(Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(narrow).Bind("T", SmallTable("x"))), "column-width=\"([\\d.]+)cm\"").Groups[1].Value;
        Require(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture) > 10, "the table has the width of the page style.");
    }

    private static void GivesInsertedNotesFreeIds()
    {
        var note = $"<text:note {TextNamespace} text:id=\"ftn1\" text:note-class=\"footnote\"><text:note-citation>1</text:note-citation><text:note-body><text:p>n</text:p></text:note-body></text:note>";
        var template = Template(DocumentFormat.Odt, OP("a") + $"<text:p {TextNamespace}>x{note}</text:p>" + OP("{{B}}"));
        var xml = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(template).Bind("B", new Paragraph(new Inline[] { new TextRun("y"), new Footnote([new Paragraph(new TextRun("new"))]) })));
        var ids = Regex.Matches(xml, "text:note [^>]*text:id=\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToList();
        Require(ids.Count == 2 && ids[0] != ids[1], "every note has its own id.");
    }

    private static void MovesTableOutOfListItem()
    {
        var item = (string text) => $"<text:list-item><text:p>{text}</text:p></text:list-item>";
        var template = Template(DocumentFormat.Odt, $"<text:list {TextNamespace}>{item("before")}{item("{{T}}")}{item("after")}</text:list>");
        var xml = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(template).Bind("T", SmallTable("INSIDE")));
        Require(!Regex.IsMatch(xml, "<text:list-item[^>]*>\\s*<table:table"), "a list item holds no table.");
        Require(xml.IndexOf("before", StringComparison.Ordinal) < xml.IndexOf("INSIDE", StringComparison.Ordinal)
            && xml.IndexOf("INSIDE", StringComparison.Ordinal) < xml.IndexOf("after", StringComparison.Ordinal), "the order stays.");

        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(
            new PortableDocument(new DocumentMetadata(), new Section(new ListBlock("Num", [new ListItem([new Paragraph(new TextRun("a")), SmallTable("MODEL")]), new ListItem([new Paragraph(new TextRun("b"))])])))
            {
                Numbering = [new NumberingDefinition("Num", [new NumberingLevel { Kind = ListKind.Number }])],
            },
            output, DocumentFormat.Odt).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        using var reader = new StreamReader(zip.GetEntry("content.xml")!.Open());
        var written = reader.ReadToEnd();
        Require(written.Contains("MODEL", StringComparison.Ordinal) && !Regex.IsMatch(written, "</text:p><table:table"), "the writer does not nest a table in a list item either.");
    }

    private static byte[] WithEntries(byte[] package, params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        buffer.Write(package);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    private static bool Has(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.GetEntry(name) is not null;
    }

    private static void RemovesTemplateSignature()
    {
        var odt = WithEntries(Template(DocumentFormat.Odt, OP("{{T}}")), ("META-INF/documentsignatures.xml", "<x/>"), ("Thumbnails/thumbnail.png", "x"));
        using (var output = new MemoryStream())
        {
            var result = DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>())), output, DocumentFormat.Odt, new OdtWriteOptions { Template = DocumentTemplate.FromBytes(odt).BindText("T", "v") }).GetAwaiter().GetResult();
            Require(!Has(output.ToArray(), "META-INF/documentsignatures.xml") && !Has(output.ToArray(), "Thumbnails/thumbnail.png"), "signature and preview of the template are gone.");
            Require(result.Warnings.Any(warning => warning.Message.Contains("signature", StringComparison.Ordinal)), "the removal is reported.");
        }

        var docx = WithEntries(Template(DocumentFormat.Docx, P("{{T}}")), ("_xmlsignatures/sig1.xml", "<x/>"));
        using (var output = new MemoryStream())
        {
            DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>())), output, DocumentFormat.Docx, new DocxWriteOptions { Template = DocumentTemplate.FromBytes(docx).BindText("T", "v") }).GetAwaiter().GetResult();
            Require(!Has(output.ToArray(), "_xmlsignatures/sig1.xml"), "the DOCX signature is gone.");
        }

        var read = DocumentReader.Read(new MemoryStream(docx), DocumentFormat.Docx).Document;
        using (var output = new MemoryStream())
        {
            var result = DocumentWriter.WriteAsync(read with { Sections = [read.Sections[0] with { Blocks = [new Paragraph(new TextRun("changed"))] }] }, output, DocumentFormat.Docx).GetAwaiter().GetResult();
            Require(!Has(output.ToArray(), "_xmlsignatures/sig1.xml") && result.Warnings.Any(warning => warning.Message.Contains("signature", StringComparison.Ordinal)), "a changed document loses the signature with a warning.");
        }
    }

    private static void RenamesCollidingBookmark()
    {
        var template = Template(DocumentFormat.Odt, $"<text:p {TextNamespace}><text:bookmark text:name=\"b\"/>x</text:p>" + OP("{{B}}"));
        var xml = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(template).Bind("B", new Paragraph(new Inline[] { new Bookmark("b"), new TextRun("y") })));
        Require(Regex.Matches(xml, "text:bookmark[^>]*text:name=\"b\"").Count == 1, "a name exists once.");
    }

    private static string OCell(string content)
        => $"<table:table {TableNamespace}><table:table-column/><table:table-row><table:table-cell>{content}</table:table-cell></table:table-row></table:table>";

    private static string Field(string instruction)
        => $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>{instruction}</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>r</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    private static string Part(DocumentFormat format) => format == DocumentFormat.Docx ? "word/document.xml" : "content.xml";

    private static byte[] Template(DocumentFormat format, string inner, string? language = null)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata { Language = language }, new Section(new Paragraph(new TextRun("start")))), buffer, format).GetAwaiter().GetResult();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(Part(format))!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            entry.Delete();
            var at = format == DocumentFormat.Docx ? xml.LastIndexOf("<w:sectPr", StringComparison.Ordinal) : xml.IndexOf("</office:text>", StringComparison.Ordinal);
            using var writer = new StreamWriter(zip.CreateEntry(Part(format)).Open());
            writer.Write(xml[..at] + inner + xml[at..]);
        }

        return buffer.ToArray();
    }

    private static string Write(DocumentFormat format, DocumentTemplate template, out IReadOnlyList<string> warnings, string? language = null)
    {
        using var output = new MemoryStream();
        WriteOptions options = format == DocumentFormat.Docx ? new DocxWriteOptions { Template = template } : new OdtWriteOptions { Template = template };
        var result = DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata { Language = language }, new Section(Array.Empty<Block>())), output, format, options).GetAwaiter().GetResult();
        warnings = result.Warnings.Select(warning => warning.Message).ToList();
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        using var reader = new StreamReader(zip.GetEntry(Part(format))!.Open());
        return reader.ReadToEnd();
    }

    private static string Write(DocumentFormat format, DocumentTemplate template) => Write(format, template, out _);

    private static string Visible(string xml) => Regex.Replace(xml, "<w:instrText[^>]*>[^<]*</w:instrText>", string.Empty) is var text ? Regex.Replace(text, "<[^>]+>", " ") : xml;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void KeepsSpaceBeforePlaceholder()
    {
        var xml = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, P("Hello {{Name}}!"))).BindText("Name", "Ada"));
        Require(xml.Contains("xml:space=\"preserve\">Hello <", StringComparison.Ordinal), "The space before the value is preserved.");
    }

    private static void ReadsAndFormatsNumbers()
    {
        foreach (var (value, picture, language, expected) in new (string, string, string?, string?)[]
        {
            ("12,50", "0.00", null, "12.50"),
            ("1234.5", "#.##0,00", "de-DE", "1.234,50"),
            ("1234.5", "#,##0.00", "en-US", "1,234.50"),
            ("1,234", "0.00", "en-US", "1234.00"),
            ("1,234", "0.00", "de-DE", "1.23"),
            ("1.234,5", "#,##0.00", null, "1,234.50"),
            ("12", "0%", null, "12%"),
            ("1234", "'EUR' #,##0", null, "EUR 1,234"),
            ("1,234", "0.00", null, null),
            ("1,23,4", "0.00", null, null),
        })
        {
            var field = $"<w:p>{Field($" MERGEFIELD A \\# &quot;{picture}&quot; ")}</w:p>";
            var xml = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, field)).BindText("A", value), out _, language);
            var text = Visible(xml);
            Require(expected is null ? xml.Contains("MERGEFIELD", StringComparison.Ordinal) : text.Contains(" " + expected + " ", StringComparison.Ordinal) || text.Contains(">" + expected, StringComparison.Ordinal),
                $"{value} with '{picture}' ({language}) gave '{text.Trim()}'.");
        }
    }

    private static void UsesTemplateLanguage()
    {
        var field = $"<w:p>{Field(" MERGEFIELD A \\# &quot;0.00&quot; ")}</w:p>";
        var text = Visible(Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, field, "de-DE")).BindText("A", "1,234")));
        Require(text.Contains("1.23", StringComparison.Ordinal), $"The language of the template decides, got '{text.Trim()}'.");
    }

    private static void AppliesCasing()
    {
        var context = FieldContext.Create(mergeValues: new Dictionary<string, string> { ["A"] = "hans MEIER" });
        Require(FieldEvaluator.Evaluate("MERGEFIELD A \\* Caps", context) == "Hans MEIER", "Caps capitalizes each word.");
        Require(FieldEvaluator.Evaluate("MERGEFIELD A \\* FirstCap", context) == "Hans MEIER", "FirstCap keeps the other letters.");
    }

    private static void RemovesConditionInsideCell()
    {
        var docx = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, Cell(P("{{If:no}}") + P("SECRET") + P("{{EndIf}}")))).Condition("no", false));
        Require(!docx.Contains("SECRET", StringComparison.Ordinal), "DOCX: a false condition in a cell removes its content.");
        Require(Regex.IsMatch(docx, "<w:p\\s*/>\\s*</w:tc>|</w:p>\\s*</w:tc>"), "DOCX: the cell still ends with a paragraph.");
        var odt = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(Template(DocumentFormat.Odt, OCell(OP("{{If:no}}") + OP("SECRET") + OP("{{EndIf}}")))).Condition("no", false));
        Require(!odt.Contains("SECRET", StringComparison.Ordinal), "ODT: a false condition in a cell removes its content.");
    }

    private static void RemovesTableWithoutRows()
    {
        var docx = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, Cell(P("{{T}}x")))).RepeatRows("T", NoRows));
        Require(!docx.Contains("<w:tbl", StringComparison.Ordinal), "DOCX: a table without rows is removed.");
        var odt = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(Template(DocumentFormat.Odt, OCell(OP("{{T}}x")))).RepeatRows("T", NoRows));
        Require(!odt.Contains("<table:table ", StringComparison.Ordinal), "ODT: a table without rows is removed.");
    }

    private static void KeepsParagraphInCell()
    {
        var xml = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, Cell(P("{{Repeat:R}}") + P("x") + P("{{EndRepeat}}")))).RepeatRegion("R", NoRows));
        Require(!Regex.IsMatch(xml, "<w:tc\\s*/>|<w:tc>\\s*</w:tc>"), "DOCX: an emptied cell keeps a paragraph.");
    }

    private static void WritesLineBreaksAndTabs()
    {
        var rows = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = "one\ntwo\tthree" } };
        using var plain = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("one\r\ntwo\tthree")))), plain, DocumentFormat.Docx).GetAwaiter().GetResult();
        using (var zip = new ZipArchive(new MemoryStream(plain.ToArray())))
        using (var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open()))
        {
            var xml = reader.ReadToEnd();
            Require(Regex.Matches(xml, "<w:br\\s*/>").Count == 1 && xml.Contains("<w:tab", StringComparison.Ordinal), "DOCX: a text run writes w:br and w:tab.");
        }

        var region = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, P("{{Repeat:R}}") + P("{{F}}") + P("{{EndRepeat}}"))).RepeatRegion("R", rows));
        Require(region.Contains("<w:br", StringComparison.Ordinal) && !region.Contains("one\n", StringComparison.Ordinal), "DOCX: a repeated value writes w:br.");
        var odt = Write(DocumentFormat.Odt, DocumentTemplate.FromBytes(Template(DocumentFormat.Odt, OP("{{Repeat:R}}") + OP("{{F}}") + OP("{{EndRepeat}}"))).RepeatRegion("R", rows));
        Require(odt.Contains("text:line-break", StringComparison.Ordinal) && odt.Contains("text:tab", StringComparison.Ordinal), "ODT: a repeated value writes line breaks and tabs.");
    }

    private static void ConvertsNativeRegionsInOneParagraph()
    {
        var rows = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = "v1" }, new Dictionary<string, string?> { ["F"] = "v2" } };
        var paragraph = $"<w:p>{Field(" MERGEFIELD TableStart:R ")}<w:r><w:t>{{{{F}}}}</w:t></w:r>{Field(" MERGEFIELD TableEnd:R ")}</w:p>";
        var text = Visible(Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, Cell(paragraph))).RepeatRegion("R", rows)));
        Require(text.Contains("v1", StringComparison.Ordinal) && text.Contains("v2", StringComparison.Ordinal) && !text.Contains("Repeat", StringComparison.Ordinal), $"Both markers in one paragraph work, got '{text.Trim()}'.");
    }

    private static void KeepsUnbalancedNativeRegion()
    {
        var rows = new[] { (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["F"] = "v1" } };
        var paragraph = "<w:p><w:fldSimple w:instr=\" MERGEFIELD TableStart:R \"><w:r><w:t>r</w:t></w:r></w:fldSimple></w:p>";
        var xml = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, Cell(paragraph))).RepeatRegion("R", rows), out var warnings);
        Require(xml.Contains("TableStart", StringComparison.Ordinal) && !xml.Contains("{{Repeat", StringComparison.Ordinal), "An unbalanced marker stays a field.");
        Require(warnings.Any(warning => warning.Contains("no matching", StringComparison.Ordinal)), "An unbalanced marker is reported.");
    }

    private static void KeepsSectionBreakInRemovedCondition()
    {
        var inner = P("{{If:no}}") + "<w:p><w:pPr><w:sectPr><w:pgSz w:w=\"5000\" w:h=\"5000\"/></w:sectPr></w:pPr></w:p>" + P("{{EndIf}}");
        var xml = Write(DocumentFormat.Docx, DocumentTemplate.FromBytes(Template(DocumentFormat.Docx, inner)).Condition("no", false), out var warnings);
        Require(Regex.Matches(xml, "<w:sectPr").Count == 2, "The section break stays.");
        Require(warnings.Any(warning => warning.Contains("section break", StringComparison.Ordinal)), "The kept section break is reported.");
    }
}
