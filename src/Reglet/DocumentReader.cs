namespace Reglet;

/// <summary>
/// entry point of the library: reads a DOCX, ODT or PDF file into a
/// immutable <see cref="PortableDocument"/> content model. The format is recognized for a
/// path recognized from the extension, or specified explicitly for a stream. Layout is not
/// preserved (reader subset, see feature matrix).
/// </summary>
public static class DocumentReader
{
    /// <summary>Reads a file into the content model based on the file extension.</summary>
    public static ReadResult Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ReadFromPath(path, cancellationToken);
    }

    /// <summary>Reads a file in the specified format into the content model.</summary>
    public static ReadResult Read(Stream input, DocumentFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatch(input, format, cancellationToken);
    }

    /// <summary>
    /// Reads a stream in the specified format, opening an encrypted DOCX (OF-E01), ODT (OF-E05)
    /// or PDF (AD02) with <paramref name="password"/>. An unencrypted document ignores the
    /// password.
    /// </summary>
    /// <exception cref="DocumentValidationException">The password is wrong or the file is damaged.</exception>
    public static ReadResult Read(
        Stream input,
        DocumentFormat format,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        return format switch
        {
            DocumentFormat.Docx => new DocxDocumentReader().Read(input, new DocxReadOptions { Password = password }, cancellationToken),
            DocumentFormat.Odt => new OdtDocumentReader().Read(input, new OdtReadOptions { Password = password }, cancellationToken),
            DocumentFormat.Pdf => new PdfDocumentReader().Read(input, new PdfReadOptions { Password = password }, cancellationToken),
            _ => Dispatch(input, format, cancellationToken),
        };
    }

    /// <summary>Determines the source format from a file extension (normalized).</summary>
    /// <exception cref="UnsupportedFeatureException">The extension is missing or not allowed.</exception>
    public static DocumentFormat FormatFromPath(string path) => DocumentFormatExtensions.FromPath(path);

    /// <summary>
    /// Extracts the text of each page of a PDF (phase AC, AC05). Layout is not reconstructed.
    /// </summary>
    /// <returns>The text of the pages in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static IReadOnlyList<string> ReadPdfPages(Stream input, CancellationToken cancellationToken = default)
        => new PdfDocumentReader().ReadPageTexts(input, cancellationToken);

    /// <summary>
    /// Extracts the text of each page of a PDF and opens an encrypted file with
    /// <paramref name="password"/> (phase AD).
    /// </summary>
    public static IReadOnlyList<string> ReadPdfPages(
        Stream input,
        string? password,
        CancellationToken cancellationToken = default)
        => new PdfDocumentReader().ReadPageTexts(input, new PdfReadOptions { Password = password }, cancellationToken);

    private static ReadResult ReadFromPath(string path, CancellationToken cancellationToken)
    {
        var format = FormatFromPath(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Dispatch(stream, format, cancellationToken);
    }

    private static ReadResult Dispatch(Stream input, DocumentFormat format, CancellationToken cancellationToken)
        => format switch
        {
            DocumentFormat.Docx => new DocxDocumentReader().Read(input, cancellationToken),
            DocumentFormat.Odt => new OdtDocumentReader().Read(input, cancellationToken),
            DocumentFormat.Pdf => new PdfDocumentReader().Read(input, cancellationToken),
            DocumentFormat.Html => new HtmlDocumentReader().Read(input, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown input format."),
        };
}
