using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// named paragraph styles (phase AC, AC01): the styles of a document are read back and written
/// again, so a read → write cycle keeps them.
/// </summary>
internal static class NamedStyleChecks
{
    internal static int Run()
    {
        try
        {
            ReadsDocxStyles();
            ReadsOdtStyles();
            ReadsDocxStyleInheritance();
            ReadsDocxParagraphStyle();
            ReadsDocxStyleRunFormatting();
            ReadsOdtStyleRunFormatting();
            ReadsOdtSpanTurnsBoldOff();
            ReadsOdtNestedTextStyles();
            ReadsOdtNoteFormatting();
            ReadsOdtNoteParagraphStyle();
            IgnoresEmptyOdtStyleName();
            ReadsDocumentRunDefaults();
            RoundTripsDocumentRunDefaults();
            ReadsDocxStyleNumbering();
            ReadsDocxParagraphDefaults();
            ReadsOdtParagraphDefaults();
            ReadsOdtDefaultRunFormatting();
            ReadsOdtStyleInheritance();
            SurvivesStyleCycles();
            ResolvesLongStyleChainsInLinearTime();
            KeepsOwnStyleProperties();
            IgnoresOdtBandStyles();
            WritesNamedStyleProperties();
            WritesStyleLinks();
            WritesChangedStyleLinks();
            DropsLinksToUnwrittenStyles();
            WritesStylesAgain();
            Console.WriteLine("Named styles (AC01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Named styles (AC01): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var quote = new ParagraphStyle
        {
            StyleName = "Quote",
            Alignment = TextAlignment.Center,
            SpaceBefore = Length.FromPoints(6),
            SpaceAfter = Length.FromPoints(12),
            LeftIndent = Length.FromPoints(24),
        };

        var paragraph = new Paragraph(new TextRun("Zitat")) { Style = quote };
        return new PortableDocument(new DocumentMetadata(), new Section(new Block[] { paragraph }))
        {
            Styles = [quote],
        };
    }

    private static void ReadsDocxStyles()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        using var input = new MemoryStream(buffer.ToArray());
        var result = new DocxDocumentReader().Read(input);

        var style = result.Document.Styles.FirstOrDefault(style => style.StyleName == "Quote");
        Require(style is not null, "DOCX: the named style is read.");
        Require(style!.Alignment == TextAlignment.Center, "DOCX: the alignment of the named style is read.");
        Require(style.SpaceBefore?.Points == 6, "DOCX: the space before of the named style is read.");
        Require((style.LeftIndent?.Points ?? 0) == 24, "DOCX: the indent of the named style is read.");
    }

    private static void ReadsOdtStyles()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        using var input = new MemoryStream(buffer.ToArray());
        var result = new OdtDocumentReader().Read(input);

        var style = result.Document.Styles.FirstOrDefault(style => style.StyleName == "Quote");
        Require(style is not null, "ODT: the named style is read.");
        Require(style!.Alignment == TextAlignment.Center, "ODT: the alignment of the named style is read.");
        Require(style.SpaceBefore?.Points == 6, "ODT: the space before of the named style is read.");
        Require((style.LeftIndent?.Points ?? 0) == 24, "ODT: the indent of the named style is read.");
    }

    private static void WritesStylesAgain()
    {
        // Read → write keeps the named styles in the package.
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();

        using var input = new MemoryStream(buffer.ToArray());
        var read = new DocxDocumentReader().Read(input).Document;

        using var second = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(read, second).GetAwaiter().GetResult();

        using var zip = new System.IO.Compression.ZipArchive(
            new MemoryStream(second.ToArray()),
            System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/styles.xml");
        Require(entry is not null, "DOCX: the styles part exists.");
        using var reader = new StreamReader(entry!.Open());
        var styles = reader.ReadToEnd();
        Require(styles.Contains("w:styleId=\"Quote\"", StringComparison.Ordinal), "DOCX: the named style is written again.");
    }

    /// <summary>A paragraph style inherits the formatting of its parent style (w:basedOn).</summary>
    private static void ReadsDocxStyleInheritance()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Base'><w:pPr><w:jc w:val='center'/>"
            + "<w:spacing w:before='120'/><w:keepNext/><w:pBdr>"
            + "<w:top w:val='single' w:sz='8' w:color='FF0000'/>"
            + "<w:bottom w:val='single' w:sz='8' w:color='FF0000'/></w:pBdr></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='Child'><w:basedOn w:val='Base'/>"
            + "<w:pPr><w:spacing w:after='60'/><w:pBdr>"
            + "<w:left w:val='single' w:sz='8' w:color='0000FF'/></w:pBdr></w:pPr></w:style>"
            + "</w:styles>";

        using var stream = new MemoryStream(DocxPackage(styles));
        var document = new DocxDocumentReader().Read(stream).Document;

        var child = document.Styles.Single(style => style.StyleName == "Child");
        Require(child.Alignment == TextAlignment.Center, "DOCX: the inherited alignment is missing.");
        Require(child.SpaceBefore?.Points == 6, "DOCX: the inherited space before is missing: " + child.SpaceBefore?.Points);
        Require(child.SpaceAfter?.Points == 3, "DOCX: the own space after is missing: " + child.SpaceAfter?.Points);
        Require(child.KeepWithNext == true, "DOCX: the inherited keep-with-next is missing.");
        // The borders are one property with several sides: the own left border must not drop the
        // inherited top and bottom border.
        Require(child.Borders is { Top: not null, Bottom: not null },
            "DOCX: the inherited borders are missing.");
        Require(child.Borders?.Left is not null, "DOCX: the own border is missing.");
    }

    /// <summary>A style that is based on itself, on a cycle or on a style the model does not name is still read.</summary>
    private static void SurvivesStyleCycles()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var docx = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Normal'><w:pPr><w:keepLines/></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='A'><w:basedOn w:val='B'/>"
            + "<w:pPr><w:jc w:val='center'/></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='B'><w:basedOn w:val='A'/>"
            + "<w:pPr><w:spacing w:after='60'/></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='C'><w:basedOn w:val='Normal'/></w:style>"
            + "</w:styles>";

        using var stream = new MemoryStream(DocxPackage(docx));
        var styles = new DocxDocumentReader().Read(stream).Document.Styles;

