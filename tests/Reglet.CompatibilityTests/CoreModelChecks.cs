using System.Runtime.InteropServices;
using Reglet;

internal static class CoreModelChecks
{
    internal static int Run()
    {
        try
        {
            CopiesImageBytes();
            CopiesOptionalCollections();
            RejectsNullGraphElements();
            RejectsInvalidProperties();
            ConvertsLengthsAndColors();
            UsesCanonicalPoints();
            UsesA4ByDefault();
            UsesIdenticalDefaultMargins();
            ResolvesTableWidths();
            SnapshotsWriteWarnings();
            ExposesErrorHierarchy();
            RejectsUnsafePackagePaths();
            Console.WriteLine("Core model: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Core-Modell: " + exception);
            return 1;
        }
    }

    private static void CopiesImageBytes()
    {
        var source = new byte[] { 1, 2, 3 };
        var image = new ImageBlock(source, ImageMediaType.Png);
        source[0] = 9;
        Require(image.Data.Span[0] == 1, "Image must copy input bytes.");

        var exposed = image.Data;
        Require(MemoryMarshal.TryGetArray(exposed, out var segment), "Test needs an array slice.");
        segment.Array![segment.Offset] = 8;
        Require(image.Data.Span[0] == 1, "Image must not expose its internal storage.");
    }

    private static void CopiesOptionalCollections()
    {
        var keywords = new List<string> { "original" };
        var metadata = new DocumentMetadata { Keywords = keywords };
        keywords[0] = "changed";
        Require(metadata.Keywords![0] == "original", "Keywords must be copied.");

        var widths = new List<Length> { Length.FromCentimeters(3) };
        var table = new TableBlock([new TableRow(new TableCell("Wert"))]) { ColumnWidths = widths };
        widths[0] = Length.FromCentimeters(5);
        Require(table.ColumnWidths![0] == Length.FromCentimeters(3), "Column widths must be copied.");
    }

    private static void RejectsNullGraphElements()
    {
        var section = new Section(new Paragraph(new TextRun("Text")));
        Throws<ArgumentException>(() => new PortableDocument(new DocumentMetadata(), new Section[] { section, null! }));
        Throws<ArgumentException>(() => new Section(new Block[] { null! }));
        Throws<ArgumentException>(() => new Paragraph(new Inline[] { null! }));
        Throws<ArgumentException>(() => new DocumentMetadata { Keywords = new string[] { null! } });
        Throws<ArgumentException>(() => new TableBlock(new TableRow[] { null! }));
    }

    private static void RejectsInvalidProperties()
    {
        var paragraph = new Paragraph(new TextRun("Text"));
        Throws<ArgumentNullException>(() => new PortableDocument(new DocumentMetadata(), new Section(paragraph)) { Defaults = null! });
        Throws<ArgumentNullException>(() => new Section(paragraph) { PageSize = null! });
        Throws<ArgumentNullException>(() => new Paragraph(new TextRun("Text")) { Style = null! });
        Throws<ArgumentOutOfRangeException>(() => new ImageBlock(new byte[] { 1 }, ImageMediaType.Png) { Width = Length.Zero });
        Throws<ArgumentOutOfRangeException>(() => new TextRun("Text") { FontSize = Length.Zero });
        Throws<ArgumentOutOfRangeException>(() => new TableBlock([]) { ColumnWidths = new[] { Length.Zero } });
        Throws<ArgumentOutOfRangeException>(() => new Heading((HeadingLevel)7, new TextRun("Title")));
        Throws<ArgumentOutOfRangeException>(() => new Section(paragraph) { Orientation = (PageOrientation)7 });
        Throws<ArgumentOutOfRangeException>(() => new PdfWriteOptions { UnsupportedContent = (UnsupportedContentBehavior)7 });
    }

