namespace Reglet;

/// <summary>
/// writes a <see cref="PortableDocument"/> as PDF 1.7 (ISO 32000-1) with its own
/// syntax writer. Uses only the Base Class Library.
/// </summary>
public sealed class PdfDocumentWriter : IDocumentWriter
{
    /// <summary>Writes the document as PDF to the stream.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="output">Target stream; it remains open. The stream does not need to be seekable; a non-seekable stream is buffered internally and written asynchronously.</param>
    /// <param name="options">Optional writer options.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <remarks>
    /// a seekable stream is written synchronously; the returned
    /// <see cref="Task{WriteResult}"/> is already completed. A non-seekable stream
    /// (e.g. an HTTP response body) is built in memory and transferred asynchronously.
    /// </remarks>
    public Task<WriteResult> WriteAsync(
        PortableDocument document,
        Stream output,
        PdfWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        document = AlignmentTabs.Expand(document);

        var effective = options ?? PdfWriteOptions.Default;
        document = PdfRevisions.Apply(document, effective.Revisions, effective.Limits);
        var warnings = new List<WriteWarning>();
        var handler = new UnsupportedFeatureHandler(effective.UnsupportedContent, warnings);

        if (document.SourcePackage is { IsEmpty: false } && document.SourceHasUnmodelledContent)
        {
            var details = SourcePreservation.UnmodelledContentDetails(document);
            handler.Report(
                "PDF: the source document contains content that is not represented in the model"
                + (details is null ? "." : ": " + details)
                + " It is not written to the PDF.");
        }

        if (effective.Signature is not null)
        {
            // BJ02: the signature covers the written bytes, so the document is built in
            // memory, signed and then transferred.
            return WriteSignedAsync(document, output, effective, handler, warnings, cancellationToken);
        }

        // The PDF needs the current stream position when writing; non-seekable targets
        // are therefore built in memory (as with DOCX/ODT) and transferred asynchronously.
        return WriterOutput.Write(
            output,
            stream => PdfDocumentBuilder.Build(document, stream, effective, handler, cancellationToken),
            warnings,
            effective,
            cancellationToken);
    }

    /// <summary>
    /// Writes the document as paged HTML (ADR-0039): laid out exactly as the PDF (page breaks, line breaks, headers, footers,
    /// fields, floating objects), one element per page drawn as SVG with selectable text in reading order and the fonts of
    /// <see cref="PdfWriteOptions.FontCollection"/> embedded. Encryption and signature of <paramref name="options"/> are ignored.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="output">Target stream; it remains open.</param>
    /// <param name="options">The PDF options the layout is computed with.</param>
    /// <param name="html">Document or fragment, and the class prefix.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<WriteResult> WriteHtmlAsync(
        PortableDocument document,
        Stream output,
        PdfWriteOptions? options = null,
        PdfHtmlOptions? html = null,
        CancellationToken cancellationToken = default)
        => PdfHtmlWriter.WriteAsync(document, output, options, html ?? PdfHtmlOptions.Default, cancellationToken);

    /// <summary>
    /// builds the document in memory, signs it and transfers the result (phase BJ, BJ02).
    /// </summary>
    private static async Task<WriteResult> WriteSignedAsync(
        PortableDocument document,
        Stream output,
        PdfWriteOptions options,
        UnsupportedFeatureHandler handler,
        List<WriteWarning> warnings,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var pages = PdfDocumentBuilder.Build(document, buffer, options, handler, cancellationToken);
        var length = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return new WriteResult(warnings)
        {
            Metrics = options.CollectMetrics
                ? new WriteMetrics { Pages = pages, Bytes = length }
                : null,
        };
    }

    /// <inheritdoc />
    Task<WriteResult> IDocumentWriter.WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken)
        => WriteAsync(document, output, null, cancellationToken);
}
