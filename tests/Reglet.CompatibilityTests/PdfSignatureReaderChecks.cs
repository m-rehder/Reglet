using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>
/// reading and verifying PDF signatures (PdfSignatureReader): the digest over the byte range, the
/// signature value, a time-stamp token of a B-T signature, a document timestamp and the chain.
/// The certificates and the time-stamp authority are created for the test.
/// </summary>
internal static class PdfSignatureReaderChecks
{
    internal static int Run()
    {
        try
        {
            ReadsValidSignature();
            ReadsPadesSignature();
            ReportsUnsignedPdfAsEmpty();
            DetectsChangedContent();
            DetectsChangedSignatureValue();
            ChecksTimestampOfBaselineT();
            RejectsTimestampForOtherSignature();
            ChecksDocumentTimestamp();
            JudgesTrustOnlyWithRoots();
            Console.WriteLine("PDF signature reader: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF signature reader: " + exception);
            return 1;
        }
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Keep the private key with the certificate beyond the disposal of the local key.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }

    private static PortableDocument Document()
        => new(
            new DocumentMetadata { Title = "Signed" },
            new Section(new Paragraph(new TextRun("The content of the signed document."))));

    private static byte[] Sign(X509Certificate2 certificate, bool pades = false, PdfTimestampOptions? timestamp = null)
    {
        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate)
            {
                Name = "Tester",
                Reason = "Automated test",
                Pades = pades,
                Timestamp = timestamp,
            },
        };
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(), buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void ReadsValidSignature()
    {
        using var certificate = Certificate("Reglet Reader Test");
        var signature = PdfSignatureReader.Read(Sign(certificate)).Single();

        Require(signature.IsValid, "A valid signature verifies: " + string.Join("; ", signature.Problems));
        Require(!signature.IsDocumentTimestamp, "It is a signature, not a document timestamp.");
        Require(signature.Subject == certificate.Subject, "The certificate of the signer is returned.");
        Require(signature.CoversWholeFile, "The signature covers the file to its end.");
        Require(signature.SignerName == "Tester" && signature.Reason == "Automated test", "Name and reason are read.");
        Require(signature.SigningTime is { } time && Math.Abs((DateTimeOffset.UtcNow - time).TotalMinutes) < 5, "The signing time is read.");
        Require(signature.IsTrusted is null, "Without trusted roots the trust stays open.");
    }

    private static void ReadsPadesSignature()
    {
        using var certificate = Certificate("Reglet Reader PAdES");
        var signature = PdfSignatureReader.Read(Sign(certificate, pades: true)).Single();

        Require(signature.IsValid, "A PAdES signature verifies: " + string.Join("; ", signature.Problems));
        Require(signature.SubFilter == "ETSI.CAdES.detached", "The sub-filter is read.");
    }

    private static void ReportsUnsignedPdfAsEmpty()
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(Document(), buffer, new PdfWriteOptions()).GetAwaiter().GetResult();
        Require(PdfSignatureReader.Read(buffer.ToArray()).Count == 0, "A PDF without a signature yields no entries.");
    }

    private static void DetectsChangedContent()
    {
        using var certificate = Certificate("Reglet Reader Tamper");
        var bytes = Sign(certificate);

        // Change a byte inside the covered part (the title in the info dictionary).
        var index = IndexOf(bytes, "Signed");
        Require(index > 0, "The title is in the file.");
        bytes[index] = (byte)'X';

        var signature = PdfSignatureReader.Read(bytes).Single();
        Require(!signature.IsValid, "A changed document no longer verifies.");
        Require(signature.Problems.Any(problem => problem.Contains("changed", StringComparison.Ordinal)), "The problem names the change: " + string.Join("; ", signature.Problems));
    }

    private static void DetectsChangedSignatureValue()
    {
        using var certificate = Certificate("Reglet Reader Value");
        var bytes = Sign(certificate);

        // Flip the last hex digit of the real signature value (the zero padding follows it).
        var contents = IndexOf(bytes, "/Contents <") + "/Contents <".Length;
        var end = Array.IndexOf(bytes, (byte)'>', contents);
        var last = end - 1;
        while (bytes[last] == (byte)'0')
        {
            last--;
        }

        bytes[last] = bytes[last] == (byte)'1' ? (byte)'2' : (byte)'1';

        var signature = PdfSignatureReader.Read(bytes).Single();
        Require(!signature.IsValid, "A changed signature value does not verify.");
    }

    private static void ChecksTimestampOfBaselineT()
    {
        using var authority = Certificate("Reglet Test TSA");
        using var certificate = Certificate("Reglet Reader B-T");
        var signature = PdfSignatureReader.Read(Sign(certificate, pades: true, timestamp: Authority(authority))).Single();

        Require(signature.IsValid, "A B-T signature verifies: " + string.Join("; ", signature.Problems));
        Require(signature.TimestampTime is { } time && Math.Abs((DateTimeOffset.UtcNow - time).TotalMinutes) < 5, "The time of the token is read.");
    }

    private static void RejectsTimestampForOtherSignature()
    {
        using var authority = Certificate("Reglet Test TSA");
        using var certificate = Certificate("Reglet Reader B-T other");

        // The authority stamps a different value than the one it was asked about.
        var wrong = Authority(authority, imprintOverride: SHA256.HashData([1, 2, 3]));
        var signature = PdfSignatureReader.Read(Sign(certificate, pades: true, timestamp: wrong)).Single();

        Require(!signature.IsValid, "A token over another value is rejected.");
        Require(signature.Problems.Any(problem => problem.Contains("imprint", StringComparison.Ordinal)), "The problem names the imprint: " + string.Join("; ", signature.Problems));
    }

    private static void ChecksDocumentTimestamp()
    {
        using var authority = Certificate("Reglet Test TSA");
        using var certificate = Certificate("Reglet Reader DocTS");
        var signed = Sign(certificate, pades: true);

        using var stamped = new MemoryStream();
        PdfDocumentTimestamp.Add(new MemoryStream(signed), stamped, Authority(authority));

        var signatures = PdfSignatureReader.Read(stamped.ToArray());
        Require(signatures.Count == 2, "The signature and the document timestamp are listed.");

        var original = signatures.Single(item => !item.IsDocumentTimestamp);
        var stamp = signatures.Single(item => item.IsDocumentTimestamp);
        Require(original.IsValid, "The signature stays valid after the timestamp: " + string.Join("; ", original.Problems));
        Require(!original.CoversWholeFile, "The signature no longer covers the file to its end.");
        Require(stamp.IsValid, "The document timestamp verifies: " + string.Join("; ", stamp.Problems));
        Require(stamp.CoversWholeFile, "The timestamp covers the whole file.");
        Require(stamp.TimestampTime is not null && stamp.Subject == authority.Subject, "Time and authority are read.");

        // Changing the signed part breaks the timestamp as well.
        var tampered = stamped.ToArray();
        var index = IndexOf(tampered, "Signed");
        tampered[index] = (byte)'X';
        Require(PdfSignatureReader.Read(tampered).Single(item => item.IsDocumentTimestamp).IsValid == false, "A changed file breaks the timestamp.");
    }

    private static void JudgesTrustOnlyWithRoots()
    {
        using var certificate = Certificate("Reglet Reader Trust");
        using var stranger = Certificate("Reglet Reader Stranger");
        var bytes = Sign(certificate);

        var trusted = PdfSignatureReader.Read(bytes, new PdfSignatureReadOptions { TrustedRoots = [certificate] }).Single();
        Require(trusted.IsTrusted == true, "A certificate that is its own trusted root is trusted: " + string.Join("; ", trusted.ChainProblems));

        var untrusted = PdfSignatureReader.Read(bytes, new PdfSignatureReadOptions { TrustedRoots = [stranger] }).Single();
        Require(untrusted.IsTrusted == false && untrusted.IsValid, "Another root does not trust it, the signature itself stays valid.");
    }

    /// <summary>A time-stamp authority (RFC 3161) that signs the requested imprint with its certificate.</summary>
    private static PdfTimestampOptions Authority(X509Certificate2 certificate, byte[]? imprintOverride = null)
        => new()
        {
            Responder = request =>
            {
                var imprint = DerNode.Parse(request, 0, request.Length).Children()[1];
                var info = PdfDer.Sequence(
                    PdfDer.Integer(1),
                    PdfDer.Oid("1.2.3.4.1"),
                    imprintOverride is null
                        ? imprint.Raw
                        : PdfDer.Sequence(PdfDer.Sequence(PdfDer.Oid("2.16.840.1.101.3.4.2.1"), PdfDer.Null()), PdfDer.OctetString(imprintOverride)),
                    PdfDer.Integer(42),
                    PdfDer.Tlv(0x18, Encoding.ASCII.GetBytes(DateTime.UtcNow.ToString("yyyyMMddHHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture))));
                return PdfDer.Sequence(PdfDer.Sequence(PdfDer.Integer(0)), Token(certificate, info));
            },
        };

    /// <summary>The CMS SignedData of a time-stamp token with the TSTInfo as encapsulated content.</summary>
    private static byte[] Token(X509Certificate2 certificate, byte[] tstInfo)
    {
        const string sha256 = "2.16.840.1.101.3.4.2.1";
        var attributes = PdfDer.SetOf(
        [
            PdfDer.Sequence(PdfDer.Oid("1.2.840.113549.1.9.3"), PdfDer.SetOf([PdfDer.Oid("1.2.840.113549.1.9.16.1.4")])),
            PdfDer.Sequence(PdfDer.Oid("1.2.840.113549.1.9.4"), PdfDer.SetOf([PdfDer.OctetString(SHA256.HashData(tstInfo))])),
        ]);
        using var rsa = certificate.GetRSAPrivateKey()!;
        var signature = rsa.SignData(attributes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var serial = certificate.SerialNumberBytes.ToArray();
        var signerInfo = PdfDer.Sequence(
            PdfDer.Integer(1),
            PdfDer.Sequence(certificate.IssuerName.RawData, PdfDer.Tlv(0x02, serial[0] >= 0x80 ? [0, .. serial] : serial)),
            PdfDer.Sequence(PdfDer.Oid(sha256), PdfDer.Null()),
            PdfDer.Context(0, constructed: true, PdfDer.ContentOf(attributes)),
            PdfDer.Sequence(PdfDer.Oid("1.2.840.113549.1.1.1"), PdfDer.Null()),
            PdfDer.OctetString(signature));

        var signedData = PdfDer.Sequence(
            PdfDer.Integer(3),
            PdfDer.SetOf([PdfDer.Sequence(PdfDer.Oid(sha256), PdfDer.Null())]),
            PdfDer.Sequence(PdfDer.Oid("1.2.840.113549.1.9.16.1.4"), PdfDer.Context(0, constructed: true, PdfDer.OctetString(tstInfo))),
            PdfDer.Context(0, constructed: true, certificate.RawData),
            PdfDer.SetOf([signerInfo]));
        return PdfDer.Sequence(PdfDer.Oid("1.2.840.113549.1.7.2"), PdfDer.Context(0, constructed: true, signedData));
    }

    private static int IndexOf(byte[] haystack, string needle)
        => haystack.AsSpan().IndexOf(Encoding.Latin1.GetBytes(needle));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
