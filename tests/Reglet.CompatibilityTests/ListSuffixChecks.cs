using System.IO.Compression;
using Reglet;

/// <summary>what follows the marker of a list item (tab, space, nothing) and the text effects of a run.</summary>
internal static class ListSuffixChecks
{
    internal static int Run()
    {
        try
        {
            foreach (var suffix in new[] { ListSuffix.Tab, ListSuffix.Space, ListSuffix.Nothing })
            {
                RoundTripsDocx(suffix);
                RoundTripsOdt(suffix);
            }

            foreach (var effects in new[] { TextEffects.Outline, TextEffects.Shadow | TextEffects.Emboss, TextEffects.Engrave, TextEffects.None })
            {
                RoundTripsEffects(effects);
            }

            Console.WriteLine("List suffix: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("List suffix: " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(ListSuffix suffix)
    {
        var definition = new NumberingDefinition("Suffix", [new NumberingLevel { Kind = ListKind.Number, Suffix = suffix }]);
        var list = new ListBlock("Suffix", [new ListItem(new Paragraph(new TextRun("One")))]) { NestingLevel = 1 };
        return new PortableDocument(new DocumentMetadata(), new Section(list)) { Numbering = [definition] };
    }

    private static void RoundTripsDocx(ListSuffix suffix)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(Document(suffix), buffer).GetAwaiter().GetResult();
        var xml = new StreamReader(new ZipArchive(new MemoryStream(buffer.ToArray())).GetEntry("word/numbering.xml")!.Open()).ReadToEnd();
        Require(xml.Contains("w:suff", StringComparison.Ordinal) == (suffix != ListSuffix.Tab), $"DOCX {suffix}: w:suff is written only when it is not a tab.");
        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        var level = TestLists.Level(read, (ListBlock)read.Sections[0].Blocks[0]);
        Require(level.Suffix == suffix, $"DOCX {suffix}: read back as {level.Suffix}.");
    }

    private static void RoundTripsOdt(ListSuffix suffix)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(Document(suffix), buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var level = TestLists.Level(read, (ListBlock)read.Sections[0].Blocks[0]);
        Require(level.Suffix == suffix, $"ODT {suffix}: read back as {level.Suffix}.");
    }

    private static void RoundTripsEffects(TextEffects effects)
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Fancy") { Effects = effects })));
        using var docx = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, docx).GetAwaiter().GetResult();
        docx.Position = 0;
        var fromDocx = ((TextRun)((Paragraph)new DocxDocumentReader().Read(docx).Document.Sections[0].Blocks[0]).Inlines[0]).Effects;
        Require(fromDocx == effects, $"DOCX: {effects} read back as {fromDocx}.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        odt.Position = 0;
        var fromOdt = ((TextRun)((Paragraph)new OdtDocumentReader().Read(odt).Document.Sections[0].Blocks[0]).Inlines[0]).Effects;
        Require(fromOdt == effects, $"ODT: {effects} read back as {fromOdt}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
