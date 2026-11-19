using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>
/// OF-E03: the timestamp of a PAdES baseline B-T signature. The writer asks a time-stamp
/// authority (here a stub) for a token over the signature value and embeds it as the unsigned
/// attribute <c>id-aa-signatureTimeStampToken</c>.
/// </summary>
internal static class PdfTimestampChecks
{
    internal static int Run()
    {
        try
        {
            BuildsTheRequest();
            EmbedsTheToken();
            RejectsARefusedRequest();
            RejectsAnInvalidResponse();
            RequiresATransport();
            Console.WriteLine("PDF signature timestamp (OF-E03): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF signature timestamp (OF-E03): " + exception);
            return 1;
        }
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet Timestamp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PortableDocument Document()
        => new(
            new DocumentMetadata { Title = "Timestamp" },
            new Section(new Paragraph(new TextRun("Content with a timestamp."))));

    /// <summary>A granted response with a token; the token is a placeholder, not a real TSA token.</summary>
    private static byte[] Response(bool granted = true)
    {
        var info = PdfDer.Sequence(PdfDer.Integer(granted ? 0 : 2));
        if (!granted)
        {
            return PdfDer.Sequence(info);
        }

        var token = PdfDer.Sequence(
            PdfDer.Oid("1.2.840.113549.1.7.2"),
            PdfDer.Concat([Encoding.ASCII.GetBytes("Stub-Token")]));
        return PdfDer.Sequence(info, token);
    }

    private static void BuildsTheRequest()
    {
        var signature = new byte[256];
        RandomNumberGenerator.Fill(signature);

        var request = PdfTimestamp.BuildRequest(signature, policy: "1.2.3.4.1");
        Require(request[0] == 0x30, "The request is a SEQUENCE.");

        // The message imprint is the SHA-256 of the signature value.
        var digest = SHA256.HashData(signature);
        var index = IndexOf(request, digest);
        Require(index > 0, "The request carries the hash of the signature value.");
        Require(IndexOf(request, PdfDer.Oid("1.2.3.4.1")) > 0, "The policy is part of the request.");
    }

    private static void EmbedsTheToken()
    {
        var seen = new List<byte[]>();
        var certificate = Certificate();
        using (certificate)
        {
            var options = new PdfWriteOptions
            {
                Signature = new PdfSignatureOptions(certificate)
                {
                    Pades = true,
                    Timestamp = new PdfTimestampOptions
                    {
                        Responder = request =>
                        {
                            seen.Add(request);
                            return Response();
                        },
                    },
                },
            };

            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(Document(), buffer, options).GetAwaiter().GetResult();
            var latin = Encoding.Latin1.GetString(buffer.ToArray());

            Require(seen.Count == 1, "The authority is asked once.");
            Require(latin.Contains("/SubFilter /ETSI.CAdES.detached", StringComparison.Ordinal), "The PAdES subfilter is written.");
            Require(latin.Contains("/ByteRange", StringComparison.Ordinal), "The document is signed.");

            // The unsigned attribute (OID 1.2.840.113549.1.9.16.2.14) carries the token; its DER is
            // part of the signature value, which is written as a hex string.
            var oid = PdfDer.Oid("1.2.840.113549.1.9.16.2.14");
            var hex = Convert.ToHexString(oid);
            Require(latin.Contains(hex, StringComparison.OrdinalIgnoreCase), "The timestamp attribute is embedded: " + hex);
            Require(
                latin.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("Stub-Token")), StringComparison.OrdinalIgnoreCase),
                "The token of the authority is embedded.");
        }
    }

    private static void RejectsARefusedRequest()
    {
        using var certificate = Certificate();
        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate)
            {
                Pades = true,
                Timestamp = new PdfTimestampOptions { Responder = _ => Response(granted: false) },
            },
        };

        Throws<DocumentValidationException>(() =>
        {
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(Document(), buffer, options).GetAwaiter().GetResult();
        });
    }

    private static void RejectsAnInvalidResponse()
    {
        using var certificate = Certificate();
        var options = new PdfWriteOptions
        {
            Signature = new PdfSignatureOptions(certificate)
            {
                Pades = true,
                Timestamp = new PdfTimestampOptions { Responder = _ => [0x01, 0x02, 0x03] },
            },
        };

        Throws<DocumentValidationException>(() =>
        {
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(Document(), buffer, options).GetAwaiter().GetResult();
        });
    }

    private static void RequiresATransport()
    {
        Throws<InvalidOperationException>(() => _ = new PdfTimestampOptions().Request([]));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {
                return index;
            }
        }

        return -1;
    }

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

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
