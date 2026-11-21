using Reglet;

/// <summary>
/// Builds a random document from a seed: awkward text (control characters, unpaired surrogates, bidi, very long
/// words), link targets, nested tables and lists, notes, comments, revisions, fields, charts and form fields. A model
/// element the constructors refuse is replaced by plain text, so every seed gives a document.
/// </summary>
internal sealed class Generator(Random r)
{
    static readonly string[] Texts =
    [
        "", " ", "  x  ", "\t", "\n", "\r\n", "a\u0000b", "\u0001\u0008\u000B\u001F", "\uFFFE\uFFFF", "\uD800", "x\uDC00", "\uD83D\uDE00\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67", "\u0645\u0631\u062D\u0628\u0627 \u0628\u0627\u0644\u0639\u0627\u0644\u0645", "\u05E9\u05DC\u05D5\u05DD", "e\u0301\u0302\u0303",
        "]]>", "<script>alert(1)</script>", "&amp;&lt;", "\"'`", "%s%n", "\\", "(unbalanced", ")", "#", "{{Name}}", "{{Repeat:x}}", "MERGEFIELD x", "\u200B\u200D\uFEFF",
        "Normal text with several words to wrap around the line end many times over and over.", new string('W', 5000), string.Concat(Enumerable.Repeat("ab ", 3000)),
        "\u202Eevil\u202C", "\uFB01 \uFB02", "\u0130\u0131", "\u00DF", "\u00AD soft", "x\u2028y\u2029z", "-1e308", "NaN",
    ];
    static readonly string[] Targets =
    [
        "https://example.com", "https://a/b c", "mailto:a@b", "#anchor", "#", "file:///etc/passwd", "javascript:alert(1)", "JaVaScRiPt:alert(1)", " javascript:x", "java\tscript:x",
        "data:text/html,<script>x</script>", "http://x/\"onmouseover=\"x", "../../x", "\\\\server\\share", "https://ex.com/?a=1&b=<2>", "vbscript:x", "x", "http://[::1]/", "https://e.com/" + new string('a', 3000),
    ];
    int depth;
    int id;

    string T() => r.Next(5) == 0 ? Texts[r.Next(Texts.Length)] + Texts[r.Next(Texts.Length)] : Texts[r.Next(Texts.Length)];
    string Name() => r.Next(3) switch { 0 => "b" + r.Next(5), 1 => T(), _ => "_Toc" + r.Next(100) };

    public PortableDocument Document()
    {
        var sections = Enumerable.Range(0, 1 + r.Next(3)).Select(_ => Section()).ToList();
        var doc = new PortableDocument(new DocumentMetadata { Title = r.Next(2) == 0 ? T() : null, Author = r.Next(2) == 0 ? T() : null, Language = r.Next(4) == 0 ? T() : "en" }, sections);
        return doc with { Numbering = [.. lists.Select(n => new NumberingDefinition(n, [new NumberingLevel { Kind = r.Next(2) == 0 ? ListKind.Bullet : ListKind.Number }]))] };
    }
    readonly List<string> lists = [];

    Section Section()
    {
        var s = new Section(Blocks(1 + r.Next(8)));
        return r.Next(3) == 0 ? new Section(s.Blocks) { Header = new HeaderFooter(Blocks(1 + r.Next(2))), Footer = new HeaderFooter(new Paragraph([new PageNumberField(), new TextRun(T()), new PageCountField()])) } : s;
    }

    List<Block> Blocks(int n) => Enumerable.Range(0, n).Select(_ => Block()).ToList();

    Block Block()
    {
        try { return BlockCore(); } catch (ArgumentException) { return new Paragraph(new TextRun("fallback")); }
    }