        var a = styles.Single(style => style.StyleName == "A");
        Require(a.Alignment == TextAlignment.Center && a.SpaceAfter?.Points == 3,
            "DOCX: a cycle lost the formatting.");
        // Normal is a built-in style and not part of Styles, but its formatting is inherited.
        Require(styles.Single(style => style.StyleName == "C").KeepTogether == true,
            "DOCX: the formatting of the built-in parent is missing.");

        // The same for ODT, whose automatic styles may reuse a name of the shared styles.
        var odt = OdtPackage(
            "<office:styles>"
            + "<style:style style:name='A' style:family='paragraph' style:parent-style-name='B'>"
            + "<style:paragraph-properties fo:text-align='center'/></style:style>"
            + "<style:style style:name='B' style:family='paragraph' style:parent-style-name='A'>"
            + "<style:paragraph-properties fo:margin-top='6pt'/></style:style>"
            + "</office:styles>");
        using var odtStream = new MemoryStream(odt);
        var odtA = new OdtDocumentReader().Read(odtStream).Document.Styles.Single(style => style.StyleName == "A");
        Require(odtA.Alignment == TextAlignment.Center && odtA.SpaceBefore?.Points == 6,
            "ODT: a cycle lost the formatting.");
    }

    /// <summary>
    /// A crafted file with tens of thousands of chained styles reads in linear time: each style is
    /// resolved once (StyleChain). Before, every style walked its whole chain again (DOCX: 159 s for
    /// 50,000 styles). The time bound is far above the linear cost and far below the quadratic one.
    /// </summary>
    private static void ResolvesLongStyleChainsInLinearTime()
    {
        const int Count = 50_000;
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var docx = new StringBuilder("<w:styles xmlns:w='" + W + "'>");
        for (var index = 0; index < Count; index++)
        {
            docx.Append($"<w:style w:type='paragraph' w:styleId='S{index}'><w:basedOn w:val='S{index + 1}'/></w:style>");
        }

        docx.Append($"<w:style w:type='paragraph' w:styleId='S{Count}'><w:name w:val='heading 2'/>"
            + "<w:pPr><w:jc w:val='center'/></w:pPr></w:style></w:styles>");
        using (var stream = new MemoryStream(DocxPackage(docx.ToString(), "<w:p><w:pPr><w:pStyle w:val='S0'/></w:pPr><w:r><w:t>x</w:t></w:r></w:p>")))
        {
            var heading = new DocxDocumentReader().Read(stream).Document.Sections[0].Blocks.OfType<Heading>().Single();
            Require(heading.Level == HeadingLevel.Level2 && heading.Style.Alignment == TextAlignment.Center,
                "DOCX: the heading level and the formatting come from the end of the chain.");
        }

        var paragraphs = new StringBuilder();
        var texts = new StringBuilder();
        for (var index = 0; index < Count; index++)
        {
            paragraphs.Append($"<style:style style:name='P{index}' style:family='paragraph' style:parent-style-name='P{index + 1}'/>");
            texts.Append($"<style:style style:name='T{index}' style:family='text' style:parent-style-name='T{index + 1}'/>");
        }

        paragraphs.Append($"<style:style style:name='P{Count}' style:family='paragraph'>"
            + "<style:paragraph-properties fo:text-align='center'/></style:style>");
        texts.Append($"<style:style style:name='T{Count}' style:family='text'>"
            + "<style:text-properties fo:font-weight='bold'/></style:style>");
        using (var stream = new MemoryStream(OdtPackage(
            "<office:styles/>",
            paragraphs.ToString() + texts,
            "<text:p text:style-name='P0'><text:span text:style-name='T0'>x</text:span></text:p>")))
        {
            var paragraph = (Paragraph)new OdtDocumentReader().Read(stream).Document.Sections[0].Blocks[0];
            Require(paragraph.Style.Alignment == TextAlignment.Center, "ODT: the paragraph formatting comes from the end of the chain.");
            Require(((TextRun)paragraph.Inlines[0]).Bold == true, "ODT: the text formatting comes from the end of the chain.");
        }

        Require(watch.Elapsed < TimeSpan.FromSeconds(20), $"Long style chains took {watch.Elapsed.TotalSeconds:F1} s.");

        // A missing or cyclic parent is still reported once.
        var cells = "<style:style style:name='C1' style:family='table-cell' style:parent-style-name='Missing'/>"
            + "<style:style style:name='C2' style:family='table-cell' style:parent-style-name='C3'/>"
            + "<style:style style:name='C3' style:family='table-cell' style:parent-style-name='C2'/>";
        using var cellStream = new MemoryStream(OdtPackage("<office:styles/>", cells));
        Require(new OdtDocumentReader().Read(cellStream).Warnings.Any(warning => warning.Message.Contains("table cell style", StringComparison.Ordinal)),
            "ODT: a broken cell style chain is reported.");
        using var fineStream = new MemoryStream(OdtPackage("<office:styles/>",
            "<style:style style:name='C1' style:family='table-cell' style:parent-style-name='C2'/>"
            + "<style:style style:name='C2' style:family='table-cell'/>"));
        Require(!new OdtDocumentReader().Read(fineStream).Warnings.Any(warning => warning.Message.Contains("table cell style", StringComparison.Ordinal)),
            "ODT: an intact cell style chain is not reported.");
    }

    /// <summary>A named style keeps its own formatting when an automatic style has the same name.</summary>
    private static void KeepsOwnStyleProperties()
    {
        var odt = OdtPackage(
            "<office:styles>"
            + "<style:style style:name='P1' style:family='paragraph'>"
            + "<style:paragraph-properties fo:text-align='center'/></style:style></office:styles>",
            "<style:style style:name='P1' style:family='paragraph'>"
            + "<style:paragraph-properties fo:margin-left='99pt'/></style:style>");

        using var stream = new MemoryStream(odt);
        var style = new OdtDocumentReader().Read(stream).Document.Styles.Single();
        Require(style.Alignment == TextAlignment.Center, "ODT: the own alignment was lost: " + style.Alignment);
        Require((style.LeftIndent?.Points ?? 0) == 0, "ODT: the formatting of the automatic style was taken: " + (style.LeftIndent?.Points ?? 0));
    }

    /// <summary>The ODT writer emits the whole paragraph formatting of a named style.</summary>
    private static void WritesNamedStyleProperties()
    {
        var rich = new ParagraphStyle
        {
            StyleName = "Reich",
            LineSpacing = LineSpacing.Multiple(1.5),
            Background = Color.FromHex("EEEEEE"),
            PageBreakBefore = true,
            Borders = new ParagraphBorders
            {
                Top = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Color.FromHex("FF0000")),
            },
            TabStops = [new TabStop(Length.FromPoints(36))],
        };

        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("x")) { Style = rich } }))
        {
            Styles = [rich],
        };

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(archive.GetEntry("styles.xml")!.Open());
        var styles = reader.ReadToEnd();
        Require(styles.Contains("fo:line-height=\"150%\"", StringComparison.Ordinal), "ODT: the line spacing is not written.");
        Require(styles.Contains("fo:background-color=\"#EEEEEE\"", StringComparison.Ordinal), "ODT: the background is not written.");
        Require(styles.Contains("fo:border-top=", StringComparison.Ordinal), "ODT: the border is not written.");
        Require(styles.Contains("style:tab-stops", StringComparison.Ordinal), "ODT: the tab stops are not written.");
        Require(styles.Contains("fo:break-before=\"page\"", StringComparison.Ordinal), "ODT: the page break is not written.");

        // And the reader gets it back.
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document.Styles.Single();
        Require(read.LineSpacing is { Rule: LineSpacingRule.Multiple }, "ODT: the line spacing is not read.");
        Require(read.Background is not null, "ODT: the background is not read.");
        Require(read.Borders?.Top is not null, "ODT: the border is not read.");
        Require(read.TabStops is { Count: 1 }, "ODT: the tab stops are not read.");
        Require(read.PageBreakBefore == true, "ODT: the page break is not read.");
    }

    /// <summary>The formatting of the referenced style counts for the paragraph; its own wins.</summary>
    private static void ReadsDocxParagraphStyle()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Basis'><w:pPr><w:jc w:val='center'/>"
            + "<w:spacing w:before='120'/></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='Zitat'><w:basedOn w:val='Basis'/>"
            + "<w:pPr><w:ind w:left='567' w:firstLine='567'/></w:pPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='EigeneUeberschrift'>"
            + "<w:pPr><w:outlineLvl w:val='0'/><w:spacing w:before='240'/></w:pPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Zitat'/></w:pPr><w:r><w:t>Zitat</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Zitat'/><w:jc w:val='right'/></w:pPr><w:r><w:t>Rechts</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Zitat'/><w:ind w:hanging='284'/></w:pPr><w:r><w:t>Haengend</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='EigeneUeberschrift'/></w:pPr><w:r><w:t>Titel</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Zitat'/></w:pPr><w:r><w:t>Wieder</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var read = new DocxDocumentReader().Read(stream).Document;
        var blocks = read.Sections[0].Blocks;

        var quote = (Paragraph)blocks[0];
        Require(quote.Style.Alignment == TextAlignment.Center, "DOCX: the alignment of the style is not on the paragraph.");
        Require(quote.Style.SpaceBefore?.Points == 6, "DOCX: the inherited space before is missing: " + quote.Style.SpaceBefore?.Points);
        Require(Math.Abs((quote.Style.LeftIndent?.Points ?? 0) - 28.35) < 0.01,
            "DOCX: the indent of the style is missing: " + (quote.Style.LeftIndent?.Points ?? 0));

        var own = (Paragraph)blocks[1];
        Require(own.Style.Alignment == TextAlignment.Right, "DOCX: the own alignment does not win: " + own.Style.Alignment);
        Require(own.Style.SpaceBefore?.Points == 6, "DOCX: the style is lost on the paragraph with own properties.");

        // The own hanging indent replaces the first-line indent of the style.
        var hanging = (Paragraph)blocks[2];
        Require(Math.Abs((hanging.Style.FirstLineIndent?.Points ?? 0) + 14.2) < 0.01,
            "DOCX: the own hanging indent did not win: " + (hanging.Style.FirstLineIndent?.Points ?? 0));
        Require(Math.Abs((hanging.Style.LeftIndent?.Points ?? 0) - 28.35) < 0.01,
            "DOCX: the inherited left indent is missing: " + (hanging.Style.LeftIndent?.Points ?? 0));

        var heading = (Heading)blocks[3];
        Require(heading.Level == HeadingLevel.Level1, "DOCX: the heading level is wrong.");
        Require(heading.Style.SpaceBefore?.Points == 12, "DOCX: the spacing of the heading style is missing: " + heading.Style.SpaceBefore?.Points);

        // The style of the later paragraph is the same as before: the merge must not change it.
        var again = (Paragraph)blocks[4];
        Require(again.Style.Alignment == TextAlignment.Center, "DOCX: the style was changed by an earlier paragraph.");
        Require(Math.Abs((again.Style.FirstLineIndent?.Points ?? 0) - 28.35) < 0.01,
            "DOCX: the style lost its first-line indent: " + (again.Style.FirstLineIndent?.Points ?? 0));
    }

    /// <summary>w:pPrDefault is the root of the paragraph formatting; the own properties win.</summary>
    private static void ReadsDocxParagraphDefaults()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'><w:docDefaults><w:pPrDefault><w:pPr>"
            + "<w:jc w:val='center'/><w:spacing w:after='240' w:line='276' w:lineRule='auto'/>"
            + "</w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type='paragraph' w:styleId='Rechts'><w:pPr><w:jc w:val='right'/></w:pPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:r><w:t>Standard</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:jc w:val='right'/></w:pPr><w:r><w:t>Rechts</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Rechts'/></w:pPr><w:r><w:t>Stil</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var read = new DocxDocumentReader().Read(stream).Document;
        Require(read.Defaults.ParagraphStyle.Alignment == TextAlignment.Center,
            "DOCX: the document paragraph default alignment is missing.");
        Require(read.Defaults.ParagraphStyle.SpaceAfter?.Points == 12,
            "DOCX: the document paragraph default spacing is missing.");
        Require(read.Defaults.ParagraphStyle.LineSpacing is { Rule: LineSpacingRule.Multiple, Factor: > 1.1 },
            "DOCX: the document paragraph default line spacing is missing.");
        var blocks = read.Sections[0].Blocks;
        var standard = (Paragraph)blocks[0];
        Require(standard.Style.Alignment == TextAlignment.Center, "DOCX: the default alignment is missing.");
        Require(standard.Style.SpaceAfter?.Points == 12,
            "DOCX: the default spacing is missing: " + standard.Style.SpaceAfter?.Points);
        Require(standard.Style.LineSpacing is { Rule: LineSpacingRule.Multiple, Factor: > 1.1 },
            "DOCX: the default line spacing is missing.");
        Require(((Paragraph)blocks[1]).Style.Alignment == TextAlignment.Right, "DOCX: the own alignment does not win.");
        Require(((Paragraph)blocks[2]).Style.Alignment == TextAlignment.Right, "DOCX: the style does not win over the defaults.");

        // Values that match the built-in typography are not materialized on the paragraph.
        var builtIn = "<w:styles xmlns:w='" + W + "'><w:docDefaults><w:pPrDefault><w:pPr>"
            + "<w:spacing w:after='120' w:line='240' w:lineRule='auto'/>"
            + "</w:pPr></w:pPrDefault></w:docDefaults></w:styles>";
        using var plainStream = new MemoryStream(DocxPackage(builtIn, "<w:p><w:r><w:t>Text</w:t></w:r></w:p>"));
        var plainDocument = new DocxDocumentReader().Read(plainStream).Document;
        var plain = (Paragraph)plainDocument.Sections[0].Blocks[0];
        Require(plain.Style.SpaceAfter is null && plain.Style.LineSpacing is null,
            "DOCX: the built-in defaults were written onto the paragraph.");
        Require(plainDocument.Defaults.ParagraphStyle == ParagraphStyle.Default,
            "DOCX: the built-in defaults were written into the document defaults.");
    }

    /// <summary>
    /// The effective formatting of a run: the paragraph style provides the base, the character style
    /// (by name) overrides it and the own formatting of the run wins (ADR-0010).
    /// </summary>
    private static TextRun Effective(PortableDocument document, Paragraph paragraph, TextRun run)
        => RunFormatResolver.Resolve(
            run,
            paragraph.Style,
            RunFormatResolver.Find(document.CharacterStyles, run.CharacterStyleName));

    /// <summary>The run formatting of a paragraph style stays on the style; a run overrides it.</summary>
    private static void ReadsDocxStyleRunFormatting()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Basis'><w:rPr>"
            + "<w:color w:val='CC0000'/><w:sz w:val='28'/></w:rPr></w:style>"
            + "<w:style w:type='paragraph' w:styleId='Zitat'><w:basedOn w:val='Basis'/>"
            + "<w:rPr><w:b/><w:i/></w:rPr></w:style>"
            + "<w:style w:type='character' w:styleId='Klein'><w:rPr>"
            + "<w:color w:val='0000CC'/><w:sz w:val='16'/></w:rPr></w:style>"
            + "<w:style w:type='character' w:styleId='Aufrecht'><w:rPr><w:b w:val='0'/><w:i/></w:rPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Zitat'/></w:pPr>"
            + "<w:r><w:t>Zitat</w:t></w:r>"
            + "<w:r><w:rPr><w:i w:val='0'/></w:rPr><w:t>aufrecht</w:t></w:r>"
            + "<w:r><w:rPr><w:rStyle w:val='Klein'/></w:rPr><w:t>klein</w:t></w:r>"
            + "<w:r><w:rPr><w:rStyle w:val='Aufrecht'/></w:rPr><w:t>nicht fett</w:t></w:r></w:p>";

        using var stream = new MemoryStream(DocxPackage(styles, body));
        var document = new DocxDocumentReader().Read(stream).Document;
        var paragraph = (Paragraph)document.Sections[0].Blocks[0];
        var runs = paragraph.Inlines.OfType<TextRun>().ToArray();

        Require(runs.Length == 4, "DOCX: the runs were not read: " + runs.Length);

        // ADR-0010: the run carries its own formatting only, the style provides the base.
        Require(runs[0].Bold is null && runs[0].Color is null, "DOCX: the style formatting was baked into the run.");
        Require(paragraph.Style.Bold == true && paragraph.Style.Italic == true,
            "DOCX: the paragraph style does not carry bold and italic.");
        Require(paragraph.Style.TextColor?.ToString() == "#CC0000",
            "DOCX: the paragraph style lost the inherited color: " + paragraph.Style.TextColor);
        Require(paragraph.Style.FontSize is { } styleSize && styleSize.Points == 14,
            "DOCX: the paragraph style lost the inherited size: " + paragraph.Style.FontSize);
        var quoted = Effective(document, paragraph, runs[0]);
        Require(quoted.Bold == true && quoted.Italic == true, "DOCX: the style formatting does not reach the run.");
        Require(quoted.Color?.ToString() == "#CC0000", "DOCX: the inherited color is missing: " + quoted.Color);
        Require(quoted.FontSize is { } size && size.Points == 14, "DOCX: the inherited font size is missing.");
        Require(document.Styles.Single(entry => entry.StyleName == "Zitat").Bold == true,
            "DOCX: the named style lost its run formatting.");

        // w:i with val=0 switches the inherited italic off, the bold of the style stays.
        Require(runs[1].Italic == false, "DOCX: the own italic-off is not read.");
        var upright = Effective(document, paragraph, runs[1]);
        Require(upright.Bold == true && upright.Italic == false, "DOCX: the own italic-off does not win.");

        // The character style sits between the paragraph style and the run: it stays a reference.
        Require(runs[2].CharacterStyleName == "Klein" && runs[2].Color is null,
            "DOCX: the character style formatting was baked into the run.");
        var small = Effective(document, paragraph, runs[2]);
        Require(small.Color?.ToString() == "#0000CC", "DOCX: the character style lost the color: " + small.Color);
        Require(small.FontSize is { } smallSize && smallSize.Points == 8,
            "DOCX: the character style lost the size: " + small.FontSize);
        Require(small.Bold == true && small.Italic == true, "DOCX: the character style dropped the inherited weight.");

        Require(runs[3].CharacterStyleName == "Aufrecht", "DOCX: the character style reference is missing.");
        Require(document.CharacterStyles.Single(entry => entry.Name == "Aufrecht").Bold == false,
            "DOCX: the character style lost its explicit bold-off.");
        var notBold = Effective(document, paragraph, runs[3]);
        Require(notBold.Bold == false && notBold.Italic == true,
            "DOCX: the character style does not switch the bold off.");
        Require(notBold.FontSize is { } inherited && inherited.Points == 14,
            "DOCX: the character style lost the inherited size: " + notBold.FontSize);
    }

    /// <summary>style:default-style is the root of the ODT paragraph formatting; the own style wins.</summary>
    private static void ReadsOdtParagraphDefaults()
    {
        var styles = "<office:styles>"
            + "<style:default-style style:family='paragraph'><style:paragraph-properties "
            + "fo:margin-bottom='12pt' fo:text-align='center'/></style:default-style>"
            + "<style:style style:name='Rechts' style:family='paragraph'>"
            + "<style:paragraph-properties fo:text-align='right'/></style:style>"
            + "</office:styles>";
        var body = "<text:p>Standard</text:p><text:p text:style-name='Rechts'>Rechts</text:p>"
            + "<text:h text:outline-level='1'>Titel</text:h>"
            + "<text:h text:outline-level='2' text:style-name='Heading2'>Untertitel</text:h>";

        using var stream = new MemoryStream(OdtPackage(styles, body: body));
        var read = new OdtDocumentReader().Read(stream).Document;
        Require(read.Defaults.ParagraphStyle.Alignment == TextAlignment.Center,
            "ODT: the document paragraph default alignment is missing.");
        Require(read.Defaults.ParagraphStyle.SpaceAfter?.Points == 12,
            "ODT: the document paragraph default spacing is missing.");
        var blocks = read.Sections[0].Blocks;
        var standard = (Paragraph)blocks[0];
        Require(standard.Style.Alignment == TextAlignment.Center, "ODT: the default alignment is missing.");
        Require(standard.Style.SpaceAfter?.Points == 12,
            "ODT: the default spacing is missing: " + standard.Style.SpaceAfter?.Points);
        var styled = (Paragraph)blocks[1];
        Require(styled.Style.Alignment == TextAlignment.Right, "ODT: the own alignment does not win.");
        Require(styled.Style.SpaceAfter?.Points == 12, "ODT: the style lost the default spacing.");
        Require(((Heading)blocks[2]).Style.Alignment == TextAlignment.Center,
            "ODT: a heading lost the default alignment.");
        Require(((Heading)blocks[3]).Style.Alignment == TextAlignment.Center,
            "ODT: a heading with an excluded built-in style lost the default alignment.");

        // The built-in spacing is not materialized on the paragraph.
        var builtIn = "<office:styles><style:default-style style:family='paragraph'>"
            + "<style:paragraph-properties fo:margin-bottom='6pt'/></style:default-style></office:styles>";
        using var plainStream = new MemoryStream(OdtPackage(builtIn, body: "<text:p>Text</text:p>"));
        var plainDocument = new OdtDocumentReader().Read(plainStream).Document;
        var plain = (Paragraph)plainDocument.Sections[0].Blocks[0];
        Require(plain.Style.SpaceAfter is null,
            "ODT: the built-in default was written onto the paragraph.");
        Require(plainDocument.Defaults.ParagraphStyle == ParagraphStyle.Default,
            "ODT: the built-in default was written into the document defaults.");
    }

    /// <summary>Bold/italic from style:default-style apply to runs; a style can switch them off.</summary>
    private static void ReadsOdtDefaultRunFormatting()
    {
        var styles = "<office:styles><style:default-style style:family='paragraph'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-style='italic' fo:font-size='13pt'/>"
            + "</style:default-style>"
            + "<style:style style:name='Aufrecht' style:family='paragraph'>"
            + "<style:text-properties fo:font-weight='normal'/></style:style></office:styles>";
        var body = "<text:p>Standard</text:p><text:p text:style-name='Aufrecht'>Aufrecht</text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, body: body));
        var document = new OdtDocumentReader().Read(stream).Document;
        var standardParagraph = (Paragraph)document.Sections[0].Blocks[0];
        var standard = standardParagraph.Inlines.OfType<TextRun>().Single();
        Require(standard.Bold is null, "ODT: the default bold was baked into the run.");
        Require(standardParagraph.Style.Bold == true && standardParagraph.Style.Italic == true,
            "ODT: the default bold/italic are missing.");
        Require(standard.FontSize is null, "ODT: the default size was written onto the run.");
        Require(document.Defaults.FontSize.Points == 13, "ODT: the default size is missing in DocumentDefaults.");
        var plain = Effective(document, standardParagraph, standard);
        Require(plain.Bold == true && plain.Italic == true, "ODT: the default bold/italic do not reach the run.");

        var uprightParagraph = (Paragraph)document.Sections[0].Blocks[1];
        var upright = uprightParagraph.Inlines.OfType<TextRun>().Single();
        Require(uprightParagraph.Style.Bold == false, "ODT: the style did not switch the default bold off.");
        var uprightEffective = Effective(document, uprightParagraph, upright);
        Require(uprightEffective.Bold == false && uprightEffective.Italic == true,
            "ODT: the style did not switch the default bold off.");
    }

    /// <summary>The run formatting of an ODT paragraph style applies to its runs; a span overrides it.</summary>
    private static void ReadsOdtStyleRunFormatting()
    {
        var styles = "<office:styles>"
            + "<style:style style:name='Basis' style:family='paragraph'><style:text-properties "
            + "fo:color='#CC0000' fo:font-size='14pt'/></style:style>"
            + "<style:style style:name='Zitat' style:family='paragraph' style:parent-style-name='Basis'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-style='italic'/></style:style>"
            + "</office:styles>";
        var automatic = "<style:style style:name='T1' style:family='text'>"
            + "<style:text-properties fo:font-size='8pt'/></style:style>";
        var body = "<text:p text:style-name='Zitat'>Zitat<text:span text:style-name='T1'> klein</text:span></text:p>"
            + "<text:p>Ohne</text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, automatic, body));
        var document = new OdtDocumentReader().Read(stream).Document;
        var blocks = document.Sections[0].Blocks;
        var paragraph = (Paragraph)blocks[0];
        var runs = paragraph.Inlines.OfType<TextRun>().ToArray();

        Require(runs.Length == 2, "ODT: the runs were not read: " + runs.Length);

        // ADR-0010: the run formatting of the style stays on the paragraph style.
        Require(runs[0].Bold is null && runs[0].Color is null, "ODT: the style formatting was baked into the run.");
        Require(paragraph.Style.Bold == true && paragraph.Style.Italic == true,
            "ODT: the paragraph style does not carry bold and italic.");
        Require(paragraph.Style.TextColor?.ToString() == "#CC0000", "ODT: the style lost the inherited color.");
        Require(paragraph.Style.FontSize is { } styleSize && styleSize.Points == 14,
            "ODT: the style lost the inherited size: " + paragraph.Style.FontSize);

        var quoted = Effective(document, paragraph, runs[0]);
        Require(quoted.Bold == true && quoted.Italic == true, "ODT: the style formatting does not reach the run.");
        Require(quoted.Color?.ToString() == "#CC0000", "ODT: the inherited color is missing: " + quoted.Color);
        Require(quoted.FontSize is { } size && size.Points == 14, "ODT: the inherited font size is missing.");

        // A span sets its own size and keeps the formatting of the paragraph style.
        Require(runs[1].FontSize is { } small && small.Points == 8,
            "ODT: the own size of the span is missing: " + runs[1].FontSize);
        Require(runs[1].Bold is null, "ODT: the span took the formatting of the paragraph style.");
        var span = Effective(document, paragraph, runs[1]);
        Require(span.Bold == true && span.Italic == true && span.Color?.ToString() == "#CC0000",
            "ODT: the span dropped the formatting of the paragraph style.");

        var plainParagraph = (Paragraph)blocks[1];
        var plain = plainParagraph.Inlines.OfType<TextRun>().Single();
        Require(plainParagraph.Style.Bold is null && plainParagraph.Style.FontSize is null,
            "ODT: an unstyled paragraph took the style formatting.");
        Require(plain.Bold is null && plain.FontSize is null, "ODT: an unstyled paragraph took the style formatting.");
    }

    /// <summary>An explicit "normal" in a span switches the paragraph style's bold off.</summary>
    private static void ReadsOdtSpanTurnsBoldOff()
    {
        var styles = "<office:styles><style:style style:name='Zitat' style:family='paragraph'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-size='14pt'/></style:style></office:styles>";
        var automatic = "<style:style style:name='Aufrecht' style:family='text'>"
            + "<style:text-properties fo:font-weight='normal'/></style:style>";
        var body = "<text:p text:style-name='Zitat'>Fett"
            + "<text:span text:style-name='Aufrecht'>aufrecht</text:span></text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, automatic, body));
        var document = new OdtDocumentReader().Read(stream).Document;
        var paragraph = (Paragraph)document.Sections[0].Blocks[0];
        var runs = paragraph.Inlines.OfType<TextRun>().ToArray();

        Require(runs.Length == 2, "ODT: the runs were not read: " + runs.Length);
        Require(paragraph.Style.Bold == true, "ODT: the bold of the paragraph style is missing.");
        Require(runs[1].Bold == false, "ODT: an explicit normal in a span is not read as an off.");
        var upright = Effective(document, paragraph, runs[1]);
        Require(upright.Bold == false, "ODT: an explicit normal in a span does not switch the bold off.");
        Require(upright.FontSize is { Points: 14 }, "ODT: the span lost the inherited size.");
    }

    /// <summary>Automatic text styles inherit along their parent chain; cycles terminate.</summary>
    private static void ReadsOdtNestedTextStyles()
    {
        var styles = "<office:styles><style:style style:name='Zitat' style:family='paragraph'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-size='14pt'/></style:style></office:styles>";
        var automatic = "<style:style style:name='T2' style:family='text'>"
            + "<style:text-properties fo:font-size='9pt'/></style:style>"
            + "<style:style style:name='T1' style:family='text' style:parent-style-name='T2'>"
            + "<style:text-properties fo:font-weight='normal'/></style:style>"
            + "<style:style style:name='C1' style:family='text' style:parent-style-name='C2'>"
            + "<style:text-properties fo:font-size='10pt'/></style:style>"
            + "<style:style style:name='C2' style:family='text' style:parent-style-name='C1'>"
            + "<style:text-properties fo:font-style='italic'/></style:style>";
        var body = "<text:p text:style-name='Zitat'>Fett"
            + "<text:span text:style-name='T1'>klein</text:span>"
            + "<text:span text:style-name='C1'>Kreis</text:span></text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, automatic, body));
        var paragraph = (Paragraph)new OdtDocumentReader().Read(stream).Document.Sections[0].Blocks[0];
        var runs = paragraph.Inlines.OfType<TextRun>().ToArray();

        Require(runs.Length == 3, "ODT: the runs were not read: " + runs.Length);
        Require(runs[1].Bold == false && runs[1].FontSize is { Points: 9 },
            "ODT: the nested text style lost the inherited size or the bold-off.");
        Require(runs[2].Italic == true && runs[2].FontSize is { Points: 10 },
            "ODT: a text style cycle lost the formatting.");
    }

    /// <summary>A note body does not take the run formatting of the paragraph with the note.</summary>
    private static void ReadsOdtNoteFormatting()
    {
        var styles = "<office:styles>"
            + "<style:style style:name='Zitat' style:family='paragraph'>"
            + "<style:text-properties fo:font-weight='bold' fo:font-size='14pt'/></style:style>"
            + "</office:styles>";
        var body = "<text:p text:style-name='Zitat'>Text<text:note text:note-class='footnote'>"
            + "<text:note-body><text:p>Fussnote</text:p></text:note-body>"
            + "</text:note></text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, body: body));
        var document = new OdtDocumentReader().Read(stream).Document;
        var paragraph = (Paragraph)document.Sections[0].Blocks[0];
        Require(paragraph.Style.Bold == true, "ODT: the style formatting of the paragraph is missing.");

        var note = paragraph.Inlines.OfType<Footnote>().Single();
        var noteParagraph = (Paragraph)note.Blocks[0];
        var noteRun = noteParagraph.Inlines.OfType<TextRun>().Single();
        Require(noteParagraph.Style.Bold is null && noteRun.Bold is null,
            "ODT: the note took the run formatting of the paragraph with the note.");
        Require(noteRun.FontSize is null, "ODT: the note took the run formatting of the paragraph with the note.");
    }

    /// <summary>A note body reads the paragraph style of the note's own paragraph.</summary>
    private static void ReadsOdtNoteParagraphStyle()
    {
        var styles = "<office:styles>"
            + "<style:style style:name='Fussnote' style:family='paragraph'>"
            + "<style:paragraph-properties fo:text-align='center'/>"
            + "<style:text-properties fo:font-style='italic' fo:font-size='9pt'/></style:style>"
            + "</office:styles>";
        var body = "<text:p>Text<text:note text:note-class='footnote'>"
            + "<text:note-body><text:p text:style-name='Fussnote'>Fussnote</text:p></text:note-body>"
            + "</text:note></text:p>";

        using var stream = new MemoryStream(OdtPackage(styles, body: body));
        var outer = (Paragraph)new OdtDocumentReader().Read(stream).Document.Sections[0].Blocks[0];
        var outerRun = outer.Inlines.OfType<TextRun>().Single();
        Require(outerRun.Italic != true, "ODT: the note style reached the paragraph with the note.");

        var note = (Paragraph)outer.Inlines.OfType<Footnote>().Single().Blocks[0];
        var run = note.Inlines.OfType<TextRun>().Single();
        Require(note.Style.Italic == true && note.Style.FontSize is { Points: 9 },
            "ODT: the run formatting of the note paragraph style is missing.");
        Require(run.Italic is null, "ODT: the run formatting of the note style was baked into the run.");
        Require(note.Style.Alignment == TextAlignment.Center,
            "ODT: the paragraph formatting of the note paragraph style is missing.");
    }

    /// <summary>An empty style name is ignored instead of rejecting the file.</summary>
    private static void IgnoresEmptyOdtStyleName()
    {
        var body = "<text:p text:style-name=''>Text</text:p>";

        using var stream = new MemoryStream(OdtPackage("<office:styles/>", body: body));
        var paragraph = (Paragraph)new OdtDocumentReader().Read(stream).Document.Sections[0].Blocks[0];
        Require(paragraph.Inlines.OfType<TextRun>().Single().Text == "Text",
            "ODT: a paragraph with an empty style name was not read.");
    }

    /// <summary>The numbering of a paragraph style counts for its paragraphs; <c>numId 0</c> removes it.</summary>
    private static void ReadsDocxStyleNumbering()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Liste'><w:pPr><w:numPr>"
            + "<w:numId w:val='7'/><w:ilvl w:val='1'/></w:numPr></w:pPr></w:style>"
            + "</w:styles>";
        var body = "<w:p><w:pPr><w:pStyle w:val='Liste'/></w:pPr><w:r><w:t>Punkt</w:t></w:r></w:p>"
            + "<w:p><w:pPr><w:pStyle w:val='Liste'/><w:numPr><w:numId w:val='0'/></w:numPr></w:pPr>"
            + "<w:r><w:t>Ohne</w:t></w:r></w:p>";
        var numbering = "<w:numbering xmlns:w='" + W + "'>"
            + "<w:abstractNum w:abstractNumId='0'><w:lvl w:ilvl='0'><w:numFmt w:val='decimal'/></w:lvl>"
            + "<w:lvl w:ilvl='1'><w:numFmt w:val='bullet'/></w:lvl></w:abstractNum>"
            + "<w:num w:numId='7'><w:abstractNumId w:val='0'/></w:num></w:numbering>";

        using var stream = new MemoryStream(DocxPackage(styles, body, ("word/numbering.xml", numbering)));
        var read = new DocxDocumentReader().Read(stream).Document;
        var blocks = read.Sections[0].Blocks;

        Require(
            blocks[0] is ListBlock list && TestLists.Level(read, list).Kind == ListKind.Bullet,
            "DOCX: the numbering of the style is not on the paragraph: " + blocks[0].GetType().Name);
        Require(blocks[1] is Paragraph, "DOCX: numId 0 does not remove the numbering: " + blocks[1].GetType().Name);
    }

    /// <summary>A paragraph style inherits the properties of its parent style (style:parent-style-name).</summary>
    private static void ReadsOdtStyleInheritance()
    {
        var styles = "<office:styles>"
            + "<style:style style:name='Base' style:family='paragraph'>"
            + "<style:paragraph-properties fo:text-align='center' fo:margin-top='6pt'/></style:style>"
            + "<style:style style:name='Child' style:family='paragraph' style:parent-style-name='Base'>"
            + "<style:paragraph-properties fo:margin-bottom='3pt'/></style:style>"
            + "</office:styles>";

        using var stream = new MemoryStream(OdtPackage(styles));
        var document = new OdtDocumentReader().Read(stream).Document;

        var child = document.Styles.Single(style => style.StyleName == "Child");
        Require(child.Alignment == TextAlignment.Center, "ODT: the inherited alignment is missing.");
        Require(child.SpaceBefore?.Points == 6, "ODT: the inherited space before is missing: " + child.SpaceBefore?.Points);
        Require(child.SpaceAfter?.Points == 3, "ODT: the own space after is missing: " + child.SpaceAfter?.Points);
    }

    /// <summary>The automatic paragraph styles of the header/footer are not named styles.</summary>
    private static void IgnoresOdtBandStyles()
    {
        var header = new Paragraph(new TextRun("Kopf"))
        {
            Style = ParagraphStyle.Default with { Alignment = TextAlignment.Center },
        };

        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("Text")) })
            {
                Header = new HeaderFooter(new Block[] { header }),
            });

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();

        using var input = new MemoryStream(buffer.ToArray());
        var read = new OdtDocumentReader().Read(input).Document;
        Require(read.Styles.Count == 0, "ODT: a generated header style was read as a named style: "
            + string.Join(",", read.Styles.Select(style => style.StyleName)));
    }

    /// <summary>The document-wide run defaults (font, size, colour) are read from both formats.</summary>
    private static void ReadsDocumentRunDefaults()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var docxStyles = "<w:styles xmlns:w='" + W + "'><w:docDefaults><w:rPrDefault><w:rPr>"
            + "<w:rFonts w:ascii='Georgia'/><w:sz w:val='26'/><w:color w:val='112233'/>"
            + "</w:rPr></w:rPrDefault></w:docDefaults></w:styles>";

        using var docx = new MemoryStream(DocxPackage(docxStyles, "<w:p><w:r><w:t>Text</w:t></w:r></w:p>"));
        RequireDefaults(new DocxDocumentReader().Read(docx).Document.Defaults, "DOCX");

        var odt = OdtPackage(
            "<office:styles><style:default-style style:family='paragraph'><style:text-properties "
            + "fo:font-family='\"Georgia\", serif' fo:font-size='13pt' fo:color='#112233'/>"
            + "</style:default-style></office:styles>");
        using var odtStream = new MemoryStream(odt);
        RequireDefaults(new OdtDocumentReader().Read(odtStream).Document.Defaults, "ODT");

        static void RequireDefaults(DocumentDefaults defaults, string format)
        {
            Require(defaults.FontFamily == "Georgia", format + ": the default font is not read: " + defaults.FontFamily);
            Require(defaults.FontSize.Points == 13, format + ": the default size is not read: " + defaults.FontSize.Points);
            Require(defaults.TextColor.ToString() == "#112233", format + ": the default color is not read: " + defaults.TextColor);
        }
    }

    /// <summary>Custom document defaults survive a write and read in both formats.</summary>
    private static void RoundTripsDocumentRunDefaults()
    {
        var defaults = new DocumentDefaults
        {
            FontFamily = "Georgia",
            FontSize = Length.FromPoints(13),
            TextColor = Color.FromHex("112233"),
            ParagraphStyle = new ParagraphStyle
            {
                Alignment = TextAlignment.Center,
                SpaceBefore = Length.FromPoints(12),
                LeftIndent = Length.FromPoints(18),
                KeepTogether = true,
            },
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("Text")) }))
        {
            Defaults = defaults,
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document.Defaults;
            Require(read.FontFamily == "Georgia", format + ": the default font is lost on writing: " + read.FontFamily);
            Require(read.FontSize.Points == 13, format + ": the default size is lost on writing: " + read.FontSize.Points);
            Require(read.TextColor.ToString() == "#112233", format + ": the default color is lost on writing: " + read.TextColor);
            Require(read.ParagraphStyle.Alignment == TextAlignment.Center,
                format + ": the default paragraph alignment is lost on writing.");
            Require(read.ParagraphStyle.SpaceBefore?.Points == 12,
                format + ": the default paragraph space before is lost on writing.");
            Require((read.ParagraphStyle.LeftIndent?.Points ?? 0) == 18,
                format + ": the default paragraph indent is lost on writing.");
            Require(read.ParagraphStyle.KeepTogether == true,
                format + ": the default keep-together is lost on writing.");
        }
    }

    /// <summary>A linked paragraph/character style keeps its link across a write and read.</summary>
    private static void WritesStyleLinks()
    {
        var paragraph = new ParagraphStyle { StyleName = "Titel", LinkedStyleName = "TitelZeichen" };
        var character = new CharacterStyle("TitelZeichen") { Bold = true, LinkedStyleName = "Titel" };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("Text")) { Style = paragraph } }))
        {
            Styles = [paragraph],
            CharacterStyles = [character],
        };

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;

        Require(read.Styles.Single().LinkedStyleName == "TitelZeichen",
            "DOCX: the link of the paragraph style is not written.");
        Require(read.CharacterStyles.Single().LinkedStyleName == "Titel",
            "DOCX: the link of the character style is not written.");
    }

    /// <summary>A changed link regenerates the styles instead of keeping the source definitions.</summary>
    private static void WritesChangedStyleLinks()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Titel'><w:name w:val='Titel'/>"
            + "<w:link w:val='TitelZeichen'/></w:style>"
            + "<w:style w:type='character' w:styleId='TitelZeichen'><w:name w:val='Titel Zeichen'/>"
            + "<w:link w:val='Titel'/><w:rPr><w:b/></w:rPr></w:style>"
            + "</w:styles>";

        using var stream = new MemoryStream(DocxPackage(styles));
        var document = new DocxDocumentReader().Read(stream).Document;
        var style = document.Styles.Single();
        var changed = document with { Styles = [style with { LinkedStyleName = "Andere" }] };

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(changed, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(archive.GetEntry("word/styles.xml")!.Open());
        Require(reader.ReadToEnd().Contains("w:link w:val=\"Andere\"", StringComparison.Ordinal),
            "DOCX: a changed style link is not written.");
    }

    /// <summary>A link to a style the reader dropped is not written as a dangling reference.</summary>
    private static void DropsLinksToUnwrittenStyles()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var styles = "<w:styles xmlns:w='" + W + "'>"
            + "<w:style w:type='paragraph' w:styleId='Titel'><w:name w:val='Titel'/>"
            + "<w:link w:val='TitelZeichen'/></w:style>"
            // No w:rPr: the character style is dropped by the reader.
            + "<w:style w:type='character' w:styleId='TitelZeichen'><w:name w:val='Titel Zeichen'/>"
            + "<w:link w:val='Titel'/></w:style>"
            + "</w:styles>";

        using var stream = new MemoryStream(DocxPackage(styles));
        var document = new DocxDocumentReader().Read(stream).Document;
        Require(document.CharacterStyles.Count == 0, "DOCX: a character style without rPr must be dropped.");
        Require(document.Styles.Single().LinkedStyleName is null,
            "DOCX: a link to a dropped character style was kept.");
    }

    /// <summary>Builds a DOCX package with a paragraph that uses the style "Child".</summary>
    private static byte[] DocxPackage(string stylesXml, string? body = null, params (string Path, string Xml)[] parts)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "word/document.xml",
                "<w:document xmlns:w='" + W + "'><w:body>"
                + (body ?? "<w:p><w:pPr><w:pStyle w:val='Child'/></w:pPr><w:r><w:t>Text</w:t></w:r></w:p>")
                + "</w:body></w:document>");
            WriteEntry(archive, "word/styles.xml", stylesXml);
            foreach (var (path, xml) in parts)
            {
                WriteEntry(archive, path, xml);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Builds a minimal ODT package with the given <c>office:styles</c> and automatic styles.</summary>
    private static byte[] OdtPackage(string stylesXml, string automaticStyles = "", string body = "<text:p>Text</text:p>")
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "content.xml",
                "<office:document-content"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:text='urn:oasis:names:tc:opendocument:xmlns:text:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
                + " xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
                + "<office:automatic-styles>" + automaticStyles + "</office:automatic-styles>"
                + "<office:body><office:text>" + body + "</office:text></office:body>"
                + "</office:document-content>");
            WriteEntry(archive, "styles.xml",
                "<office:document-styles"
                + " xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0'"
                + " xmlns:style='urn:oasis:names:tc:opendocument:xmlns:style:1.0'"
                + " xmlns:fo='urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0'>"
                + stylesXml + "</office:document-styles>");
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
