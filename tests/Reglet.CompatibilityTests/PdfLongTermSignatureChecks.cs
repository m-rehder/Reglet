using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// OF-E03: the long-term part of a PAdES signature (B-LT with a document security store and a VRI
/// entry) and the document timestamp. Both are appended as incremental updates, so the signature
/// over the first revision stays valid.
/// </summary>
internal static class PdfLongTermSignatureChecks
{
    internal static int Run()
    {
        try
        {
            WritesTheValidationStore();
            KeysTheVriByTheSignature();
            KeepsTheSignedRevision();
            RequiresPades();
            AppendsADocumentTimestamp();
            RejectsAnEncryptedFile();
            Console.WriteLine("PDF long-term signature (OF-E03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF long-term signature (OF-E03): " + exception);
            return 1;
        }
    }

    private static X509Certificate2 Certificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PortableDocument Document()
        => new(
            new DocumentMetadata { Title = "Langzeit" },
            new Section(new Paragraph(new TextRun("Inhalt mit Langzeitvalidierung."))));

    /// <summary>A granted time-stamp response with a small placeholder token.</summary>
    private static byte[] Response()
    {
        var token = PdfDer.Sequence(
            PdfDer.Oid("1.2.840.113549.1.7.2"),
            PdfDer.Concat([Encoding.ASCII.GetBytes("Langzeit-Token")]));
        return PdfDer.Sequence(PdfDer.Sequence(PdfDer.Integer(0)), token);
    }

    private static byte[] Sign(PdfSignatureOptions signature)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(Document(), buffer, new PdfWriteOptions { Signature = signature })
            .GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static PdfSignatureOptions PadesOptions(X509Certificate2 certificate, PdfValidationData? validation = null)
        => new(certificate)
        {
            Pades = true,
            Timestamp = new PdfTimestampOptions { Responder = _ => Response() },
            LongTermValidation = validation,
        };

    private static void WritesTheValidationStore()
    {
        using var signer = Certificate("CN=Langzeit Signer");
        using var authority = Certificate("CN=Langzeit CA");

        var pdf = Sign(PadesOptions(signer, new PdfValidationData
        {
            Certificates = [signer, authority],
            OcspResponses = [[0x30, 0x03, 0x0A, 0x01, 0x00]],
            Crls = [[0x30, 0x03, 0x0A, 0x01, 0x01]],
        }));

        var latin = Encoding.Latin1.GetString(pdf);
        AssertContains(latin, "/Type /DSS");
        AssertContains(latin, "/VRI");
        AssertContains(latin, "/OCSPs");
        AssertContains(latin, "/CRLs");
        AssertContains(latin, "/Cert [");

        // The update is a second revision: a second xref/trailer with /Prev.
        Require(CountOccurrences(latin, "%%EOF") == 2, "The store is appended as a second revision.");
        AssertContains(latin, "/Prev ");

        // Both OCSP response and CRL are embedded as streams.
        Require(CountOccurrences(latin, "/Type /EmbeddedFile") == 0, "No embedded file is written.");
        Require(CountOccurrences(latin, "stream\n") >= 2, "The validation data is written as streams.");
    }

    /// <summary>The VRI entry is keyed by the SHA-1 of the signature contents.</summary>
    private static void KeysTheVriByTheSignature()
    {
        using var signer = Certificate("CN=VRI Signer");
        using var authority = Certificate("CN=VRI CA");

        var pdf = Sign(PadesOptions(signer, new PdfValidationData { Certificates = [signer, authority] }));
        var latin = Encoding.Latin1.GetString(pdf);

        var contents = Regex.Match(latin, @"/Contents <([0-9A-Fa-f]+)>");
        Require(contents.Success, "The signature contents are found.");

        // The placeholder is padded with zeros; the DER header states the real length.
        var padded = Convert.FromHexString(contents.Groups[1].Value);
        var length = DerLength(padded);
        Require(length is > 0 and <= int.MaxValue && length < padded.Length, "The length of the CMS is readable.");

        var key = Convert.ToHexString(SHA1.HashData(padded.AsSpan(0, (int)length)));
        Require(
            latin.Contains("/" + key, StringComparison.Ordinal),
            "The VRI key is the SHA-1 of the signature contents: " + key);
    }

