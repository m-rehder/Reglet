namespace Reglet;

using System.IO.Compression;

/// <summary>
/// writes a <see cref="PortableDocument"/> as DOCX directly according to ECMA-376 (OPC package
/// with WordprocessingML). Uses only the Base Class Library.
/// </summary>
public sealed class DocxDocumentWriter : IDocumentWriter
{
    /// <summary>Writes the document as DOCX to the stream.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="output">Target stream; it remains open.</param>
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
        DocxWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();

        var effective = options ?? DocxWriteOptions.Default;
        var warnings = new List<WriteWarning>();
        var handler = new UnsupportedFeatureHandler(effective.UnsupportedContent, warnings);

        // OF-E01/OF-E02: a signature and an encrypted container both need the whole package in
        // memory, so it is built there first (the signature is added before the encryption).
        if (effective.Encryption is not null || effective.Signature is not null)
        {
            return WriterOutput.Write(
                output,
                stream =>
                {
                    using var plaintext = new MemoryStream();
                    WritePackage(document, plaintext, effective, handler, cancellationToken);
                    var package = plaintext.ToArray();
                    if (effective.Signature is { } signature)
                    {
                        package = DocxSignatureWriter.Sign(package, signature, cancellationToken);
                    }

                    if (effective.Encryption is { } encryption)
                    {
                        DocxAgileEncryption.Encrypt(package, encryption, stream, cancellationToken);
                    }
                    else
                    {
                        stream.Write(package);
                    }

                    return null;
                },
                warnings,
                effective,
                cancellationToken);
        }

        return WriterOutput.Write(
            output,
            stream =>
            {
                WritePackage(document, stream, effective, handler, cancellationToken);
                return null;
            },
            warnings,
            effective,
            cancellationToken);
    }

    /// <summary>Writes the unencrypted package (the body of the writer, OF-E01 splits it out).</summary>
    private static void WritePackage(
        PortableDocument document,
        Stream stream,
        DocxWriteOptions effective,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        {
            {
                // Only a DOCX source may be reused; a document read from another format (e.g. for
                // a DOCX→ODT conversion) must be regenerated.
                var source = document.SourcePackage is { IsEmpty: false } bytes && IsDocxPackage(bytes)
                    ? bytes
                    : (ReadOnlyMemory<byte>?)null;

                if (effective.Template is null
                    && source is { } own
                    && SourcePreservation.IsPristine(document)
                    && SameMacroProject(own, document)
                    && !RequiresSettingsPatch(effective))
                {
                    // AF07 (open+save): the document is the unmodified instance the reader returned,
                    // so the original package is the most faithful output — byte-identical, all
                    // modelled and unmodelled content preserved.
                    stream.Write(own.Span);
                }
                else if (effective.Template is null
                    && source is { } preserved
                    && PreservesBody(document, effective, preserved)
                    && (document.Metadata.MacroProject is null || HasPart(preserved, DocxConstants.VbaProjectPart)))
                {
                    // AF07 (body unchanged): only metadata/settings changed. Adopt the original
                    // package and patch those parts, so unmodelled body content survives.
                    DocxPreservedPackageWriter.Build(document, preserved, stream, effective, handler, cancellationToken);
                }
                else
                {
                    // AF09: the body is regenerated from the model. Content the reader could not
                    // model is then lost, which must not happen silently.
                    if (source is not null && document.SourceHasUnmodelledContent)
                    {
                        var details = SourcePreservation.UnmodelledContentDetails(document);
                        handler.Report(
                            "DOCX: the source document contains content the reader does not model"
                            + (details is null
                                ? " (for example comments, text boxes, tracked changes or embedded objects)."
                                : ": " + details)
                            + " It is not preserved when the document is changed; write the "
                            + "unmodified document to keep it.");
                    }

                    if (effective.Template is { } template)
                    {
                        DocxTemplateWriter.Build(document, template, stream, effective, handler, cancellationToken);
                    }
                    else
                    {
                        DocxPackageBuilder.Build(document, stream, effective, handler, cancellationToken);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    Task<WriteResult> IDocumentWriter.WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken)
        => WriteAsync(document, output, null, cancellationToken);

    /// <summary>
    /// Whether only parts this writer patches changed (see
    /// <see cref="SourcePreservation.IsBodyUnchanged"/>). Then the original package can be adopted
    /// for the body. Settings options need a settings part to patch; without one the package is
    /// rebuilt so the option is not lost silently.
    /// </summary>
    private static bool PreservesBody(
        PortableDocument document,
        DocxWriteOptions options,
        ReadOnlyMemory<byte> source)
        => SourcePreservation.IsBodyUnchanged(document)
            && (!RequiresSettingsPatch(options) || HasPart(source, DocxConstants.SettingsPart));

    /// <summary>Whether the model carries the same VBA project as the source (both none counts), ADR-0028.</summary>
    private static bool SameMacroProject(ReadOnlyMemory<byte> source, PortableDocument document)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(source.ToArray(), writable: false), ZipArchiveMode.Read);
            var entry = archive.GetEntry(DocxConstants.VbaProjectPart);
            if (entry is null || document.Metadata.MacroProject is not { } project)
            {
                return entry is null && document.Metadata.MacroProject is null;
            }

            using var content = ReaderGuard.OpenBounded(entry);
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).SequenceEqual(project.Data.Span);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool RequiresSettingsPatch(DocxWriteOptions options)
        => options.Protection != DocxProtection.None || options.AutoHyphenation;

    private static bool IsDocxPackage(ReadOnlyMemory<byte> source)
        => HasPart(source, DocxConstants.DocumentPart);

    private static bool HasPart(ReadOnlyMemory<byte> source, string partName)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(source.ToArray(), writable: false), ZipArchiveMode.Read);
            return archive.GetEntry(partName) is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