    private static void ResolvesTableWidths()
    {
        var rows = new[] { new TableRow(new TableCell("a")) };
        var withExplicit = new TableBlock(rows) { ColumnWidths = new[] { Length.FromPoints(100) } };
        var widths = TableLayout.ResolveColumnWidths(withExplicit, 3, 400);
        Require(widths.Length == 3, "Three columns resolved.");
        Require(widths[0] == 100, "Explizite Breite bleibt absolut.");
        Require(Math.Abs(widths[1] - 150) < 1e-9 && Math.Abs(widths[2] - 150) < 1e-9,
            "The remaining width is distributed evenly.");

        var equal = TableLayout.ResolveColumnWidths(new TableBlock(rows), 2, 300);
        Require(Math.Abs(equal[0] - 150) < 1e-9 && Math.Abs(equal[1] - 150) < 1e-9,
            "Without a specification all columns are distributed evenly.");

        Throws<ArgumentException>(() => TableLayout.ResolveColumnWidths(withExplicit, 2, 100));
        Throws<DocumentValidationException>(() => TableLayout.ResolveColumnWidths(withExplicit, 1, 0));
        var excess = new TableBlock(rows)
        {
            ColumnWidths = new[] { Length.FromPoints(50), Length.FromPoints(50) },
        };
        Throws<ArgumentException>(() => TableLayout.ResolveColumnWidths(excess, 1, 200));

        // A full set wider than the text area is scaled down instead of rejected.
        var wide = TableLayout.ResolveColumnWidths(new TableBlock(rows) { ColumnWidths = [Length.FromPoints(300), Length.FromPoints(100)] }, 2, 200);
        Require(Math.Abs(wide[0] - 150) < 1e-9 && Math.Abs(wide[1] - 50) < 1e-9, "A too wide full set of column widths is scaled down.");

        // A table read from DOCX or ODT exceeds the text width by rounding; it can be written again.
        var table = new PortableDocument(new DocumentMetadata(), new Section(new TableBlock([new TableRow(new TableCell("a"), new TableCell("b"))])));
        foreach (var (source, targets) in new[] { (DocumentFormat.Docx, new[] { DocumentFormat.Odt, DocumentFormat.Pdf }), (DocumentFormat.Odt, new[] { DocumentFormat.Docx, DocumentFormat.Pdf }) })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(table, buffer, source).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, source).Document;
            foreach (var target in targets)
            {
                DocumentWriter.WriteAsync(read, new MemoryStream(), target).GetAwaiter().GetResult();
            }
        }
    }

    private static void RejectsUnsafePackagePaths()
    {
        Require(PackagePath.Validate("word/document.xml") == "word/document.xml", "Normaler Pfad bleibt erlaubt.");
        Require(PackagePath.Validate("[Content_Types].xml") == "[Content_Types].xml", "OPC path with braces.");
        Require(PackagePath.Validate("_rels/.rels") == "_rels/.rels", "Beziehungspfad.");
        Throws<DocumentValidationException>(() => PackagePath.Validate("/etc/passwd"));
        Throws<DocumentValidationException>(() => PackagePath.Validate("../secret.xml"));
        Throws<DocumentValidationException>(() => PackagePath.Validate("word/../../x.xml"));
        Throws<DocumentValidationException>(() => PackagePath.Validate("C:/x"));
    }

    private static void ExposesErrorHierarchy()
    {
        Require(new DocumentValidationException("v") is ArgumentException, "Validation is ArgumentException.");
        Require(new UnsupportedFeatureException("u") is NotSupportedException, "Unsupported is NotSupportedException.");
        Require(new DocumentWriteException("w") is IOException, "Write is IOException.");

        foreach (Exception error in new Exception[]
                 {
                     new DocumentValidationException("v"),
                     new UnsupportedFeatureException("u"),
                     new FontException("f"),
                     new ImageException("i"),
                     new DocumentWriteException("w"),
                 })
        {
            Require(error is IRegletError, $"{error.GetType().Name} is marked as a Reglet error.");
        }
    }

    private static void SnapshotsWriteWarnings()
    {
        var warnings = new List<WriteWarning> { new("First warning") };
        var result = new WriteResult(warnings);
        warnings.Add(new WriteWarning("Second warning"));
        Require(result.Warnings.Count == 1, "WriteResult takes a snapshot.");
        Require(result.Warnings is not IList<WriteWarning> exposed || exposed.IsReadOnly,
            "WriteResult does not hand out a mutable list.");
    }

    private static void UsesIdenticalDefaultMargins()
    {
        var section = new Section(new Paragraph(new TextRun("x")));
        Require(section.Margins == PageMargins.Normal, "Normal is the default margin.");
        Require(PageMargins.Normal.Left.Points == 72, "Left margin is 1 inch.");
        Require(PageMargins.Normal.Right.Points == 72, "Right margin is 1 inch.");
        Require(PageMargins.Normal.Top.Points == 72, "Top margin is 1 inch.");
        Require(PageMargins.Normal.Bottom.Points == 72, "Bottom margin is 1 inch.");
    }

    private static void UsesA4ByDefault()
    {
        var section = new Section(new Paragraph(new TextRun("x")));
        Require(section.PageSize == PageSize.A4, "A4 is the default size.");
        Require(Math.Abs(PageSize.A4.Width.Points - 210 * 72 / 25.4) < 1e-9, "A4-Breite (210 mm).");
        Require(Math.Abs(PageSize.A4.Height.Points - 297 * 72 / 25.4) < 1e-9, "A4-Hoehe (297 mm).");
        Require(PageSize.Letter != PageSize.A4, "Letter bleibt explizit waehlbar.");
    }

    private static void UsesCanonicalPoints()
    {
        Require(Math.Abs(Length.FromInches(1).Points - 72) < 1e-9, "1 inch is 72 points.");
        Require(Math.Abs(Length.FromMillimeters(25.4).Points - 72) < 1e-9, "25.4 mm is 72 points.");
        Require(Math.Abs(Length.FromCentimeters(2.54).Points - 72) < 1e-9, "2.54 cm is 72 points.");
        Require(
            Math.Abs(Length.FromCentimeters(2.54).To(LengthUnit.Centimeters) - 2.54) < 1e-9,
            "The back-conversion is low-loss.");
        Require(
            new Length(72).To(LengthUnit.Inches) == 1,
            "The canonical unit is points.");
    }

    private static void ConvertsLengthsAndColors()
    {
        var inch = Length.FromInches(1);
        Require(Math.Abs(inch.To(LengthUnit.Millimeters) - 25.4) < 1e-10, "Zoll-Umrechnung.");
        Require(Color.FromHex("#1A2B3C") == new Color(0x1A, 0x2B, 0x3C), "Hex-Farbe.");
        Throws<ArgumentException>(() => Color.FromHex("#12345G"));
        Throws<ArgumentException>(() => Color.FromHex(" 12345"));
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