    /// <summary>The signed revision is unchanged; the store is appended after it.</summary>
    private static void KeepsTheSignedRevision()
    {
        using var signer = Certificate("CN=Keep Signer");
        using var authority = Certificate("CN=Keep CA");

        var signed = Sign(PadesOptions(signer));
        var withStore = Sign(PadesOptions(signer, new PdfValidationData { Certificates = [signer, authority] }));

        // Everything up to the last end-of-file marker of the signed revision is identical.
        var firstEnd = IndexOf(signed, "%%EOF") + "%%EOF".Length;
        Require(
            withStore.AsSpan(0, firstEnd).SequenceEqual(signed.AsSpan(0, Math.Min(firstEnd, signed.Length))),
            "The signed revision is not changed by the update.");

        // The file is still readable and extractable.
        using var buffer = new MemoryStream(withStore);
        var read = new PdfDocumentReader().Read(buffer).Document;
        Require(read.Sections.Count > 0, "The PDF with the store is readable.");

        using var extracted = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(withStore), 1, 1, extracted);
        Require(extracted.Length > 0, "The page can be extracted from the updated file.");
    }

    private static void RequiresPades()
    {
        using var signer = Certificate("CN=No Pades");
        var options = new PdfSignatureOptions(signer)
        {
            LongTermValidation = new PdfValidationData { Certificates = [signer] },
        };

        Throws<ArgumentException>(() => Sign(options));
    }

    private static void AppendsADocumentTimestamp()
    {
        using var signer = Certificate("CN=Timestamp Signer");
        var signed = Sign(new PdfSignatureOptions(signer) { Pades = true });
        Require(CountOccurrences(Encoding.Latin1.GetString(signed), "%%EOF") == 1, "The input has one revision.");

        using var source = new MemoryStream(signed);
        using var output = new MemoryStream();
        PdfDocumentTimestamp.Add(source, output, new PdfTimestampOptions { Responder = _ => Response() });

        var stamped = output.ToArray();
        var latin = Encoding.Latin1.GetString(stamped);
        AssertContains(latin, "/Type /DocTimeStamp");
        AssertContains(latin, "/SubFilter /ETSI.RFC3161");
        AssertContains(latin, "/ByteRange");
        AssertContains(latin, "/AcroForm");
        Require(CountOccurrences(latin, "%%EOF") == 2, "The timestamp is a second revision.");
        Require(
            latin.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Langzeit-Token")), StringComparison.OrdinalIgnoreCase),
            "The token of the authority is embedded.");

        // The byte range covers the file except the token. It is the one of the timestamp
        // dictionary, not the range of the signature of the first revision.
        var timestampIndex = latin.IndexOf("/Type /DocTimeStamp", StringComparison.Ordinal);
        Require(timestampIndex >= 0, "The timestamp dictionary is written.");
        var range = Regex.Match(
            latin[timestampIndex..],
            @"/ByteRange \[0 (\d+) (\d+) (\d+)\]");
        Require(range.Success, "The byte range is written.");
        var first = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
        var second = long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
        var third = long.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
        Require(first < second, "The byte range starts before the token.");
        Require(second + third == stamped.Length, $"The byte range ends at the file end ({second} + {third} != {stamped.Length}).");

        // The timestamped file is still a valid PDF and carries the field of the timestamp.
        using var buffer = new MemoryStream(stamped);
        var read = new PdfDocumentReader().Read(buffer).Document;
        Require(read.Sections.Count > 0, "The timestamped PDF is readable.");
        Require(latin.Contains("(Timestamp1)", StringComparison.Ordinal), "The timestamp field is named.");
    }

    private static void RejectsAnEncryptedFile()
    {
        using var signer = Certificate("CN=Encrypted");
        var signed = Sign(new PdfSignatureOptions(signer) { Pades = true });

        // Encrypt the signed file by rewriting it through the writer is not possible, so encrypt a
        // plain document and try the timestamp on it.
        using var encrypted = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(), encrypted, new PdfWriteOptions
        {
            Encryption = new PdfEncryptionOptions { UserPassword = "geheim" },
        }).GetAwaiter().GetResult();

        using var source = new MemoryStream(encrypted.ToArray());
        using var output = new MemoryStream();
        Throws<DocumentValidationException>(() =>
            PdfDocumentTimestamp.Add(source, output, new PdfTimestampOptions { Responder = _ => Response() }));
    }

    /// <summary>The total length of a DER element at the start of the buffer, or -1.</summary>
    private static long DerLength(byte[] data)
    {
        if (data.Length < 2)
        {
            return -1;
        }

        var first = data[1];
        if (first < 0x80)
        {
            return 2 + first;
        }

        var count = first & 0x7F;
        if (count is 0 or > 4 || data.Length < 2 + count)
        {
            return -1;
        }

        long content = 0;
        for (var index = 0; index < count; index++)
        {
            content = (content << 8) | data[2 + index];
        }

        return 2 + count + content;
    }

    private static int IndexOf(byte[] haystack, string needle)
    {
        var bytes = Encoding.ASCII.GetBytes(needle);
        return haystack.AsSpan().IndexOf(bytes);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertContains(string text, string expected)
        => Require(text.Contains(expected, StringComparison.Ordinal), $"Expected: {expected}");

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Erwartete {typeof(TException).Name} blieb aus.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
