using System.Text;
using Reglet;

/// <summary>HTML as a format (ADR-0032): reader subset, security, writer, round trip, conversion.</summary>
internal static class HtmlChecks
{
    internal static int Run()
    {
        try
        {
            ReadsStructure();
            ReadsEntitiesAndEncodings();
            IsSafe();
            ReadsTables();
            WritesAndRoundTrips();
            ConvertsToOtherFormats();
            Console.WriteLine("HTML: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("HTML: " + exception);
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

    private static ReadResult Read(string html, Encoding? encoding = null)
        => new HtmlDocumentReader().Read(new MemoryStream((encoding ?? new UTF8Encoding(false)).GetBytes(html)));

    private static IReadOnlyList<Block> Blocks(ReadResult result) => result.Document.Sections[0].Blocks;

    private static string Text(Paragraph paragraph) => string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text));

    private static void ReadsStructure()
    {
        var result = Read("<html><head><title>Title</title><style>p{color:red}</style></head><body><h1>Head</h1><p>One <b>bold</b> and <i>slanted</i><br>Line<p>Two <a href=\"https://example.org\">Link</a>"
            + "<ul><li>a<ul><li>b</li></ul></li><li>c</ol></body></html>");
        var blocks = Blocks(result);
        Require(result.Document.Metadata.Title == "Title", "title");
        Require(blocks[0] is Heading { Level: HeadingLevel.Level1 }, "heading");
        var first = (Paragraph)blocks[1];
        Require(Text(first) == "One bold and slanted\nLine", "text: " + Text(first));
        Require(first.Inlines.OfType<TextRun>().Any(run => run.Bold == true && run.Text == "bold"), "bold");
        Require(first.Inlines.OfType<TextRun>().Any(run => run.Italic == true), "italic");
        Require(((Paragraph)blocks[2]).Inlines.OfType<Hyperlink>().Single().Target == "https://example.org", "link");
        var list = (ListBlock)blocks[3];
        Require(list.Items.Count == 2 && list.Items[0].Blocks.OfType<ListBlock>().Count() == 1, "nested list");
        Require(!result.Document.Sections[0].Blocks.OfType<Paragraph>().Any(p => Text(p).Contains("color:red", StringComparison.Ordinal)), "style dropped");
        var styled = Blocks(Read("<p style=\"text-align:center\"><span style=\"color:#ff0000;font-size:14pt\">Rot</span></p>"));
        var run = (TextRun)((Paragraph)styled[0]).Inlines[0];
        Require(((Paragraph)styled[0]).Style.Alignment == TextAlignment.Center && run.Color is { Red: 255 } && run.FontSize?.Points == 14, "css subset");
    }

    private static void ReadsEntitiesAndEncodings()
    {
        Require(Text((Paragraph)Blocks(Read("<p>a &amp; b &lt; &#65;&#x42; &auml; &euro; &bogus;</p>"))[0]) == "a & b < AB ä € &bogus;", "entities");
        var latin = Read("<meta charset=\"iso-8859-1\"><p>Crème brûlée</p>", Encoding.Latin1);
        Require(Text((Paragraph)Blocks(latin)[0]) == "Crème brûlée", "latin1");
        var bom = Read("﻿<p>Ü</p>", new UTF8Encoding(true));
        Require(Text((Paragraph)Blocks(bom)[0]) == "Ü", "bom");
        Require(Text((Paragraph)Blocks(Read("<p>a\n   b\t c</p>"))[0]) == "a b c", "whitespace");
    }

    private static void IsSafe()
    {
        var result = Read("<p onclick=\"x()\">T<script>alert(1)</script><a href=\"javascript:alert(1)\">bad</a><img src=\"http://example.org/a.png\" alt=\"Picture\"><iframe src=\"x\"></iframe></p>");
        var paragraph = (Paragraph)Blocks(result)[0];
        Require(!paragraph.Inlines.OfType<Hyperlink>().Any(), "javascript link dropped");
        Require(Text(paragraph).Contains("bad", StringComparison.Ordinal) && Text(paragraph).Contains("Picture", StringComparison.Ordinal), "text kept");
        Require(!Text(paragraph).Contains("alert", StringComparison.Ordinal), "script dropped");
        Require(result.Warnings.Count >= 2, "warnings");
        var deep = Read(string.Concat(Enumerable.Repeat("<div>", 5000)) + "deep");
        Require(Blocks(deep).Count >= 1, "deep nesting survives");
        var image = Blocks(Read("<img src=\"data:image/png;base64," + Convert.ToBase64String(Png()) + "\" alt=\"x\">"));
        Require(image.OfType<ImageBlock>().Count() == 1, "data uri image");
    }

    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static void ReadsTables()
    {
        var blocks = Blocks(Read("<table border=1><tr><th>A<th>B<tr><td colspan=2>x</table>"));
        var table = (TableBlock)blocks[0];
        Require(table.Rows.Count == 2 && table.Rows[0].IsHeader && table.Rows[1].Cells[0].ColumnSpan == 2, "table");
    }

    private static PortableDocument Sample()
        => new(new DocumentMetadata { Title = "A & B" }, new Section(new Block[]
        {
            new Heading(HeadingLevel.Level2, new TextRun("Head <1>")),
            new Paragraph(new Inline[] { new TextRun("Bold") { Bold = true }, new TextRun(" and "), new Hyperlink("https://example.org/?a=1&b=2", "Link") }),
            new TableBlock(new[] { new TableRow(new TableCell("x"), new TableCell("y")) { IsHeader = true } }),
            new ImageBlock(Png(), ImageMediaType.Png) { AltText = "Pixel" },
            new Equation(new MathText("x")),
        }));

    private static void WritesAndRoundTrips()
    {
        var stream = new MemoryStream();
        var write = new HtmlDocumentWriter().WriteAsync(Sample(), stream).GetAwaiter().GetResult();
        var html = Encoding.UTF8.GetString(stream.ToArray());
        Require(html.Contains("<title>A &amp; B</title>", StringComparison.Ordinal) && html.Contains("Head &lt;1&gt;", StringComparison.Ordinal), "escaping");
        Require(html.Contains("data:image/png;base64,", StringComparison.Ordinal) && html.Contains("<b>Bold</b>", StringComparison.Ordinal), "content");
        Require(write.Warnings.Any(w => w.Message.Contains("Equation", StringComparison.Ordinal)), "equation reported");
        var back = Blocks(new HtmlDocumentReader().Read(new MemoryStream(stream.ToArray())));
        Require(back[0] is Heading && back.OfType<TableBlock>().Count() == 1 && back.OfType<ImageBlock>().Count() == 1, "round trip");
        Require(((Paragraph)back[1]).Inlines.OfType<Hyperlink>().Single().Target == "https://example.org/?a=1&b=2", "link round trip");
    }

    private static void ConvertsToOtherFormats()
    {
        var html = Encoding.UTF8.GetBytes("<h1>Hello</h1><p>World <b>bold</b></p><ul><li>one</li></ul>");
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            var read = DocumentReader.Read(new MemoryStream(html), DocumentFormat.Html);
            var output = new MemoryStream();
            DocumentWriter.WriteAsync(read.Document, output, format).GetAwaiter().GetResult();
            Require(output.Length > 200, "convert " + format);
        }

        Require(DocumentReader.Read(new MemoryStream(html), DocumentFormat.Html).Document.Sections[0].Blocks.Count == 3, "facade read");
    }
}
