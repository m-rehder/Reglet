using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>
/// the digital signature of a PDF (phase BJ, BJ02): the signature dictionary, the byte range
/// and the detached CMS signature. The test verifies the signature with the public key of the
/// certificate (a self-signed certificate is created for it).
/// </summary>
internal static class PdfSignatureChecks
{
    internal static int Run()
    {
        try
        {
            SignsDocument();
            SignsPadesDocument();
            RejectsSignatureWithEncryption();
            EmbedsCertificateChain();
            RejectsInvalidSignatureOptions();
            Console.WriteLine("PDF signature (BJ02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF signature (BJ02): " + exception);
            return 1;
        }
    }

    private static void SignsDocument()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var document = new PortableDocument(
            new DocumentMetadata { Title = "Signed" },
            new Section(new Paragraph(new TextRun("The content of the signed document."))));

        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate)
            {
                Name = "Tester",
                Reason = "Automated test",
            },
        };

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, options)
            .GetAwaiter()
            .GetResult();

        var bytes = buffer.ToArray();
        Require(IndexOf(bytes, "/ByteRange") > 0, "PDF: the signature has a byte range.");
        Require(IndexOf(bytes, "/Contents") > 0, "PDF: the signature has a contents value.");
        Require(IndexOf(bytes, "Adobe.PPKLite") > 0, "PDF: the filter of the signature is set.");
        Require(IndexOf(bytes, "/Prop_Build <</App <</Name /Reglet") > 0, "PDF: the signature names the creating software.");

        var range = ReadByteRange(bytes);
        var contents = ReadContents(bytes);

        Require(range.Length == 4, "PDF: the byte range has four values.");
        Require(range[0] == 0, "PDF: the byte range starts at the beginning of the file.");
        Require(range[3] + range[2] == bytes.Length, "PDF: the byte range ends at the end of the file.");
        Require(bytes[range[1]] == (byte)'<' && bytes[range[2] - 1] == (byte)'>',
            "PDF: the gap of the byte range is the whole hex string including < and > (ISO 32000-2, 12.8.1).");
        Require(IndexOf(bytes, "/SigFlags 3") > 0, "PDF: the AcroForm marks that signatures exist (SigFlags 3).");
        RequireXrefPointsAtObjects(bytes);
        var field = FieldReference(bytes);
        Require(field.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(Encoding.Latin1.GetString(bytes), @"/Annots \[[^\]]*\b" + field),
            "PDF: the signature widget is listed in the Annots of the first page.");

        // The signed data: the file without the signature value.
        var signed = new byte[range[1] + (bytes.Length - range[2])];
        Array.Copy(bytes, 0, signed, 0, range[1]);
        Array.Copy(bytes, range[2], signed, range[1], bytes.Length - range[2]);

        var cms = Convert.FromHexString(contents);
        VerifyCms(cms, signed, certificate);

        // The signature value is not empty and the text of the document is unchanged.
        Require(contents.Trim('0').Length > 0, "PDF: the signature value is written.");
        // The text is extracted per show-text operation (the kerning splits the words).
        var text = string.Join(' ', PdfStreams.ExtractText(bytes).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Require(
            text.Contains("The content of the signed document.", StringComparison.Ordinal),
            "PDF: the content of the document is unchanged.");
    }

    private static void SignsPadesDocument()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet PAdES", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var document = new PortableDocument(
            new DocumentMetadata { Title = "Signed" },
            new Section(new Paragraph(new TextRun("PAdES content."))));

        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate) { Pades = true },
        };

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, options)
            .GetAwaiter()
            .GetResult();

        var bytes = buffer.ToArray();
        var text = Encoding.Latin1.GetString(bytes);
        Require(IndexOf(bytes, "ETSI.CAdES.detached") > 0, "PDF: the subfilter of the PAdES signature is set.");
        Require(IndexOf(bytes, "/DSS") > 0, "PDF: the catalog has a document security store.");
        Require(IndexOf(bytes, "/DSS") < IndexOf(bytes, "/Type /DSS"), "PDF: the document security store is a dictionary.");

        var range = ReadByteRange(bytes);
        var contents = ReadContents(bytes);
        var signed = new byte[range[1] + (bytes.Length - range[2])];
        Array.Copy(bytes, 0, signed, 0, range[1]);
        Array.Copy(bytes, range[2], signed, range[1], bytes.Length - range[2]);

        var cms = Convert.FromHexString(contents);
        VerifyCms(cms, signed, certificate);

        // The signed attributes name the certificate of the signer (RFC 5035).
        var attributes = ReadSignedAttributes(cms);
        Require(
            attributes.ContainsKey("1.2.840.113549.1.9.16.2.47"),
            "PDF: the signing certificate attribute is present.");
        Require(
            !attributes.ContainsKey("1.2.840.113549.1.9.5"),
            "PDF: a PAdES signature has no signing-time attribute (ETSI EN 319 142-1).");
    }

    /// <summary>The certificate chain is embedded in the CMS and, for PAdES, in the DSS.</summary>
    private static void EmbedsCertificateChain()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Reglet Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=Reglet Test Signer", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issued = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3, 4]);
        using var leaf = issued.CopyWithPrivateKey(leafKey);

        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(leaf) { Pades = true, AdditionalCertificates = [ca] },
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(SignedDocument(), buffer, options).GetAwaiter().GetResult();

        var bytes = buffer.ToArray();
        var cms = Convert.FromHexString(ReadContents(bytes));
        Require(cms.AsSpan().IndexOf(ca.RawData) >= 0, "PDF: the CMS carries the CA certificate of the chain.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(Encoding.Latin1.GetString(bytes), @"/Certs \[\d+ 0 R \d+ 0 R ?\]"),
            "PDF: the DSS lists the signer and the CA certificate.");
    }

    /// <summary>Invalid signature options fail before the document is laid out.</summary>
    private static void RejectsInvalidSignatureOptions()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Throws<ArgumentOutOfRangeException>(() => _ = new PdfSignatureOptions(certificate) { ReservedSize = 0 });
        Throws<ArgumentOutOfRangeException>(() => _ = new PdfSignatureOptions(certificate) { ReservedSize = int.MaxValue });

        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        var options = new PdfWriteOptions { Signature = new PdfSignatureOptions(publicOnly) };
        Throws<ArgumentException>(() => new PdfDocumentWriter().WriteAsync(SignedDocument(), new MemoryStream(), options).GetAwaiter().GetResult());
    }

    private static PortableDocument SignedDocument()
        => new(new DocumentMetadata { Title = "Signed" }, new Section(new Paragraph(new TextRun("Signed content."))));

    /// <summary>The "n 0 R" reference of the signature field (the value of /Fields).</summary>
    private static string FieldReference(byte[] bytes)
        => System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(bytes), @"/Fields \[(\d+ 0 R) ?\]").Groups[1].Value;

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    /// <summary>The values of the signed attributes (object identifier → value).</summary>
    private static Dictionary<string, byte[]> ReadSignedAttributes(byte[] cms)
    {
        var index = 0;
        var contentInfo = ReadTlv(cms, ref index);
        var innerIndex = 0;
        ReadTlv(contentInfo.Value, ref innerIndex);
        var signedDataHolder = ReadTlv(contentInfo.Value, ref innerIndex);

        var dataIndex = 0;
        var data = ReadTlv(signedDataHolder.Value, ref dataIndex);
        var position = 0;
        ReadTlv(data.Value, ref position);
        ReadTlv(data.Value, ref position);
        ReadTlv(data.Value, ref position);
        ReadTlv(data.Value, ref position);
        var signerInfos = ReadTlv(data.Value, ref position);
        var signerInfoIndex = 0;
        var signerInfo = ReadTlv(signerInfos.Value, ref signerInfoIndex);

        var signerPosition = 0;
        ReadTlv(signerInfo.Value, ref signerPosition);
        ReadTlv(signerInfo.Value, ref signerPosition);
        ReadTlv(signerInfo.Value, ref signerPosition);
        var signedAttributes = ReadTlv(signerInfo.Value, ref signerPosition);

        return ReadAttributes(signedAttributes.Value);
    }

    /// <summary>
    /// Every in-use xref entry points at its object; a reserved but unwritten object (the PAdES
    /// objects of a plain signature) must be free, or Acrobat rebuilds the xref and rejects the
    /// signature.
    /// </summary>
    private static void RequireXrefPointsAtObjects(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var start = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 6;
        var lines = text[start..].Split('\n');
        var count = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        for (var number = 1; number < count; number++)
        {
            var entry = lines[number + 1];
            if (entry[17] == 'n')
            {
                var offset = int.Parse(entry[..10], CultureInfo.InvariantCulture);
                Require(text[offset..].StartsWith(number + " 0 obj", StringComparison.Ordinal),
                    $"PDF: xref entry {number} does not point at its object (offset {offset}).");
            }
        }
    }

    private static void RejectsSignatureWithEncryption()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Text"))));
        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate),
            Encryption = new PdfEncryptionOptions { UserPassword = "secret" },
        };

        using var buffer = new MemoryStream();
        try
        {
            new PdfDocumentWriter()
                .WriteAsync(document, buffer, options)
                .GetAwaiter()
                .GetResult();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("A signed document must not be encrypted.");
    }

    /// <summary>
    /// verifies the CMS signature: the message digest of the signed attributes must match the
    /// digest of the signed data, and the signature must verify with the public key.
    /// </summary>
    private static void VerifyCms(byte[] cms, byte[] signed, X509Certificate2 certificate)
    {
        // ContentInfo: SEQUENCE { OID, [0] SignedData }
        var index = 0;
        var contentInfo = ReadTlv(cms, ref index);
        var innerIndex = 0;
        ReadTlv(contentInfo.Value, ref innerIndex); // the object identifier
        var signedDataHolder = ReadTlv(contentInfo.Value, ref innerIndex);

        // SignedData: SEQUENCE { version, digestAlgorithms, encapContentInfo, [0] certificates, signerInfos }
        var dataIndex = 0;
        var data = ReadTlv(signedDataHolder.Value, ref dataIndex);
        var position = 0;
        ReadTlv(data.Value, ref position); // version
        ReadTlv(data.Value, ref position); // digest algorithms
        ReadTlv(data.Value, ref position); // encapsulated content info
        ReadTlv(data.Value, ref position); // certificates

        var signerInfos = ReadTlv(data.Value, ref position);
        var signerInfoIndex = 0;
        var signerInfo = ReadTlv(signerInfos.Value, ref signerInfoIndex);

        var signerPosition = 0;
        ReadTlv(signerInfo.Value, ref signerPosition); // version
        ReadTlv(signerInfo.Value, ref signerPosition); // issuer and serial number
        ReadTlv(signerInfo.Value, ref signerPosition); // digest algorithm

        var signedAttributes = ReadTlv(signerInfo.Value, ref signerPosition);
        ReadTlv(signerInfo.Value, ref signerPosition); // signature algorithm
        var signature = ReadTlv(signerInfo.Value, ref signerPosition);

        // The attributes were signed as a SET OF: the tag of the implicit [0] is replaced.
        var signedBytes = new byte[signedAttributes.Raw.Length];
        signedAttributes.Raw.CopyTo(signedBytes, 0);
        signedBytes[0] = 0x31;

        // The message digest attribute must match the digest of the signed data.
        var digest = SHA256.HashData(signed);
        var attributes = ReadAttributes(signedAttributes.Value);
        Require(attributes.ContainsKey("1.2.840.113549.1.9.4"), "PDF: the message digest attribute is present.");
        Require(
            attributes["1.2.840.113549.1.9.4"].SequenceEqual(digest),
            "PDF: the message digest matches the signed data.");

        using var publicKey = certificate.GetRSAPublicKey();
        Require(publicKey is not null, "PDF: the certificate has a public key.");
        Require(
            publicKey!.VerifyData(signedBytes, signature.Value, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "PDF: the signature verifies with the public key of the certificate.");
    }

    /// <summary>The values of the signed attributes (object identifier → value).</summary>
    private static Dictionary<string, byte[]> ReadAttributes(byte[] content)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var index = 0;
        while (index < content.Length)
        {
            var attribute = ReadTlv(content, ref index);
            var position = 0;
            var oid = ReadTlv(attribute.Value, ref position);
            var values = ReadTlv(attribute.Value, ref position);
            var valuePosition = 0;
            var value = ReadTlv(values.Value, ref valuePosition);
            result[OidText(oid.Value)] = value.Value;
        }

        return result;
    }

    /// <summary>The dotted notation of an object identifier.</summary>
    private static string OidText(byte[] content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<long> { content[0] / 40, content[0] % 40 };
        var value = 0L;
        for (var index = 1; index < content.Length; index++)
        {
            value = (value << 7) | (long)(content[index] & 0x7F);
            if ((content[index] & 0x80) == 0)
            {
                parts.Add(value);
                value = 0;
            }
        }

        return string.Join('.', parts);
    }

    /// <summary>Reads a DER element (tag, content and the raw bytes).</summary>
    private static (byte Tag, byte[] Value, byte[] Raw) ReadTlv(byte[] data, ref int index)
    {
        var start = index;
        var tag = data[index++];
        var first = data[index++];
        int length;
        if (first < 0x80)
        {
            length = first;
        }
        else
        {
            var count = first & 0x7F;
            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | data[index++];
            }
        }

        var value = data[index..(index + length)];
        index += length;
        return (tag, value, data[start..index]);
    }

    /// <summary>The four values of the byte range of the signature.</summary>
    private static long[] ReadByteRange(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var start = text.IndexOf("/ByteRange", StringComparison.Ordinal);
        Require(start > 0, "PDF: the byte range is missing.");
        var open = text.IndexOf('[', start);
        var close = text.IndexOf(']', open);
        var values = text[(open + 1)..close]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        return values;
    }

    /// <summary>The signature value (the hex digits of the contents).</summary>
    private static string ReadContents(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var start = -1;
        var position = 0;
        while (true)
        {
            var candidate = text.IndexOf("/Contents", position, StringComparison.Ordinal);
            if (candidate < 0)
            {
                break;
            }

            var open = candidate + "/Contents".Length;
            while (open < text.Length && text[open] == ' ')
            {
                open++;
            }

            if (open < text.Length && text[open] == '<')
            {
                start = candidate;
                break;
            }

            position = candidate + 1;
        }

        Require(start > 0, "PDF: the signature value is missing.");
        var openHex = text.IndexOf('<', start);
        var closeHex = text.IndexOf('>', openHex);
        return text[(openHex + 1)..closeHex];
    }

    private static int IndexOf(byte[] bytes, string value)
        => Encoding.Latin1.GetString(bytes).IndexOf(value, StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
