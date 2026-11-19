using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// adds a document timestamp to a signed PDF (OF-E03, ISO 32000-2, 12.8.5): a time-stamp
/// authority signs the bytes of the file, which proves that the document existed at that time.
/// The timestamp is appended as an incremental update, so the signature over the first revision
/// stays valid.
/// </summary>
public static class PdfDocumentTimestamp
{
    /// <summary>The width of the placeholders of the byte range (12 digits, as in a signature).</summary>
    private const int ByteRangeDigits = 12;

    /// <summary>
    /// appends a document timestamp to the PDF in <paramref name="source"/> and writes the result to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid, encrypted or the authority refuses.</exception>
    public static void Add(
        Stream source,
        Stream destination,
        PdfTimestampOptions timestamp,
        PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(timestamp);
        if (!timestamp.IsConfigured)
        {
            throw new ArgumentException("The timestamp has neither a responder nor a URL.", nameof(timestamp));
        }

        ReaderGuard.Run("PDF", () =>
        {
            var data = ReaderGuard.ReadAllBounded(source, "PDF");
            destination.Write(Build(data, timestamp, options ?? PdfReadOptions.Default));
            destination.Flush();
            return 0;
        });
    }

    /// <summary>appends the timestamp to the given bytes and returns the new file.</summary>
    internal static byte[] Build(byte[] data, PdfTimestampOptions timestamp, PdfReadOptions options)
    {
        var file = PdfFile.Load(data, options.Password);
        var update = new PdfIncrementalUpdate(file, data);

        var fieldObject = update.Reserve();
        var timestampObject = update.Reserve();

        // The timestamp dictionary: the placeholders of the byte range and of the token are
        // patched after the file is complete (the range covers the cross-reference section too).
        long byteRangeOffset = 0;
        long contentsStart = 0;
        long contentsEnd = 0;
        using (var body = new MemoryStream())
        {
            var tokens = new PdfTokenWriter(body);
            tokens.WriteDictionary(dictionary =>
            {
                dictionary.WriteName("Type");
                dictionary.WriteName("DocTimeStamp");
                dictionary.WriteName("Filter");
                dictionary.WriteName("Adobe.PPKLite");
                dictionary.WriteName("SubFilter");
                dictionary.WriteName("ETSI.RFC3161");
                dictionary.WriteName("ByteRange");
                byteRangeOffset = body.Position;
                tokens.WriteRaw("[0 ");
                tokens.WriteRaw(new string('0', ByteRangeDigits));
                tokens.WriteRaw(" ");
                tokens.WriteRaw(new string('0', ByteRangeDigits));
                tokens.WriteRaw(" ");
                tokens.WriteRaw(new string('0', ByteRangeDigits));
                tokens.WriteRaw("]");

                dictionary.WriteName("Contents");
                contentsStart = body.Position + 1; // after "<"
                tokens.WriteRaw("<");
                tokens.WriteRaw(new string('0', 16384 * 2));
                tokens.WriteRaw(">");
                contentsEnd = body.Position + 1; // after ">"
            });
            update.WriteRawObject(timestampObject, body.ToArray());
        }

        // The field that carries the timestamp and the revisions of the page and the catalog.
        WriteField(update, fieldObject, timestampObject, file, data);
        WritePageAnnotation(update, fieldObject, file);
        WriteAcroFormField(update, fieldObject, file);

        var result = update.Complete();
        var objectStart = update.OffsetOf(timestampObject) + ObjectHeaderLength(timestampObject);
        // The gap is the whole contents string including "<" and ">", as in a signature.
        var start = objectStart + contentsStart - 1;
        var end = objectStart + contentsEnd - 1;

        // The ranges that the authority hashes: the file without the token.
        var range = string.Create(
            CultureInfo.InvariantCulture,
            $"[0 {start.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)} "
            + $"{end.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)} "
            + $"{(result.Length - end).ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)}]");
        var rangeBytes = Encoding.ASCII.GetBytes(range);
        Array.Copy(rangeBytes, 0, result, objectStart + byteRangeOffset, rangeBytes.Length);

        var signed = new byte[start + (result.Length - end)];
        Array.Copy(result, 0, signed, 0, start);
        Array.Copy(result, end, signed, start, result.Length - end);

        var token = PdfTimestamp.ReadToken(timestamp.Request(PdfTimestamp.BuildRequest(signed, timestamp.Policy)));
        var hex = Convert.ToHexString(token);
        if (hex.Length > 16384 * 2)
        {
            throw new InvalidOperationException(
                $"The time-stamp token needs {token.Length} bytes, but only 16384 are reserved.");
        }

        var hexBytes = Encoding.ASCII.GetBytes(hex.PadRight(16384 * 2, '0'));
        Array.Copy(hexBytes, 0, result, start + 1, hexBytes.Length);
        return result;
    }

