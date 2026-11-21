using System.Xml.Linq;
using Reglet;

/// <summary>
/// AF-A: the preserving-round-trip harness. AF01 checks the canonical model comparer, AF02 the
/// package preservation diff. Both are the measurement tools for the follow-up workstreams
/// AF-B (DOCX) and AF-C (ODT).
/// </summary>
internal static class PreservationChecks
{
    internal static int Run()
    {
        try
        {
            ComparesModelsDeeply();
            ComparesIdenticalModels();
            ComparesPackages();
            DetectsPackageLosses();
            PreservesUnknownPartsByteIdentical();
            PreservesUnknownOdtParts();
            PreservesUnknownOdtPartsOnChange();
            PreservesOdtBodyWhenOnlyMetadataChanges();
            PreservesUnmodelledBlockWhenAnotherBlockChanges();
            PreservesUnmodelledStyledBlockWhenAnotherBlockChanges();
            PreservesHyperlinkBlockWhenAnotherBlockChanges();
            PreservesDrawingBlockAndManagesIds();
            PreservesUnmodelledBlockAcrossSections();
            PreservesUnmodelledBandBlockWhenBandTextIsReplaced();
            PreservesUnmodelledBandBlockWhenTheBodyChanges();
            PreservesUnmodelledBandBlockOfTheUnusedBandVariant();
            PreservesImageRelationshipOfAPreservedBandBlock();
            PreservesUnmodelledBlockAfterInsertion();
            PreservesUnmodelledBlockWithNoteReference();
            KeepsNoteIdsWhenANoteIsInsertedBefore();
            PreservesUnmodelledOdtBlockAcrossSections();
            PreservesUnmodelledOdtBlockAcrossSectionsWithVariables();
            PreservesUnmodelledOdtBlockWhenAnotherBlockChanges();
            PreservesUnmodelledOdtHeadingWhenAnotherBlockChanges();
            PreservesUnmodelledFormattedOdtParagraph();
            PreservesUnmodelledOdtListBlock();
            PreservesUnmodelledOdtDrawingWhenAnotherBlockChanges();
            PreservesUnmodelledOdtExternalLinkWhenAnotherBlockChanges();
            PreservesOdtParagraphWithInlineImage();
            KeepsSourcePictureNamesForPreservedDrawings();
            PreservesUnmodelledOdtTableBlock();
            PreservesUnmodelledFormattedOdtTableCell();
            PreservesUnmodelledTableLevelFormattedOdtTable();
            PreservesUnmodelledBrokenOdtTable();
            PreservesSourceOdtStyles();
            PreservesSourceOdtSettings();
            WritesUnmodifiedDocumentVerbatim();
            PreservesSourceSettings();
            PreservesSourceStyles();
            PreservesSourceNumbering();
            PreservesBodyWhenOnlyMetadataChanges();
            WarnsWhenUnmodelledContentIsLost();
            WarnsWhenUnmodelledContentIsWrittenAsOdtAndPdf();
            RoundTripPreservesAllParts(DocumentFormat.Docx);
            RoundTripPreservesAllParts(DocumentFormat.Odt);
            Console.WriteLine("Preservation (AF-A): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Preservation (AF-A): " + exception);
            return 1;
        }
    }

