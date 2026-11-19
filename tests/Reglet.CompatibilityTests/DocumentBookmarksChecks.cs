using Reglet;

/// <summary>editing a document at a bookmark.</summary>
internal static class DocumentBookmarksChecks
{
    internal static int Run()
    {
        try
        {
            ReplacesARange();
            InsertsAfterAZeroWidthBookmark();
            FindsBookmarksInTableCells();
            InsertsBlocksAfter();
            RejectsUnknownAndForeignRanges();
            Console.WriteLine("Edit at a bookmark: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Edit at a bookmark: " + exception);
            return 1;
        }
    }

    private static PortableDocument Doc(params Block[] blocks) => new(new DocumentMetadata(), new Section(blocks));

    private static string TextOf(PortableDocument document)
        => string.Concat(document.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<TextRun>().Select(run => run.Text));

    private static void ReplacesARange()
    {
        var document = Doc(new Paragraph(new Inline[] { new TextRun("A "), new Bookmark("x"), new TextRun("old"), new BookmarkEnd("x"), new TextRun(" B") }));
        var result = DocumentBookmarks.ReplaceContent(document, "x", [new TextRun("new")]);
        Require(TextOf(result) == "A new B", "The range content is replaced: " + TextOf(result));
        Require(TextOf(document) == "A old B", "The original stays.");
    }

    private static void InsertsAfterAZeroWidthBookmark()
    {
        var document = Doc(new Paragraph(new Inline[] { new TextRun("A"), new Bookmark("x"), new TextRun("B") }));
        Require(TextOf(DocumentBookmarks.ReplaceContent(document, "x", [new TextRun("-")])) == "A-B", "A zero-width bookmark gets the content after it.");
    }

    private static void FindsBookmarksInTableCells()
    {
        var cell = new TableCell(new Block[] { new Paragraph(new Inline[] { new Bookmark("c"), new BookmarkEnd("c") }) });
        var document = Doc(new TableBlock(new[] { new TableRow(new[] { cell }) }));
        var result = DocumentBookmarks.ReplaceContent(document, "c", [new TextRun("in cell")]);
        var text = ((TableBlock)result.Sections[0].Blocks[0]).Rows[0].Cells[0].Blocks.OfType<Paragraph>().Single().Inlines.OfType<TextRun>().Single().Text;
        Require(text == "in cell", "A bookmark in a table cell is found.");
    }

    private static void InsertsBlocksAfter()
    {
        var document = Doc(new Paragraph(new Inline[] { new Bookmark("p"), new TextRun("first") }), new Paragraph(new TextRun("last")));
        var result = DocumentBookmarks.InsertBlocksAfter(document, "p", [new Paragraph(new TextRun("between"))]);
        Require(TextOf(result) == "firstbetweenlast", "The blocks come right after the block of the bookmark: " + TextOf(result));
    }

    private static void RejectsUnknownAndForeignRanges()
    {
        var document = Doc(
            new Paragraph(new Inline[] { new Bookmark("r"), new TextRun("a") }),
            new Paragraph(new Inline[] { new TextRun("b"), new BookmarkEnd("r") }));
        Throws<ArgumentException>(() => DocumentBookmarks.ReplaceContent(document, "missing", [new TextRun("x")]));
        Throws<InvalidOperationException>(() => DocumentBookmarks.ReplaceContent(document, "r", [new TextRun("x")]));
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

        throw new InvalidOperationException($"{typeof(TException).Name} expected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
