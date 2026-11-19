using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>Alignment tabs (<c>w:ptab</c>): the model rules, DOCX read and write, and the tab stops ODT and PDF get.</summary>
internal static class AlignmentTabChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidTabs();
            RoundTripsDocx();
            WritesTabStopsInOdt();
            RendersPdf();
            Console.WriteLine("Alignment tabs: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Alignment tabs: " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var footer = new Paragraph(new Inline[]
        {
            new TextRun("links"),
            new AlignmentTab(TabStopAlignment.Center),
            new TextRun("Mitte"),
            new AlignmentTab(TabStopAlignment.Right, TabStopLeader.Dot, AlignmentTabRelativeTo.Margin),
            new TextRun("rechts"),
        });
        var body = new Paragraph(new Inline[]
        {
            new TextRun("Kapitel"),
            new AlignmentTab(TabStopAlignment.Right, relativeTo: AlignmentTabRelativeTo.Indent),
            new TextRun("7"),
        }) { Style = new ParagraphStyle { LeftIndent = Length.FromPoints(36), RightIndent = Length.FromPoints(18) } };
        return new PortableDocument(new DocumentMetadata(), new Section(body) { Footer = new HeaderFooter(footer) });
    }

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void RejectsInvalidTabs()
    {
        Throws(() => new AlignmentTab(TabStopAlignment.Decimal));
        Throws(() => new AlignmentTab(TabStopAlignment.Left, (TabStopLeader)99));
        Throws(() => new AlignmentTab(TabStopAlignment.Left, relativeTo: (AlignmentTabRelativeTo)99));
    }

    private static void RoundTripsDocx()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();
        var footerXml = Part(package, "word/footer1.xml");
        Require(footerXml.Contains("w:alignment=\"center\"", StringComparison.Ordinal)
            && footerXml.Contains("w:alignment=\"right\"", StringComparison.Ordinal)
            && footerXml.Contains("w:leader=\"dot\"", StringComparison.Ordinal)
            && footerXml.Contains("w:relativeTo=\"margin\"", StringComparison.Ordinal), "DOCX: the ptab attributes: " + footerXml);
        Require(Part(package, "word/document.xml").Contains("w:relativeTo=\"indent\"", StringComparison.Ordinal), "DOCX: the indent reference.");

        var read = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Docx);
        Require(!read.Warnings.Any(warning => warning.Message.Contains("alignment tab", StringComparison.OrdinalIgnoreCase)), "DOCX read: no warning.");
        var footer = (Paragraph)read.Document.Sections[0].Footer!.Blocks.Single();
        var tabs = footer.Inlines.OfType<AlignmentTab>().ToList();
        Require(tabs.Count == 2 && tabs[0].Alignment == TabStopAlignment.Center && tabs[1].Alignment == TabStopAlignment.Right
            && tabs[1].Leader == TabStopLeader.Dot && tabs[1].RelativeTo == AlignmentTabRelativeTo.Margin, "DOCX read: the footer tabs.");
        Require(footer.Inlines.Select(inline => inline is TextRun run ? run.Text : "|").SequenceEqual(new[] { "links", "|", "Mitte", "|", "rechts" }), "DOCX read: the text between the tabs.");
        var body = (Paragraph)read.Document.Sections[0].Blocks.Single();
        Require(body.Inlines.OfType<AlignmentTab>().Single().RelativeTo == AlignmentTabRelativeTo.Indent, "DOCX read: the indent reference.");
    }

    private static void WritesTabStopsInOdt()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "ODT: no warning: " + string.Join("|", result.Warnings.Select(warning => warning.Message)));
        var package = buffer.ToArray();
        // A4 with 1 inch margins: 595.276 - 144 = 451.276 pt of text; the footer paragraph stands at the margin.
        var styles = Part(package, "styles.xml");
        Require(styles.Contains("style:position=\"225.64pt\"", StringComparison.Ordinal) && styles.Contains("style:type=\"center\"", StringComparison.Ordinal), "ODT: the center stop: " + styles);
        Require(styles.Contains("style:position=\"451.28pt\"", StringComparison.Ordinal) && styles.Contains("style:type=\"right\"", StringComparison.Ordinal)
            && styles.Contains("style:leader-text=\".\"", StringComparison.Ordinal), "ODT: the right stop with its leader.");
        Require(styles.Split(new[] { "<text:tab" }, StringSplitOptions.None).Length == 3, "ODT: two tabs in the footer.");
        // The body paragraph is relative to its indents: right edge 451.276 - 18, counted from the indent 36 in ODF.
        Require(Part(package, "content.xml").Contains("style:position=\"397.28pt\"", StringComparison.Ordinal), "ODT: the indent-relative stop.");
    }

    private static void RendersPdf()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(Document(), buffer).GetAwaiter().GetResult();
        Require(!result.HasWarnings, "PDF: no warning.");
        if (Environment.GetEnvironmentVariable("PD_PTAB_OUT") is { Length: > 0 } output)
        {
            File.WriteAllBytes(output, buffer.ToArray());
        }

        var text = string.Join("\n", DocumentReader.ReadPdfPages(new MemoryStream(buffer.ToArray())));
        Require(text.Contains("links", StringComparison.Ordinal) && text.Contains("rechts", StringComparison.Ordinal) && text.Contains("Kapitel", StringComparison.Ordinal), "PDF: the text: " + text);
    }

    private static void Throws(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Expected an ArgumentException.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