    /// <summary>The length of "<c>&lt;n&gt; 0 obj\n</c>", the header the update writes.</summary>
    private static int ObjectHeaderLength(int number)
        => number.ToString(CultureInfo.InvariantCulture).Length + " 0 obj\n".Length;

    /// <summary>Writes the timestamp dictionary's signature field.</summary>
    private static void WriteField(
        PdfIncrementalUpdate update,
        int fieldObject,
        int timestampObject,
        PdfFile file,
        byte[] data)
    {
        var name = NextFieldName(file);
        update.WriteObject(fieldObject, tokens => tokens.WriteDictionary(field =>
        {
            field.WriteName("Type");
            field.WriteName("Annot");
            field.WriteName("Subtype");
            field.WriteName("Widget");
            field.WriteName("FT");
            field.WriteName("Sig");
            field.WriteName("T");
            tokens.WriteLiteralString(name);
            field.WriteName("V");
            field.WriteReference(timestampObject);
            field.WriteName("Rect");
            field.WriteArray(rect =>
            {
                rect.WriteInteger(0);
                rect.WriteInteger(0);
                rect.WriteInteger(0);
                rect.WriteInteger(0);
            });

            // Hidden and locked: the timestamp has no visible appearance.
            field.WriteName("F");
            field.WriteInteger(2);
            _ = data;
        }));
    }

    /// <summary>Adds the field to the annotations of the first page (a revision of that page).</summary>
    private static void WritePageAnnotation(PdfIncrementalUpdate update, int fieldObject, PdfFile file)
    {
        var (pageNumber, page) = FirstPage(file);

        update.WriteObject(pageNumber, tokens => tokens.WriteDictionary(rewritten =>
        {
            foreach (var (name, value) in page.Entries.Where(entry => entry.Key != "Annots"))
            {
                rewritten.WriteName(name);
                PdfPageOperations.WriteValue(rewritten, value);
            }

            rewritten.WriteName("Annots");
            rewritten.WriteArray(annots =>
            {
                if (file.Array(page.Get("Annots")) is { } existing)
                {
                    foreach (var item in existing.Items)
                    {
                        PdfPageOperations.WriteValue(annots, item);
                    }
                }

                annots.WriteReference(fieldObject);
            });
        }));
    }

    /// <summary>Names the field in the AcroForm of the catalog (a revision of the catalog).</summary>
    private static void WriteAcroFormField(PdfIncrementalUpdate update, int fieldObject, PdfFile file)
    {
        var root = (PdfReference)file.Trailer.Get("Root")!;
        var catalog = file.Dictionary(root) ?? throw new DocumentValidationException("PDF catalog is missing.");
        var acroForm = file.Dictionary(catalog.Get("AcroForm"));

        update.WriteObject(root.ObjectNumber, tokens => tokens.WriteDictionary(rewritten =>
        {
            foreach (var (name, value) in catalog.Entries.Where(entry => entry.Key != "AcroForm"))
            {
                rewritten.WriteName(name);
                PdfPageOperations.WriteValue(rewritten, value);
            }

            rewritten.WriteName("AcroForm");
            rewritten.WriteDictionary(form =>
            {
                if (acroForm is not null)
                {
                    foreach (var (name, value) in acroForm.Entries.Where(entry => entry.Key != "Fields"))
                    {
                        form.WriteName(name);
                        PdfPageOperations.WriteValue(form, value);
                    }
                }

                form.WriteName("Fields");
                form.WriteArray(fields =>
                {
                    if (acroForm is not null && file.Array(acroForm.Get("Fields")) is { } existing)
                    {
                        foreach (var item in existing.Items)
                        {
                            PdfPageOperations.WriteValue(fields, item);
                        }
                    }

                    fields.WriteReference(fieldObject);
                });
            });
        }));
    }

    /// <summary>The object number and dictionary of the first page.</summary>
    private static (int Number, PdfDictionary Page) FirstPage(PdfFile file)
    {
        var pages = PdfPageOperations.CollectPages(file);
        if (pages.Count == 0)
        {
            throw new DocumentValidationException("The PDF has no page to carry the timestamp field.");
        }

        var number = pages[0].Page;
        return file.Dictionary(new PdfReference(number)) is { } page
            ? (number, page)
            : throw new DocumentValidationException("The first page of the PDF is not readable.");
    }

    /// <summary>the next free name of a signature field.</summary>
    private static string NextFieldName(PdfFile file)
    {
        var catalog = file.Dictionary(file.Trailer.Get("Root"));
        var acroForm = file.Dictionary(catalog?.Get("AcroForm"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (acroForm is not null && file.Array(acroForm.Get("Fields")) is { } fields)
        {
            foreach (var item in fields.Items)
            {
                if (file.Dictionary(item) is { } field
                    && file.Resolve(field.Get("T")) is PdfString text)
                {
                    names.Add(PdfTextString.Decode(text.Bytes));
                }
            }
        }

        for (var index = 1; ; index++)
        {
            var name = "Timestamp" + index.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }
}
