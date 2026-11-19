using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// the validation material for a PAdES baseline B-LT signature (OF-E03, ETSI EN 319 142-1): the
/// certificates, OCSP responses and CRLs that a validator needs later, when the signer's
/// certificate has expired. It is written as a document security store in an incremental update,
/// so the signature over the first revision stays intact.
/// </summary>
public sealed record PdfValidationData
{
    private IReadOnlyList<X509Certificate2> _certificates = [];
    private IReadOnlyList<byte[]> _ocspResponses = [];
    private IReadOnlyList<byte[]> _crls = [];

    /// <summary>The certificates of the chain (including the signer's).</summary>
    public IReadOnlyList<X509Certificate2> Certificates
    {
        get => _certificates;
        init => _certificates = value is null ? [] : [.. value];
    }

    /// <summary>The DER-encoded OCSP responses for the signer's certificate.</summary>
    public IReadOnlyList<byte[]> OcspResponses
    {
        get => _ocspResponses;
        init => _ocspResponses = value is null ? [] : [.. value.Select(response => response.ToArray())];
    }

    /// <summary>The DER-encoded certificate revocation lists.</summary>
    public IReadOnlyList<byte[]> Crls
    {
        get => _crls;
        init => _crls = value is null ? [] : [.. value.Select(crl => crl.ToArray())];
    }

    /// <summary>Whether the data holds anything at all.</summary>
    internal bool IsEmpty => Certificates.Count == 0 && OcspResponses.Count == 0 && Crls.Count == 0;
}

/// <summary>
/// adds the document security store and its VRI entry for a signature (OF-E03, PAdES B-LT and
/// ISO 32000-1, 12.8.4.3) in an incremental update.
/// </summary>
internal static class PdfLongTermValidation
{
    /// <summary>
    /// appends the store to the signed file. The VRI entry is keyed by the SHA-1 of the signature
    /// contents, which is what a validator computes.
    /// </summary>
    /// <param name="file">the signed file, loaded for reading.</param>
    /// <param name="data">the signed bytes (the first revision).</param>
    /// <param name="dssObject">the object number of the document security store, or null.</param>
    /// <param name="signatureContents">the DER of the signature (the value of /Contents).</param>
    /// <param name="validation">the validation material.</param>
    public static byte[] Append(
        PdfFile file,
        byte[] data,
        int? dssObject,
        byte[] signatureContents,
        PdfValidationData validation)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(signatureContents);

        var update = new PdfIncrementalUpdate(file, data);

        var certificates = validation.Certificates
            .Select(certificate => WriteStream(update, certificate.RawData))
            .ToList();
        var ocsp = validation.OcspResponses.Select(response => WriteStream(update, response)).ToList();
        var crls = validation.Crls.Select(crl => WriteStream(update, crl)).ToList();

        if (validation.Certificates.Count + validation.OcspResponses.Count + validation.Crls.Count == 0)
        {
            return data;
        }

        // The key of the VRI entry: the SHA-1 of the signature contents in upper-case hex.
        var key = Convert.ToHexString(SHA1.HashData(signatureContents));

        var store = dssObject ?? update.Reserve();
        update.WriteObject(store, tokens => tokens.WriteDictionary(dss =>
        {
            dss.WriteName("Type");
            dss.WriteName("DSS");
            WriteReferences(dss, "Certs", certificates);
            WriteReferences(dss, "OCSPs", ocsp);
            WriteReferences(dss, "CRLs", crls);
            dss.WriteName("VRI");
            dss.WriteDictionary(vri =>
            {
                vri.WriteName(key);
                vri.WriteDictionary(entry =>
                {
                    WriteReferences(entry, "Cert", certificates);
                    WriteReferences(entry, "OCSP", ocsp);
                    WriteReferences(entry, "CRL", crls);
                });
            });
        }));

        // The catalog of the first revision has to name the store when it is new.
        if (dssObject is null)
        {
            var catalogNumber = ((PdfReference)file.Trailer.Get("Root")!).ObjectNumber;
            var catalog = file.Dictionary(file.Trailer.Get("Root"))
                ?? throw new DocumentValidationException("PDF catalog is missing.");
            update.WriteObject(catalogNumber, tokens => tokens.WriteDictionary(rewritten =>
            {
                foreach (var (name, value) in catalog.Entries.Where(entry => entry.Key != "DSS"))
                {
                    rewritten.WriteName(name);
                    PdfPageOperations.WriteValue(rewritten, value);
                }

                rewritten.WriteName("DSS");
                rewritten.WriteReference(store);
            }));
        }

        return update.Complete();
    }

    private static int WriteStream(PdfIncrementalUpdate update, byte[] content)
    {
        var number = update.Reserve();
        update.WriteStreamObject(number, content);
        return number;
    }

    private static void WriteReferences(PdfTokenWriter tokens, string name, IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0)
        {
            return;
        }

        tokens.WriteName(name);
        tokens.WriteArray(array =>
        {
            foreach (var number in numbers)
            {
                array.WriteReference(number);
            }
        });
    }
}
