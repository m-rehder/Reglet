using System.Text;
using Reglet;

/// <summary>
/// OF-F01: non-link annotations. Sticky notes, highlights, stamps and free text boxes are written
/// into the page dictionary and read back; an annotation on a page that does not exist is reported.
/// </summary>
internal static class PdfAnnotationChecks
{
    internal static int Run()
    {
        try
        {
            WritesAllFourKinds();
            ReadsTheAnnotationsBack();
            PlacesAnnotationsPerPage();
            ReportsAMissingPage();
            RejectsInvalidAreas();
            Console.WriteLine("PDF annotations (OF-F01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF annotations (OF-F01): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(string text = "Inhalt")
        => PortableDocument.Create().Paragraph(text).Build();

    private static void WritesAllFourKinds()
    {
        var pdf = Write(Document(), [
            new PdfAnnotation { Kind = PdfAnnotationKind.Text, Page = 1, X = 10, Y = 700, Content = "Notiz", Author = "Maik", Icon = "Note" },
            new PdfAnnotation { Kind = PdfAnnotationKind.Highlight, Page = 1, X = 20, Y = 680, Width = 100, Height = 12, Content = "wichtig", Color = Color.FromRgb(0xFF, 0xFF, 0x00) },
            new PdfAnnotation { Kind = PdfAnnotationKind.Stamp, Page = 1, X = 200, Y = 600, Width = 80, Height = 30, Icon = "Approved" },
            new PdfAnnotation { Kind = PdfAnnotationKind.FreeText, Page = 1, X = 300, Y = 500, Width = 120, Height = 20, Content = "Freitext" },
        ]);

        var latin = Encoding.Latin1.GetString(pdf);
        AssertContains(latin, "/Subtype /Text");
        AssertContains(latin, "/Subtype /Highlight");
        AssertContains(latin, "/Subtype /Stamp");
        AssertContains(latin, "/Subtype /FreeText");
        AssertContains(latin, "/Annots [");
        AssertContains(latin, "/Name /Approved");
        AssertContains(latin, "/F 4");
        AssertContains(latin, "/C [");
    }

    private static void ReadsTheAnnotationsBack()
    {
        var pdf = Write(Document(), [
            new PdfAnnotation
            {
                Kind = PdfAnnotationKind.Text,
                Page = 1,
                X = 10,
                Y = 700,
                Content = "Prüfnotiz",
                Author = "Maik",
                Subject = "Review",
                Icon = "Comment",
                Color = Color.FromRgb(0xFF, 0x00, 0x00),
            },
            new PdfAnnotation { Kind = PdfAnnotationKind.Highlight, Page = 1, X = 20, Y = 680, Width = 100, Height = 12, Content = "wichtig" },
            new PdfAnnotation { Kind = PdfAnnotationKind.Stamp, Page = 1, X = 200, Y = 600, Width = 80, Height = 30, Icon = "Draft" },
            new PdfAnnotation { Kind = PdfAnnotationKind.FreeText, Page = 1, X = 300, Y = 500, Width = 120, Height = 20, Content = "Freitext" },
        ]);

        var annotations = Read(pdf);
        Require(annotations.Count == 4, $"Four annotations are read: {annotations.Count}");

        var note = annotations[0];
        Require(note.Kind == PdfAnnotationKind.Text, "The first annotation is a note.");
        Require(note.Content == "Prüfnotiz", "The content survives: " + note.Content);
        Require(note.Author == "Maik", "The author survives.");
        Require(note.Subject == "Review", "The subject survives.");
        Require(note.Icon == "Comment", "The icon survives: " + note.Icon);
        Require(note.Color?.ToString() == Color.FromRgb(0xFF, 0x00, 0x00).ToString(), "The colour survives: " + note.Color);
        Require(Math.Abs(note.X - 10) < 0.01 && Math.Abs(note.Y - 700) < 0.01, "The position survives.");
        Require(Math.Abs(note.Width - 18) < 0.01, "The default size is 18 points: " + note.Width);

        Require(annotations[1].Kind == PdfAnnotationKind.Highlight, "The second annotation is a highlight.");
        Require(Math.Abs(annotations[1].Width - 100) < 0.01, "The highlight area survives.");
        Require(annotations[2].Kind == PdfAnnotationKind.Stamp, "The third annotation is a stamp.");
        Require(annotations[2].Icon == "Draft", "The stamp name survives: " + annotations[2].Icon);
        Require(annotations[3].Kind == PdfAnnotationKind.FreeText && annotations[3].Content == "Freitext", "The free text survives.");
    }

    /// <summary>Every annotation sits on the page it names.</summary>
    private static void PlacesAnnotationsPerPage()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("eins")), new PageBreak(), new Paragraph(new TextRun("zwei")) }));

        var pdf = Write(document, [
            new PdfAnnotation { Page = 1, Content = "auf Seite eins" },
            new PdfAnnotation { Page = 2, Content = "auf Seite zwei" },
        ]);

        var annotations = Read(pdf);
        Require(annotations.Count == 2, "Two annotations are read.");
        Require(annotations[0].Page == 1 && annotations[0].Content == "auf Seite eins", "The first sits on page 1.");
        Require(annotations[1].Page == 2 && annotations[1].Content == "auf Seite zwei", "The second sits on page 2.");
    }

    private static void ReportsAMissingPage()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter()
            .WriteAsync(Document(), buffer, new PdfWriteOptions
            {
                Annotations = [new PdfAnnotation { Page = 5, Content = "zu weit" }],
            })
            .GetAwaiter().GetResult();

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("annotation on page 5 is skipped", StringComparison.Ordinal)),
            "The annotation without a page is reported: " + string.Join(" | ", result.Warnings.Select(warning => warning.Message)));
        Require(Read(buffer.ToArray()).Count == 0, "No annotation object is written.");
    }

    private static void RejectsInvalidAreas()
    {
        Throws<ArgumentOutOfRangeException>(() => _ = new PdfAnnotation { Width = 0 });
        Throws<ArgumentOutOfRangeException>(() => _ = new PdfAnnotation { Height = -1 });
        Throws<ArgumentOutOfRangeException>(() => _ = new PdfAnnotation { Width = double.NaN });
    }

    private static byte[] Write(PortableDocument document, IReadOnlyList<PdfAnnotation> annotations)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { Annotations = annotations })
            .GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static IReadOnlyList<PdfAnnotation> Read(byte[] pdf)
    {
        using var buffer = new MemoryStream(pdf);
        return new PdfDocumentReader().ReadAnnotations(buffer);
    }

    private static void AssertContains(string text, string expected)
        => Require(text.Contains(expected, StringComparison.Ordinal), $"Expected: {expected}");

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

        throw new InvalidOperationException($"Erwartete {typeof(TException).Name} blieb aus.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
