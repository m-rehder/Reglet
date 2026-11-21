using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// reads and verifies the digital signatures and document timestamps of a PDF (ISO 32000, 12.8):
/// the byte range, the CMS signature over its digest, an embedded time-stamp token and, with
/// trusted roots, the certificate chain. The reader checks what the file proves; trust, revocation
/// and the validation material stay with the caller, as for the DOCX and ODT signatures.
/// </summary>
public static class PdfSignatureReader
{
    private const int MaxFields = 10_000;
    private static readonly System.Buffers.SearchValues<byte> Whitespace = System.Buffers.SearchValues.Create(" \n\r\t\0"u8);
    private const string DocumentTimestampFilter = "ETSI.RFC3161";

    /// <summary>
    /// reads the signatures of a PDF. A file without a signature yields an empty list; a signature
    /// that does not verify is returned with <see cref="PdfSignatureInfo.IsValid"/> false and the
    /// reasons in <see cref="PdfSignatureInfo.Problems"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The file is not a readable PDF.</exception>
    public static IReadOnlyList<PdfSignatureInfo> Read(Stream input, PdfSignatureReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReadCore(ReaderGuard.ReadAllBounded(input, "PDF"), options ?? PdfSignatureReadOptions.Default);
    }

    /// <summary>Reads the signatures of a PDF from its bytes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The file is not a readable PDF.</exception>
    public static IReadOnlyList<PdfSignatureInfo> Read(byte[] pdf, PdfSignatureReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length > ReaderGuard.MaxInputBytes)
        {
            throw new DocumentValidationException("PDF signature: the file exceeds the input size limit.");
        }

