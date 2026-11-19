using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// checks the filling of ODT templates (phase S, S03): inline and block placeholders,
/// body placeholder, preservation of styles and headers, lists, images and the renaming
/// of colliding automatic styles.
/// </summary>
internal static class OdtTemplateChecks
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    internal static int Run()
    {
        try
        {
            BindsInlinePlaceholder();
            BindsPlaceholderSplitAcrossRuns();
            BindsBlockPlaceholderWithStyle();
            BindsBodyPlaceholder();
            KeepsCharacterStylesInTemplate();
            ReportsRoundedTablesInTemplate();
            BindsBodyPlaceholderPerSection();
            WritesAutoHyphenation();
            AppendsWithoutBodyPlaceholder();
            PreservesTemplateParts();
            BindsHeadersAndFooters();
            BindsFirstAndEvenBands();
            BindsTableAndRepeatedRegionInBands();
            ClonesComplexDrawingInRegion();
            ClonesTrackedChangesInRegion();
            ReportsSectionInRepeatedRegion();
            BindsListInBlockContent();
            BindsImageInBlockContent();
            RepeatsTableRows();
            RenamesCollidingStyles();
            ReportsUnboundPlaceholder();
            ReportsBoundPlaceholderMissingFromTemplate();
            ReportsMissingBodyPlaceholder();
            BindsBlockConditions();
            RejectsTemplateWithoutContentPart();
            RejectsInvalidTemplateBytes();
            Console.WriteLine("ODT templates (S03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ODT templates (S03): " + exception);
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
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("Date", "1. October 2026"),
            },
            out var package);

        Require(!result.HasWarnings, "Inline binding produces no warning.");

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{Date}}"), "The placeholder is replaced.");
        Require(ContainsText(document, "October"), "The inserted text is present.");

        var node = document.DescendantNodes().OfType<XText>()
            .FirstOrDefault(text => text.Value.Contains("October", StringComparison.Ordinal));
        Require(node?.Parent?.Name == Text + "span", "The inserted text stands in the template's text span.");
        Require(node!.Parent!.Attribute(Text + "style-name") is not null, "The span carries the template's style.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsPlaceholderSplitAcrossRuns()
    {
        // Word/LibreOffice often split placeholders arbitrarily across several spans.
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph(p => p.Text("Date: ").Text("{{Da").Bold("te}}"))
            .Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("Date", "1. October"),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{"), "A placeholder split across spans is replaced.");
        Require(ContainsText(document, "October"), "The inserted text is present.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsBlockPlaceholderWithStyle()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Heading("{{Title}}", 1).Build());

        Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes)
                    .Bind("Title", new Paragraph(new TextRun("New title"))),
            },
            out var package);

        using var zip = Open(package);
        var document = ReadDocument(zip);
        Require(!ContainsText(document, "{{Title}}"), "The placeholder is replaced.");

        var paragraph = document.Descendants(Text + "p").FirstOrDefault(p => p.Value.Contains("New", StringComparison.Ordinal));
        Require(paragraph is not null, "The inserted paragraph is present.");
        Require((string?)paragraph!.Attribute(Text + "style-name") == "Heading1", "The paragraph inherits the template's style.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create()
            .Paragraph("Headertext")
            .Paragraph("{{Content}}")
            .Paragraph("Footertext")
            .Build());

        Write(
            PortableDocument.Create().Paragraph("First").Paragraph("Second").Build(),
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        using var zip = Open(package);
        var content = ReadText(zip, "content.xml");
        Require(!content.Contains("{{Content}}", StringComparison.Ordinal), "The body placeholder is replaced.");
        Require(content.Contains("First", StringComparison.Ordinal) && content.Contains("Second", StringComparison.Ordinal), "Both paragraphs are inserted.");
        Require(
            content.IndexOf("Headertext", StringComparison.Ordinal) < content.IndexOf("First", StringComparison.Ordinal)
            && content.IndexOf("First", StringComparison.Ordinal) < content.IndexOf("Footertext", StringComparison.Ordinal),
            "The content stands between header and footer text.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void KeepsCharacterStylesInTemplate()
    {
        foreach (var defineStrong in new[] { false, true })
        {
            var templateBytes = TemplateWithStrong(defineStrong);
            Write(
                CharacterStyledContent(),
                templateBytes,
                new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
                out var package);

            using var zip = Open(package);
            var content = ReadDocument(zip);
            var span = content.Descendants(Text + "span").SingleOrDefault(element => element.Value == "STARK");
            Require(span is not null, "ODT template: the character-styled run keeps its span.");
            var spanStyle = content.Descendants(Style + "style")
                .Single(element => (string?)element.Attribute(Style + "name") == (string?)span!.Attribute(Text + "style-name"));
            Require((string?)spanStyle.Attribute(Style + "parent-style-name") == "Strong",
                "ODT template: the span style is based on the character style.");
            Require(spanStyle.ToString().Contains("fo:font-weight=\"bold\"", StringComparison.Ordinal),
                "ODT template: the run shows the formatting of its character style.");
            var styles = XDocument.Parse(ReadText(zip, "styles.xml"));
            var strong = styles.Descendants(Style + "style")
                .Where(element => (string?)element.Attribute(Style + "name") == "Strong").ToList();
            Require(strong.Count == 1, "ODT template: the character style is defined exactly once.");
            Require(strong[0].ToString().Contains(defineStrong ? "italic" : "bold", StringComparison.Ordinal),
                "ODT template: a style the template defines stays the template's.");
            Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
        }
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
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        using var zip = Open(package);
        var xml = ReadDocument(zip);
        var body = xml.Root!.Element(Office + "body")!.Element(Office + "text")!;
        var sections = body.Elements(Text + "section").ToList();
        Require(sections.Count == 2, "The template keeps two sections.");

        string SectionText(XElement section)
            => string.Concat(section.DescendantNodes().OfType<XText>().Select(text => text.Value));

        var first = SectionText(sections[0]);
        var second = SectionText(sections[1]);
        Require(first.Contains("FirstSection", StringComparison.Ordinal) && !first.Contains("SecondSection", StringComparison.Ordinal),
            "The first model section is bound into the first template section.");
        Require(second.Contains("SecondSection", StringComparison.Ordinal) && !second.Contains("FirstSection", StringComparison.Ordinal),
            "The second model section is bound into the second template section.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("merged into one area", StringComparison.Ordinal)),
            "A template with one body placeholder per section is not reported as merged.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void WritesAutoHyphenation()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("Template").Build());

        Write(
            PortableDocument.Create().Paragraph("Text").Build(),
            templateBytes,
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes),
                AutoHyphenation = true,
            },
            out var package);

        using var zip = Open(package);
        Require(ReadText(zip, "styles.xml").Contains("fo:hyphenate=\"true\"", StringComparison.Ordinal),
            "The template's default style enables hyphenation.");
    }

    private static void AppendsWithoutBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("Template").Build());

        Write(
            PortableDocument.Create().Paragraph("Appended").Build(),
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out var package);

        using var zip = Open(package);
        var content = ReadText(zip, "content.xml");
        Require(content.Contains("Template", StringComparison.Ordinal) && content.Contains("Appended", StringComparison.Ordinal), "Template and content are present.");
        Require(
            content.IndexOf("Template", StringComparison.Ordinal) < content.IndexOf("Appended", StringComparison.Ordinal),
            "The content is appended.");
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
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        using var zip = Open(package);
        Require(ReadText(zip, "styles.xml").Contains("Letterhead", StringComparison.Ordinal), "The template's header is preserved.");
        Require(ReadText(zip, "content.xml").Contains("Text", StringComparison.Ordinal), "The content is inserted.");
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
        var template = DocumentTemplate.FromBytes(source).Bind("Name", new TextRun("Ada") { Bold = true })
            .Bind("Image", new ImageBlock(CreatePng(24, 12), ImageMediaType.Png));
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);
        using var zip = Open(package);
        var styles = ReadText(zip, "styles.xml");
        Require(styles.Contains("Ada", StringComparison.Ordinal), "A split header placeholder is bound.");
        Require(styles.Contains("Pictures/image", StringComparison.Ordinal), "A footer block image is inserted.");
        Require(styles.Contains("PDT", StringComparison.Ordinal), "Formatted header text has an automatic style in styles.xml.");
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
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "First/even band bindings produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var styles = ReadText(zip, "styles.xml");
        foreach (var text in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta" })
        {
            Require(styles.Contains(text, StringComparison.Ordinal), $"The bound text '{text}' is present in a band.");
        }

        Require(!styles.Contains("{{", StringComparison.Ordinal), "No band placeholder remains.");
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
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Band table and region bindings produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var styles = ReadText(zip, "styles.xml");
        Require(styles.Contains("CellValue", StringComparison.Ordinal), "A table cell placeholder in a band is bound.");
        Require(styles.Contains("Alice", StringComparison.Ordinal) && styles.Contains("Bob", StringComparison.Ordinal), "A repeated region in a band is expanded.");
        Require(!styles.Contains("{{", StringComparison.Ordinal), "No band placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesComplexDrawingInRegion()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(document);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        XNamespace svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

        var paragraph = xml.Descendants(text + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        paragraph.Add(new XElement(
            draw + "frame",
            new XAttribute(draw + "name", "ShapeFrame"),
            new XAttribute(XNamespace.Xml + "id", "shape1"),
            new XAttribute(text + "anchor-type", "page"),
            new XAttribute(svg + "x", "1cm"),
            new XAttribute(svg + "y", "2cm"),
            new XAttribute(svg + "width", "2cm"),
            new XAttribute(svg + "height", "1cm"),
            new XElement(
                draw + "rect",
                new XAttribute(draw + "name", "ShapeRect"),
                new XAttribute(XNamespace.Xml + "id", "shape2"),
                new XAttribute(svg + "x", "0cm"),
                new XAttribute(svg + "y", "0cm"),
                new XAttribute(svg + "width", "2cm"),
                new XAttribute(svg + "height", "1cm"))));
        paragraph.Add(new XElement(
            draw + "frame",
            new XAttribute(draw + "copy-of", "shape1"),
            new XAttribute(text + "anchor-type", "as-char"),
            new XAttribute(svg + "width", "2cm"),
            new XAttribute(svg + "height", "1cm")));
        source = TestPackages.ReplacePart(
            source,
            "content.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "A complex ODT drawing in a region produces no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var rewritten = XDocument.Parse(ReadText(zip, "content.xml"));
        Require(rewritten.Descendants(draw + "frame").Count() == 4, "Both ODT frames are cloned for both rows.");
        Require(rewritten.Descendants(draw + "rect").Count() == 2, "The inner shape is cloned for both rows.");
        var ids = rewritten.Descendants().Attributes()
            .Where(attribute => attribute.Name == XNamespace.Xml + "id" || attribute.Name == draw + "id")
            .Select(attribute => attribute.Value)
            .ToList();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Count, "Cloned drawings do not duplicate an id.");
        var xmlIds = ids.ToHashSet(StringComparer.Ordinal);
        var copies = rewritten.Descendants().Attributes(draw + "copy-of").Select(attribute => attribute.Value).ToList();
        Require(copies.Count == 2 && copies.Distinct(StringComparer.Ordinal).Count() == 2,
            "A draw:copy-of is remapped per clone instead of keeping the source id.");
        Require(copies.All(xmlIds.Contains), "Each remapped copy-of points at a local drawing id.");
        var content = ReadText(zip, "content.xml");
        Require(content.Contains("one", StringComparison.Ordinal) && content.Contains("two", StringComparison.Ordinal), "The row values are bound.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ClonesTrackedChangesInRegion()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(document);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";

        var paragraph = xml.Descendants(text + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        paragraph.AddFirst(new XElement(text + "change-start", new XAttribute(text + "change-id", "ct1")));
        paragraph.Add(new XElement(text + "change-end", new XAttribute(text + "change-id", "ct1")));
        var officeText = xml.Descendants(office + "text").First();
        officeText.Add(new XElement(
            text + "tracked-changes",
            new XElement(
                text + "changed-region",
                new XAttribute(text + "id", "ct1"),
                new XElement(
                    text + "insertion",
                    new XElement(
                        office + "change-info",
                        new XElement(dc + "creator", "A"),
                        new XElement(dc + "date", "2020-01-01T00:00:00"))))));
        source = TestPackages.ReplacePart(
            source,
            "content.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);

        Require(!result.HasWarnings, "Tracked changes in an ODT region produce no warnings: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var rewritten = XDocument.Parse(ReadText(zip, "content.xml"));
        var regions = rewritten.Descendants(text + "changed-region")
            .Select(region => (string?)region.Attribute(text + "id"))
            .ToList();
        Require(regions.Count == 2 && regions.Distinct(StringComparer.Ordinal).Count() == 2,
            "The changed region is duplicated with a fresh id.");
        var ids = rewritten.Descendants()
            .Where(element => element.Name == text + "change-start" || element.Name == text + "change-end")
            .Select(element => (string?)element.Attribute(text + "change-id"))
            .ToList();
        Require(ids.Count == 4 && ids.Distinct(StringComparer.Ordinal).Count() == 2
                && ids.All(regions.Contains),
            "Both clones point at their own changed region.");
        var content = ReadText(zip, "content.xml");
        Require(content.Contains("one", StringComparison.Ordinal) && content.Contains("two", StringComparison.Ordinal),
            "The row values are bound.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void ReportsSectionInRepeatedRegion()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("{{Repeat:Rows}}")),
                new Paragraph(new TextRun("{{Name}}")),
                new Paragraph(new TextRun("{{EndRepeat}}")),
            }));
        var source = CreateTemplate(document);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(source, "content.xml")));
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

        var paragraph = xml.Descendants(text + "p").First(item => item.Value.Contains("{{Name}}", StringComparison.Ordinal));
        var inner = new XElement(text + "section", new XAttribute(text + "name", "Inner"));
        paragraph.ReplaceWith(inner);
        inner.Add(paragraph);
        source = TestPackages.ReplacePart(
            source,
            "content.xml",
            Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));

        var template = DocumentTemplate.FromBytes(source).RepeatRegion("Rows", new[]
        {
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "one" },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Name"] = "two" },
        });
        var result = Write(EmptyDocument(), source, new OdtWriteOptions { Template = template }, out var package);

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("cannot safely be duplicated", StringComparison.Ordinal)),
            "A section in a repeated region is reported: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        using var zip = Open(package);
        var rewritten = XDocument.Parse(ReadText(zip, "content.xml"));
        Require(
            rewritten.Descendants(text + "section").Count(item => (string?)item.Attribute(text + "name") == "Inner") == 1,
            "The section is not duplicated.");
        Require(ReadText(zip, "content.xml").Contains("{{Name}}", StringComparison.Ordinal), "The region is left unexpanded.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsListInBlockContent()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{List}}").Build());

        var list = TestLists.List(ListKind.Bullet, new ListItem[]
        {
            new(new Paragraph(new TextRun("one"))),
            new(new Paragraph(new TextRun("two"))),
        });

        Write(
            TestLists.WithList(EmptyDocument(), list),
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Bind("List", list) },
            out var package);

        using var zip = Open(package);
        var content = ReadText(zip, "content.xml");
        Require(content.Contains("<text:list", StringComparison.Ordinal), "The list is inserted.");
        Require(content.Contains("text:style-name=\"" + list.NumberingName + "\"", StringComparison.Ordinal), "The list uses its numbering definition.");
        Require(content.Contains("list-level-style-bullet", StringComparison.Ordinal), "The list style is defined.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void BindsImageInBlockContent()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Image}}").Build());
        var png = CreatePng(24, 12);

        Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes)
                    .Bind("Image", new ImageBlock(png, ImageMediaType.Png) { AltText = "Alt" }),
            },
            out var package);

        using var zip = Open(package);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Require(names.Contains("Pictures/image1.png"), "The image is added as a media part.");
        Require(ReadText(zip, "META-INF/manifest.xml").Contains("Pictures/image1.png", StringComparison.Ordinal), "The manifest lists the image.");

        var document = ReadDocument(zip);
        Require(document.Descendants(Draw + "frame").Any(), "The frame is inserted.");
        Require(
            document.Descendants(Draw + "image").Any(image => image.Attribute(XLink + "href") is not null),
            "The image references the media part.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static void RenamesCollidingStyles()
    {
        // The template already contains a list that uses the numbering definition "Shared"; the
        // inserted list uses a definition of the same name, so one of the two list styles has to
        // be renamed (OF-B01: the list style carries the definition name).
        var templateBytes = CreateTemplate(SharedListDocument());
        var inserted = SharedListDocument();

        Write(
            inserted,
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out var package);

        using var zip = Open(package);
        var content = ReadText(zip, "content.xml");
        Require(content.Contains("text:list-style style:name=\"Shared\"", StringComparison.Ordinal), "The template list style is kept.");
        Require(content.Contains("text:list-style style:name=\"Shared_PD\"", StringComparison.Ordinal), "The colliding list style is renamed.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static PortableDocument SharedListDocument()
        => new PortableDocument(
            new DocumentMetadata(),
            new Section(new ListBlock("Shared", [new ListItem(new Paragraph(new TextRun("item")))])))
        {
            Numbering = [new NumberingDefinition("Shared", [new NumberingLevel { Kind = ListKind.Bullet }])],
        };

    private static void ReportsUnboundPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("{{Missing}}").Build());

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes) },
            out var package);

        Require(result.Warnings.Any(warning => warning.Message.Contains("Missing", StringComparison.Ordinal)), "An unbound placeholder is reported.");
        using var zip = Open(package);
        Require(ReadText(zip, "content.xml").Contains("{{Missing}}", StringComparison.Ordinal), "The unbound placeholder remains.");
    }

    private static void ReportsBoundPlaceholderMissingFromTemplate()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("No placeholder").Build());

        var result = Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).BindText("NotThere", "silently dropped"),
            },
            out var package);

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("{{NotThere}}", StringComparison.Ordinal)
                && warning.Message.Contains("not present in the template", StringComparison.Ordinal)),
            "A bound placeholder that the template does not contain is reported.");
        using var zip = Open(package);
        Require(!ReadText(zip, "content.xml").Contains("silently dropped", StringComparison.Ordinal), "The bound content is not written.");
    }

    private static void ReportsMissingBodyPlaceholder()
    {
        var templateBytes = CreateTemplate(PortableDocument.Create().Paragraph("No placeholder").Build());

        var result = Write(
            PortableDocument.Create().Paragraph("Contenttext").Build(),
            templateBytes,
            new OdtWriteOptions { Template = DocumentTemplate.FromBytes(templateBytes).Body("Content") },
            out var package);

        Require(result.Warnings.Any(warning => warning.Message.Contains("Content", StringComparison.Ordinal)), "The missing body placeholder is reported.");
        using var zip = Open(package);
        Require(ReadText(zip, "content.xml").Contains("Contenttext", StringComparison.Ordinal), "The content is appended instead of discarded.");
    }


    private static void BindsBlockConditions()
    {
        var document = TestLists.Fix(new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("A")),
                new Paragraph(new TextRun("{{If:Show}}")),
                new Paragraph(new TextRun("B")),
                new Paragraph(new TextRun("{{EndIf}}")),
                new Paragraph(new TextRun("C")),
                new TableBlock(new[]
                {
                    new TableRow([new TableCell("R1")]),
                    new TableRow([new TableCell("{{If:Show}}")]),
                    new TableRow([new TableCell("R2")]),
                    new TableRow([new TableCell("{{EndIf}}")]),
                    new TableRow([new TableCell("R3")]),
                }),
                TestLists.List(ListKind.Bullet, new[]
                {
                    new ListItem(new Paragraph(new TextRun("L1"))),
                    new ListItem(new Paragraph(new TextRun("{{If:Show}}"))),
                    new ListItem(new Paragraph(new TextRun("L2"))),
                    new ListItem(new Paragraph(new TextRun("{{EndIf}}"))),
                    new ListItem(new Paragraph(new TextRun("L3"))),
                }),
            })));
        var templateBytes = CreateTemplate(document);

        AssertBlockCondition(templateBytes, false, ["A", "C", "R1", "R3", "L1", "L3"], ["B", "R2", "L2"]);
        AssertBlockCondition(templateBytes, true, ["A", "B", "C", "R1", "R2", "R3", "L1", "L2", "L3"], []);
    }

    private static void AssertBlockCondition(byte[] templateBytes, bool value, string[] present, string[] absent)
    {
        Write(
            EmptyDocument(),
            templateBytes,
            new OdtWriteOptions
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

    private static void RejectsTemplateWithoutContentPart()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("foo.txt");
            using var stream = entry.Open();
            stream.WriteByte(0x01);
        }

        try
        {
            Write(EmptyDocument(), buffer.ToArray(), new OdtWriteOptions { Template = DocumentTemplate.FromBytes(buffer.ToArray()) }, out _);
        }
        catch (DocumentValidationException)
        {
            return;
        }

        throw new InvalidOperationException("A template without content.xml must be rejected.");
    }

    private static void RejectsInvalidTemplateBytes()
    {
        try
        {
            Write(
                EmptyDocument(),
                Array.Empty<byte>(),
                new OdtWriteOptions { Template = DocumentTemplate.FromBytes(new byte[] { 1, 2, 3, 4 }) },
                out _);
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
            new OdtWriteOptions
            {
                Template = DocumentTemplate.FromBytes(templateBytes).RepeatRows("Rows", rows),
            },
            out var package);

        Require(!result.HasWarnings, "Row repetition produces no warning.");

        using var zip = Open(package);
        var document = ReadDocument(zip);
        var tableRows = document.Descendants(Table + "table-row").Count();
        Require(tableRows == 3, $"The row is repeated (header + two rows), got {tableRows}.");
        Require(ContainsText(document, "Apple") && ContainsText(document, "Pear"), "The data values are bound.");
        Require(ContainsText(document, "3") && ContainsText(document, "5"), "The second column is bound.");
        Require(!ContainsText(document, "{{"), "No placeholder remains.");
        Require(Valid(package).Count == 0, "Package is valid: " + string.Join("; ", Valid(package)));
    }

    private static WriteResult Write(PortableDocument document, byte[] templateBytes, OdtWriteOptions options, out byte[] package)
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        package = buffer.ToArray();
        return result;
    }

    private static byte[] CreateTemplate(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
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

    private static void ReportsRoundedTablesInTemplate()
    {
        var source = CreateTemplate(PortableDocument.Create().Paragraph("{{Table}}").Build());
        var table = new TableBlock([new TableRow([new TableCell("x")])]) { CornerRadius = Length.FromPoints(6) };
        foreach (var bind in new[] { false, true })
        {
            var document = bind ? EmptyDocument() : new PortableDocument(new DocumentMetadata(), new Section([table]));
            var template = DocumentTemplate.FromBytes(source);
            if (bind) template = template.Bind("Table", table);
            var result = Write(document, source, new OdtWriteOptions { Template = template }, out _);
            Require(result.Warnings.Any(warning => warning.Message.Contains("rounded table corners", StringComparison.Ordinal)),
                "ODT template: unsupported rounded table corners must be reported.");
        }
    }

    private static PortableDocument EmptyDocument()
        => new(new DocumentMetadata(), new Section(Array.Empty<Block>()));

    private static ZipArchive Open(byte[] package)
        => new(new MemoryStream(package), ZipArchiveMode.Read);

    private static IReadOnlyList<string> Valid(byte[] package) => OdtPackageValidator.Validate(package);

    private static XDocument ReadDocument(ZipArchive zip)
    {
        using var stream = ReadEntry(zip, "content.xml");
        return XDocument.Load(stream);
    }

    private static bool ContainsText(XDocument document, string text)
        => document.DescendantNodes().OfType<XText>().Any(node => node.Value.Contains(text, StringComparison.Ordinal));

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