    Block BlockCore()
    {
        depth++;
        try
        {
            var deep = depth > 4;
            switch (deep ? r.Next(2) : r.Next(14))
            {
                case 0: case 1: return Para();
                case 2: return new Heading((HeadingLevel)(1 + r.Next(6)), Inlines());
                case 3: { var n = "L" + r.Next(3); if (!lists.Contains(n)) lists.Add(n); return new ListBlock(n, Enumerable.Range(0, 1 + r.Next(4)).Select(_ => new ListItem(Blocks(1 + r.Next(2))))) { NestingLevel = 1 }; }
                case 4: case 5:
                {
                    var cols = 1 + r.Next(4);
                    var rows = Enumerable.Range(0, 1 + r.Next(4)).Select(i => new TableRow(Enumerable.Range(0, cols).Select(_ => new TableCell(Blocks(1 + r.Next(2))) { ColumnSpan = r.Next(6) == 0 ? 2 : 1, RowSpan = r.Next(8) == 0 ? 2 : 1, Background = r.Next(3) == 0 ? new Color(1, 2, 3) : null })) { IsHeader = i == 0 && r.Next(2) == 0 });
                    return new TableBlock(rows) { CornerRadius = r.Next(4) == 0 ? Length.FromPoints(r.Next(30)) : null, WidthPercent = r.Next(3) == 0 ? 1 + r.Next(100) : null };
                }
                case 6: return r.Next(2) == 0 ? new PageBreak() : new ColumnBreak();
                case 7:
                {
                    var kind = (ChartKind)r.Next(Enum.GetValues<ChartKind>().Length);
                    var n = 1 + r.Next(40);
                    var series = Enumerable.Range(0, kind == ChartKind.Pie ? 1 : 1 + r.Next(30)).Select(i => new ChartSeries(T(), Enumerable.Range(0, n).Select(_ => r.Next(4) == 0 ? -r.NextDouble() * 1e9 : r.NextDouble() * Math.Pow(10, r.Next(-5, 15))), kind == ChartKind.Scatter ? Enumerable.Range(0, n).Select(_ => r.NextDouble()) : null)).ToList();
                    return new Chart(kind, series, kind == ChartKind.Scatter ? null : Enumerable.Range(0, n).Select(_ => T()).ToList()) { Title = r.Next(2) == 0 ? T() : null };
                }
                case 8: return r.Next(2) == 0 ? new FormField(FormFieldKind.DropDown, Name(), options: [T(), T()]) : new FormField((FormFieldKind)r.Next(Enum.GetValues<FormFieldKind>().Length), Name());
                case 9: return new TableOfContents();
                case 10: return new AlphabeticalIndex();
                case 11: return new Paragraph([new TextRun(T()), new Footnote(Blocks(1 + r.Next(2)))]);
                default: return Para();
            }
        }
        finally { depth--; }
    }

    Paragraph Para()
    {
        var p = new Paragraph(Inlines());
        return r.Next(4) != 0 ? p : new Paragraph(p.Inlines) { Style = new ParagraphStyle { RightToLeft = r.Next(2) == 0, FontFamily = r.Next(2) == 0 ? T() : null, FontSize = Length.FromPoints(1 + r.Next(200)), LeftIndent = Length.FromPoints(r.Next(500)), FirstLineIndent = Length.FromPoints(r.Next(-100, 100)) } };
    }

    List<Inline> Inlines()
    {
        var list = new List<Inline>();
        for (var i = 0; i < 1 + r.Next(6); i++)
        {
            try { AddInline(list); } catch (ArgumentException) { list.Add(new TextRun("f")); }
        }
        return list;
    }

    void AddInline(List<Inline> list)
    {
        {
            switch (r.Next(16))
            {
                case 0: list.Add(new Hyperlink(Targets[r.Next(Targets.Length)], [Run()])); break;
                case 1: { var n = Name(); list.Add(new Bookmark(n)); list.Add(Run()); list.Add(new BookmarkEnd(n)); break; }
                case 2: { var c = id++; list.Add(new Comment(c, T(), [T(), T()])); list.Add(Run()); if (r.Next(3) > 0) list.Add(new CommentEnd(c)); break; }
                case 3: { var c = id++; list.Add(new TrackedChange(c, (TrackedChangeKind)r.Next(2), T())); list.Add(Run()); if (r.Next(3) > 0) list.Add(new TrackedChangeEnd(c)); break; }
                case 4: list.Add(new Field(r.Next(2) == 0 ? T() : "=1/0+" + T(), r.Next(2) == 0 ? T() : null)); break;
                case 5: list.Add(new PageRefField(Name())); break;
                case 6: list.Add(new DocPropertyField(T())); break;
                case 7: list.Add(new IndexEntry(T(), r.Next(2) == 0 ? T() : null)); break;
                case 8: list.Add(new DateField(r.Next(2) == 0 ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue)); break;
                case 9: list.Add(new Hyperlink(Targets[r.Next(Targets.Length)], [new Comment(id++, "a", "b"), Run()])); break;
                case 10: list.Add(new DocVariableField(T())); break;
                default: list.Add(Run()); break;
            }
        }
    }

    TextRun Run() => new(T())
    {
        Bold = r.Next(3) == 0, Italic = r.Next(3) == 0, Underline = r.Next(5) == 0, Hidden = r.Next(10) == 0, AllCaps = r.Next(8) == 0, SmallCaps = r.Next(8) == 0,
        Color = r.Next(4) == 0 ? new Color((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256)) : null,
        CharacterSpacing = r.Next(6) == 0 ? Length.FromPoints(r.Next(-50, 50)) : null, Position = r.Next(6) == 0 ? Length.FromPoints(r.Next(-50, 50)) : null,
        Highlight = r.Next(8) == 0 ? new Color(255, 255, 0) : null, CharacterStyleName = r.Next(10) == 0 ? T() : null,
    };
}
