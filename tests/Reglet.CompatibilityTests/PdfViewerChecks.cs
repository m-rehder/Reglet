using System.Text;
using Reglet;

/// <summary>
/// OF-F02: the viewer preferences, the page layout and the page mode of the PDF.
/// </summary>
internal static class PdfViewerChecks
{
    internal static int Run()
    {
        try
        {
            WritesViewerPreferenceFlags();
            WritesPageLayout();
            WritesPageMode();
            OmitsTheDefaults();
            Console.WriteLine("PDF viewer (OF-F02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF viewer (OF-F02): " + exception);
            return 1;
        }
    }

    /// <summary>OF-F02: the window flags are written into /ViewerPreferences.</summary>
    private static void WritesViewerPreferenceFlags()
    {
        var latin = Encoding.Latin1.GetString(Write(new PdfWriteOptions
        {
            ViewerPreferences = PdfViewerPreferences.HideToolbar | PdfViewerPreferences.FitWindow,
        }));

        AssertContains(latin, "/ViewerPreferences");
        AssertContains(latin, "/HideToolbar true");
        AssertContains(latin, "/FitWindow true");
        Require(!latin.Contains("/HideMenubar", StringComparison.Ordinal), "Nicht gesetzte Flags fehlen.");
        Require(!latin.Contains("/CenterWindow", StringComparison.Ordinal), "Nicht gesetzte Flags fehlen.");
        Require(!latin.Contains("/FullScreen", StringComparison.Ordinal), "Nicht gesetzte Flags fehlen.");
    }

    /// <summary>OF-F02: /PageLayout carries the requested layout.</summary>
    private static void WritesPageLayout()
    {
        var latin = Encoding.Latin1.GetString(Write(new PdfWriteOptions { PageLayout = PdfPageLayout.TwoColumnLeft }));
        AssertContains(latin, "/PageLayout /TwoColumnLeft");

        var single = Encoding.Latin1.GetString(Write(new PdfWriteOptions { PageLayout = PdfPageLayout.SinglePage }));
        AssertContains(single, "/PageLayout /SinglePage");
    }

    /// <summary>OF-F02: /PageMode carries the requested mode.</summary>
    private static void WritesPageMode()
    {
        var latin = Encoding.Latin1.GetString(Write(new PdfWriteOptions { PageMode = PdfPageMode.UseOutlines }));
        AssertContains(latin, "/PageMode /UseOutlines");

        var thumbs = Encoding.Latin1.GetString(Write(new PdfWriteOptions { PageMode = PdfPageMode.UseThumbnails }));
        AssertContains(thumbs, "/PageMode /UseThumbs");
    }

    /// <summary>OF-F02: without options only the title flag is written.</summary>
    private static void OmitsTheDefaults()
    {
        var latin = Encoding.Latin1.GetString(Write(new PdfWriteOptions()));
        AssertContains(latin, "/ViewerPreferences");
        AssertContains(latin, "/DisplayDocTitle true");
        Require(!latin.Contains("/PageLayout", StringComparison.Ordinal), "Ohne Layout fehlt /PageLayout.");
        Require(!latin.Contains("/PageMode", StringComparison.Ordinal), "Ohne Modus fehlt /PageMode.");
        Require(!latin.Contains("/HideToolbar", StringComparison.Ordinal), "Ohne Flags fehlt /HideToolbar.");
    }

    private static byte[] Write(PdfWriteOptions options)
    {
        var document = PortableDocument.Create().Paragraph("Hallo").Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void AssertContains(string text, string expected)
        => Require(text.Contains(expected, StringComparison.Ordinal), $"Expected: {expected}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
