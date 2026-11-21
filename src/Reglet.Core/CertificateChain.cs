using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>The certificate chain check of all signature readers: a signer chains to exactly the trusted roots.</summary>
internal static class CertificateChain
{
    /// <summary>
    /// Builds the chain of <paramref name="certificate"/> against <paramref name="roots"/>, with the certificates the
    /// signature carries as intermediates, at <paramref name="time"/> or now. The reasons of a failure are added to
    /// <paramref name="problems"/>.
    /// </summary>
    public static bool Check(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2> carried,
        IReadOnlyList<X509Certificate2> roots,
        X509RevocationMode revocationMode,
        DateTimeOffset? time,
        List<string> problems)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = revocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots.ToArray());
        if (time is { } verificationTime)
        {
            chain.ChainPolicy.VerificationTime = verificationTime.UtcDateTime;
        }

        foreach (var other in carried)
        {
            if (!chain.ChainPolicy.ExtraStore.Contains(other))
            {
                chain.ChainPolicy.ExtraStore.Add(other);
            }
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
