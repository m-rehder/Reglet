using Reglet;

internal static class BuilderChecks
{
    internal static int Run()
    {
        try
        {
            BuildReturnsImmutableDocument();
            BuilderCanBeReused();
            ReportsErrorsEarly();
            Console.WriteLine("Builder (E03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Builder (E03): " + exception);
            return 1;
        }
    }

    private static void BuildReturnsImmutableDocument()
    {
        var builder = PortableDocument.Create()
            .Metadata(title: "Title", author: "Author")
            .Heading("Heading", level: 1)
            .Paragraph("Paragraph");

        var document = builder.Build();

        // Later builder changes must not modify the built document.
        builder.Paragraph("later");

        Require(document.Sections.Count == 1, "Build yields exactly one section.");
        Require(document.Sections[0].Blocks.Count == 2, "The built document is an immutable snapshot.");
        Require(document.Metadata.Title == "Title", "Metadata was adopted.");
        Require(document.Sections[0].Blocks[0] is Heading, "First block is the heading.");
    }

    private static void BuilderCanBeReused()
    {
        var builder = PortableDocument.Create().Paragraph("A");

        var first = builder.Build();
        builder.Paragraph("B");
        var second = builder.Build();

        Require(first.Sections[0].Blocks.Count == 1, "First document stays at one block.");
        Require(second.Sections[0].Blocks.Count == 2, "Second document contains both blocks.");
    }

    private static void ReportsErrorsEarly()
    {
        Throws<ArgumentOutOfRangeException>(() => PortableDocument.Create().Heading("Title", level: 0));
        Throws<ArgumentOutOfRangeException>(() => PortableDocument.Create().Heading("Title", level: 7));
        Throws<ArgumentNullException>(() => PortableDocument.Create().Heading(null!));
        Throws<ArgumentNullException>(() => PortableDocument.Create().Paragraph((string)null!));
        Throws<ArgumentNullException>(() => PortableDocument.Create().Page(null!));
        Throws<ArgumentOutOfRangeException>(() => PortableDocument.Create().Page(PageSize.A4, orientation: (PageOrientation)7));
        Throws<ArgumentException>(() => PortableDocument.Create().Page(
            new PageSize(Length.FromPoints(100), Length.FromPoints(100)),
            new PageMargins(Length.FromPoints(60))));
        Throws<ArgumentException>(() => PortableDocument.Create().Image(ReadOnlyMemory<byte>.Empty, ImageMediaType.Png));
        Throws<ArgumentOutOfRangeException>(() => PortableDocument.Create().Image(new byte[] { 1 }, ImageMediaType.Png, width: Length.Zero));
        Throws<ImageException>(() => PortableDocument.Create().Image(new byte[] { 1, 2, 3 }, ImageMediaType.Png));
        Throws<ImageException>(() => PortableDocument.Create().Image(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, ImageMediaType.Jpeg));
        Throws<ArgumentException>(() => PortableDocument.Create().Metadata(keywords: new[] { "ok", null! }));
        Throws<ArgumentNullException>(() => PortableDocument.Create().Table(t => t.Row("x", null!)));
        Throws<ArgumentException>(() => Color.FromHex("no-hex"));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}