        return ReadCore(pdf, options ?? PdfSignatureReadOptions.Default);
    }

    private static IReadOnlyList<PdfSignatureInfo> ReadCore(byte[] data, PdfSignatureReadOptions options)
        => ReaderGuard.Run("PDF", () =>
        {
            var file = PdfFile.Load(data, options.Password);
            var result = new List<PdfSignatureInfo>();
            var catalog = file.Dictionary(file.Trailer.Get("Root"));
            var acroForm = file.Dictionary(catalog?.Get("AcroForm"));
            if (file.Array(acroForm?.Get("Fields")) is not { } fields)
            {
                return result;
            }

            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<(PdfValue Field, string Name)>(fields.Items.Select(item => (item, string.Empty)));
            while (queue.Count > 0 && visited.Count < MaxFields)
            {
                var (item, parentName) = queue.Dequeue();
                if (file.Dictionary(item) is not { } field || !visited.Add(field))
                {
                    continue;
                }

                var name = file.Resolve(field.Get("T")) is PdfString text
                    ? (parentName.Length == 0 ? string.Empty : parentName + ".") + PdfTextString.Decode(text.Bytes)
                    : parentName;
                if (file.Array(field.Get("Kids")) is { } kids)
                {
                    foreach (var kid in kids.Items)
                    {
                        queue.Enqueue((kid, name));
                    }
                }

                if (file.Dictionary(field.Get("V")) is { } value && value.Get("ByteRange") is not null)
                {
                    result.Add(Verify(file, data, value, name, options));
                }
            }

            return result;
        });

    private static PdfSignatureInfo Verify(PdfFile file, byte[] data, PdfDictionary signature, string fieldName, PdfSignatureReadOptions options)
    {
        var subFilter = file.Name(signature.Get("SubFilter"));
        var isTimestamp = file.Name(signature.Get("Type")) == "DocTimeStamp" || subFilter == DocumentTimestampFilter;
        var info = new PdfSignatureInfo
        {
            FieldName = fieldName,
            SubFilter = subFilter,
            IsDocumentTimestamp = isTimestamp,
            SignerName = Text(file, signature, "Name"),
            Reason = Text(file, signature, "Reason"),
            Location = Text(file, signature, "Location"),
        };

        var problems = new List<string>();
        var covered = ReadByteRange(file, data, signature, problems, out var contents, out var coversWholeFile);
        if (covered is null || contents is null)
        {
            return info with { Problems = problems };
        }

        var chainProblems = new List<string>();
        X509Certificate2? certificate;
        IReadOnlyList<X509Certificate2> extra;
        DateTimeOffset? signingTime = ParseDate(Text(file, signature, "M"));
        DateTimeOffset? timestampTime = null;
        X509Certificate2? stampAuthority = null;
        IReadOnlyList<X509Certificate2> stampCertificates = [];
        var stampSound = false;

        byte[] Digest(HashAlgorithmName name) => PdfCmsVerifier.HashData(name, covered());

        if (isTimestamp)
        {
            // A document timestamp is a time-stamp token over the byte range.
            timestampTime = PdfCmsVerifier.VerifyTimestamp(contents, covered(), problems, out certificate, out extra);
            signingTime = timestampTime ?? signingTime;
        }
        else
        {
            var cms = PdfCmsVerifier.Verify(contents, Digest);
            problems.AddRange(cms.Problems);
            certificate = cms.Signer;
            extra = cms.Certificates;
            signingTime = cms.SigningTime ?? signingTime;
            if (cms.TimestampToken is { } token)
            {
                var before = problems.Count;
                timestampTime = PdfCmsVerifier.VerifyTimestamp(token, cms.SignatureValue, problems, out stampAuthority, out stampCertificates);
                stampSound = problems.Count == before;
            }
        }

        bool? trusted = null;
        if (certificate is not null && options.TrustedRoots.Count > 0)
        {
            // The chain is judged at the verified time-stamp time, or now: a signing time that the
            // signer claims by itself must not decide whether its certificate was valid. A time-stamp
            // counts only when it is sound and its authority chains to a trusted root too; otherwise
            // anyone holding an expired key could stamp a time when the certificate was still valid.
            var chainTime = timestampTime;
            if (!isTimestamp && timestampTime is not null)
            {
                var authorityProblems = new List<string>();
                if (!stampSound || stampAuthority is null
                    || !CheckChain(stampAuthority, stampCertificates, options, timestampTime, authorityProblems))
                {
                    chainProblems.Add("The time-stamp authority is not trusted; the chain is judged at the current time.");
                    chainProblems.AddRange(authorityProblems.Select(problem => "Time-stamp authority: " + problem));
                    chainTime = null;
                }
            }

            trusted = CheckChain(certificate, extra, options, chainTime, chainProblems);
        }

        return info with
        {
            Certificate = certificate,
            SigningTime = signingTime,
            TimestampTime = timestampTime,
            CoversWholeFile = coversWholeFile,
            IsValid = problems.Count == 0,
            Problems = problems,
            IsTrusted = trusted,
            ChainProblems = chainProblems,
        };
    }

    /// <summary>
    /// checks the byte range ([0 a b c]: the file without the contents string) and returns a function
    /// that yields the covered bytes, plus the decoded contents. Null covered means a bad range.
    /// </summary>
    private static Func<byte[]>? ReadByteRange(
        PdfFile file,
        byte[] data,
        PdfDictionary signature,
        List<string> problems,
        out byte[]? contents,
        out bool coversWholeFile)
    {
        contents = null;
        coversWholeFile = false;
        var range = file.Array(signature.Get("ByteRange"))?.Items.Select(item => file.Integer(item)).ToList();
        if (range is not { Count: 4 } || range.Any(value => value is null or < 0))
        {
            problems.Add("PDF signature: the byte range is missing or malformed.");
            return null;
        }

        var (start, firstLength, secondStart, secondLength) = (range[0]!.Value, range[1]!.Value, range[2]!.Value, range[3]!.Value);
        if (start != 0 || firstLength > secondStart || secondStart + secondLength > data.Length)
        {
            problems.Add("PDF signature: the byte range does not fit the file.");
            return null;
        }

        // The gap is the contents string <hex…>; the signature must not hide behind another gap.
        var gap = data.AsSpan((int)firstLength, (int)(secondStart - firstLength));
        if (gap.Length < 2 || gap[0] != (byte)'<' || gap[^1] != (byte)'>')
        {
            problems.Add("PDF signature: the byte range leaves out more than the signature contents.");
            return null;
        }

        var hex = new List<byte>(gap.Length / 2);
        var high = -1;
        foreach (var character in gap[1..^1])
        {
            var digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - '0',
                >= (byte)'a' and <= (byte)'f' => character - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                if (character is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')
                {
                    continue;
                }

                problems.Add("PDF signature: the contents are not a hexadecimal string.");
                return null;
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                hex.Add((byte)((high << 4) | digit));
                high = -1;
            }
        }

        contents = [.. hex];
        coversWholeFile = data.AsSpan((int)(secondStart + secondLength)).IndexOfAnyExcept(Whitespace) < 0;
        return () =>
        {
            var joined = new byte[firstLength + secondLength];
            data.AsSpan(0, (int)firstLength).CopyTo(joined);
            data.AsSpan((int)secondStart, (int)secondLength).CopyTo(joined.AsSpan((int)firstLength));
            return joined;
        };
    }

    private static string? Text(PdfFile file, PdfDictionary dictionary, string key)
        => file.Resolve(dictionary.Get(key)) is PdfString text ? PdfTextString.Decode(text.Bytes) : null;

    /// <summary>reads a PDF date (<c>D:YYYYMMDDHHmmSSOHH'mm'</c>, ISO 32000, 7.9.4).</summary>
    private static DateTimeOffset? ParseDate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var value = text.StartsWith("D:", StringComparison.Ordinal) ? text[2..] : text;
        if (value.Length < 4 || !value[..4].All(char.IsAsciiDigit))
        {
            return null;
        }

        int Part(int index, int length, int fallback)
            => value.Length >= index + length && int.TryParse(value.AsSpan(index, length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : fallback;

        try
        {
            var offset = TimeSpan.Zero;
            if (value.Length > 14 && value[14] is '+' or '-')
            {
                offset = new TimeSpan(Part(15, 2, 0), Part(18, 2, 0), 0);
                if (value[14] == '-')
                {
                    offset = -offset;
                }
            }

            return new DateTimeOffset(
                Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0), Part(10, 2, 0), Part(12, 2, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool CheckChain(
        X509Certificate2 certificate,
        IReadOnlyList<X509Certificate2> embedded,
        PdfSignatureReadOptions options,
        DateTimeOffset? verifiedTime,
        List<string> problems)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = options.RevocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(options.TrustedRoots.ToArray());
        if (verifiedTime is { } time)
        {
            chain.ChainPolicy.VerificationTime = time.UtcDateTime;
        }

        foreach (var other in embedded)
        {
            chain.ChainPolicy.ExtraStore.Add(other);
        }

        try
        {
            if (chain.Build(certificate))
            {
                return true;
            }
        }
        catch (CryptographicException exception)
        {
            problems.Add("The certificate chain could not be built: " + exception.Message);
            return false;
        }

        foreach (var status in chain.ChainStatus)
        {
            problems.Add($"{status.Status}: {status.StatusInformation.Trim()}");
        }

        return false;
    }
}

/// <summary>the result of reading one signature or document timestamp of a PDF.</summary>
public sealed record PdfSignatureInfo
{
    /// <summary>The name of the signature field.</summary>
    public string? FieldName { get; init; }

    /// <summary>The sub-filter, for example <c>adbe.pkcs7.detached</c>, <c>ETSI.CAdES.detached</c> or <c>ETSI.RFC3161</c>.</summary>
    public string? SubFilter { get; init; }

    /// <summary>Whether this entry is a document timestamp and not a signature of a person.</summary>
    public bool IsDocumentTimestamp { get; init; }

    /// <summary>The certificate of the signer (of the time-stamp authority for a document timestamp).</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>The subject of the certificate, for display.</summary>
    public string? Subject => Certificate?.Subject;

    /// <summary>The name, reason and location the signer stated (not verified).</summary>
    public string? SignerName { get; init; }

    /// <summary>The reason stated in the signature (not verified).</summary>
    public string? Reason { get; init; }

    /// <summary>The location stated in the signature (not verified).</summary>
    public string? Location { get; init; }

    /// <summary>
    /// The signing time: the verified time of a document timestamp, otherwise the time the signer
    /// claims (the signed attribute or <c>/M</c>); the claim proves nothing without
    /// <see cref="TimestampTime"/>.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>The time of a verified time-stamp token over the signature (B-T); null without one.</summary>
    public DateTimeOffset? TimestampTime { get; init; }

    /// <summary>
    /// Whether the signature covers the file up to its end. False means later revisions follow
    /// (another signature, a document timestamp, form filling or other changes).
    /// </summary>
    public bool CoversWholeFile { get; init; }

    /// <summary>Whether the signature, its digest and a time-stamp token are valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Whether the certificate chains to a trusted root of the read options; null when no trusted
    /// roots were given.
    /// </summary>
    public bool? IsTrusted { get; init; }

    /// <summary>The problems of the chain check (expired, untrusted root, revocation, …).</summary>
    public IReadOnlyList<string> ChainProblems { get; init; } = [];

    /// <summary>What is wrong with the signature; empty when it is valid.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>the settings for reading the signatures of a PDF.</summary>
public sealed record PdfSignatureReadOptions
{
    private IReadOnlyList<X509Certificate2> _trustedRoots = [];

    /// <summary>Default options: the signature is verified, the certificate chain is not evaluated.</summary>
    public static PdfSignatureReadOptions Default { get; } = new();

    /// <summary>The password of an encrypted PDF.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// The trusted root certificates. Without them only the cryptographic signature is checked and a
    /// certificate is not judged trusted; with them the chain is built against exactly these roots.
    /// </summary>
    public IReadOnlyList<X509Certificate2> TrustedRoots
    {
        get => _trustedRoots;
        init => _trustedRoots = value is null ? [] : [.. value];
    }

    /// <summary>
    /// How the chain checks revocation. Default is <see cref="X509RevocationMode.NoCheck"/>, because
    /// the library never calls the network.
    /// </summary>
    public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.NoCheck;
}
