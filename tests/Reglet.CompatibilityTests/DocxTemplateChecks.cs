using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// checks the filling of DOCX templates (phase S, S02): inline and block placeholders,
/// body placeholder, preservation of styles and headers as well as lists and images in
/// bound content.
/// </summary>
internal static class DocxTemplateChecks
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    internal static int Run()
    {
        try
        {
            BindsInlinePlaceholder();
            BindsMultipleInlineRuns();
            BindsPlaceholderSplitAcrossRuns();
            BindsBlockPlaceholderWithStyle();
            BindsBodyPlaceholder();
            KeepsCharacterStylesInTemplate();
            KeepsTableStylesInTemplate();
            BindsBodyPlaceholderPerSection();
            MergesBodyWhenSectionsDoNotMatch();
            AppendsWithoutBodyPlaceholder();
            PreservesTemplateParts();
            BindsHeadersAndFooters();
            BindsFirstAndEvenBands();
            BindsTableAndRepeatedRegionInBands();
            BindsHyperlinksInBodyAndBands();
            BindsWordMergeFields();
            BindsNativeWordRegions();
            BindsNativeWordRegionInTable();
            BindsContentControlInRepeatedRegion();
            ClonesBookmarkRangeAcrossParagraphs();
            ClonesComplexDrawingInRegion();
            ClonesTrackedChangesInRegion();
            ClonesFootnoteInRegion();
            ClonesNestedNoteInRegion();
            ClonesNoteCommentInRegion();
            ClonesCommentInRepeatedRegion();
            ReportsSectionInRepeatedRegion();
            FormatsWordMergeFields();
            FormatsAdditionalWordMergeSwitches();
            FormatsNumericWordMergeFields();
            AddsConditionalWordMergeText();
            BindsNestedSimpleWordField();
            BindsNestedComplexWordField();
            BindsListInBlockContent();
            BindsImageInBlockContent();
            RepeatsTableRows();
            KeepsPlaceholdersInsideRowValues();
            NestsConditions();
            ReportsUnboundPlaceholder();
            ReportsBoundPlaceholderMissingFromTemplate();
            ReportsMissingBodyPlaceholder();
            KeepsTemplateBookmarkIdsDistinct();
            ReportsUnsetCondition();
            BindsBlockConditions();
            BindsPlaceholderStartingWithIf();
            RejectsTemplateWithoutDocumentPart();
            RejectsInvalidTemplateBytes();
            Console.WriteLine("DOCX templates (S02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DOCX templates (S02): " + exception);
            return 1;
        }
    }

    private static void BindsInlinePlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph(p => p.Text("Date: ").Bold("{{Date}}"))
            .Build());

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("Date", "1. October 2026"),
            },
            out var package);

        Require(!result.HasWarnings, "Inline binding produces no warning.");

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{Date}}"), "The placeholder is replaced.");
        var run = FindRun(document, "1. October 2026");
        Require(run?.Element(W + "rPr")?.Element(W + "b") is not null, "The inserted run inherits the bold run style.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsMultipleInlineRuns()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph(p => p.Text("Date: ").Text("{{Date}}"))
            .Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Bind(
                    "Date",
                    new Inline[] { new TextRun("1. "), new TextRun("October") { Bold = true } }),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{Date}}"), "The placeholder is replaced.");
        Require(ContainsText(document, "1."), "The first run is present.");
        Require(ContainsText(document, "October"), "The second run is present.");
        Require(
            FindRun(document, "October")?.Element(W + "rPr")?.Element(W + "b") is not null,
            "The second run is bold.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsPlaceholderSplitAcrossRuns()
    {
        // Word/LibreOffice often split placeholders arbitrarily across several runs.
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph(p => p.Text("Date: ").Text("{{Da").Bold("te}}"))
            .Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("Date", "1. October"),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{"), "A placeholder split across runs is replaced.");
        Require(ContainsText(document, "October"), "The inserted text is present.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsBlockPlaceholderWithStyle()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Heading("{{Title}}", 1).Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes)
                    .Bind("Title", new Paragraph(new TextRun("New title"))),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{Title}}"), "The placeholder is replaced.");

        var paragraph = document.Descendants(W + "p").FirstOrDefault(p => p.Value.Contains("New title", StringComparison.Ordinal));
        Require(paragraph is not null, "The inserted paragraph is present.");
        Require(
            (string?)paragraph!.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") == "Heading1",
            "The inserted paragraph inherits the template's heading style.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph("header text")
            .Paragraph("{{Content}}")
            .Paragraph("Footer text")
            .Build());

        var document = PortableDocument.Create().Paragraph("First").Paragraph("Second").Build();

        Write(
            document,
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Body("Content"),
            },
            out var package);

        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        Require(!body.Contains("{{Content}}", StringComparison.Ordinal), "The body placeholder is replaced.");
        Require(body.Contains("First", StringComparison.Ordinal) && body.Contains("Second", StringComparison.Ordinal), "Both paragraphs are inserted.");
        Require(
            body.IndexOf("header text", StringComparison.Ordinal) < body.IndexOf("First", StringComparison.Ordinal)
            && body.IndexOf("First", StringComparison.Ordinal) < body.IndexOf("Footer text", StringComparison.Ordinal),
            "The content stands between header and footer text.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsBodyPlaceholderPerSection()
    {
        var templateDocument = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("{{Content}}"))),
            new Section(new Paragraph(new TextRun("{{Content}}"))),
        });
        var templateBytes = CreateTemplate(templateDocument);
        var document = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("FirstSection"))),
            new Section(new Paragraph(new TextRun("SecondSection"))),
        });

        var result = Write(
            document,
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Body("Content"),
            },
            out var package);

        using var zip = Open(package);
        var xml = ReadDocument(zip);
        var body = xml.Root!.Element(W + "body")!;
        var children = body.Elements().ToList();
        var breakIndex = children.FindIndex(element => element.Name == W + "p"
            && element.Element(W + "pPr")?.Element(W + "sectPr") is not null);
        Require(breakIndex >= 0, "The template keeps its section break.");

        string Text(IEnumerable<XElement> elements)
            => string.Concat(elements.SelectMany(element => element.Descendants(W + "t")).Select(text => text.Value));

        var first = Text(children.Take(breakIndex + 1));
        var second = Text(children.Skip(breakIndex + 1));
        Require(first.Contains("FirstSection", StringComparison.Ordinal) && !first.Contains("SecondSection", StringComparison.Ordinal),
            "The first model section is bound into the first template section.");
        Require(second.Contains("SecondSection", StringComparison.Ordinal) && !second.Contains("FirstSection", StringComparison.Ordinal),
            "The second model section is bound into the second template section.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("merged into one area", StringComparison.Ordinal)),
            "A template with one body placeholder per section is not reported as merged.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void MergesBodyWhenSectionsDoNotMatch()
    {
        var templateBytes = CreateTemplate(new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("{{Content}}"))),
            new Section(new Paragraph(new TextRun("Static second section"))),
        }));
        var document = new PortableDocument(new DocumentMetadata(), new[]
        {
            new Section(new Paragraph(new TextRun("FirstSection"))),
            new Section(new Paragraph(new TextRun("SecondSection"))),
        });

        var result = Write(
            document,
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        Require(body.Contains("FirstSection", StringComparison.Ordinal) && body.Contains("SecondSection", StringComparison.Ordinal),
            "Without a placeholder per section, both model sections are inserted into the single body placeholder.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("merged into one area", StringComparison.Ordinal)),
            "A template without a body placeholder per section reports the merged content.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void AppendsWithoutBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("Template").Build());

        Write(
            PortableDocument.Create().Paragraph("Appended").Build(),
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out var package);

        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        Require(body.Contains("Template", StringComparison.Ordinal), "The template text is preserved.");
        Require(body.Contains("Appended", StringComparison.Ordinal), "The document content is appended.");
        Require(
            body.IndexOf("Template", StringComparison.Ordinal) < body.IndexOf("Appended", StringComparison.Ordinal),
            "The appended content stands after the template text.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void PreservesTemplateParts()
    {
        var section = new Section(new Paragraph(new TextRun("{{Content}}")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Letterhead"))),
        };
        var templateBytes = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));

        Write(
            PortableDocument.Create().Paragraph("Text").Build(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Body("Content"),
            },
            out var package);

        using var zip = Open(package);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("word/header1.xml"), "The template's header is preserved.");
        Require(ReadText(zip, "word/header1.xml").Contains("Letterhead", StringComparison.Ordinal), "The header text is preserved.");
        Require(ReadText(zip, "word/styles.xml").Contains("Heading1", StringComparison.Ordinal), "The template's styles are preserved.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsHeadersAndFooters()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("{{Na"), new TextRun("me}}") })),
            Footer = new HeaderFooter(new Paragraph(new TextRun("{{Image}}"))),
        };
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));
        var template = DocumentTemplate.FromBytes(source).BindText("Name", "Ada")
            .Bind("Image", new ImageBlock(CreatePng(24, 12), ImageMediaType.Png));
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);
        using var zip = Open(package);
        Require(ReadText(zip, "word/header1.xml").Contains("Ada", StringComparison.Ordinal), "A split header placeholder is bound.");
        Require(ReadText(zip, "word/footer1.xml").Contains("drawing", StringComparison.Ordinal), "A footer block image is inserted.");
        Require(ReadText(zip, "word/_rels/footer1.xml.rels").Contains("media/image", StringComparison.Ordinal), "Footer images have their own relationship.");
        Require(!result.HasWarnings, "Header and footer bindings produce no warnings.");
        Require(Valid(package).Count == 0, "Header and footer template package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsFirstAndEvenBands()
    {
        var section = new Section(new Paragraph(new TextRun("Body")))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("{{Default}}"))),
            FirstHeader = new HeaderFooter(new Paragraph(new TextRun("{{First}}"))),
            EvenHeader = new HeaderFooter(new Paragraph(new TextRun("{{Even}}"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("{{DefaultFooter}}"))),
            FirstFooter = new HeaderFooter(new Paragraph(new TextRun("{{FirstFooter}}"))),
            EvenFooter = new HeaderFooter(new Paragraph(new TextRun("{{EvenFooter}}"))),
        };
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));
        var template = DocumentTemplate.FromBytes(source)
            .BindText("Default", "Alpha")
            .BindText("First", "Beta")
            .BindText("Even", "Gamma")
            .BindText("DefaultFooter", "Delta")
            .BindText("FirstFooter", "Epsilon")
            .BindText("EvenFooter", "Zeta");
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "First/even band bindings produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var bands = string.Concat(zip.Entries
            .Where(entry => entry.FullName.StartsWith("word/header", StringComparison.Ordinal)
                || entry.FullName.StartsWith("word/footer", StringComparison.Ordinal))
            .Select(entry => ReadText(zip, entry.FullName)));
        foreach (var text in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta" })
        {
            Require(bands.Contains(text, StringComparison.Ordinal), $"The bound text '{text}' is present in a band.");
        }

        Require(!bands.Contains("{{", StringComparison.Ordinal), "No band placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsTableAndRepeatedRegionInBands()
    {
        var header = new HeaderFooter(new Block[]
        {
            new TableBlock(new[] { new TableRow(new TableCell("{{Cell}}")) }),
            new Paragraph(new TextRun("{{Repeat:Rows}}")),
            new Paragraph(new TextRun("{{Name}}")),
            new Paragraph(new TextRun("{{EndRepeat}}")),
        });
        var section = new Section(new Paragraph(new TextRun("Body"))) { Header = header };
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));
        var template = DocumentTemplate.FromBytes(source)
            .BindText("Cell", "CellValue")
            .RepeatRegion("Rows", new[]
            {
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Alice" },
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Bob" },
            });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Band table and region bindings produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var bands = string.Concat(zip.Entries
            .Where(entry => entry.FullName.StartsWith("word/header", StringComparison.Ordinal)
                || entry.FullName.StartsWith("word/footer", StringComparison.Ordinal))
            .Select(entry => ReadText(zip, entry.FullName)));
        Require(bands.Contains("CellValue", StringComparison.Ordinal), "A table cell placeholder in a band is bound.");
        Require(bands.Contains("Alice", StringComparison.Ordinal) && bands.Contains("Bob", StringComparison.Ordinal), "A repeated region in a band is expanded.");
        Require(!bands.Contains("{{", StringComparison.Ordinal), "No band placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsHyperlinksInBodyAndBands()
    {
        var section = new Section(new Paragraph(new Inline[]
        {
            new TextRun("{{BodyLink}} "),
            new TextRun("{{Internal}}"),
        }))
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("{{HeaderLink}}"))),
        };
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));
        var template = DocumentTemplate.FromBytes(source)
            .Bind("BodyLink", new Hyperlink("https://body.example/", "body"))
            .Bind("Internal", new Hyperlink("#Target", "jump"))
            .Bind("HeaderLink", new Hyperlink("https://header.example/", "header"));
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Hyperlink bindings produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        var header = ReadText(zip, "word/header1.xml");
        var bodyRels = ReadText(zip, "word/_rels/document.xml.rels");
        var headerRels = ReadText(zip, "word/_rels/header1.xml.rels");
        Require(body.Contains("w:hyperlink", StringComparison.Ordinal) && bodyRels.Contains("https://body.example/", StringComparison.Ordinal), "A body hyperlink has its relationship.");
        Require(body.Contains("w:anchor=\"Target\"", StringComparison.Ordinal), "An internal body link uses w:anchor.");
        Require(!bodyRels.Contains("#Target", StringComparison.Ordinal), "An internal link needs no relationship.");
        Require(header.Contains("w:hyperlink", StringComparison.Ordinal) && headerRels.Contains("https://header.example/", StringComparison.Ordinal), "A header hyperlink has its own relationship.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsWordMergeFields()
    {
        var section = new Section(new Paragraph(new Inline[]
        {
            new TextRun("Hello "), new Field(" MERGEFIELD Name ", "«Name»"), new TextRun("; "),
            new Field(" MERGEFIELD Unknown ", "«Unknown»"), new TextRun("; "),
            new Field(" MERGEFIELD Name \\* Roman ", "«Name»"), new TextRun("; "),
            new Field(" MERGEFIELD EmptyResult "), new TextRun("; {{NewField}}"),
        }))
        {
            Header = new HeaderFooter(new Paragraph(new Field(" MERGEFIELD Name ", "«Name»"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("simple"))),
        };
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), section));
        var bodyXml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var instruction = bodyXml.Descendants(W + "instrText").First();
        instruction.Value = " MERGE";
        instruction.Parent!.AddAfterSelf(new XElement(W + "r", new XElement(W + "instrText", "FIELD Name ")));
        source = TestPackages.ReplacePart(source, "word/document.xml", Encoding.UTF8.GetBytes(bodyXml.ToString()));
        var footer = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/footer1.xml")));
        var run = footer.Descendants(W + "r").First();
        run.ReplaceWith(new XElement(W + "fldSimple", new XAttribute(W + "instr", " MERGEFIELD \"Full Name\" \\* MERGEFORMAT "),
            new XElement(W + "r", new XElement(W + "rPr", new XElement(W + "b")), new XElement(W + "t", "«Full Name»"))));
        source = TestPackages.ReplacePart(source, "word/footer1.xml", Encoding.UTF8.GetBytes(footer.ToString()));
        var template = DocumentTemplate.FromBytes(source).BindText("name", "{{literal}}")
            .BindText("Full Name", "Ada").BindText("EmptyResult", "set")
            .Bind("NewField", new Field(" MERGEFIELD Name ", "inserted"));
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);
        using var zip = Open(package);
        var body = ReadDocument(zip);
        var text = string.Concat(body.Descendants(W + "t").Select(node => node.Value));
        Require(text.Contains("Hello {{literal}}", StringComparison.Ordinal) && text.Contains("set", StringComparison.Ordinal), "DOCX: complex fields are replaced without interpreting values as placeholders.");
        Require(body.Descendants(W + "instrText").Count() == 3 && text.Contains("inserted", StringComparison.Ordinal),
            "DOCX: missing, unsupported and newly inserted fields remain intact.");
        Require(result.Warnings.Count >= 2, "DOCX: unbound and unsupported merge fields are reported.");
        Require(ReadText(zip, "word/header1.xml").Contains("{{literal}}", StringComparison.Ordinal), "DOCX: complex merge fields in headers are bound.");
        var footerXml = XDocument.Parse(ReadText(zip, "word/footer1.xml"));
        Require(FindRun(footerXml, "Ada")?.Element(W + "rPr")?.Element(W + "b") is not null, "DOCX: simple merge fields preserve their run style.");
        Require(!footerXml.Descendants(W + "fldSimple").Any(), "DOCX: bound simple fields are replaced.");
        Require(Valid(package).Count == 0, "Merge field template package is valid: " + string.Join("; ", Valid(package)));
        var rejected = false;
        try
        {
            Write(EmptyDocument(), source, new DocxWriteOptions { Template = template, UnsupportedContent = UnsupportedContentBehavior.Fail }, out _);
        }
        catch (UnsupportedFeatureException)
        {
            rejected = true;
        }
        Require(rejected, "DOCX: unsupported merge fields fail in strict mode.");
    }

    private static void BindsNativeWordRegions()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("Title")),
                new Paragraph(new Field(" MERGEFIELD TableStart:Rows \\* MERGEFORMAT ", "")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new Field(" MERGEFIELD TableEnd:Rows ", "")),
                new Paragraph(new TextRun("Footer")),
            }));
        var source = CreateTemplate(document);
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Alice" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Bob" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A native paragraph region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var body = ReadDocument(zip);
        var text = string.Concat(body.Descendants(W + "t").Select(node => node.Value));
        Require(text.Contains("Title", StringComparison.Ordinal) && text.Contains("Footer", StringComparison.Ordinal), "The surrounding paragraphs survive.");
        Require(text.Contains("Alice", StringComparison.Ordinal) && text.Contains("Bob", StringComparison.Ordinal), "The native paragraph region is expanded.");
        Require(!text.Contains("TableStart", StringComparison.Ordinal) && !text.Contains("TableEnd", StringComparison.Ordinal), "The native region markers are removed.");
        Require(!text.Contains("{{", StringComparison.Ordinal), "No placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsNativeWordRegionInTable()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("Name"), new TableCell("Qty") }) { IsHeader = true },
            new TableRow(new[]
            {
                new TableCell(new Paragraph(new Field(" MERGEFIELD TableStart:Rows \\* MERGEFORMAT ", ""))),
                new TableCell("{{Name}}"),
                new TableCell("{{Qty}}"),
                new TableCell(new Paragraph(new Field(" MERGEFIELD TableEnd:Rows ", ""))),
            }),
        });
        var source = CreateTemplate(new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { table, new Paragraph(new TextRun("After")) })));
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Alice", ["Qty"] = "3" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Bob", ["Qty"] = "5" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A native table region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        var rows = document.Descendants(W + "tr").Count();
        Require(rows == 3, "The table region repeats the row once per data row, got " + rows + " rows.");
        var text = string.Concat(document.Descendants(W + "t").Select(node => node.Value));
        Require(text.Contains("Alice", StringComparison.Ordinal) && text.Contains("Bob", StringComparison.Ordinal), "The table values are bound.");
        Require(text.Contains("3", StringComparison.Ordinal) && text.Contains("5", StringComparison.Ordinal), "The second column is bound.");
        Require(!text.Contains("TableStart", StringComparison.Ordinal) && !text.Contains("TableEnd", StringComparison.Ordinal), "The native region markers are removed.");
        Require(!text.Contains("{{", StringComparison.Ordinal), "No placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsContentControlInRepeatedRegion()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("Name") }) { IsHeader = true },
            new TableRow(new[] { new TableCell("{{Repeat:Rows}}") }),
            new TableRow(new[] { new TableCell("{{Name}}") }),
            new TableRow(new[] { new TableCell("{{EndRepeat}}") }),
        });
        var source = CreateTemplate(new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { table })));

        // Word writes a content control around the repeated field; the binder must clone it with a
        // fresh w:id instead of rejecting the region.
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var paragraph = xml.Descendants(W + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        var run = paragraph.Elements(W + "r").Single();
        run.Remove();
        paragraph.Add(new XElement(
            W + "sdt",
            new XElement(W + "sdtPr", new XElement(W + "id", new XAttribute(W + "val", "42"))),
            new XElement(W + "sdtContent", run)));
        source = TestPackages.ReplacePart(source, "word/document.xml", Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Alice" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Bob" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A content control in a repeated region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        var controls = document.Descendants(W + "sdt").ToList();
        Require(controls.Count == 2, "The content control is cloned for both data rows.");
        var ids = controls
            .Select(control => (string?)control.Element(W + "sdtPr")?.Element(W + "id")?.Attribute(W + "val"))
            .ToList();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == 2, "Each cloned content control has a fresh ID.");
        var text = string.Concat(document.Descendants(W + "t").Select(node => node.Value));
        Require(text.Contains("Alice", StringComparison.Ordinal) && text.Contains("Bob", StringComparison.Ordinal), "The repeated values are bound.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesBookmarkRangeAcrossParagraphs()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new Inline[] { new Bookmark("Range"), new TextRun("first") }),
                new Paragraph(new Inline[] { new TextRun("second"), new BookmarkEnd("Range") }),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(document);
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A cross-paragraph bookmark region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var body = ReadDocument(zip);
        var starts = body.Descendants(W + "bookmarkStart").Select(node => (string?)node.Attribute(W + "name")).ToList();
        var ends = body.Descendants(W + "bookmarkEnd").Count();
        Require(starts.Count == 2 && ends == 2, "The bookmark range is cloned for both rows.");
        Require(starts.Distinct(StringComparer.Ordinal).Count() == 2, "Each clone has a distinct bookmark name.");
        Require(ContainsText(body, "first") && ContainsText(body, "second"), "Both paragraphs of the region are cloned.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesComplexDrawingInRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(sourceDocument);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        XNamespace w = W;
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        var paragraph = xml.Descendants(w + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        var text = new XElement(
            w + "txbxContent",
            new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", "Box"))));
        var shape = new XElement(
            wps + "wsp",
            new XElement(wps + "cNvPr", new XAttribute("id", "8"), new XAttribute("name", "textbox")),
            new XElement(wps + "txbx", text));
        var graphic = new XElement(
            a + "graphic",
            new XElement(a + "graphicData", new XAttribute("uri", wps.NamespaceName), shape));
        var drawing = new XElement(
            w + "drawing",
            new XElement(
                wp + "inline",
                new XElement(wp + "docPr", new XAttribute("id", "7"), new XAttribute("name", "shape")),
                graphic));
        paragraph.Add(new XElement(w + "r", drawing));
        source = TestPackages.ReplacePart(
            source,
            "word/document.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A complex drawing in a region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(document.Descendants(W + "drawing").Count() == 2, "The complex drawing is cloned for both rows.");
        var drawingIds = document.Descendants()
            .Where(element => element.Name == wp + "docPr" || element.Name.LocalName == "cNvPr")
            .Select(element => (string?)element.Attribute("id"))
            .ToList();
        Require(drawingIds.Count == 4 && drawingIds.Distinct(StringComparer.Ordinal).Count() == 4,
            "Every docPr and cNvPr of the clones has its own id.");
        Require(ContainsText(document, "Box") && ContainsText(document, "one") && ContainsText(document, "two"),
            "The drawing and the row values are both present.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesTrackedChangesInRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(sourceDocument);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var paragraph = xml.Descendants(W + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        paragraph.Add(
            new XElement(
                W + "ins",
                new XAttribute(W + "id", "5"),
                new XAttribute(W + "author", "Author"),
                new XAttribute(W + "date", "2020-01-01T00:00:00Z"),
                new XElement(W + "r", new XElement(W + "t", "inserted"))),
            new XElement(
                W + "del",
                new XAttribute(W + "id", "6"),
                new XAttribute(W + "author", "Author"),
                new XAttribute(W + "date", "2020-01-01T00:00:00Z"),
                new XElement(W + "r", new XElement(W + "delText", "deleted"))));
        source = TestPackages.ReplacePart(
            source,
            "word/document.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Tracked changes in a region produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(document.Descendants(W + "ins").Count() == 2 && document.Descendants(W + "del").Count() == 2,
            "The tracked insertion and deletion are cloned for both rows.");
        var ids = document.Descendants()
            .Where(element => element.Name == W + "ins" || element.Name == W + "del")
            .Select(element => (string?)element.Attribute(W + "id"))
            .ToList();
        Require(ids.Count == 4 && ids.Distinct(StringComparer.Ordinal).Count() == 4,
            "Each cloned tracked change has its own revision id.");
        Require(ContainsText(document, "inserted") && ContainsText(document, "one") && ContainsText(document, "two"),
            "The inserted text and both row values are present.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesFootnoteInRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new Inline[]
                {
                    new TextRun("{{Name}}"),
                    new Footnote(new Paragraph(new Inline[] { new Bookmark("NoteTarget"), new TextRun("Note text") })),
                }),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(sourceDocument);
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A footnote in a repeated region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        var references = document.Descendants(W + "footnoteReference")
            .Select(node => (string?)node.Attribute(W + "id"))
            .ToList();
        Require(references.Count == 2 && references.Distinct(StringComparer.Ordinal).Count() == 2,
            "Both rows have their own footnote reference.");
        var notes = XDocument.Parse(ReadText(zip, "word/footnotes.xml"));
        Require(notes.Descendants(W + "footnote").Any(note => (string?)note.Attribute(W + "id") == "1")
                && notes.Descendants(W + "footnote").Any(note => (string?)note.Attribute(W + "id") == "2"),
            "The template's note and the cloned note are both present.");
        Require(notes.Descendants(W + "t").Count(text => text.Value == "Note text") == 2,
            "The note body is duplicated once per row.");
        var bookmarkNames = notes.Descendants(W + "bookmarkStart").Select(bookmark => (string?)bookmark.Attribute(W + "name")).ToList();
        Require(bookmarkNames.Count == 2 && bookmarkNames.Distinct(StringComparer.Ordinal).Count() == 2,
            "A bookmark inside the note gets a unique name in the clone.");
        var bookmarkIds = notes.Descendants(W + "bookmarkStart").Concat(notes.Descendants(W + "bookmarkEnd"))
            .Select(bookmark => (string?)bookmark.Attribute(W + "id")).ToList();
        Require(bookmarkIds.Count == 4 && bookmarkIds.Distinct(StringComparer.Ordinal).Count() == 2,
            "The copied note bookmark has its own start/end id pair.");
        Require(ContainsText(document, "one") && ContainsText(document, "two"), "The row values are bound.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesNestedNoteInRegion()
    {
        var outer = new Footnote(new Paragraph(new Inline[]
        {
            new TextRun("outer "),
            new Footnote(new Paragraph(new TextRun("inner"))),
        }));
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new Inline[] { new TextRun("{{Name}}"), outer }),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(sourceDocument);
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A nested note in a repeated region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var notes = XDocument.Parse(ReadText(zip, "word/footnotes.xml"));
        Require(notes.Descendants(W + "t").Count(text => text.Value == "outer ") == 2
                && notes.Descendants(W + "t").Count(text => text.Value == "inner") == 2,
            "The outer and the nested note body are duplicated for both rows.");
        var nestedReferences = notes.Descendants(W + "footnoteReference")
            .Select(reference => (string?)reference.Attribute(W + "id"))
            .ToList();
        Require(nestedReferences.Count == 2 && nestedReferences.Distinct(StringComparer.Ordinal).Count() == 2,
            "The nested note references also get their own ids.");
        Require(notes.Descendants(W + "footnote").Select(note => (string?)note.Attribute(W + "id"))
                .Distinct(StringComparer.Ordinal).Count() == notes.Descendants(W + "footnote").Count(),
            "The notes part has no duplicate note ids.");
        var document = ReadDocument(zip);
        Require(document.Descendants(W + "footnoteReference").Select(reference => (string?)reference.Attribute(W + "id"))
                .Distinct(StringComparer.Ordinal).Count() == 2,
            "Both rows reference their own outer note.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesNoteCommentInRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new Inline[]
                {
                    new TextRun("{{Name}}"),
                    new Footnote(new Paragraph(new TextRun("Note text"))),
                }),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = AddNoteComment(CreateTemplate(sourceDocument));
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A comment in a repeated note produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var comments = XDocument.Parse(ReadText(zip, "word/comments.xml"));
        var commentIds = comments.Descendants(W + "comment")
            .Select(comment => (string?)comment.Attribute(W + "id"))
            .ToList();
        Require(commentIds.Count == 2 && commentIds.Distinct(StringComparer.Ordinal).Count() == 2,
            "The note comment is duplicated with a fresh id.");
        Require(comments.Descendants(W + "t").Count(text => text.Value == "A comment") == 2,
            "The comment body is duplicated once per note.");
        var notes = XDocument.Parse(ReadText(zip, "word/footnotes.xml"));
        var noteComments = notes.Descendants(W + "commentReference")
            .Select(reference => (string?)reference.Attribute(W + "id"))
            .ToList();
        Require(noteComments.Count == 2 && noteComments.Distinct(StringComparer.Ordinal).Count() == 2,
            "Both notes point at their own comment.");
        var document = ReadDocument(zip);
        Require(document.Descendants(W + "footnoteReference")
                .Select(reference => (string?)reference.Attribute(W + "id"))
                .Distinct(StringComparer.Ordinal).Count() == 2,
            "Both rows reference their own note.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static byte[] AddNoteComment(byte[] package)
    {
        var withComments = AddCommentsPart(package);
        var notes = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(withComments, "word/footnotes.xml")));
        var note = notes.Descendants(W + "footnote")
            .First(footnote => (string?)footnote.Attribute(W + "id") == "1");
        var paragraph = note.Elements(W + "p").Last();
        paragraph.Add(new XElement(
            W + "r",
            new XElement(W + "commentReference", new XAttribute(W + "id", "1"))));
        return TestPackages.ReplacePart(withComments, "word/footnotes.xml", Encoding.UTF8.GetBytes(notes.ToString()));
    }

    private static byte[] AddBodyComment(byte[] package)
    {
        var withComments = AddCommentsPart(package);
        var document = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(withComments, "word/document.xml")));
        var paragraph = document.Descendants(W + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        paragraph.Add(new XElement(
            W + "r",
            new XElement(W + "commentReference", new XAttribute(W + "id", "1"))));
        return TestPackages.ReplacePart(withComments, "word/document.xml", Encoding.UTF8.GetBytes(document.ToString()));
    }

    private static byte[] AddCommentsPart(byte[] package)
    {
        var commentsXml = "<w:comments xmlns:w='" + W + "'>"
            + "<w:comment w:id='1' w:author='A' w:initials='A'>"
            + "<w:p><w:r><w:t>A comment</w:t></w:r></w:p>"
            + "</w:comment></w:comments>";
        var withComments = TestPackages.AddPart(package, "word/comments.xml", commentsXml);

        XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(withComments, "[Content_Types].xml")));
        types.Root!.Add(new XElement(
            contentTypes + "Override",
            new XAttribute("PartName", "/word/comments.xml"),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")));
        withComments = TestPackages.ReplacePart(
            withComments, "[Content_Types].xml", Encoding.UTF8.GetBytes(types.ToString()));

        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        var rels = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(withComments, "word/_rels/document.xml.rels")));
        var next = rels.Root!.Elements(relationships + "Relationship")
            .Select(relationship => (string?)relationship.Attribute("Id"))
            .Where(id => id is not null && id.StartsWith("rId", StringComparison.Ordinal))
            .Select(id => int.Parse(id![3..], System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max() + 1;
        rels.Root.Add(new XElement(
            relationships + "Relationship",
            new XAttribute("Id", "rId" + next.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments"),
            new XAttribute("Target", "comments.xml")));
        return TestPackages.ReplacePart(
            withComments, "word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(rels.ToString()));
    }

    private static void ClonesCommentInRepeatedRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = AddBodyComment(CreateTemplate(sourceDocument));
        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A comment in a repeated region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var comments = XDocument.Parse(ReadText(zip, "word/comments.xml"));
        var commentIds = comments.Descendants(W + "comment")
            .Select(comment => (string?)comment.Attribute(W + "id"))
            .ToList();
        Require(commentIds.Count == 2 && commentIds.Distinct(StringComparer.Ordinal).Count() == 2,
            "The comment is duplicated with a fresh id.");
        Require(comments.Descendants(W + "t").Count(text => text.Value == "A comment") == 2,
            "The comment body is present twice.");
        var document = ReadDocument(zip);
        var references = document.Descendants(W + "commentReference")
            .Select(reference => (string?)reference.Attribute(W + "id"))
            .ToList();
        Require(references.Count == 2 && references.Distinct(StringComparer.Ordinal).Count() == 2,
            "Both clones point at their own comment.");
        Require(ContainsText(document, "one") && ContainsText(document, "two"), "The row values are bound.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ReportsSectionInRepeatedRegion()
    {
        var sourceDocument = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(sourceDocument);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var paragraph = xml.Descendants(W + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        var properties = paragraph.Element(W + "pPr");
        if (properties is null)
        {
            properties = new XElement(W + "pPr");
            paragraph.AddFirst(properties);
        }

        properties.Add(new XElement(
            W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", "11906"), new XAttribute(W + "h", "16838"))));
        source = TestPackages.ReplacePart(
            source,
            "word/document.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("cannot safely be duplicated", StringComparison.Ordinal)),
            "A section break in a repeated region is reported: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(
            document.Descendants(W + "p").Count(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal)) == 1,
            "The section-bearing paragraph is not duplicated.");
        Require(!ContainsText(document, "one") && !ContainsText(document, "two"), "The region is left unexpanded.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void FormatsWordMergeFields()
    {
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new Field(" MERGEFIELD Name \\* Upper \\* MERGEFORMAT ", "old"), new TextRun("/"),
            new Field(" MERGEFIELD Name \\* Lower ", "old"), new TextRun("/"),
            new Field(" MERGEFIELD Mixed \\* Upper ", "keep"), new TextRun("/"),
            new Field(" MERGEFIELD Name \\* Upper \\* Lower ", "conflict"),
        }))));
        var template = DocumentTemplate.FromBytes(source)
            .Bind("Name", new Inline[] { new TextRun("Ada ") { Bold = true }, new TextRun("Lovelace") { Italic = true } })
            .Bind("Mixed", new Inline[] { new TextRun("text"), new PageNumberField() });
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);
        using var zip = Open(package);
        var xml = ReadDocument(zip);
        Require(FindRun(xml, "ADA ")?.Element(W + "rPr")?.Element(W + "b") is not null,
            "DOCX: upper case preserves the formatting of each input run.");
        Require(FindRun(xml, "LOVELACE")?.Element(W + "rPr")?.Element(W + "i") is not null,
            "DOCX: upper case preserves italic formatting.");
        Require(ContainsText(xml, "ada ") && ContainsText(xml, "lovelace"), "DOCX: lower case applies to each input run.");
        Require(xml.Descendants(W + "instrText").Count() == 2 && ContainsText(xml, "keep") && ContainsText(xml, "conflict"),
            "DOCX: non-text and conflicting formatting switches remain unchanged.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("unsupported formatting", StringComparison.Ordinal)),
            "DOCX: unsupported formatting is reported.");
    }

    private static void FormatsAdditionalWordMergeSwitches()
    {
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new Field(" MERGEFIELD Name \\* Caps ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Name \\* FirstCap ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Name \\* CHARFORMAT \\v \\h ", "old"),
        }))));
        var template = DocumentTemplate.FromBytes(source).BindText("Name", "ada lovelace");
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Caps, FirstCap, CHARFORMAT and \\v/\\h produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var xml = ReadDocument(zip);
        Require(ContainsText(xml, "Ada Lovelace"), "DOCX: \\* Caps capitalizes each word.");
        Require(ContainsText(xml, "Ada lovelace"), "DOCX: \\* FirstCap uppercases only the first letter.");
        Require(ContainsText(xml, "ada lovelace"), "DOCX: CHARFORMAT and \\v/\\h are accepted without changing the text.");
        Require(!ContainsText(xml, "MERGEFIELD"), "DOCX: all three fields are bound.");
    }

    private static void FormatsNumericWordMergeFields()
    {
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new Field(" MERGEFIELD Amount \\# \"#,##0.00\" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Ratio \\# \"0.0%\" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Count \\# \"000\" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Count \\# \"$#,##0.00\" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Invalid \\# \"0.00\" ", "old"),
        }))));
        var template = DocumentTemplate.FromBytes(source)
            .BindText("Amount", "1234.5")
            .BindText("Ratio", "0.125")
            .BindText("Count", "7")
            .BindText("Invalid", "abc");
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);

        using var zip = Open(package);
        var xml = ReadDocument(zip);
        Require(ContainsText(xml, "1,234.50"), "DOCX: \\# applies grouping and decimals.");
        Require(ContainsText(xml, "0.1%"), "DOCX: \\# prints a percent sign without scaling, as Word does.");
        Require(ContainsText(xml, "007"), "DOCX: \\# pads with zeros.");
        Require(ContainsText(xml, "$7.00"), "DOCX: \\# keeps a literal prefix.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("numeric picture", StringComparison.Ordinal)),
            "DOCX: a numeric picture on non-numeric text is reported.");
        Require(xml.Descendants(W + "instrText").Count() == 1 && ContainsText(xml, "old"), "DOCX: the unsupported numeric field remains unchanged.");
    }

    private static void AddsConditionalWordMergeText()
    {
        var source = CreateTemplate(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[]
        {
            new Field(" MERGEFIELD City \\b \"City: \" \\f \", \" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD Empty \\b \"before\" \\f \"after\" ", "old"), new TextRun("|"),
            new Field(" MERGEFIELD City \\b \"before\" \\b \"again\" ", "duplicate"), new TextRun("|"),
            new Field(" MERGEFIELD City \\f \"!\" \\* Upper ", "combined"),
        }))));
        var template = DocumentTemplate.FromBytes(source).Bind("City", new Inline[]
        {
            new TextRun("Ber") { Bold = true }, new TextRun("lin") { Italic = true },
        }).BindText("Empty", "");
        var result = Write(EmptyDocument(), source, new DocxWriteOptions { Template = template }, out var package);
        using var zip = Open(package);
        var xml = ReadDocument(zip);
        var text = string.Concat(xml.Descendants(W + "t").Select(node => node.Value));
        Require(text == "City: Berlin, ||duplicate|combined", "DOCX: before and after text is conditional on a nonempty value: " + text);
        Require(FindRun(xml, "Ber")?.Element(W + "rPr")?.Element(W + "b") is not null
            && FindRun(xml, "lin")?.Element(W + "rPr")?.Element(W + "i") is not null,
            "DOCX: the merged value keeps its formatting.");
        Require(xml.Descendants(W + "instrText").Count() == 2 && result.HasWarnings,
            "DOCX: duplicate and combined unsupported switches are preserved and reported.");
    }

    private static void BindsNestedSimpleWordField()
    {
        var source = CreateTemplate(PortableDocument.Create().Paragraph("placeholder").Build());
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var paragraph = xml.Descendants(W + "p").First();
        paragraph.RemoveNodes();
        paragraph.Add(new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin"))),
            new XElement(W + "r", new XElement(W + "instrText", " IF ")),
            new XElement(W + "fldSimple", new XAttribute(W + "instr", "MERGEFIELD Name"),
                new XElement(W + "r", new XElement(W + "t", "original"))),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end"))));
        source = TestPackages.ReplacePart(source, "word/document.xml", Encoding.UTF8.GetBytes(xml.ToString()));
        var result = Write(EmptyDocument(), source, new DocxWriteOptions
        {
            Template = DocumentTemplate.FromBytes(source).BindText("Name", "changed"),
        }, out var package);
        using var zip = Open(package);
        var resultXml = ReadDocument(zip);
        Require(!resultXml.Descendants(W + "fldSimple").Any() && ContainsText(resultXml, "changed"),
            "DOCX: a MERGEFIELD nested in an outer field is bound.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("nested", StringComparison.Ordinal)
                || warning.Message.Contains("unsupported", StringComparison.Ordinal)),
            "DOCX: binding a nested simple MERGEFIELD produces no warning.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsNestedComplexWordField()
    {
        var source = CreateTemplate(PortableDocument.Create().Paragraph("placeholder").Build());
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "word/document.xml")));
        var paragraph = xml.Descendants(W + "p").First();
        paragraph.RemoveNodes();
        paragraph.Add(
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin"))),
            new XElement(W + "r", new XElement(W + "instrText", " IF ")),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin"))),
            new XElement(W + "r", new XElement(W + "instrText", " MERGEFIELD Name ")),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate"))),
            new XElement(W + "r", new XElement(W + "t", "old")),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end"))),
            new XElement(W + "r", new XElement(W + "instrText", " = \"M\" \"Mr\" \"Ms\" ")),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate"))),
            new XElement(W + "r", new XElement(W + "t", "Ms")),
            new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end"))));
        source = TestPackages.ReplacePart(source, "word/document.xml", Encoding.UTF8.GetBytes(xml.ToString()));
        var result = Write(EmptyDocument(), source, new DocxWriteOptions
        {
            Template = DocumentTemplate.FromBytes(source).BindText("Name", "M"),
        }, out var package);

        using var zip = Open(package);
        var resultXml = ReadDocument(zip);
        Require(resultXml.Descendants(W + "fldChar").Count() == 3
                && !resultXml.Descendants(W + "instrText").Any(text => text.Value.Contains("MERGEFIELD", StringComparison.Ordinal)),
            "DOCX: a nested complex MERGEFIELD is bound while the outer IF field remains.");
        Require(ContainsText(resultXml, "M") && ContainsText(resultXml, "Ms"), "DOCX: the bound value and the outer result are present.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("MERGEFIELD", StringComparison.Ordinal)),
            "DOCX: binding a nested complex MERGEFIELD produces no warning.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsListInBlockContent()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Liste}}").Build());

        var list = TestLists.List(ListKind.Bullet, new ListItem[]
        {
            new(new Paragraph(new TextRun("one"))),
            new(new Paragraph(new TextRun("two"))),
        });

        Write(
            TestLists.WithList(EmptyDocument(), list),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Bind("Liste", list),
            },
            out var package);

        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        Require(body.Contains("w:numPr", StringComparison.Ordinal), "The list uses numbering.");
        Require(body.Contains("w:numId w:val=\"1\"", StringComparison.Ordinal), "The list uses the first numId (1).");

        var numbering = ReadText(zip, "word/numbering.xml");
        Require(numbering.Contains("w:abstractNumId=\"0\"", StringComparison.Ordinal), "A new bullet definition is appended.");
        Require(numbering.Contains("w:numId=\"1\"", StringComparison.Ordinal), "The new numId is defined.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsImageInBlockContent()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Image}}").Build());
        var png = CreatePng(24, 12);

        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes)
                    .Bind("Image", new ImageBlock(png, ImageMediaType.Png) { AltText = "Alt" }),
            },
            out var package);

        using var zip = Open(package);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("word/media/image1.png"), "The image is added as a media part.");
        Require(ReadText(zip, "word/_rels/document.xml.rels").Contains("media/image1.png", StringComparison.Ordinal), "The image relationship is added.");

        var document = ReadDocument(zip);
        Require(document.Descendants(W + "drawing").Any(), "The drawing is inserted.");
        Require(
            document.Descendants(A + "blip").Any(blip => blip.Attribute(R + "embed") is not null),
            "The drawing references a relationship.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ReportsUnboundPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Missing}}").Build());

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out var package);

        Require(result.Warnings.Any(warning => warning.Message.Contains("Missing", StringComparison.Ordinal)), "An unbound placeholder is reported.");
        using var zip = Open(package);
        Require(ReadText(zip, "word/document.xml").Contains("{{Missing}}", StringComparison.Ordinal), "The unbound placeholder remains.");
    }

    private static void ReportsBoundPlaceholderMissingFromTemplate()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("No placeholder").Build());

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("NotThere", "silently dropped"),
            },
            out var package);

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("{{NotThere}}", StringComparison.Ordinal)
                && warning.Message.Contains("not present in the template", StringComparison.Ordinal)),
            "A bound placeholder that the template does not contain is reported.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("NotThere", StringComparison.Ordinal)
                && warning.Message.Contains("unbound", StringComparison.Ordinal)),
            "A missing bound placeholder is not confused with an unbound placeholder.");
        using var zip = Open(package);
        Require(!ReadText(zip, "word/document.xml").Contains("silently dropped", StringComparison.Ordinal), "The bound content is not written.");
    }

    private static void ReportsMissingBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("No placeholder").Build());

        var result = Write(
            PortableDocument.Create().Paragraph("Content text").Build(),
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        Require(result.Warnings.Any(warning => warning.Message.Contains("Content", StringComparison.Ordinal)), "The missing body placeholder is reported.");
        using var zip = Open(package);
        var body = ReadText(zip, "word/document.xml");
        Require(body.Contains("Content text", StringComparison.Ordinal), "The content is appended instead of discarded.");
    }


    private static void ReportsUnsetCondition()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("A{{If:Missing}}B{{EndIf}}C").Build());
        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out _);
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("condition '{{If:Missing}}'", StringComparison.Ordinal)),
            "An unset condition is reported.");
    }


    private static void BindsBlockConditions()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph("A")
            .Paragraph("{{If:Show}}")
            .Paragraph("B")
            .Paragraph("{{EndIf}}")
            .Paragraph("C")
            .Table(table => table
                .Row("R1")
                .Row("{{If:Show}}")
                .Row("R2")
                .Row("{{EndIf}}")
                .Row("R3"))
            .Build());

        AssertBlockCondition(templateBytes, false, ["A", "C", "R1", "R3"], ["B", "R2"]);
        AssertBlockCondition(templateBytes, true, ["A", "B", "C", "R1", "R2", "R3"], []);
    }

    private static void AssertBlockCondition(byte[] templateBytes, bool value, string[] present, string[] absent)
    {
        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Condition("Show", value),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        var text = string.Concat(document.DescendantNodes().OfType<XText>().Select(node => node.Value));
        foreach (var part in present)
        {
            Require(text.Contains(part, StringComparison.Ordinal), $"Block condition keeps '{part}'.");
        }

        foreach (var part in absent)
        {
            Require(!text.Contains(part, StringComparison.Ordinal), $"Block condition removes '{part}'.");
        }

        Require(!text.Contains("{{", StringComparison.Ordinal), "No condition marker remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }


    private static void BindsPlaceholderStartingWithIf()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Interface}}").Build());
        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).BindText("Interface", "kept") },
            out var package);

        using var zip = Open(package);
        Require(ContainsText(ReadDocument(zip), "kept"), "A placeholder starting with 'If' is not a condition.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("condition", StringComparison.Ordinal)), "No condition warning for an ordinary placeholder.");
    }

    private static void RejectsTemplateWithoutDocumentPart()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("foo.txt");
            using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        var template = DocumentTemplate.FromBytes(buffer.ToArray());
        try
        {
            Write(EmptyDocument(), buffer.ToArray(), new DocxWriteOptions { Template = template }, out _);
        }
        catch (DocumentValidationException)
        {
            return;
        }

        throw new InvalidOperationException("A template without word/document.xml must be rejected.");
    }

    private static void RejectsInvalidTemplateBytes()
    {
        var template = DocumentTemplate.FromBytes(new byte[] { 1, 2, 3, 4 });
        try
        {
            Write(EmptyDocument(), Array.Empty<byte>(), new DocxWriteOptions { Template = template }, out _);
        }
        catch (DocumentValidationException)
        {
            return;
        }

        throw new InvalidOperationException("Damaged template bytes must be rejected.");
    }

    private static void RepeatsTableRows()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Table(table => table
                .Row("Name", "Qty")
                .Row("{{Rows}}{{Name}}", "{{Qty}}"))
            .Build());

        var rows = new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Apple", ["Qty"] = "3" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "Pear", ["Qty"] = "5" },
        };

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).RepeatRows("Rows", rows),
            },
            out var package);

        Require(!result.HasWarnings, "Row repetition produces no warning.");

        using var zip = Open(package);
        var document = ReadDocument(zip);
        var tableRows = document.Descendants(W + "tr").Count();
        Require(tableRows == 3, $"The row is repeated (header + two rows), got {tableRows}.");
        Require(ContainsText(document, "Apple") && ContainsText(document, "Pear"), "The data values are bound.");
        Require(ContainsText(document, "3") && ContainsText(document, "5"), "The second column is bound.");
        Require(!ContainsText(document, "{{"), "No placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void KeepsPlaceholdersInsideRowValues()
    {
        // Shared by the DOCX and ODT binders: a value is inserted once and never scanned again.
        var data = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "{{Qty}}", ["Qty"] = "3", ["Note"] = null };
        var text = TemplatePlaceholders.ReplaceFields("{{Rows}}{{Name}}/{{Qty}}/{{Note}}/{{Other}}", "{{Rows}}", data);
        Require(text == "{{Qty}}/3/{{Note}}/{{Other}}", $"A row value is not replaced a second time, got '{text}'.");
    }

    private static void NestsConditions()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph(p => p.Text("A{{If:Outer}}B{{If:Inner}}C{{EndIf}}D{{EndIf}}E"))
            .Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes)
                    .Condition("Outer", true)
                    .Condition("Inner", false),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        var text = string.Concat(document.DescendantNodes().OfType<System.Xml.Linq.XText>().Select(node => node.Value));
        Require(text.Contains("ABDE", StringComparison.Ordinal), "Nested If: the outer branch is kept, the inner is dropped.");
        Require(!text.Contains("{{", StringComparison.Ordinal), "No placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid.");
    }


    private static void KeepsTemplateBookmarkIdsDistinct()
    {
        var templateBytes = InjectBookmark(
            CreateTemplate(PortableDocument.Create().Paragraph("{{B}}").Build()),
            1);
        Write(
            EmptyDocument(),
            templateBytes,
            new DocxWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).Bind("B", new Bookmark("Inserted")),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        var ids = document.Descendants(W + "bookmarkStart")
            .Select(bookmark => (string?)bookmark.Attribute(W + "id"))
            .ToList();
        Require(ids.Count == 2 && ids.Distinct(StringComparer.Ordinal).Count() == 2, "The inserted bookmark gets a free ID.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static byte[] InjectBookmark(byte[] package, int id)
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        var paragraph = xml.Root!.Element(W + "body")!.Elements(W + "p").First();
        paragraph.AddFirst(new XElement(
            W + "bookmarkStart",
            new XAttribute(W + "id", id),
            new XAttribute(W + "name", "Existing")));
        paragraph.Add(new XElement(W + "bookmarkEnd", new XAttribute(W + "id", id)));
        return TestPackages.ReplacePart(package, "word/document.xml", Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
    }

    private static WriteResult Write(PortableDocument document, byte[] templateBytes, DocxWriteOptions options, out byte[] package)
    {
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        package = buffer.ToArray();
        return result;
    }

    private static byte[] CreateTemplate(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>A run with a character style keeps its formatting in a template (ADR-0010).</summary>
    private static PortableDocument CharacterStyledContent()
        => new(new DocumentMetadata(), new Section(new Paragraph(new TextRun("STARK") { CharacterStyleName = "Strong" })))
        {
            CharacterStyles = [new CharacterStyle("Strong") { Bold = true }],
        };

    private static byte[] TemplateWithStrong(bool defineStrong)
        => CreateTemplate(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("{{Content}}"))))
        {
            CharacterStyles = defineStrong ? [new CharacterStyle("Strong") { Italic = true }] : [],
        });

    private static void KeepsCharacterStylesInTemplate()
    {
        foreach (var defineStrong in new[] { false, true })
        {
            var templateBytes = TemplateWithStrong(defineStrong);
            Write(
                CharacterStyledContent(),
                templateBytes,
                new DocxWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
                out var package);

            using var zip = Open(package);
            var run = FindRun(ReadDocument(zip), "STARK");
            Require((string?)run?.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val") == "Strong",
                "DOCX template: the run references its character style.");
            Require(run!.Element(W + "rPr")!.Element(W + "b") is not null,
                "DOCX template: the run shows the formatting of its character style.");
            var styles = XDocument.Parse(ReadText(zip, "word/styles.xml"));
            var strong = styles.Descendants(W + "style")
                .Where(element => (string?)element.Attribute(W + "styleId") == "Strong").ToList();
            Require(strong.Count == 1, "DOCX template: the character style is defined exactly once.");
            Require(strong[0].Descendants(W + (defineStrong ? "i" : "b")).Any(),
                "DOCX template: a style the template defines stays the template's.");
            Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
        }
    }

    private static void KeepsTableStylesInTemplate()
    {
        foreach (var bind in new[] { false, true })
        {
            var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Table}}").Build());
            var table = new TableBlock([new TableRow([new TableCell("Kopf")]) { IsHeader = true }])
            {
                Style = new TableStyle("Raster") { HeaderRow = new TableStyleArea { Bold = true } },
            };
            var document = bind ? EmptyDocument() : new PortableDocument(new DocumentMetadata(), new Section([table]));
            var template = DocumentTemplate.FromBytes(templateBytes);
            if (bind) template = template.Bind("Table", table);
            Write(document, templateBytes, new DocxWriteOptions { Template = template }, out var package);
            using var zip = Open(package);
            var styles = XDocument.Parse(ReadText(zip, "word/styles.xml"));
            var area = styles.Descendants(W + "style").Single(element => (string?)element.Attribute(W + "styleId") == "Raster");
            Require(area.Descendants(W + "b").Any(), "DOCX template: conditional table style is retained.");
            Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
        }
    }

    private static PortableDocument EmptyDocument()
        => new(new DocumentMetadata(), new Section(Array.Empty<Block>()));

    private static ZipArchive Open(byte[] package)
        => new(new MemoryStream(package), ZipArchiveMode.Read);

    private static IReadOnlyList<string> Valid(byte[] package) => DocxPackageValidator.Validate(package);

    private static XDocument ReadDocument(ZipArchive zip)
    {
        using var stream = ReadEntry(zip, "word/document.xml");
        return XDocument.Load(stream);
    }

    private static bool ContainsText(XDocument document, string text)
        => document.Descendants(W + "t").Any(t => t.Value.Contains(text, StringComparison.Ordinal));

    private static XElement? FindRun(XDocument document, string text)
        => document.Descendants(W + "r").FirstOrDefault(run => run.Elements(W + "t").Any(t => t.Value.Contains(text, StringComparison.Ordinal)));

    private static string ReadText(ZipArchive zip, string name)
    {
        using var stream = ReadEntry(zip, name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        return entry.Open();
    }

    private static byte[] CreatePng(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(bytes, "IHDR", ihdr);
        AppendChunk(bytes, "IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        target.AddRange(data);
        target.AddRange(new byte[4]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
