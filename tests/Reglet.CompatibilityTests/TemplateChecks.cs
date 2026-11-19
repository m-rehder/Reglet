using System.Runtime.InteropServices;
using Reglet;

/// <summary>
/// checks the format-neutral template contracts (phase S, S01): snapshots,
/// immutability, validation and the binding of inline and block content.
/// </summary>
internal static class TemplateChecks
{
    internal static int Run()
    {
        try
        {
            CopiesTemplateBytes();
            SnapshotsBindings();
            BindsInlineAndBlockContent();
            RejectsInvalidPlaceholderNames();
            SetsBodyPlaceholder();
            ValidatesWatermark();
            ValidatesPageFrame();
            ComposesPageTemplate();
            Console.WriteLine("Templates (S01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Templates (S01): " + exception);
            return 1;
        }
    }

    private static void CopiesTemplateBytes()
    {
        var source = new byte[] { 1, 2, 3 };
        var template = DocumentTemplate.FromBytes(source);
        source[0] = 9;
        Require(template.Data.Span[0] == 1, "The template must copy input bytes.");

        var exposed = template.Data;
        Require(MemoryMarshal.TryGetArray(exposed, out var segment), "Test needs an array slice.");
        segment.Array![segment.Offset] = 8;
        Require(template.Data.Span[0] == 1, "The template must not expose its internal storage.");

        Throws<ArgumentException>(() => DocumentTemplate.FromBytes(ReadOnlyMemory<byte>.Empty));
    }

    private static void SnapshotsBindings()
    {
        var original = DocumentTemplate.FromBytes(new byte[] { 1 })
            .BindText("Title", "Erst");

        var extended = original.BindText("Autor", "Second");

        Require(original.Placeholders.Count == 1, "The original template remains unchanged.");
        Require(extended.Placeholders.Count == 2, "Bind yields an extended snapshot.");
        Require(extended.Placeholders["Title"].Inlines[0] is TextRun { Text: "Erst" }, "The first content is preserved.");

        Require(
            original.Placeholders is IDictionary<string, TemplateContent> { IsReadOnly: true },
            "The placeholders are read-only.");

        var rebound = original.BindText("Title", "Neu");
        Require(
            rebound.Placeholders["Title"].Inlines[0] is TextRun { Text: "Neu" },
            "A rebind replaces the previous content.");
        Require(
            original.Placeholders["Title"].Inlines[0] is TextRun { Text: "Erst" },
            "Rebinding does not modify the original template.");
    }

    private static void BindsInlineAndBlockContent()
    {
        var inline = TemplateContent.FromInline(new TextRun("Hallo"));
        Require(inline.Kind == TemplateContentKind.Inline, "Single inline content has the inline kind.");
        Require(inline.Blocks.Count == 0, "Inline content has no blocks.");
        Require(inline.Inlines.Count == 1, "Inline content contains exactly one element.");

        var block = TemplateContent.FromBlocks(new Block[] { new Paragraph(new TextRun("Text")) });
        Require(block.Kind == TemplateContentKind.Block, "Block content has the block kind.");
        Require(block.Inlines.Count == 0, "Block content has no inlines.");
        Require(block.Blocks.Count == 1, "Block content contains exactly one element.");

        var source = new List<Inline> { new TextRun("a") };
        var snapshot = TemplateContent.FromInlines(source);
        source.Add(new TextRun("b"));
        Require(snapshot.Inlines.Count == 1, "Inline content takes a snapshot.");

        Throws<ArgumentException>(() => TemplateContent.FromInlines(new Inline[] { null! }));
        Throws<ArgumentException>(() => TemplateContent.FromBlocks(new Block[] { null! }));
        Throws<ArgumentNullException>(() => TemplateContent.FromInline(null!));
        Throws<ArgumentNullException>(() => TemplateContent.FromBlock(null!));
        Throws<ArgumentNullException>(() => TemplateContent.FromInlines(null!));
        Throws<ArgumentNullException>(() => TemplateContent.FromBlocks(null!));
    }