    /// <summary>AF01: differences are detected per path, including properties the old dump ignored.</summary>
    private static void ComparesModelsDeeply()
    {
        var baseline = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("text"))));
        var changed = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("text") { Kerning = Length.FromPoints(12) })));

        var differences = DocumentComparer.Compare(baseline, changed);
        Require(differences.Count == 1, $"Exactly one difference, was {differences.Count}.");
        Require(
            differences[0].Path.EndsWith(".Kerning", StringComparison.Ordinal),
            $"The path names the changed property: {differences[0].Path}");
    }

    /// <summary>AF01: a model equals itself (empty diff).</summary>
    private static void ComparesIdenticalModels()
    {
        var document = TestPackages.SampleDocument();
        var differences = DocumentComparer.Compare(document, document);
        Require(differences.Count == 0, $"Identical models differ: {string.Join("; ", differences)}");
    }

    /// <summary>AF02: an identical package is classified unchanged.</summary>
    private static void ComparesPackages()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx);
        var differences = PreservationDiff.Compare(package, package);
        Require(differences.Count > 0, "The package has parts.");
        Require(
            differences.All(difference => difference.Status == PackagePartStatus.Unchanged),
            "An identical package is unchanged throughout.");
    }

    /// <summary>AF02: a part that disappears is reported as a loss; a new part as added.</summary>
    private static void DetectsPackageLosses()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx);
        var extended = TestPackages.AddPart(package, "custom/extra.xml", "<extra/>");

        var losses = PreservationDiff.Losses(PreservationDiff.Compare(extended, package));
        Require(losses.Count == 1, $"Exactly one loss, was {losses.Count}.");
        Require(losses[0].Name == "custom/extra.xml", $"The lost part is named: {losses[0].Name}.");

        var added = PreservationDiff.Compare(package, extended)
            .SingleOrDefault(difference => difference.Name == "custom/extra.xml");
        Require(added.Status == PackagePartStatus.Added, "A new part is classified as added.");
    }

    /// <summary>
    /// AF02 baseline: reading and rewriting a document produced by our own writer must not drop
    /// any package part. (Byte-level changes are allowed; a missing part is not.)
    /// </summary>
    /// <summary>AF05: an unmodelled DOCX package part survives a read→write byte-identically.</summary>
    private static void PreservesUnknownPartsByteIdentical()
    {
        var package = TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx),
            "custom/extra.xml",
            "<extra value=\"42\"/>");

        var rewritten = TestPackages.Write(TestPackages.Read(package, DocumentFormat.Docx), DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF05 unknown part");
        var part = PreservationDiff.Compare(package, rewritten)
            .Single(difference => difference.Name == "custom/extra.xml");

        Require(
            part.Status == PackagePartStatus.Unchanged,
            $"The unknown DOCX part is preserved byte-identically, was {part.Status}.");
    }

    /// <summary>AF11: an unmodified ODT is written back byte-identically (open+save), including unknown parts.</summary>
    private static void PreservesUnknownOdtParts()
    {
        var package = TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt),
            "custom/extra.xml",
            "<extra/>");

        // The fixture itself must be a valid ODT (manifest lists the part, mimetype stored);
        // otherwise office applications reject the byte-identical copy as damaged.
        var fixtureErrors = OdtPackageValidator.Validate(package);
        Require(fixtureErrors.Count == 0, $"AF11: the ODT fixture with an unknown part is valid: {string.Join("; ", fixtureErrors)}");

        var rewritten = TestPackages.Write(TestPackages.Read(package, DocumentFormat.Odt), DocumentFormat.Odt);
        Require(
            package.AsSpan().SequenceEqual(rewritten),
            "An unmodified ODT is written back byte-identically (AF11).");
    }

    /// <summary>AF11: an unknown ODT part also survives a changed-body rewrite and stays listed in the manifest.</summary>
    private static void PreservesUnknownOdtPartsOnChange()
    {
        var package = TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt),
            "custom/extra.xml",
            "<extra/>");

        var document = TestPackages.Read(package, DocumentFormat.Odt);
        var rewritten = TestPackages.Write(WithAddedBlock(document), DocumentFormat.Odt);

        var part = PreservationDiff.Compare(package, rewritten)
            .Single(difference => difference.Name == "custom/extra.xml");
        Require(part.Status == PackagePartStatus.Unchanged, "AF11: the unknown ODT part survives a body change.");

        var errors = OdtPackageValidator.Validate(rewritten);
        Require(errors.Count == 0, $"AF11: the rewritten ODT package is valid: {string.Join("; ", errors)}");
    }

    /// <summary>AF12: an ODT metadata-only edit adopts the package, so content.xml survives byte-identically.</summary>
    private static void PreservesOdtBodyWhenOnlyMetadataChanges()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt);
        var document = TestPackages.Read(package, DocumentFormat.Odt);

        var modified = document with { Metadata = document.Metadata with { Title = "Renamed" } };
        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);

        var errors = OdtPackageValidator.Validate(rewritten);
        Require(errors.Count == 0, $"AF12: the rewritten ODT package is valid: {string.Join("; ", errors)}");

        Require(
            TestPackages.ReadPart(package, "content.xml").AsSpan()
                .SequenceEqual(TestPackages.ReadPart(rewritten, "content.xml")),
            "AF12: content.xml is byte-identical for an untouched body.");

        var meta = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "meta.xml"));
        Require(meta.Contains("Renamed", StringComparison.Ordinal), "AF12: the metadata is updated in the adopted package.");
    }

    /// <summary>AF13: a source ODT <c>styles.xml</c> is adopted when the section layout is unchanged.</summary>
    private static void PreservesSourceOdtStyles()
    {
        var document = PortableDocument.Create().Paragraph("first").Paragraph("second").Build();
        var package = TestPackages.Write(document, DocumentFormat.Odt);

        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var styles = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "styles.xml")));
        styles.Root!.Element(office + "styles")!.Add(new XElement(
            style + "style",
            new XAttribute(style + "name", "CustomNamed"),
            new XAttribute(style + "family", "paragraph")));
        package = TestPackages.ReplacePart(
            package,
            "styles.xml",
            System.Text.Encoding.UTF8.GetBytes(styles.ToString(SaveOptions.DisableFormatting)));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var rewrittenStyles = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "styles.xml"));
        Require(rewrittenStyles.Contains("CustomNamed", StringComparison.Ordinal), "AF13: the source styles survive a changed-body rewrite.");
    }

    /// <summary>AF13: a source ODT <c>settings.xml</c> survives a changed-body rewrite verbatim.</summary>
    private static void PreservesSourceOdtSettings()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt);
        const string settings = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<office:document-settings xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
            + "xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\" office:version=\"1.3\">"
            + "<office:settings><config:config-item-set config:name=\"custom\">"
            + "<config:config-item config:name=\"Marker\" config:type=\"string\">CUSTOM_SETTING</config:config-item>"
            + "</config:config-item-set></office:settings></office:document-settings>";
        package = TestPackages.ReplacePart(package, "settings.xml", System.Text.Encoding.UTF8.GetBytes(settings));

        var document = TestPackages.Read(package, DocumentFormat.Odt);
        var rewritten = TestPackages.Write(WithAddedBlock(document), DocumentFormat.Odt);

        Require(
            OdtPackageValidator.Validate(rewritten).Count == 0,
            "AF13: the rewritten ODT package is valid.");

        var rewrittenSettings = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "settings.xml"));
        Require(rewrittenSettings.Contains("CUSTOM_SETTING", StringComparison.Ordinal), "AF13: the source settings survive a changed-body rewrite.");
    }

    /// <summary>
    /// AF06: when one block of a single-section body changes, another (reference-unchanged,
    /// self-contained) block is re-emitted verbatim, so its unmodelled XML survives.
    /// </summary>
    private static void PreservesUnmodelledBlockWhenAnotherBlockChanges()
    {
        var package = TestPackages.Write(
            PortableDocument.Create().Paragraph("first").Paragraph("second").Paragraph("third").Build(),
            DocumentFormat.Docx);
        package = InjectCommentRange(package);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 per-block");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the unmodelled block is kept.");
        Require(documentXml.Contains("changed", StringComparison.Ordinal), "AF06: the changed block is regenerated.");
        Require(
            !TestPackages.ReadPart(package, "word/document.xml").AsSpan()
                .SequenceEqual(TestPackages.ReadPart(rewritten, "word/document.xml")),
            "AF06: the changed body differs from the source.");
    }

    /// <summary>
    /// AF06/AF08: a styled block (heading with <c>w:pStyle</c>) is also re-emitted verbatim when
    /// the source styles are adopted, so its unmodelled XML survives.
    /// </summary>
    private static void PreservesUnmodelledStyledBlockWhenAnotherBlockChanges()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Heading(HeadingLevel.Level1, new TextRun("title")),
                new Paragraph(new TextRun("body")),
                new Paragraph(new TextRun("tail")),
            }));
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 styled block");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the styled unmodelled block is kept.");
        Require(documentXml.Contains("changed", StringComparison.Ordinal), "AF06: the changed block is regenerated.");
    }

    /// <summary>
    /// AF06: a block with a hyperlink (<c>r:id</c>) that the reader does not model is kept
    /// verbatim; the referenced source relationship is preserved with a fresh id and remapped.
    /// </summary>
    private static void PreservesHyperlinkBlockWhenAnotherBlockChanges()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new Hyperlink("https://example.com/", "link")),
                new Paragraph(new TextRun("body")),
            }));
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 hyperlink block");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the hyperlink block is kept.");
        Require(documentXml.Contains("w:hyperlink", StringComparison.Ordinal), "AF06: the hyperlink survives.");

        var rels = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/_rels/document.xml.rels"));
        Require(rels.Contains("https://example.com/", StringComparison.Ordinal), "AF06: the hyperlink relationship is preserved.");
    }

    /// <summary>
    /// AF06: a drawing block is kept verbatim and the generated drawing ids start above the source
    /// maximum, so a preserved <c>wp:docPr</c> id never collides with a generated one.
    /// </summary>
    private static void PreservesDrawingBlockAndManagesIds()
    {
        var image = TestImages.RgbPng(2, 2, 200, 10, 10);
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new TextRun("first")),
                new ImageBlock(image, ImageMediaType.Png),
                new Paragraph(new TextRun("tail")),
            }));
        var package = InjectDrawingShape(TestPackages.Write(document, DocumentFormat.Docx), 7);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new ImageBlock(image, ImageMediaType.Png);
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 drawing block");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("id=\"7\"", StringComparison.Ordinal), "AF06: the preserved drawing keeps its docPr id.");
        Require(documentXml.Contains("id=\"8\"", StringComparison.Ordinal), "AF06: the regenerated drawing is offset above the source maximum.");
    }

    private static byte[] InjectDrawingShape(byte[] package, int id)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

        var paragraph = xml.Root!.Element(w + "body")!.Elements(w + "p").First();
        paragraph.Add(new XElement(
            w + "r",
            new XElement(
                w + "drawing",
                new XElement(
                    wp + "inline",
                    new XElement(wp + "docPr", new XAttribute("id", id.ToString()), new XAttribute("name", "shape"))))));

        var bytes = System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "word/document.xml", bytes);
    }

    /// <summary>AF06: per-block patching also works across several sections (blocks flattened).</summary>
    private static void PreservesUnmodelledBlockAcrossSections()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new[]
            {
                new Section(new Block[] { new Paragraph(new TextRun("first")), new Paragraph(new TextRun("second")) }),
                new Section(new Block[] { new Paragraph(new TextRun("third")) }),
            });
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 multi-section");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the unmodelled block survives across sections.");
        Require(documentXml.Contains("changed", StringComparison.Ordinal), "AF06: the changed block is regenerated.");
    }

    /// <summary>AF06: a changed block list still preserves the unchanged source blocks.</summary>
    private static void PreservesUnmodelledBlockAfterInsertion()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new TextRun("first")),
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks.Insert(1, new Paragraph(new TextRun("inserted")));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 inserted block");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the unchanged block survives an insertion.");
        Require(documentXml.Contains("inserted", StringComparison.Ordinal), "AF06: the inserted block is written.");
    }


    private static void PreservesUnmodelledBlockWithNoteReference()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new Footnote(new Paragraph(new TextRun("note")))),
                new Paragraph(new TextRun("second")),
            }));
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 note reference");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the unmodelled block with the note reference is kept.");
        Require(documentXml.Contains("footnoteReference", StringComparison.Ordinal), "The note reference is written.");
        Require(documentXml.Contains("changed", StringComparison.Ordinal), "The changed block is regenerated.");

        Require(
            NoteIds(TestPackages.ReadPart(package, "word/document.xml"))
                .SequenceEqual(NoteIds(TestPackages.ReadPart(rewritten, "word/document.xml"))),
            "AF06: a preserved note reference keeps its source id.");
    }

    private static void KeepsNoteIdsWhenANoteIsInsertedBefore()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new Footnote(new Paragraph(new TextRun("source note")))),
                new Paragraph(new TextRun("second")),
            }));
        var package = InjectCommentRange(TestPackages.Write(document, DocumentFormat.Docx));

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks.Insert(0, new Paragraph(new Footnote(new Paragraph(new TextRun("new note")))));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF06 inserted note");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "AF06: the unchanged source note block is kept.");
        Require(
            NoteIds(TestPackages.ReadPart(rewritten, "word/document.xml")).SequenceEqual(new[] { "2", "1" }),
            "AF06: a new note gets the next id and the source note keeps its id.");

        var footnotes = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/footnotes.xml"));
        Require(footnotes.Contains("new note", StringComparison.Ordinal) && footnotes.Contains("source note", StringComparison.Ordinal),
            "AF06: both note bodies are written.");
    }

    private static IReadOnlyList<string> NoteIds(byte[] part)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(part));
        return xml.Descendants(w + "footnoteReference")
            .Select(reference => (string)reference.Attribute(w + "id")!)
            .ToList();
    }


    private static void PreservesUnmodelledOdtBlockAcrossSectionsWithVariables()
    {
        var document = new PortableDocument(
            new DocumentMetadata
            {
                Language = "en-US",
                Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "Ada" },
            },
            new[]
            {
                new Section(new Block[] { new Paragraph(new TextRun("first")), new Paragraph(new TextRun("second")) }),
                new Section(new Block[] { new Paragraph(new TextRun("third")), new Paragraph(new TextRun("fourth")) }),
            });
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF12: the multi-section ODT with variables is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF12: variables do not disable section preservation.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF12: the changed block is regenerated.");
    }

    /// <summary>AF12: per-block patching works across several ODT sections.</summary>
    private static void PreservesUnmodelledOdtBlockAcrossSections()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new[]
            {
                new Section(new Block[] { new Paragraph(new TextRun("first")), new Paragraph(new TextRun("second")) }),
                new Section(new Block[] { new Paragraph(new TextRun("third")), new Paragraph(new TextRun("fourth")) }),
            });
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF12: the rewritten multi-section ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF12: the unmodelled block survives across ODT sections.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF12: the changed block is regenerated.");
    }

    /// <summary>
    /// AF12: when one block of a single-section ODT body changes, another style-free
    /// (reference-unchanged) block is re-emitted verbatim, so its unmodelled XML survives.
    /// </summary>
    private static void PreservesUnmodelledOdtBlockWhenAnotherBlockChanges()
    {
        var document = PortableDocument.Create().Paragraph("first").Paragraph("second").Paragraph("third").Build();
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF12: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF12: the unmodelled ODT block is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF12: the changed block is regenerated.");
    }

    /// <summary>AF13: a plain heading (<c>text:h</c> with a Heading-N style) is kept verbatim.</summary>
    private static void PreservesUnmodelledOdtHeadingWhenAnotherBlockChanges()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Heading(HeadingLevel.Level1, new TextRun("title")),
                new Paragraph(new TextRun("body")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt), "h");

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the heading block is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>
    /// AF13: a formatted ODT paragraph (automatic paragraph and text styles) is kept verbatim;
    /// the generated automatic names are offset and the source styles are preserved.
    /// </summary>
    private static void PreservesUnmodelledFormattedOdtParagraph()
    {
        var formatted = new Paragraph(new TextRun("bold") { Bold = true })
        {
            Style = new ParagraphStyle { Alignment = TextAlignment.Justify },
        };
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[] { formatted, new Paragraph(new TextRun("second")), new Paragraph(new TextRun("third")) }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[2] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the formatted block is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>AF13: a list block (fixed list styles + automatic paragraph styles) is kept verbatim.</summary>
    private static void PreservesUnmodelledOdtListBlock()
    {
        var document = TestLists.Document(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                TestLists.List(ListKind.Bullet, new[]
                {
                    new ListItem(new Paragraph(new TextRun("a"))),
                    new ListItem(new Paragraph(new TextRun("b"))),
                }),
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[2] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the list block is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>
    /// OF-A01: an unchanged ODT paragraph with unmodelled drawing content is re-emitted verbatim;
    /// the reader cannot map a frame without an image, so this is exactly the content that would
    /// otherwise be lost on the next change.
    /// </summary>
    private static void PreservesUnmodelledOdtDrawingWhenAnotherBlockChanges()
    {
        var document = PortableDocument.Create().Paragraph("first").Paragraph("second").Paragraph("third").Build();
        var package = InjectOdtDrawing(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        Require(
            package.AsSpan().SequenceEqual(TestPackages.Write(read, DocumentFormat.Odt)),
            "OF-A01: an unmodified ODT with a drawing is written back byte-identically.");
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "OF-A01: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(Drawings(content, "rect") == 1, "OF-A01: the unmodelled drawing survives.");
        Require(content.Contains("CustomRect", StringComparison.Ordinal), "OF-A01: the drawing name survives.");
        Require(content.Contains("changed", StringComparison.Ordinal), "OF-A01: the changed block is regenerated.");
    }

    /// <summary>
    /// OF-A01: an unchanged ODT paragraph with an external link is re-emitted verbatim (the link
    /// itself is model content, but unmodelled attributes on <c>text:a</c> survive).
    /// </summary>
    private static void PreservesUnmodelledOdtExternalLinkWhenAnotherBlockChanges()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new TextRun("first")),
                new Paragraph(new Hyperlink("https://example.com/", "example")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtLinkAttribute(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[0] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "OF-A01: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("KeptLink", StringComparison.Ordinal), "OF-A01: the unmodelled link attribute survives.");
        Require(content.Contains("https://example.com/", StringComparison.Ordinal), "OF-A01: the external link survives.");
        Require(content.Contains("changed", StringComparison.Ordinal), "OF-A01: the changed block is regenerated.");
    }

    /// <summary>
    /// OF-A01: a paragraph with an inline image is one source element that the reader splits into
    /// a paragraph and an image block. Both are kept once: the source paragraph is re-emitted and
    /// the derived image block is skipped instead of duplicating the picture.
    /// </summary>
    private static void PreservesOdtParagraphWithInlineImage()
    {
        var image = new ImageBlock(TestImages.RgbPng(4, 2, 0, 128, 255), ImageMediaType.Png);
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new Paragraph(new TextRun("before")),
                image,
                new Paragraph(new TextRun("after")),
            }));
        var package = MoveOdtImageIntoParagraph(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        Require(read.Sections[0].Blocks.Count == 3, "OF-A01: the inline frame is read as a paragraph plus an image block.");

        var rewritten = TestPackages.Write(WithAddedBlock(read), DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "OF-A01: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(Drawings(content, "image") == 1, "OF-A01: the inline image is written once, not twice.");
        Require(content.Contains("Pictures/image1.png", StringComparison.Ordinal), "OF-A01: the source picture reference is kept.");
        Require(content.Contains("added", StringComparison.Ordinal), "OF-A01: the inserted block is written.");
        Require(TestPackages.ReadPart(rewritten, "Pictures/image1.png").Length > 0, "OF-A01: the picture part survives.");
    }

    /// <summary>
    /// OF-A01: a generated picture never reuses a source picture name, so an unchanged drawing
    /// that references the source part keeps showing its own image.
    /// </summary>
    private static void KeepsSourcePictureNamesForPreservedDrawings()
    {
        var first = TestImages.RgbPng(2, 2, 255, 0, 0);
        var second = TestImages.RgbPng(2, 2, 0, 0, 255);
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new ImageBlock(first, ImageMediaType.Png),
                new Paragraph(new TextRun("text")),
            }));
        var read = TestPackages.Read(TestPackages.Write(document, DocumentFormat.Odt), DocumentFormat.Odt);

        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[0] = new ImageBlock(second, ImageMediaType.Png);
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "OF-A01: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(!content.Contains("Pictures/image1.png", StringComparison.Ordinal), "OF-A01: the generated image skips the source name.");
        Require(content.Contains("Pictures/image2.png", StringComparison.Ordinal), "OF-A01: the generated image gets a free name.");
        Require(!TestPackages.Names(rewritten).Contains("Pictures/image1.png"), "OF-A01: the replaced source picture is not carried along (ADR-0036).");
        Require(TestPackages.ReadPart(rewritten, "Pictures/image2.png").AsSpan().SequenceEqual(second), "OF-A01: the generated picture part is written.");
    }

    /// <summary>Counts the elements of the drawing namespace with the given local name.</summary>
    private static int Drawings(string content, string localName)
    {
        XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        return XDocument.Parse(content).Descendants(draw + localName).Count();
    }

    /// <summary>Adds a drawing frame with an unmodelled shape to a paragraph.</summary>
    private static byte[] InjectOdtDrawing(byte[] package)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        XNamespace svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

        var body = xml.Root!.Element(office + "body")!.Element(office + "text")!;
        var container = body.Elements(text + "section").FirstOrDefault() ?? body;
        container.Descendants(text + "p").First().Add(
            new XElement(
                draw + "frame",
                new XAttribute(draw + "name", "CustomFrame"),
                new XAttribute(text + "anchor-type", "as-char"),
                new XAttribute(svg + "width", "2cm"),
                new XAttribute(svg + "height", "1cm"),
                new XElement(
                    draw + "rect",
                    new XAttribute(draw + "name", "CustomRect"),
                    new XAttribute(svg + "x", "0cm"),
                    new XAttribute(svg + "y", "0cm"),
                    new XAttribute(svg + "width", "2cm"),
                    new XAttribute(svg + "height", "1cm"))));

        return TestPackages.ReplacePart(
            package,
            "content.xml",
            System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>Adds an office:name attribute to the first external link.</summary>
    private static byte[] InjectOdtLinkAttribute(byte[] package)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

        xml.Descendants(text + "a").First().SetAttributeValue(office + "name", "KeptLink");
        return TestPackages.ReplacePart(
            package,
            "content.xml",
            System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>Moves the image frame of its own paragraph into the preceding text paragraph.</summary>
    private static byte[] MoveOdtImageIntoParagraph(byte[] package)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

        var body = xml.Root!.Element(office + "body")!.Element(office + "text")!;
        var container = body.Elements(text + "section").FirstOrDefault() ?? body;
        var elements = container.Elements().ToList();
        var imageIndex = elements.FindIndex(element => element.Descendants(draw + "frame").Any());
        var target = elements.Take(imageIndex).Last(element => element.Name == text + "p");
        foreach (var frame in elements[imageIndex].Elements(draw + "frame").ToList())
        {
            frame.Remove();
            target.Add(frame);
        }

        elements[imageIndex].Remove();
        return TestPackages.ReplacePart(
            package,
            "content.xml",
            System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>AF13: a plain table block (fixed cell/column styles) is kept verbatim.</summary>
    private static void PreservesUnmodelledOdtTableBlock()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new TableBlock(new[]
                {
                    new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                    new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                }),
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[2] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the table block is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>AF13: a table with a directly formatted cell (automatic <c>TC</c> style) is kept verbatim.</summary>
    private static void PreservesUnmodelledFormattedOdtTableCell()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new TableBlock(new[]
                {
                    new TableRow(new[]
                    {
                        new TableCell("A") { Background = Color.FromHex("#FF0000") },
                        new TableCell("B"),
                    }),
                    new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                }),
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[2] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the formatted-cell table is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>AF13: a table with table-level formatting (automatic <c>TT</c> style) is kept verbatim.</summary>
    private static void PreservesUnmodelledTableLevelFormattedOdtTable()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new TableBlock(new[]
                {
                    new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                    new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                })
                {
                    Border = new ParagraphBorder(),
                },
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[2] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the table-level formatted table is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    /// <summary>AF13: a table with a page break (break-parent table style) is kept verbatim.</summary>
    private static void PreservesUnmodelledBrokenOdtTable()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Block[]
            {
                new PageBreak(),
                new TableBlock(new[]
                {
                    new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                    new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                })
                {
                    Border = new ParagraphBorder(),
                },
                new Paragraph(new TextRun("second")),
                new Paragraph(new TextRun("third")),
            }));
        var package = InjectOdtAnnotation(TestPackages.Write(document, DocumentFormat.Odt));

        var read = TestPackages.Read(package, DocumentFormat.Odt);
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks[^1] = new Paragraph(new TextRun("changed"));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        var rewritten = TestPackages.Write(modified, DocumentFormat.Odt);
        Require(OdtPackageValidator.Validate(rewritten).Count == 0, "AF13: the rewritten ODT package is valid.");

        var content = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "content.xml"));
        Require(content.Contains("office:annotation", StringComparison.Ordinal), "AF13: the broken table is kept.");
        Require(content.Contains("changed", StringComparison.Ordinal), "AF13: the changed block is regenerated.");
    }

    private static byte[] InjectOdtAnnotation(byte[] package, string localName = "p")
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";

        var body = xml.Root!.Element(office + "body")!.Element(office + "text")!;
        var container = body.Elements(text + "section").FirstOrDefault() ?? body;
        container.Descendants(text + localName).First().Add(new XElement(
            office + "annotation",
            new XElement(dc + "creator", "me"),
            new XElement(dc + "date", "2020-01-01T00:00:00"),
            new XElement(text + "p", "note")));

        var bytes = System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "content.xml", bytes);
    }

    /// <summary>AF07: an unmodified DOCX is written back byte-identically; a rebuilt one is regenerated.</summary>
    private static void WritesUnmodifiedDocumentVerbatim()
    {
        var source = TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx),
            "custom/extra.xml",
            "<extra/>");

        var document = TestPackages.Read(source, DocumentFormat.Docx);
        Require(
            source.AsSpan().SequenceEqual(TestPackages.Write(document, DocumentFormat.Docx)),
            "An unmodified DOCX is written back byte-identically (open+save).");

        // Rebuilding the model produces a new instance; the body is unchanged, so the original
        // package is adopted and only the metadata is patched (AF07 body-unchanged).
        var modified = document with { Metadata = document.Metadata with { Title = "Changed" } };
        var regenerated = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(regenerated, "AF07 modified");
        Require(!source.AsSpan().SequenceEqual(regenerated), "A modified DOCX is rewritten.");

        var part = PreservationDiff.Compare(source, regenerated)
            .Single(difference => difference.Name == "custom/extra.xml");
        Require(part.Status == PackagePartStatus.Unchanged, "AF05 still preserves the unknown part after a change.");
    }

    /// <summary>
    /// AF08: source <c>settings.xml</c> content (compatibility flags, rsids) survives a modified
    /// rewrite; the managed elements are merged in without clobbering the rest.
    /// </summary>
    private static void PreservesSourceSettings()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx);
        const string settings = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:rsids><w:rsidRoot w:val=\"00AB12CD\"/></w:rsids>"
            + "</w:settings>";
        package = TestPackages.ReplacePart(package, "word/settings.xml", System.Text.Encoding.UTF8.GetBytes(settings));

        var document = TestPackages.Read(package, DocumentFormat.Docx);
        var modified = WithAddedBlock(document);

        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF08 settings");
        var rewrittenSettings = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/settings.xml"));

        Require(rewrittenSettings.Contains("rsidRoot", StringComparison.Ordinal), "The source settings survive a rewrite (AF08).");
        Require(rewrittenSettings.Contains("compat", StringComparison.Ordinal), "The managed compatibility setting is present (AF08).");
    }

    /// <summary>
    /// AF08: source styles (custom docDefaults, character/table/latent styles) survive a modified
    /// rewrite while generated styles the source lacks are merged in; changing the defaults
    /// regenerates instead of silently keeping the source.
    /// </summary>
    private static void PreservesSourceStyles()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx);
        const string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Georgia\"/></w:rPr></w:rPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"CustomX\"><w:name w:val=\"Custom X\"/></w:style>"
            + "</w:styles>";
        package = TestPackages.ReplacePart(package, "word/styles.xml", System.Text.Encoding.UTF8.GetBytes(styles));

        var document = TestPackages.Read(package, DocumentFormat.Docx);
        var modified = WithAddedBlock(document);
        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF08 styles");

        var rewrittenStyles = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/styles.xml"));
        Require(rewrittenStyles.Contains("CustomX", StringComparison.Ordinal), "The source style survives (AF08).");
        Require(rewrittenStyles.Contains("Georgia", StringComparison.Ordinal), "The source docDefaults survive (AF08).");
        Require(rewrittenStyles.Contains("Heading1", StringComparison.Ordinal), "A missing generated style is merged in (AF08).");

        // Changing the document defaults must not silently keep the source styles.
        var changedDefaults = document with { Defaults = DocumentDefaults.Default with { FontFamily = "Arial" } };
        var regenerated = TestPackages.Write(changedDefaults, DocumentFormat.Docx);
        var regeneratedStyles = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(regenerated, "word/styles.xml"));
        Require(!regeneratedStyles.Contains("Georgia", StringComparison.Ordinal), "Changed defaults regenerate the styles (AF08).");
        Require(regeneratedStyles.Contains("Arial", StringComparison.Ordinal), "The changed default wins (AF08).");
    }

    /// <summary>
    /// AF09: regenerating a changed document whose source had content the reader does not model
    /// must not happen silently. Open+save stays silent (verbatim), a change warns, and
    /// <see cref="UnsupportedContentBehavior.Fail"/> turns it into an error.
    /// </summary>
    private static void WarnsWhenUnmodelledContentIsLost()
    {
        var package = InjectEmbeddedObject(
            TestPackages.Write(PortableDocument.Create().Paragraph("source").Build(), DocumentFormat.Docx));
        var document = TestPackages.Read(package, DocumentFormat.Docx);
        Require(document.SourceHasUnmodelledContent, "The reader recorded the unmodelled embedded object.");

        var unchanged = WriteWithResult(document, DocxWriteOptions.Default);
        Require(
            !unchanged.Warnings.Any(warning => warning.Message.Contains("not preserved", StringComparison.Ordinal)),
            "Open+save does not warn (verbatim).");

        var modified = WithAddedBlock(document);
        var result = WriteWithResult(modified, DocxWriteOptions.Default);
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("not preserved", StringComparison.Ordinal)),
            "AF09: a change warns that unmodelled content is lost.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("embedded object", StringComparison.Ordinal)),
            "AF09: the warning names the reader finding.");

        Throws<UnsupportedFeatureException>(
            () => WriteWithResult(modified, new DocxWriteOptions { UnsupportedContent = UnsupportedContentBehavior.Fail }),
            "AF09: Fail turns the loss into an error.");
    }

    /// <summary>
    /// AF08: a source <c>numbering.xml</c> is kept and the generated definitions are appended with
    /// ids above the source maxima, so no numId collides and the schema order is preserved.
    /// </summary>
    private static void PreservesSourceNumbering()
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx);

        // Add custom definitions (abstractNumId 5, numId 7) to the generated numbering so the
        // document's own list references stay resolvable.
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var numbering = XDocument.Parse(
            System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/numbering.xml")));
        var root = numbering.Root!;
        root.Elements(w + "num").First().AddBeforeSelf(
            new XElement(
                w + "abstractNum",
                new XAttribute(w + "abstractNumId", "5"),
                new XElement(
                    w + "lvl",
                    new XAttribute(w + "ilvl", "0"),
                    new XElement(w + "start", new XAttribute(w + "val", "1")),
                    new XElement(w + "numFmt", new XAttribute(w + "val", "decimal")),
                    new XElement(w + "lvlText", new XAttribute(w + "val", "%1.")))));
        root.Add(
            new XElement(
                w + "num",
                new XAttribute(w + "numId", "7"),
                new XElement(w + "abstractNumId", new XAttribute(w + "val", "5"))));
        package = TestPackages.ReplacePart(
            package,
            "word/numbering.xml",
            System.Text.Encoding.UTF8.GetBytes(numbering.ToString(SaveOptions.DisableFormatting)));

        var document = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = document.Sections.ToList();
        sections[0] = sections[0] with
        {
            Blocks = sections[0].Blocks.Concat(
            [
                TestLists.List(ListKind.Bullet, [new ListItem(new Paragraph(new TextRun("added")))]),
            ]).ToList(),
        };
        var rewritten = TestPackages.Write(
            TestLists.WithList(document with { Sections = sections }, (ListBlock)sections[0].Blocks[^1]),
            DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF08 numbering");

        var rewrittenNumbering = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/numbering.xml"));
        Require(rewrittenNumbering.Contains("w:abstractNumId=\"5\"", StringComparison.Ordinal), "AF08: the source abstractNum survives.");
        Require(rewrittenNumbering.Contains("w:numId=\"7\"", StringComparison.Ordinal), "AF08: the source num survives.");
        Require(rewrittenNumbering.Contains("w:numId=\"8\"", StringComparison.Ordinal), "AF08: the generated numId is offset above the source maximum.");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("w:numId w:val=\"8\"", StringComparison.Ordinal), "AF08: the regenerated list references the offset numId.");

        // ECMA-376: all abstractNum before all num.
        var rewrittenDocument = XDocument.Parse(rewrittenNumbering);
        var names = rewrittenDocument.Root!.Elements().Select(element => element.Name.LocalName).ToList();
        Require(
            names.LastIndexOf("abstractNum") < names.IndexOf("num"),
            "AF08: numbering.xml keeps the schema order (abstractNum before num).");
    }

    /// <summary>
    /// AF07 (body unchanged): a metadata-only edit adopts the original package, so unmodelled body
    /// XML survives and only <c>docProps/core.xml</c> changes.
    /// </summary>
    private static void PreservesBodyWhenOnlyMetadataChanges()
    {
        var package = InjectCommentRange(TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx));
        var document = TestPackages.Read(package, DocumentFormat.Docx);

        var modified = document with { Metadata = document.Metadata with { Title = "Renamed" } };
        var rewritten = TestPackages.Write(modified, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "AF07 body unchanged");

        var documentXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/document.xml"));
        Require(documentXml.Contains("commentRangeStart", StringComparison.Ordinal), "Unmodelled body XML survives a metadata-only edit (AF07).");

        var core = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "docProps/core.xml"));
        Require(core.Contains("Renamed", StringComparison.Ordinal), "The metadata is updated in the adopted package (AF07).");

        Require(
            TestPackages.ReadPart(package, "word/document.xml").AsSpan()
                .SequenceEqual(TestPackages.ReadPart(rewritten, "word/document.xml")),
            "document.xml is byte-identical for an untouched body (AF07).");
    }

    /// <summary>Returns a copy with one block appended (a body change, so the body is regenerated).</summary>
    private static PortableDocument WithAddedBlock(PortableDocument document)
    {
        var sections = document.Sections.ToList();
        sections[0] = sections[0] with
        {
            Blocks = sections[0].Blocks.Concat([new Paragraph(new TextRun("added"))]).ToList(),
        };
        return document with { Sections = sections };
    }

    /// <summary>
    /// OF-B08: replacing text in one paragraph of a header keeps the source XML of the other
    /// paragraphs of that header, so unmodelled XML inside them survives the change.
    /// </summary>
    private static void PreservesUnmodelledBandBlockWhenBandTextIsReplaced()
    {
        var package = InjectBandCommentRange(
            TestPackages.Write(BandSampleDocument(), DocumentFormat.Docx),
            "word/header1.xml",
            1);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        Require(
            DocumentSearch.Find(read, new TextQuery("first header line")).Count == 1,
            "OF-B08: the header of a read document is searched.");
        var replaced = DocumentSearch.Replace(read, new TextQuery("first"), "changed");

        var rewritten = TestPackages.Write(replaced, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "OF-B08 band blocks");

        var headerXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/header1.xml"));
        Require(
            headerXml.Contains("commentRangeStart", StringComparison.Ordinal),
            "OF-B08: the band paragraph that was not replaced keeps its unmodelled XML.");
        Require(headerXml.Contains("second header line", StringComparison.Ordinal), "OF-B08: the other band text is kept.");
        Require(
            BandText(TestPackages.Read(rewritten, DocumentFormat.Docx).Sections[0].Header)
                == "changed header line\nsecond header line",
            "OF-B08: the replaced band text is written and read back.");

        var footerXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/footer1.xml"));
        Require(footerXml.Contains("footer text", StringComparison.Ordinal), "OF-B08: the untouched footer is kept.");

        var roundTrip = TestPackages.Read(rewritten, DocumentFormat.Docx);
        Require(
            BandText(roundTrip.Sections[0].Header) == "changed header line\nsecond header line",
            "OF-B08: the rewritten bands read back with the replaced text.");
    }

    /// <summary>
    /// OF-B08: a band that the model did not change keeps its unmodelled XML although the body is
    /// regenerated, because its blocks are re-emitted as their source XML.
    /// </summary>
    private static void PreservesUnmodelledBandBlockWhenTheBodyChanges()
    {
        var package = InjectBandCommentRange(
            TestPackages.Write(BandSampleDocument(), DocumentFormat.Docx),
            "word/header1.xml",
            0);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var sections = read.Sections.ToList();
        sections[0] = sections[0] with { Blocks = [new Paragraph(new TextRun("changed body"))] };

        var rewritten = TestPackages.Write(read with { Sections = sections }, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "OF-B08 unchanged band");

        var headerXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/header1.xml"));
        Require(
            headerXml.Contains("commentRangeStart", StringComparison.Ordinal),
            "OF-B08: the header of a document whose body changed keeps its unmodelled XML.");
        Require(headerXml.Contains("first header line", StringComparison.Ordinal), "OF-B08: the band text is kept.");
        Require(
            BandText(TestPackages.Read(rewritten, DocumentFormat.Docx).Sections[0].Header)
                == "first header line\nsecond header line",
            "OF-B08: the untouched band is written and read back unchanged.");
    }

    /// <summary>
    /// OF-B08: the source band parts are matched to the band variants they belong to. The band that
    /// is not used for the replaced text keeps its unmodelled XML.
    /// </summary>
    private static void PreservesUnmodelledBandBlockOfTheUnusedBandVariant()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Paragraph(new TextRun("body")))
            {
                Footer = new HeaderFooter(new Paragraph(new TextRun("default footer"))),
                FirstFooter = new HeaderFooter(new Paragraph(new TextRun("first page footer"))),
            });
        var package = InjectBandCommentRange(TestPackages.Write(document, DocumentFormat.Docx), "word/footer2.xml", 0);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var replaced = DocumentSearch.Replace(read, new TextQuery("default"), "changed");
        Require(
            BandText(replaced.Sections[0].Footer) == "changed footer",
            "OF-B08: only the default footer text is replaced.");
        Require(
            BandText(replaced.Sections[0].FirstFooter) == "first page footer",
            "OF-B08: the first page footer keeps its text.");

        var rewritten = TestPackages.Write(replaced, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "OF-B08 band variants");

        var firstPageFooter = System.Text.Encoding.UTF8.GetString(
            TestPackages.ReadPart(rewritten, FirstFooterPart(rewritten)));
        Require(
            firstPageFooter.Contains("commentRangeStart", StringComparison.Ordinal),
            "OF-B08: the band variant that was not replaced keeps its unmodelled XML.");
        var readBack = TestPackages.Read(rewritten, DocumentFormat.Docx);
        Require(BandText(readBack.Sections[0].Footer) == "changed footer", "OF-B08: the replaced band variant is written and read back.");
        Require(
            BandText(readBack.Sections[0].FirstFooter) == "first page footer",
            "OF-B08: the band variant that was not replaced is written and read back.");
    }

    /// <summary>
    /// OF-B08: a preserved band block keeps its relationships: the picture relationship is
    /// registered for the band part again, so the image of the band is not left dangling.
    /// </summary>
    private static void PreservesImageRelationshipOfAPreservedBandBlock()
    {
        var image = new ImageBlock(TestImages.RgbPng(4, 2, 0, 128, 255), ImageMediaType.Png) { AltText = "Logo" };
        var document = new PortableDocument(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Paragraph(new TextRun("body text")))
            {
                Header = new HeaderFooter(new Block[] { new Paragraph(new TextRun("first header line")), image }),
            });
        var package = InjectBandCommentRange(TestPackages.Write(document, DocumentFormat.Docx), "word/header1.xml", 1);

        var read = TestPackages.Read(package, DocumentFormat.Docx);
        var replaced = DocumentSearch.Replace(read, new TextQuery("first"), "changed");

        var rewritten = TestPackages.Write(replaced, DocumentFormat.Docx);
        RequireValidDocx(rewritten, "OF-B08 band image");

        var headerXml = System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(rewritten, "word/header1.xml"));
        Require(
            headerXml.Contains("commentRangeStart", StringComparison.Ordinal),
            "OF-B08: the band paragraph that holds the image keeps its unmodelled XML.");
        Require(headerXml.Contains("drawing", StringComparison.Ordinal), "OF-B08: the image of the band is written.");

        var readBack = TestPackages.Read(rewritten, DocumentFormat.Docx);
        Require(
            readBack.Sections[0].Header!.Blocks.OfType<ImageBlock>().Count() == 1,
            "OF-B08: the image of the preserved band block is read back.");
        Require(BandText(readBack.Sections[0].Header) == "changed header line", "OF-B08: the band text is still replaced.");
    }

    /// <summary>The part of the first page footer in a rewritten package (part names may shift).</summary>
    private static string FirstFooterPart(byte[] package)
    {
        using var archive = new System.IO.Compression.ZipArchive(
            new MemoryStream(package, writable: false),
            System.IO.Compression.ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("word/footer", StringComparison.Ordinal)
                || !entry.FullName.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            if (System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Contains("first page footer", StringComparison.Ordinal))
            {
                return entry.FullName;
            }
        }

        throw new InvalidOperationException("OF-B08: the first page footer part is missing.");
    }

    private static PortableDocument BandSampleDocument()
        => new(
            new DocumentMetadata { Language = "en-US" },
            new Section(new Paragraph(new TextRun("body text")))
            {
                Header = new HeaderFooter(new Block[]
                {
                    new Paragraph(new TextRun("first header line")),
                    new Paragraph(new TextRun("second header line")),
                }),
                Footer = new HeaderFooter(new Paragraph(new TextRun("footer text"))),
            });

    private static string BandText(HeaderFooter? band)
        => string.Join(
            "\n",
            (band?.Blocks ?? []).OfType<Paragraph>().Select(paragraph =>
                string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text))));

    /// <summary>OF-B08: marks one paragraph of a header/footer part with unmodelled XML.</summary>
    private static byte[] InjectBandCommentRange(byte[] package, string partName, int paragraphIndex)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, partName)));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var paragraph = xml.Root!.Elements(w + "p").ElementAt(paragraphIndex);
        paragraph.AddFirst(new XElement(w + "commentRangeStart", new XAttribute(w + "id", "1")));
        paragraph.Add(new XElement(w + "commentRangeEnd", new XAttribute(w + "id", "1")));

        var bytes = System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, partName, bytes);
    }

    private static byte[] InjectCommentRange(byte[] package)
    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var paragraph = xml.Root!.Element(w + "body")!.Elements(w + "p").First();
        paragraph.AddFirst(new XElement(w + "commentRangeStart", new XAttribute(w + "id", "1")));
        paragraph.Add(new XElement(w + "commentRangeEnd", new XAttribute(w + "id", "1")));

        var bytes = System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "word/document.xml", bytes);
    }

    private static byte[] InjectEmbeddedObject(byte[] package)    {
        var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var paragraph = xml.Root!.Element(w + "body")!.Elements(w + "p").First();
        paragraph.Add(new XElement(w + "r", new XElement(w + "object")));

        var bytes = System.Text.Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "word/document.xml", bytes);
    }


    private static void WarnsWhenUnmodelledContentIsWrittenAsOdtAndPdf()
    {
        var package = InjectEmbeddedObject(
            TestPackages.Write(PortableDocument.Create().Paragraph("source").Build(), DocumentFormat.Docx));
        var document = TestPackages.Read(package, DocumentFormat.Docx);

        using var odt = new MemoryStream();
        var odtResult = new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(
            odtResult.Warnings.Any(warning => warning.Message.Contains("does not model", StringComparison.Ordinal)),
            "ODT warns when a source with unmodelled content is rebuilt.");

        using var pdf = new MemoryStream();
        var pdfResult = new PdfDocumentWriter().WriteAsync(document, pdf).GetAwaiter().GetResult();
        Require(
            pdfResult.Warnings.Any(warning => warning.Message.Contains("not represented", StringComparison.Ordinal)),
            "PDF warns when a source with unmodelled content is rebuilt.");
    }

    private static WriteResult WriteWithResult(PortableDocument document, DocxWriteOptions options)
    {
        using var buffer = new MemoryStream();
        return new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
    }

    private static void RoundTripPreservesAllParts(DocumentFormat format)
    {
        var package = TestPackages.Write(TestPackages.SampleDocument(), format);

        using var read = new MemoryStream(package, writable: false);
        var document = DocumentReader.Read(read, format).Document;

        var rewritten = TestPackages.Write(document, format);
        if (format == DocumentFormat.Docx)
        {
            RequireValidDocx(rewritten, "AF08 round trip");
        }

        var losses = PreservationDiff.Losses(PreservationDiff.Compare(package, rewritten));
        Require(losses.Count == 0, $"{format}: the round trip lost parts: {string.Join(", ", losses)}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>AF08: the produced DOCX package must be structurally valid (no dangling rels).</summary>
    private static void RequireValidDocx(byte[] package, string context)
    {
        var errors = DocxPackageValidator.Validate(package);
        Require(errors.Count == 0, $"{context}: invalid DOCX package: {string.Join("; ", errors)}");
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
