namespace Reglet;

/// <summary>
/// entry point of the library: writes an immutable
/// <see cref="PortableDocument"/> to DOCX, ODT or PDF. For a path the format is recognized from
/// the extension, or specified explicitly for a stream.
/// </summary>
/// <remarks>
/// since the root namespace is already named <c>Reglet</c>, a class of the same name would
/// class would be resolved by the compiler as a namespace; therefore the facade is named
/// <c>DocumentWriter</c>.
/// </remarks>
public static class DocumentWriter
{
    /// <summary>Writes the document to a file based on the file extension.</summary>
    public static Task<WriteResult> WriteAsync(
        PortableDocument document,
        string path,
        CancellationToken cancellationToken = default)
        => WriteAsync(document, path, options: null, cancellationToken);

    /// <summary>Writes the document to a file based on the file extension, with options.</summary>
    public static Task<WriteResult> WriteAsync(
        PortableDocument document,
        string path,
        WriteOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        return WriteToPathAsync(document, path, options, cancellationToken);
    }

    /// <summary>Writes the document in the specified format to a stream.</summary>
    /// <remarks>
    /// the passed stream is not closed. A seekable stream is written
    /// written; a non-seekable stream (e.g. an HTTP response body) is buffered in memory
    /// built and transferred asynchronously.
    /// </remarks>
    public static Task<WriteResult> WriteAsync(
        PortableDocument document,
        Stream output,
        DocumentFormat format,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        return Dispatch(document, output, format, options, cancellationToken);
    }

    /// <summary>Determines the output format from a file extension (normalized).</summary>
    /// <exception cref="UnsupportedFeatureException">The extension is missing or not allowed.</exception>
    public static DocumentFormat FormatFromPath(string path) => DocumentFormatExtensions.FromPath(path);

    private static async Task<WriteResult> WriteToPathAsync(
        PortableDocument document,
        string path,
        WriteOptions? options,
        CancellationToken cancellationToken)
    {
        var format = FormatFromPath(path);
        ValidateOptions(format, options);
        cancellationToken.ThrowIfCancellationRequested();

        // Written into a temporary file next to the target and moved over it at the end: a
        // failed write (validation error, cancellation, full disk) keeps an existing file intact
        // and never leaves a half-written document behind.
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        FileStream stream;
        try
        {
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new DocumentWriteException($"The file '{path}' could not be opened.", exception);
        }

        WriteResult result;
        try
        {
            await using (stream)
            {
                result = await Dispatch(document, stream, format, options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        try
        {
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new DocumentWriteException($"The file '{path}' could not be written.", exception);
        }

        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original exception is more meaningful than a cleanup error.
        }
    }

    private static Task<WriteResult> Dispatch(
        PortableDocument document,
        Stream output,
        DocumentFormat format,
        WriteOptions? options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(format, options);
        return format switch
        {
            DocumentFormat.Docx => new DocxDocumentWriter().WriteAsync(document, output, (DocxWriteOptions?)options, cancellationToken),
            DocumentFormat.Odt => new OdtDocumentWriter().WriteAsync(document, output, (OdtWriteOptions?)options, cancellationToken),
            DocumentFormat.Pdf => new PdfDocumentWriter().WriteAsync(document, output, (PdfWriteOptions?)options, cancellationToken),
            DocumentFormat.Html => new HtmlDocumentWriter().WriteAsync(document, output, (HtmlWriteOptions?)options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format."),
        };
    }

    private static void ValidateOptions(DocumentFormat format, WriteOptions? options)
    {
        if (format is not (DocumentFormat.Docx or DocumentFormat.Odt or DocumentFormat.Pdf or DocumentFormat.Html))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format.");
        }

        if (options is not null && (format switch
            {
                DocumentFormat.Docx => options is not DocxWriteOptions,
                DocumentFormat.Odt => options is not OdtWriteOptions,
                DocumentFormat.Pdf => options is not PdfWriteOptions,
                DocumentFormat.Html => options is not HtmlWriteOptions,
                _ => false,
            }))
        {
            throw new DocumentValidationException($"The options do not match the format {format}.", nameof(options));
        }
    }
}
