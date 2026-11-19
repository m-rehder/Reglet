namespace Reglet;

/// <summary>
/// rewrites a PDF while preserving the whole reachable object graph (phase AF, AF15) and optionally
/// replaces the document metadata (AF16). Unlike <see cref="PdfPageOperations"/> the catalog is
/// copied as-is, so outlines, name trees, metadata and forms survive; the output is normalised
/// (classic xref, filters kept). An encrypted source is decrypted with the password from the
/// options and the output is **not** re-encrypted.
/// </summary>
public static class PdfRewriter
{
    /// <summary>Reads <paramref name="source"/> and writes an equivalent, normalised PDF.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid or the password is wrong or missing.</exception>
    public static void Rewrite(Stream source, Stream destination, PdfReadOptions? options = null)
        => RewriteCore(source, destination, metadata: null, options);

    /// <summary>
    /// Reads <paramref name="source"/> and writes a normalised PDF with the Info dictionary and the
    /// catalog's XMP metadata replaced by <paramref name="metadata"/>; everything else is preserved.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="destination"/> or <paramref name="metadata"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid or the password is wrong or missing.</exception>
    public static void Rewrite(Stream source, Stream destination, DocumentMetadata metadata, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        RewriteCore(source, destination, metadata, options);
    }

    private static void RewriteCore(Stream source, Stream destination, DocumentMetadata? metadata, PdfReadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ReaderGuard.Run("PDF", () =>
        {
            RewriteFile(source, destination, metadata, options);
            return 0;
        });
    }

    private static void RewriteFile(Stream source, Stream destination, DocumentMetadata? metadata, PdfReadOptions? options)
    {
        var effective = options ?? PdfReadOptions.Default;

        var data = ReaderGuard.ReadAllBounded(source, "PDF");
        var file = PdfFile.Load(data, effective.Password);

        if (file.Trailer.Get("Root") is not PdfReference root)
        {
            throw new DocumentValidationException("PDF catalog is missing.");
        }

        var catalog = file.Dictionary(root);
        if (catalog?.Get("Pages") is null)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var xmpOldNumber = (catalog.Get("Metadata") as PdfReference)?.ObjectNumber;
        var infoOldNumber = (file.Trailer.Get("Info") as PdfReference)?.ObjectNumber;

        // Buffered so that a failure leaves nothing in the caller's stream.
        using var buffer = new MemoryStream();
        var writer = new PdfFileWriter(buffer);
        var map = new Dictionary<int, int>();
        var queue = new List<(int NewNumber, int OldNumber)>();
        const int NewObject = int.MinValue;

        int CopyReference(int oldNumber)
        {
            if (map.TryGetValue(oldNumber, out var existing))
            {
                return existing;
            }

            var newNumber = writer.ReserveObject();
            map[oldNumber] = newNumber;
            queue.Add((newNumber, oldNumber));
            return newNumber;
        }

        PdfValue Remap(PdfValue value)
        {
            ReaderGuard.EnsureStack();
            return RemapCore(value);
        }

        PdfValue RemapCore(PdfValue value) => value switch
        {
            PdfReference reference => new PdfReference(CopyReference(reference.ObjectNumber)),
            PdfArray array => new PdfArray(array.Items.Select(Remap).ToList()),
            PdfDictionary dictionary => new PdfDictionary(dictionary.Entries.ToDictionary(
                pair => pair.Key, pair => Remap(pair.Value), StringComparer.Ordinal)),
            PdfStreamValue stream => new PdfStreamValue((PdfDictionary)Remap(stream.Dictionary), stream.Raw),
            _ => value,
        };

        var newRoot = CopyReference(root.ObjectNumber);

        int? newInfo;
        if (metadata is not null)
        {
            if (infoOldNumber is { } oldInfo)
            {
                newInfo = CopyReference(oldInfo);
            }
            else
            {
                newInfo = writer.ReserveObject();
                queue.Add((newInfo.Value, NewObject));
            }
        }
        else
        {
            newInfo = infoOldNumber is { } oldInfo ? CopyReference(oldInfo) : null;
        }

        for (var index = 0; index < queue.Count; index++)
        {
            var (newNumber, oldNumber) = queue[index];

            if (metadata is not null && (oldNumber == NewObject || oldNumber == infoOldNumber))
            {
                writer.WriteObject(newNumber, tokens => PdfDocumentBuilder.WriteInfo(tokens, metadata));
                continue;
            }

            if (metadata is not null && oldNumber == xmpOldNumber)
            {
                writer.WriteStreamObject(
                    newNumber,
                    PdfDocumentBuilder.BuildXmp(metadata, PdfAIdentity.Conformance(file)),
                    "/Type /Metadata /Subtype /XML");
                continue;
            }

            var remapped = Remap(file.GetObject(oldNumber));
            if (remapped is PdfStreamValue stream)
            {
                writer.WriteStreamObject(newNumber, stream.Raw, PdfPageOperations.SerializeDictionary(stream.Dictionary));
            }
            else
            {
                writer.WriteObject(newNumber, tokens => PdfPageOperations.WriteValue(tokens, remapped));
            }
        }

        var (idOriginal, idChanging) = PdfAIdentity.FileId(file);
        writer.Complete(newRoot, newInfo, idOriginal, idChanging);
        buffer.Position = 0;
        buffer.CopyTo(destination);
    }
}
