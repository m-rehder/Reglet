using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>
/// OF-E04: the signature conformance check with an independent validator. Set
/// <c>REGLET_PYHANKO</c> to a Python interpreter with <c>pyHanko</c>; the test then runs a
/// local time-stamp authority and validates the B-T, B-LT and document-timestamp output.
/// </summary>
internal static class PdfSignatureValidatorChecks
{
    internal static int Run()
    {
        try
        {
            ValidateWithPyHankoWhenConfigured();
            Console.WriteLine("Signature conformance (OF-E04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Signature conformance (OF-E04): " + exception);
            return 1;
        }
    }

    private const string TsaScript = """
        import sys
        from asn1crypto import keys, tsp, x509 as asn1x509
        from cryptography import x509 as pyca
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
        from cryptography.x509.oid import NameOID
        from pyhanko.sign.timestamps import dummy_client
        import datetime, os

        base = os.path.dirname(os.path.abspath(__file__))
        cert_path = os.path.join(base, 'tsa-cert.der')
        key_path = os.path.join(base, 'tsa-key.pem')
        if not (os.path.exists(cert_path) and os.path.exists(key_path)):
            key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
            name = pyca.Name([pyca.NameAttribute(NameOID.COMMON_NAME, 'Reglet Test TSA')])
            now = datetime.datetime.now(datetime.timezone.utc)
            cert = (pyca.CertificateBuilder().subject_name(name).issuer_name(name)
                    .public_key(key.public_key()).serial_number(pyca.random_serial_number())
                    .not_valid_before(now - datetime.timedelta(days=1))
                    .not_valid_after(now + datetime.timedelta(days=3650))
                    .add_extension(pyca.BasicConstraints(ca=True, path_length=None), critical=True)
                    .add_extension(pyca.ExtendedKeyUsage([pyca.oid.ExtendedKeyUsageOID.TIME_STAMPING]), critical=True)
                    .sign(key, hashes.SHA256()))
            open(cert_path, 'wb').write(cert.public_bytes(serialization.Encoding.DER))
            open(key_path, 'wb').write(key.private_bytes(
                serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))

        request = tsp.TimeStampReq.load(sys.stdin.buffer.read())
        cert = asn1x509.Certificate.load(open(cert_path, 'rb').read())
        private_key = serialization.load_pem_private_key(open(key_path, 'rb').read(), None)
        key = keys.PrivateKeyInfo.load(private_key.private_bytes(
            serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        stamp = dummy_client.DummyTimeStamper(cert, key).request_tsa_response(request)
        sys.stdout.buffer.write(stamp.dump())
        """;

    private const string VerifyScript = """
        import os, sys
        from asn1crypto import x509 as asn1x509
        from pyhanko.pdf_utils.reader import PdfFileReader
        from pyhanko.sign.validation import validate_pdf_signature, validate_pdf_timestamp
        from pyhanko_certvalidator import ValidationContext

        base = os.path.dirname(os.path.abspath(__file__))
        tsa = asn1x509.Certificate.load(open(os.path.join(base, 'tsa-cert.der'), 'rb').read())

        with open(sys.argv[1], 'rb') as handle:
            reader = PdfFileReader(handle)
            for signature in reader.embedded_signatures:
                ctx = ValidationContext(trust_roots=[signature.signer_cert, tsa])
                if signature.sig_object.get('/Type') == '/DocTimeStamp':
                    status = validate_pdf_timestamp(signature, validation_context=ctx)
                    print('timestamp', signature.field_name, status.summary())
                    if not status.intact:
                        sys.exit('document timestamp is not intact')
                    continue

                status = validate_pdf_signature(
                    signature, signer_validation_context=ctx, ts_validation_context=ctx)
                print('signature', signature.field_name, status.summary())
                if not status.intact:
                    sys.exit('signature is not intact')
                expected = os.environ.get('EXPECT_TIMESTAMP') == '1'
                if expected and status.timestamp_validity is None:
                    sys.exit('the timestamp token is missing')
                if expected and not status.timestamp_validity.intact:
                    sys.exit('the timestamp token is not intact')
        """;

    private static void ValidateWithPyHankoWhenConfigured()
    {
        var python = Environment.GetEnvironmentVariable("REGLET_PYHANKO");
        if (string.IsNullOrEmpty(python) || !File.Exists(python))
        {
            Console.WriteLine("Signature validator skipped (REGLET_PYHANKO not set).");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "reglet-pyhanko-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var tsaScript = Path.Combine(folder, "tsa.py");
        var verifyScript = Path.Combine(folder, "verify.py");
        File.WriteAllText(tsaScript, TsaScript);
        File.WriteAllText(verifyScript, VerifyScript);

        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Reglet Validation", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            byte[] Responder(byte[] payload)
            {
                var info = new ProcessStartInfo(python)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add(tsaScript);
                using var process = Process.Start(info) ?? throw new InvalidOperationException("The TSA could not be started.");
                process.StandardInput.BaseStream.Write(payload);
                process.StandardInput.Close();
                using var output = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(output);
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("The TSA failed: " + process.StandardError.ReadToEnd());
                }

                return output.ToArray();
            }

            // B-T: the signature carries a real RFC 3161 token.
            var signed = Write(new PdfSignatureOptions(certificate)
            {
                Pades = true,
                Timestamp = new PdfTimestampOptions { Responder = Responder },
            });
            Validate(python, verifyScript, folder, signed, expectTimestamp: true);

            // Document timestamp: a second revision with a /DocTimeStamp field.
            using var source = new MemoryStream(signed);
            using var stamped = new MemoryStream();
            PdfDocumentTimestamp.Add(source, stamped, new PdfTimestampOptions { Responder = Responder });
            Validate(python, verifyScript, folder, stamped.ToArray(), expectTimestamp: true);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static byte[] Write(PdfSignatureOptions signature)
    {
        var document = new PortableDocument(
            new DocumentMetadata { Title = "Validierung" },
            new Section(new Paragraph(new TextRun("Inhalt für die Signaturprüfung."))));

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { Signature = signature })
            .GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void Validate(string python, string script, string folder, byte[] pdf, bool expectTimestamp)
    {
        var path = Path.Combine(folder, "document.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(script);
            info.ArgumentList.Add(path);
            info.Environment["EXPECT_TIMESTAMP"] = expectTimestamp ? "1" : "0";
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The validator could not be started.");
            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(
                process.ExitCode == 0,
                $"pyHanko rejected the signature: {output} {errors}".Trim());
            Require(output.Contains("INTACT", StringComparison.Ordinal), "The validator reports an intact signature: " + output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
