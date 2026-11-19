using System.Security.Cryptography;

namespace Reglet;

/// <summary>
/// a time-stamp authority for a PAdES baseline B-T signature (OF-E03, ETSI EN 319 142-1): the
/// writer asks it for a timestamp token over the signature value and embeds the token as an
/// unsigned attribute.
/// </summary>
public sealed record PdfTimestampOptions
{
    /// <summary>The default SHA-256 object identifier (RFC 3161 message imprint).</summary>
    internal const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>
    /// the transport of the request: it receives the DER of a <c>TimeStampReq</c> (RFC 3161) and
    /// returns the DER of the <c>TimeStampResp</c>. Use this to plug in any TSA client.
    /// </summary>
    public Func<byte[], byte[]>? Responder { get; init; }

    /// <summary>
    /// the URL of a TSA (RFC 3161 over HTTP). Ignored when <see cref="Responder"/> is set.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// the policy the TSA should apply (RFC 3161 <c>reqPolicy</c>); null leaves the choice to the
    /// authority.
    /// </summary>
    public string? Policy { get; init; }

    /// <summary>whether a timestamp is configured (a responder or a URL).</summary>
    internal bool IsConfigured => Responder is not null || Url is { Length: > 0 };

    /// <summary>sends the request to the configured TSA and returns the response.</summary>
    /// <exception cref="InvalidOperationException">No responder and no URL is configured.</exception>
    /// <exception cref="DocumentValidationException">The TSA could not be reached or answered nonsense.</exception>
    internal byte[] Request(byte[] request)
    {
        if (Responder is { } responder)
        {
            return responder(request)
                ?? throw new DocumentValidationException("The time-stamp responder returned nothing.");
        }

        if (Url is not { Length: > 0 } url)
        {
            throw new InvalidOperationException("The timestamp has neither a responder nor a URL.");
        }

        // RFC 3161 over HTTP: POST with content type application/timestamp-query.
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var content = new ByteArrayContent(request);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
            using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new DocumentValidationException($"The time-stamp authority at '{url}' did not answer: {exception.Message}", exception);
        }
    }
}

/// <summary>
/// builds and reads the RFC 3161 time-stamp structures of a B-T signature (OF-E03): the
/// <c>TimeStampReq</c> over the signature value and the <c>TimeStampToken</c> of the response.
/// </summary>
internal static class PdfTimestamp
{
    /// <summary>The status "granted" of a time-stamp response (RFC 3161, 2.4.2).</summary>
    private const long Granted = 0;

    /// <summary>The status "granted with modifications".</summary>
    private const long GrantedWithModifications = 1;

    /// <summary>builds the DER of a <c>TimeStampReq</c> for the given signature value.</summary>
    public static byte[] BuildRequest(byte[] signature, string? policy)
    {
        ArgumentNullException.ThrowIfNull(signature);

        var imprint = PdfDer.Sequence(
            PdfDer.Sequence(PdfDer.Oid(PdfTimestampOptions.Sha256Oid), PdfDer.Null()),
            PdfDer.OctetString(SHA256.HashData(signature)));

        var elements = new List<byte[]>
        {
            PdfDer.Integer(1),
            imprint,
        };

        if (policy is { Length: > 0 } oid)
        {
            elements.Add(PdfDer.Oid(oid));
        }

        // A nonce protects against replaying an old token.
        elements.Add(PdfDer.Integer(RandomNumberGenerator.GetInt32(1, int.MaxValue)));

        // certReq: the token should carry the certificate of the TSA.
        elements.Add(PdfDer.Tlv(0x01, [0xFF]));

        return PdfDer.Sequence([.. elements]);
    }

    /// <summary>
    /// reads the <c>TimeStampResp</c> and returns the DER of its <c>TimeStampToken</c>.
    /// </summary>
    /// <exception cref="DocumentValidationException">The response is malformed or not granted.</exception>
    public static byte[] ReadToken(byte[] response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var outer = ReadElement(response, 0, response.Length);
        if (outer.Tag != 0x30)
        {
            throw new DocumentValidationException("The time-stamp response is not a SEQUENCE.");
        }

        var status = ReadElement(response, outer.ContentStart, outer.ContentEnd);
        if (status.Tag != 0x30)
        {
            throw new DocumentValidationException("The time-stamp response has no status.");
        }

        var value = ReadElement(response, status.ContentStart, status.ContentEnd);
        if (value.Tag != 0x02 || value.ContentEnd - value.ContentStart is < 1 or > 8)
        {
            throw new DocumentValidationException("The time-stamp response has no status value.");
        }

        var code = 0L;
        for (var index = value.ContentStart; index < value.ContentEnd; index++)
        {
            code = (code << 8) | response[index];
        }

        if (code is not (Granted or GrantedWithModifications))
        {
            throw new DocumentValidationException($"The time-stamp authority refused the request (status {code}).");
        }

        if (status.ContentEnd >= outer.ContentEnd)
        {
            throw new DocumentValidationException("The time-stamp response carries no token.");
        }

        var token = ReadElement(response, status.ContentEnd, outer.ContentEnd);
        if (token.Tag != 0x30)
        {
            throw new DocumentValidationException("The time-stamp token is not a SEQUENCE.");
        }

        return response.AsSpan(token.Start, token.TotalLength).ToArray();
    }

    /// <summary>the CMS attribute that carries the token in the SignerInfo (RFC 3161).</summary>
    public static byte[] TimestampAttribute(byte[] token)
        => PdfDer.Sequence(
            PdfDer.Oid("1.2.840.113549.1.9.16.2.14"),
            PdfDer.SetOf([token]));

    private readonly record struct Element(byte Tag, int Start, int ContentStart, int ContentEnd, int TotalLength);

    /// <summary>reads one DER element (definite length, one-byte tag) from the buffer.</summary>
    private static Element ReadElement(byte[] buffer, int offset, int end)
    {
        if (offset >= end)
        {
            throw new DocumentValidationException("The time-stamp structure ends unexpectedly.");
        }

        var tag = buffer[offset];
        var position = offset + 1;
        if (position >= end)
        {
            throw new DocumentValidationException("The time-stamp structure has no length.");
        }

        var length = (int)buffer[position++];
        if ((length & 0x80) != 0)
        {
            var bytes = length & 0x7F;
            if (bytes is 0 or > 4 || position + bytes > end)
            {
                throw new DocumentValidationException("The time-stamp structure has an invalid length.");
            }

            var longLength = 0;
            for (var index = 0; index < bytes; index++)
            {
                longLength = (longLength << 8) | buffer[position++];
            }

            length = longLength;
        }

        if (position + length > end)
        {
            throw new DocumentValidationException("The time-stamp structure is truncated.");
        }

        return new Element(tag, offset, position, position + length, position + length - offset);
    }
}