    private static void RejectsInvalidPlaceholderNames()
    {
        var template = DocumentTemplate.FromBytes(new byte[] { 1 });

        Throws<ArgumentNullException>(() => template.BindText(null!, "x"));
        Throws<ArgumentException>(() => template.BindText("   ", "x"));
        Throws<ArgumentException>(() => template.BindText("{x}", "x"));
        Throws<ArgumentException>(() => template.BindText("x}", "x"));
        Throws<ArgumentNullException>(() => template.Bind("Title", (TemplateContent)null!));
        Throws<ArgumentNullException>(() => DocumentTemplate.Marker(null!));
        Throws<ArgumentException>(() => DocumentTemplate.Marker("a{b"));

        Require(DocumentTemplate.Marker("Title") == "{{Title}}", "Marker forms the identifier.");
        Require(DocumentTemplate.OpeningDelimiter == "{{", "Opening identifier.");
        Require(DocumentTemplate.ClosingDelimiter == "}}", "Closing identifier.");
    }

    private static void SetsBodyPlaceholder()
    {
        var template = DocumentTemplate.FromBytes(new byte[] { 1 });
        Require(template.BodyPlaceholder is null, "Without a specification there is no body placeholder.");

        var withBody = template.Body("Content");
        Require(withBody.BodyPlaceholder == "Content", "The body placeholder is adopted.");
        Require(template.BodyPlaceholder is null, "Body does not modify the original template.");

        Throws<ArgumentNullException>(() => template.Body(null!));
        Throws<ArgumentException>(() => template.Body(""));
        Throws<ArgumentException>(() => template.Body("{{x}}"));
    }

    private static void ValidatesWatermark()
    {
        var watermark = new Watermark("ENTWURF");
        Require(watermark.Opacity == 0.1, "Standard-Deckkraft.");
        Require(watermark.Rotation == 45, "Standard-Drehung.");
        Require(watermark.FontSize == Length.FromPoints(48), "Default font size.");

        Require(new Watermark("x") { Opacity = 0 }.Opacity == 0, "Opacity 0 is allowed.");
        Require(new Watermark("x") { Opacity = 1 }.Opacity == 1, "Opacity 1 is allowed.");
        Require(new Watermark("x") { Rotation = -360 }.Rotation == -360, "Rotation -360 is allowed.");

        Throws<ArgumentException>(() => new Watermark("  "));
        Throws<ArgumentOutOfRangeException>(() => new Watermark("x") { Opacity = -0.1 });
        Throws<ArgumentOutOfRangeException>(() => new Watermark("x") { Opacity = 1.1 });
        Throws<ArgumentOutOfRangeException>(() => new Watermark("x") { Opacity = double.NaN });
        Throws<ArgumentOutOfRangeException>(() => new Watermark("x") { Rotation = 361 });
        Throws<ArgumentOutOfRangeException>(() => new Watermark("x") { FontSize = Length.Zero });
    }

    private static void ValidatesPageFrame()
    {
        var frame = new PageFrame();
        Require(frame.Width == Length.FromPoints(1), "Default border width.");
        Require(frame.Inset == Length.FromPoints(18), "Default frame spacing.");
        Require(frame.Color == Color.Black, "Default frame color.");

        Require(new PageFrame { Inset = Length.Zero }.Inset == Length.Zero, "Frame spacing 0 is allowed.");
        Throws<ArgumentOutOfRangeException>(() => new PageFrame { Width = Length.Zero });
        Throws<ArgumentOutOfRangeException>(() => new PageFrame { Inset = Length.FromPoints(-1) });
    }

    private static void ComposesPageTemplate()
    {
        var template = new PageTemplate
        {
            Header = new HeaderFooter(new Paragraph(new TextRun("Header"))),
            Footer = new HeaderFooter(new Paragraph(new TextRun("Footer"))),
            Watermark = new Watermark("ENTWURF"),
            PageFrame = new PageFrame(),
        };

        Require(template.Header is not null, "Header is adopted.");
        Require(template.Footer is not null, "Footer is adopted.");
        Require(template.Watermark is { Text: "ENTWURF" }, "Watermark is adopted.");
        Require(template.PageFrame is not null, "Page frame is adopted.");

        var empty = new PageTemplate();
        Require(
            empty is { Header: null, Footer: null, Watermark: null, PageFrame: null },
            "A page template without specifications is valid.");
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

        throw new InvalidOperationException($"Erwartete Ausnahme {typeof(TException).Name} blieb aus.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
