using Reglet;

namespace Gallery;

/// <summary>Text: character and paragraph formatting, styles, lists, links and right-to-left scripts.</summary>
public static class Text
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("text", "character-formatting", "Bold, italic, underline, colours, fonts, sizes, highlight, sub- and superscript.")
        {
            Document = CharacterFormatting,
        };
        yield return new Example("text", "paragraph-formatting", "Alignment, indents, spacing, line spacing, borders, shading and tab stops.")
        {
            Document = ParagraphFormatting,
        };
        yield return new Example("text", "styles", "Named paragraph and character styles that Word and LibreOffice show in their style lists.")
        {
            Document = Styles,
        };
        yield return new Example("text", "lists", "Bulleted, numbered and nested lists, a list that starts at 5 and Roman numbers.")
        {
            Document = Lists,
        };
        yield return new Example("text", "links-and-bookmarks", "Web links, a link to a place in the document and a bookmark.")
        {
            Document = Links,
        };
        // The PDF standard fonts have no Arabic or Hebrew letters: the PDF embeds a system font that has them.
        var font = Assets.FindFont("Arial Unicode.ttf", "arial.ttf", "DejaVuSans.ttf");
        yield return new Example("text", "right-to-left", "Arabic and Hebrew paragraphs next to English, in the right reading order.")
        {
            Document = RightToLeft,
            Formats = font is null ? ["docx", "odt"] : ["docx", "odt", "pdf"],
            OptionsFor = extension => extension == "pdf" && font is not null ? new PdfWriteOptions { Fonts = new Dictionary<string, byte[]> { [""] = File.ReadAllBytes(font) } } : null,
        };
    }

    private static PortableDocument CharacterFormatting()
        => PortableDocument.Create()
            .Metadata(title: "Character formatting")
            .Heading("Character formatting")
            .Paragraph(p => p.Bold("Bold").Text(", ").Italic("italic").Text(", ").Underline("underlined")
                .Text(" and ").Run(new TextRun("all three") { Bold = true, Italic = true, Underline = true }).Text("."))
            .Paragraph(p => p.Span("Petrol", color: Assets.Petrol).Text(" and ").Span("orange", color: Assets.Accent).Text(" text, ")
                .Run(new TextRun("highlighted") { Highlight = Color.FromHex("FFF59D") }).Text(", ")
                .Run(new TextRun("struck through") { Strikethrough = TextStrikethrough.Single }).Text("."))
            .Paragraph(p => p.Span("Times New Roman 14 pt", fontFamily: "Times New Roman", fontSize: Length.FromPoints(14)).Text(" · ")
                .Span("Courier New 10 pt", fontFamily: "Courier New", fontSize: Length.FromPoints(10)))
            .Paragraph(p => p.Text("Water is H").Run(new TextRun("2") { Baseline = TextBaseline.Subscript })
                .Text("O and E = mc").Run(new TextRun("2") { Baseline = TextBaseline.Superscript }).Text(".")
                .Text(" ").Run(new TextRun("Small capitals") { SmallCaps = true }).Text(", ")
                .Run(new TextRun("spaced out") { CharacterSpacing = Length.FromPoints(2) }).Text("."))
            .Build();

    private static PortableDocument ParagraphFormatting()
    {
        var box = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Assets.Petrol);
        return PortableDocument.Create()
            .Metadata(title: "Paragraph formatting")
            .Heading("Paragraph formatting")
            .Paragraph(p => p.Text("Centred.").Alignment(TextAlignment.Center))
            .Paragraph(p => p.Text("Right-aligned.").Alignment(TextAlignment.Right))
            .Paragraph(p => p.Text("Justified: the words of this paragraph are spread so that every full line ends at the right margin, which is how books and newspapers set their columns.")
                .Alignment(TextAlignment.Justify))
            .Paragraph(p => p.Text("Indented 2 cm on the left and 1 cm on the right, with 12 pt of space before and after.")
                .LeftIndent(Length.FromCentimeters(2)).RightIndent(Length.FromCentimeters(1))
                .SpaceBefore(Length.FromPoints(12)).SpaceAfter(Length.FromPoints(12)))
            .Paragraph(p => p.Text("A hanging indent: the first line starts further left than the rest of the paragraph, as in a bibliography or a glossary.")
                .Style(ParagraphStyle.Default with { LeftIndent = Length.FromCentimeters(1), FirstLineIndent = Length.FromCentimeters(-1) }))
            .Paragraph(p => p.Text("Double line spacing makes room for handwritten notes between the lines of a draft.")
                .Style(ParagraphStyle.Default with { LineSpacing = LineSpacing.Multiple(2) }))
            .Paragraph(p => p.Text("A paragraph in a box with a tinted background.")
                .Style(ParagraphStyle.Default with { Borders = ParagraphBorders.All(box), Background = Assets.Tint }))
            .Paragraph(p => p.Text("Item\tQuantity\tPrice")
                .Style(ParagraphStyle.Default with
                {
                    TabStops = [new TabStop(Length.FromCentimeters(8), TabStopAlignment.Center), new TabStop(Length.FromCentimeters(15), TabStopAlignment.Right)],
                }))
            .Paragraph(p => p.Text("Coffee\t2\t5.80")
                .Style(ParagraphStyle.Default with
                {
                    TabStops = [new TabStop(Length.FromCentimeters(8), TabStopAlignment.Center), new TabStop(Length.FromCentimeters(15), TabStopAlignment.Right, TabStopLeader.Dot)],
                }))
            .Build();
    }

    private static PortableDocument Styles()
    {
        // Named styles: a document changes its look by changing the style, not every paragraph.
        var note = new ParagraphStyle { StyleName = "Note", LeftIndent = Length.FromCentimeters(1), TextColor = Assets.Petrol, Italic = true };
        var term = new CharacterStyle("Term") { Bold = true, Color = Assets.Accent };
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Styles")),
            new Paragraph(new Inline[]
            {
                new TextRun("A "), new TextRun("paragraph style") { CharacterStyleName = "Term" },
                new TextRun(" formats whole paragraphs, a "), new TextRun("character style") { CharacterStyleName = "Term" },
                new TextRun(" formats words inside them."),
            }),
            new Paragraph(new TextRun("This paragraph uses the style \"Note\". Change the style in Word or LibreOffice and every note follows."))
            {
                Style = new ParagraphStyle { StyleName = "Note" },
            },
        };
        return new PortableDocument(new DocumentMetadata { Title = "Styles" }, new Section(blocks))
        {
            Styles = [note],
            CharacterStyles = [term],
        };
    }

    private static PortableDocument Lists()
    {
        NumberingDefinition bullets = new("bullets", [new NumberingLevel { Kind = ListKind.Bullet }, new NumberingLevel { Kind = ListKind.Bullet }]);
        NumberingDefinition steps = new("steps", [new NumberingLevel { Kind = ListKind.Number }, new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.LowerLetter }]);
        NumberingDefinition fromFive = new("from-five", [new NumberingLevel { Kind = ListKind.Number, Start = 5, NumberColor = Assets.Accent, NumberBold = true }]);
        NumberingDefinition roman = new("roman", [new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.UpperRoman }]);

        static ListItem Item(string text, ListBlock? children = null)
            => children is null ? new ListItem(new Paragraph(new TextRun(text))) : new ListItem(new Block[] { new Paragraph(new TextRun(text)), children });

        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Lists")),
            new Paragraph(new TextRun("Packing list:")),
            new ListBlock("bullets", [Item("Passport"), Item("Charger", new ListBlock("bullets", [Item("Phone"), Item("Laptop")])), Item("Umbrella")]),
            new Paragraph(new TextRun("Setting up:")),
            new ListBlock("steps", [Item("Unpack the device."), Item("Connect it:", new ListBlock("steps", [Item("power"), Item("network")])), Item("Switch it on.")]),
            new Paragraph(new TextRun("Continued from page 2:")),
            new ListBlock("from-five", [Item("Check the seals."), Item("Sign the form.")]),
            new Paragraph(new TextRun("Chapters:")),
            new ListBlock("roman", [Item("Introduction"), Item("Method"), Item("Results")]),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Lists" }, new Section(blocks))
        {
            Numbering = [bullets, steps, fromFive, roman],
        };
    }

    private static PortableDocument Links()
    {
        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Links and bookmarks")),
            new Paragraph(new Inline[] { new TextRun("Read the specification of "), new Hyperlink("https://www.iso.org/standard/71178.html", "Office Open XML"), new TextRun(".") }),
            new Paragraph(new Inline[] { new TextRun("Jump to the "), new Hyperlink("#appendix", "appendix"), new TextRun(" at the end of this document.") }),
            new PageBreak(),
            new Heading(HeadingLevel.Level2, [new Bookmark("appendix"), new TextRun("Appendix")]),
            new Paragraph(new TextRun("The link on the first page brings you here.")),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Links and bookmarks" }, new Section(blocks));
    }

    private static PortableDocument RightToLeft()
        => PortableDocument.Create()
            .Metadata(title: "Right-to-left", language: "ar")
            .Heading("Right-to-left scripts")
            .Paragraph("Arabic and Hebrew run from right to left; numbers and Latin words inside them keep their own direction.")
            .Paragraph(p => p.Text("مرحبا بالعالم، هذا مستند تجريبي رقم 42.").Style(new ParagraphStyle { RightToLeft = true }))
            .Paragraph(p => p.Text("שלום עולם, זהו מסמך לדוגמה עם Reglet.").Style(new ParagraphStyle { RightToLeft = true }))
            .Build();
}
