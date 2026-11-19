namespace Reglet;

using System.IO.Compression;

/// <summary>
/// writes a <see cref="PortableDocument"/> as ODT directly according to ODF 1.3 (ZIP package with
/// ODF XML). Uses only the Base Class Library.
/// </summary>
public sealed class OdtDocumentWriter : IDocumentWriter
{
    /// <summary>Writes the document as ODT to the stream.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="output">Target stream; it remains open.</param>
    /// <param name="options">Optionale Writer-Optionen.</param>
    /// <param name="cancellationToken">Abbruchsignal.</param>
    /// <remarks>
    /// a seekable stream is written synchronously; the returned
    /// <see cref="Task{WriteResult}"/> is already completed. A non-seekable stream
    /// (e.g. an HTTP response body) is built in memory and transferred asynchronously.
    /// </remarks>
    public Task<WriteResult> WriteAsync(
        PortableDocument document,
        Stream output,
        OdtWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        document = AlignmentTabs.Expand(document);

        var effective = options ?? OdtWriteOptions.Default;
        var warnings = new List<WriteWarning>();
        var handler = new UnsupportedFeatureHandler(effective.UnsupportedContent, warnings);
        return WriterOutput.Write(
            output,
            stream =>
            {
                if (effective.Signature is { } signature)
                {
                    // A signature covers the finished package; build it in memory first, then add
                    // the signature part (which stays unencrypted even over an encrypted package).
                    using var package = new MemoryStream();
                    BuildPackage(package);
                    var signed = OdtSignatureWriter.Sign(package.ToArray(), signature, cancellationToken);
                    stream.Write(signed);
                }
                else
                {
                    BuildPackage(stream);
                }

                return null;
            },
            warnings,
            effective,
            cancellationToken);

        void BuildPackage(Stream target)
        {
            if (effective.Template is { } template)
            {
                if (effective.Encryption is not null)
                {
                    throw new UnsupportedFeatureException(
                        "ODT templates and encryption cannot be combined.");
                }

                OdtTemplateWriter.Build(document, template, target, effective, handler, cancellationToken);
            }
            else if (document.SourcePackage is { IsEmpty: false } source
                && IsOdtPackage(source)
                && SourcePreservation.IsPristine(document)
                && effective.Encryption is null
                && !effective.AutoHyphenation)
            {
                // AF11 (open+save): the document is the unmodified instance the reader returned,
                // so the original package is the most faithful output — byte-identical, all
                // modelled and unmodelled content preserved. A source of another format
                // (DOCX→ODT) is ignored.
                target.Write(source.Span);
            }
            else if (document.SourcePackage is { IsEmpty: false } preserved
                && IsOdtPackage(preserved)
                && SourcePreservation.IsBodyUnchanged(document)
                && effective.Encryption is null
                && !effective.AutoHyphenation)
            {
                // AF12 (body unchanged): only the metadata changed. Adopt the original package
                // and patch meta.xml, so content.xml, styles and unmodelled parts survive.
                OdtPreservedPackageWriter.Build(document, preserved, target, effective, cancellationToken);
            }
            else
            {
                if (document.SourcePackage is { IsEmpty: false } && document.SourceHasUnmodelledContent)
                {
                    var details = SourcePreservation.UnmodelledContentDetails(document);
                    handler.Report(
                        "ODT: the source document contains content the reader does not model"
                        + (details is null
                            ? "."
                            : ": " + details)
                        + " It is not preserved when the document is changed; write the "
                        + "unmodified document to keep it.");
                }

                using var diagrams = OdtDiagrams.Use(document);
                OdtPackageBuilder.Build(document, target, effective, handler, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    Task<WriteResult> IDocumentWriter.WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken)
        => WriteAsync(document, output, null, cancellationToken);

    private static bool IsOdtPackage(ReadOnlyMemory<byte> source)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(source.ToArray(), writable: false), ZipArchiveMode.Read);
            return archive.GetEntry("content.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
