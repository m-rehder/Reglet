namespace Reglet;

/// <summary>A font a PDF page refers to.</summary>
/// <param name="BaseName">The PostScript name without the subset prefix (<c>ABCDEF+</c>).</param>
/// <param name="Subtype">The font type: TrueType, Type0, Type1, Type3.</param>
/// <param name="IsEmbedded">Whether the font program is in the file (FontFile, FontFile2, FontFile3, or a Type3 font).</param>
/// <param name="IsSubset">Whether the name carries a subset prefix.</param>
public sealed record PdfInspectedFont(string BaseName, string Subtype, bool IsEmbedded, bool IsSubset);

/// <summary>An image XObject drawn on a page.</summary>
/// <param name="PixelWidth">Width in pixels (<c>/Width</c>).</param>
/// <param name="PixelHeight">Height in pixels (<c>/Height</c>).</param>
public sealed record PdfInspectedImage(int PixelWidth, int PixelHeight);

/// <summary>What a PDF page holds.</summary>
/// <param name="Width">Width of the media box in points.</param>
/// <param name="Height">Height of the media box in points.</param>
/// <param name="Fonts">The fonts of the page's resource dictionary. Reglet shares one resource dictionary between pages, so its pages list all fonts of the document.</param>
/// <param name="Images">The distinct image XObjects the page draws, also those inside form XObjects; one drawn twice counts once.</param>
public sealed record PdfInspectedPage(double Width, double Height, IReadOnlyList<PdfInspectedFont> Fonts, IReadOnlyList<PdfInspectedImage> Images);

/// <summary>A read-only look at the pages, fonts and images of a PDF, for tests of written files.</summary>
/// <param name="Pages">The pages in document order.</param>
public sealed record PdfInspection(IReadOnlyList<PdfInspectedPage> Pages)
{
    /// <summary>Reads a PDF.</summary>
    /// <exception cref="DocumentValidationException">The PDF is invalid or encrypted with a password.</exception>
    public static PdfInspection Read(Stream input, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReaderGuard.Run("PDF", () => PdfDocumentReader.Inspect(ReaderGuard.ReadAllBounded(input, "PDF"), options?.Password));
    }
}

public sealed partial class PdfDocumentReader
{
    internal static PdfInspection Inspect(byte[] data, string? password)
    {
        var file = PdfFile.Load(data, password);
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Pages")) is not { } pagesRoot)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<PageEntry>();
        CollectPages(file, pagesRoot, new PageEntry(pagesRoot, null, null), pages, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), depth: 0);
        var context = new ExtractionContext(file);
        return new PdfInspection(pages.Select(page =>
        {
            context.ExtractWithImages(page, out _, out var images);
            var size = ReadMediaBox(file, page.MediaBox) ?? PageSize.A4;
            return new PdfInspectedPage(
                size.Width.Points,
                size.Height.Points,
                InspectFonts(file, page.Resources),
                images.Select(image => new PdfInspectedImage(
                    (int)(file.Integer(image.Dictionary.Get("Width")) ?? 0),
                    (int)(file.Integer(image.Dictionary.Get("Height")) ?? 0))).ToList());
        }).ToList());
    }

    // shortcut: fonts used only inside form XObjects are not listed; add them when a caller needs them.
    private static List<PdfInspectedFont> InspectFonts(PdfFile file, PdfDictionary? resources)
    {
        var fonts = new List<PdfInspectedFont>();
        if (resources is null || file.Dictionary(resources.Get("Font")) is not { } fontDictionary)
        {
            return fonts;
        }

        foreach (var (_, value) in fontDictionary.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (file.Dictionary(value) is not { } font)
            {
                continue;
            }

            var subtype = file.Name(font.Get("Subtype")) ?? string.Empty;
            var name = file.Name(font.Get("BaseFont")) ?? string.Empty;
            var subset = name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper);
            var descriptorOwner = subtype == "Type0" && file.Array(font.Get("DescendantFonts")) is { Items.Count: > 0 } descendants
                ? file.Dictionary(descendants.Items[0])
                : font;
            var descriptor = file.Dictionary(descriptorOwner?.Get("FontDescriptor"));
            var embedded = subtype == "Type3"
                || descriptor?.Get("FontFile") is not null
                || descriptor?.Get("FontFile2") is not null
                || descriptor?.Get("FontFile3") is not null;
            fonts.Add(new PdfInspectedFont(subset ? name[7..] : name, subtype, embedded, subset));
        }

        return fonts;
    }
}
