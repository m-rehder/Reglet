using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// writes the signature field and the signature dictionary of a PDF and signs the document
/// (phase BJ, BJ02). The signature value is a detached CMS SignedData over the file bytes
/// without the signature value itself (the <c>/ByteRange</c>).
///
/// <para>
/// The placeholders (the byte range and the signature value) have a fixed width, so the
/// values can be written into the finished file without changing its length.
/// </para>
/// </summary>
internal sealed class PdfSignatureWriter
{
    /// <summary>The number of digits of the placeholders of the byte range.</summary>
    private const int ByteRangeDigits = 12;

    private readonly PdfFileWriter _file;
    private readonly Stream _stream;
    private readonly PdfSignatureOptions _options;
    private readonly int _fieldObject;
    private readonly int _signatureObject;
    private readonly int _dssObject;
    private readonly int _certificateObject;
    private long _byteRangeStart;
    private long _contentsStart;
    private long _contentsEnd;

    /// <summary>Creates the writer and reserves the objects.</summary>
    public PdfSignatureWriter(PdfFileWriter file, Stream stream, PdfSignatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (options.LongTermValidation is not null && !options.Pades)
        {
            throw new ArgumentException(
                "The validation material of a long-term signature requires the PAdES baseline (Pades = true).",
                nameof(options));
        }

        _file = file;
        _stream = stream;
        _options = options;
        _fieldObject = file.ReserveObject();
        _signatureObject = file.ReserveObject();
        _dssObject = file.ReserveObject();
        _certificateObject = file.ReserveObject();
    }

    /// <summary>The object number of the signature field (the catalog refers to it).</summary>
    public int FieldObject => _fieldObject;

    /// <summary>
    /// The object number of the document security store (the catalog refers to it in the
    /// PAdES baseline); -1 when the signature does not use it.
    /// </summary>
    public int DssObject => _options.Pades ? _dssObject : -1;

    /// <summary>
    /// writes the field and the signature dictionary with the placeholders. The dictionary
    /// must be written before the cross-reference table (the objects belong to it).
    /// </summary>
    /// <param name="pageObject">The object number of the first page (the field belongs to it).</param>
    public void WriteObjects(int? pageObject)
    {
        _file.WriteObject(_fieldObject, tokens => tokens.WriteDictionary(field =>
        {
            field.WriteName("Type");
            field.WriteName("Annot");
            field.WriteName("Subtype");
            field.WriteName("Widget");
            field.WriteName("FT");
            field.WriteName("Sig");
            field.WriteName("T");
            tokens.WriteLiteralString("Signature1");
            field.WriteName("V");
            field.WriteReference(_signatureObject);
            field.WriteName("Rect");
            field.WriteArray(rect =>
            {
                rect.WriteInteger(0);
                rect.WriteInteger(0);
                rect.WriteInteger(0);
                rect.WriteInteger(0);
            });
            field.WriteName("F");
            field.WriteInteger(4);
            if (pageObject is { } page)
            {
                field.WriteName("P");
                field.WriteReference(page);
            }
        }));

        _file.WriteObject(_signatureObject, tokens => tokens.WriteDictionary(signature =>
        {
            signature.WriteName("Type");
            signature.WriteName("Sig");
            signature.WriteName("Filter");
            signature.WriteName("Adobe.PPKLite");
            signature.WriteName("SubFilter");
            tokens.WriteName(_options.Pades ? "ETSI.CAdES.detached" : "adbe.pkcs7.detached");
            signature.WriteName("ByteRange");

            // The placeholders of the byte range: "[0 <a> <b> <c>]" with a fixed width.
            _byteRangeStart = _file.Position;
            tokens.WriteRaw("[0 ");
            tokens.WriteRaw(new string('0', ByteRangeDigits));
            tokens.WriteRaw(" ");
            tokens.WriteRaw(new string('0', ByteRangeDigits));
            tokens.WriteRaw(" ");
            tokens.WriteRaw(new string('0', ByteRangeDigits));
            tokens.WriteRaw("]");

            signature.WriteName("Contents");

            // The gap of the byte range is the whole hex string including "<" and ">"
            // (ISO 32000-2, 12.8.1); validators reject a gap without the delimiters.
            _contentsStart = _file.Position;
            tokens.WriteRaw("<");
            tokens.WriteRaw(new string('0', _options.ReservedSize * 2));
            tokens.WriteRaw(">");
            _contentsEnd = _file.Position;

            if (_options.Name is { Length: > 0 } name)
            {
                signature.WriteName("Name");
                tokens.WriteLiteralString(name);
            }

            if (_options.Reason is { Length: > 0 } reason)
            {
                signature.WriteName("Reason");
                tokens.WriteLiteralString(reason);
            }

            if (_options.Location is { Length: > 0 } location)
            {
                signature.WriteName("Location");
                tokens.WriteLiteralString(location);
            }

            signature.WriteName("M");
            tokens.WriteLiteralString(PdfDate(DateTimeOffset.Now));

            // The signature build properties name the creating software; Acrobat otherwise shows
            // "created with: not available".
            signature.WriteName("Prop_Build");
            signature.WriteDictionary(build =>
            {
                build.WriteName("App");
                build.WriteDictionary(app =>
                {
                    app.WriteName("Name");
                    app.WriteName("Reglet");
                });
            });
        }));

        if (_options.Pades)
        {
            // The document security store (ETSI EN 319 142-1, 5.4): the certificate of the
            // signer and the chain as streams with the DER encoding.
            _file.WriteStreamObject(_certificateObject, _options.Certificate.RawData);
            var chain = _options.AdditionalCertificates.Select(certificate =>
            {
                var number = _file.ReserveObject();
                _file.WriteStreamObject(number, certificate.RawData);
                return number;
            }).ToList();

            _file.WriteObject(_dssObject, tokens => tokens.WriteDictionary(dss =>
            {
                dss.WriteName("Type");
                dss.WriteName("DSS");
                dss.WriteName("Certs");
                dss.WriteArray(certs =>
                {
                    certs.WriteReference(_certificateObject);
                    foreach (var number in chain)
                    {
                        certs.WriteReference(number);
                    }
                });
            }));
        }
    }

