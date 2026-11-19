using Reglet;

namespace Gallery;

/// <summary>Page layout: sizes and margins, sections, headers and footers, columns, notes and a table of contents.</summary>
public static class Layout
{
    /// <summary>The examples of this category.</summary>
    public static IEnumerable<Example> All()
    {
        yield return new Example("layout", "page-setup", "A4 portrait, A4 landscape and US Letter in one document, each with its own margins.")
        {
            Document = PageSetup,
        };
        yield return new Example("layout", "headers-and-footers", "A different first page, \"Page 3 of 7\" in the footer and the title in the header.")
        {
            Document = HeadersAndFooters,
        };
        yield return new Example("layout", "columns", "A newspaper page: a headline across the page and the text in two columns.")
        {
            Document = Columns,
        };
        yield return new Example("layout", "footnotes", "Footnotes numbered automatically at the foot of each page.")
        {
            Document = Footnotes,
        };
        yield return new Example("layout", "table-of-contents", "A table of contents built from the headings, with page numbers and links.")
        {
            Document = TableOfContents,
        };
    }

    private static PortableDocument PageSetup()
    {
        static Section Page(string text, PageSize size, PageOrientation orientation, PageMargins margins)
            => new(new Block[] { new Heading(HeadingLevel.Level1, new TextRun(text)), new Paragraph(new TextRun("Each section has its own page size, orientation and margins.")) })
            {
                PageSize = size,
                Orientation = orientation,
                Margins = margins,
            };

        var cm = Length.FromCentimeters;
        return new PortableDocument(new DocumentMetadata { Title = "Page setup" },
        [
            Page("A4 portrait, normal margins", PageSize.A4, PageOrientation.Portrait, PageMargins.Normal),
            Page("A4 landscape, narrow margins", PageSize.A4, PageOrientation.Landscape, new PageMargins(cm(1.27), cm(1.27), cm(1.27), cm(1.27))),
            Page("US Letter, wide left margin for binding", PageSize.Letter, PageOrientation.Portrait, new PageMargins(cm(2.5), cm(2), cm(4), cm(2))),
        ]);
    }

    private static PortableDocument HeadersAndFooters()
    {
        var blocks = new List<Block> { new Heading(HeadingLevel.Level1, new TextRun("Annual report")) };
        for (var chapter = 1; chapter <= 6; chapter++)
        {
            blocks.Add(new Heading(HeadingLevel.Level2, new TextRun($"Chapter {chapter}")));
            blocks.Add(new Paragraph(new TextRun(Words.Paragraph(chapter, 120))));
            blocks.Add(new PageBreak());
        }

        blocks.RemoveAt(blocks.Count - 1);
        var centred = ParagraphStyle.Default with { Alignment = TextAlignment.Center };
        var section = new Section(blocks)
        {
            // The first page is a title page: no header, a short footer.
            FirstHeader = new HeaderFooter(new Paragraph(new TextRun(""))),
            FirstFooter = new HeaderFooter(new Paragraph(new TextRun("Reglet examples · 2026")) { Style = centred }),
            Header = new HeaderFooter(new Paragraph(new TextRun("Annual report") { Color = Assets.Petrol }) { Style = ParagraphStyle.Default with { Alignment = TextAlignment.Right } }),
            Footer = new HeaderFooter(new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField(), new TextRun(" of "), new PageCountField() }) { Style = centred }),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Headers and footers" }, section);
    }

    private static PortableDocument Columns()
    {
        var headline = new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Town opens its new library")),
            new Paragraph(new TextRun("A reglet is the strip of wood a printer puts between paragraphs. This page sets its text in two columns.") { Italic = true }),
        })
        {
            Margins = PageMargins.Normal,
        };
        var body = new Section(Enumerable.Range(1, 6).Select(index => (Block)new Paragraph(new TextRun(Words.Paragraph(index, 70)))).ToList())
        {
            TextColumns = 2,
            StartType = SectionStartType.Continuous,
        };
        return new PortableDocument(new DocumentMetadata { Title = "Columns" }, [headline, body]);
    }

    private static PortableDocument Footnotes()
    {
        static Paragraph WithNote(string before, string note, string after)
            => new(new Inline[] { new TextRun(before), new Footnote(new Paragraph(new TextRun(note))), new TextRun(after) });

        var blocks = new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Footnotes")),
            WithNote("Movable type came to Europe in the fifteenth century", "Johannes Gutenberg, Mainz, about 1450.", " and changed how books were made."),
            WithNote("Between the lines of type the printer set strips of lead or wood", "Thin strips of lead are called leads, wider strips of wood reglets.", "; their width decided the spacing."),
            new Paragraph(new TextRun(Words.Paragraph(3, 90))),
        };
        return new PortableDocument(new DocumentMetadata { Title = "Footnotes" }, new Section(blocks));
    }

    private static PortableDocument TableOfContents()
    {
        var blocks = new List<Block>
        {
            new Heading(HeadingLevel.Level1, new TextRun("Handbook")),
            new Reglet.TableOfContents { Title = "Contents", MaxLevel = 2 },
            new PageBreak(),
        };
        foreach (var (chapter, sections) in new[] { ("Getting started", new[] { "Installation", "First document" }), ("Formatting", new[] { "Text", "Tables" }), ("Output", new[] { "PDF", "HTML" }) })
        {
            blocks.Add(new Heading(HeadingLevel.Level1, new TextRun(chapter)));
            foreach (var title in sections)
            {
                blocks.Add(new Heading(HeadingLevel.Level2, new TextRun(title)));
                blocks.Add(new Paragraph(new TextRun(Words.Paragraph(title.Length, 160))));
            }
        }

        return new PortableDocument(new DocumentMetadata { Title = "Table of contents" }, new Section(blocks));
    }
}

/// <summary>Neutral filler text in English, so the layout examples have something to lay out.</summary>
public static class Words
{
    private static readonly string[] Pool =
    [
        "the", "page", "line", "letter", "printer", "paper", "ink", "column", "margin", "word", "space", "type",
        "measure", "chapter", "reader", "story", "press", "proof", "sheet", "edition", "heading", "note", "table",
        "picture", "author", "copy", "draft", "layout", "setting", "frame",
    ];

    /// <summary>A sentence-like paragraph of about <paramref name="words"/> words; the seed varies it.</summary>
    public static string Paragraph(int seed, int words)
    {
        var random = new Random(seed);
        var text = new System.Text.StringBuilder();
        var sentence = 0;
        for (var index = 0; index < words; index++)
        {
            var word = Pool[random.Next(Pool.Length)];
            text.Append(sentence == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);
            sentence++;
            if (sentence > 7 && random.Next(4) == 0 || index == words - 1)
            {
                text.Append(". ");
                sentence = 0;
            }
            else
            {
                text.Append(' ');
            }
        }

        return text.ToString().TrimEnd();
    }
}