    /// <summary>
    /// signs the document and writes the byte range and the signature value into the finished
    /// file. The stream must be readable and seekable.
    /// </summary>
    public void Sign()
    {
        var length = _file.Position;
        var start = _file.Start;

        // The byte range: the pairs (0, contents start) and (contents end, file end).
        // It is written first, because it belongs to the signed data: the values must be
        // final before the digest is calculated.
        var range = string.Create(
            CultureInfo.InvariantCulture,
            $"[0 {_contentsStart.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)} "
            + $"{_contentsEnd.ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)} "
            + $"{(length - _contentsEnd).ToString("D" + ByteRangeDigits, CultureInfo.InvariantCulture)}]");

        _stream.Position = start + _byteRangeStart;
        var rangeBytes = Encoding.ASCII.GetBytes(range);
        _stream.Write(rangeBytes, 0, rangeBytes.Length);

        // The signed data: the file without the signature value.
        var data = new byte[length];
        _stream.Position = start;
        _stream.ReadExactly(data);

        var signed = new byte[_contentsStart + (length - _contentsEnd)];
        Array.Copy(data, 0, signed, 0, _contentsStart);
        Array.Copy(data, (int)_contentsEnd, signed, (int)_contentsStart, (int)(length - _contentsEnd));

        var signature = PdfCms.SignDetached(
            signed, _options.Certificate, DateTimeOffset.Now, _options.Pades, _options.AdditionalCertificates, _options.Timestamp);
        if (signature.Length > _options.ReservedSize)
        {
            throw new InvalidOperationException(
                $"The signature needs {signature.Length} bytes, but only {_options.ReservedSize} are reserved.");
        }

        var hex = new StringBuilder(signature.Length * 2);
        foreach (var value in signature)
        {
            hex.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        var padded = hex.ToString().PadRight(_options.ReservedSize * 2, '0');
        var hexBytes = Encoding.ASCII.GetBytes(padded);
        _stream.Position = start + _contentsStart + 1; // after "<"
        _stream.Write(hexBytes, 0, hexBytes.Length);
        _stream.Flush();

        // OF-E03 (B-LT): the validation material goes into an incremental update, so the signed
        // revision stays untouched and the signature keeps its value.
        if (_options.LongTermValidation is { IsEmpty: false } validation)
        {
            _stream.Position = start;
            var signedFile = new byte[length];
            _stream.ReadExactly(signedFile);

            var extended = PdfLongTermValidation.Append(
                PdfFile.Load(signedFile), signedFile, _options.Pades ? _dssObject : null, signature, validation);
            if (extended.Length > length)
            {
                _stream.Position = start;
                _stream.Write(extended, 0, extended.Length);
                _stream.Flush();
            }
        }
    }

    /// <summary>The PDF date format (ISO 32000-1, 7.9.4).</summary>
    private static string PdfDate(DateTimeOffset value)
    {
        var builder = new StringBuilder("D:");
        builder.Append(value.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        builder.Append('Z');
        return builder.ToString();
    }
}
