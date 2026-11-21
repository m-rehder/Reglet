using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>The encryption parameters of one ODF file entry (part of the manifest).</summary>
internal sealed record OdtEncryptionInfo(
    string Checksum,
    string ChecksumType,
    string Iv,
    string Salt,
    int Iterations,
    int KeySize,
    int StartKeySize,
    string Algorithm,
    string KeyDerivation,
    string StartKeyGeneration);

/// <summary>An encrypted ODF part with the parameters the manifest has to carry.</summary>
internal sealed record OdtEncryptedPart(byte[] Cipher, long PlainSize, OdtEncryptionInfo Info);

/// <summary>
/// ODF 1.3 package encryption (OF-E05): per-file AES-256-CBC with a PBKDF2-derived key, a
/// SHA-256 start key and a SHA-256/1K checksum over the compressed plaintext. The format follows
/// ODF 1.3 part 2, section 3.4 and the manifest elements in section 4.
/// </summary>
internal static class OdtEncryption
{
    public const string Aes256Cbc = "http://www.w3.org/2001/04/xmlenc#aes256-cbc";
    public const string Aes256CbcManifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#aes256-cbc";
    public const string Pbkdf2 = "PBKDF2";
    public const string Pbkdf2Uri = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#pbkdf2";
    public const string Sha256 = "http://www.w3.org/2000/09/xmldsig#sha256";
    public const string Sha1 = "http://www.w3.org/2000/09/xmldsig#sha1";
    public const string Sha256Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256";
    public const string Sha1Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha1";
    public const string Sha256Checksum = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha256-1k";
    public const string Sha1Checksum = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0#sha1-1k";

    private static readonly XNamespace Manifest = OdtNamespaces.Manifest;

    /// <summary>Encrypts one part and returns the ciphertext with its manifest parameters.</summary>
    public static OdtEncryptedPart Encrypt(byte[] plain, OdtEncryptionOptions options)
    {
        var compressed = Compress(plain);
        var checksum = Convert.ToBase64String(
            SHA256.HashData(compressed.AsSpan(0, Math.Min(1024, compressed.Length))));
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        var startKey = StartKey(options.Password, Sha256);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            startKey, salt, options.IterationCount, HashAlgorithmName.SHA1, 32);
        var cipher = Transform(cipherMode: true, compressed, key, iv);
        var info = new OdtEncryptionInfo(
            checksum,
            Sha256Checksum,
            Convert.ToBase64String(iv),
            Convert.ToBase64String(salt),
            options.IterationCount,
            32,
            startKey.Length,
            Aes256Cbc,
            Pbkdf2,
            Sha256);
        return new OdtEncryptedPart(cipher, plain.Length, info);
    }

    /// <summary>Decrypts one part and verifies its checksum.</summary>
    /// <exception cref="DocumentValidationException">The parameters are unsupported or the password is wrong.</exception>
    public static byte[] Decrypt(byte[] cipher, XElement encryptionData, string password)
    {
        try
        {
            var info = ParseInfo(encryptionData);
            if (info.Algorithm is not (Aes256Cbc or Aes256CbcManifest))
            {
                throw new DocumentValidationException($"ODT encryption algorithm '{info.Algorithm}' is not supported.");
            }

            if (info.KeyDerivation is not (Pbkdf2 or Pbkdf2Uri))
            {
                throw new DocumentValidationException($"ODT key derivation '{info.KeyDerivation}' is not supported.");
            }

            var startKey = StartKey(password, info.StartKeyGeneration);
            var salt = Convert.FromBase64String(info.Salt);
            var iv = Convert.FromBase64String(info.Iv);
            if (info.StartKeySize > 0 && startKey.Length > info.StartKeySize)
            {
                startKey = startKey[..info.StartKeySize];
            }

            var key = Rfc2898DeriveBytes.Pbkdf2(
                startKey, salt, info.Iterations, HashAlgorithmName.SHA1, info.KeySize);
            var compressed = Transform(cipherMode: false, cipher, key, iv);
            var expected = Convert.FromBase64String(info.Checksum);
            var actual = info.ChecksumType switch
            {
                Sha256Checksum => SHA256.HashData(compressed.AsSpan(0, Math.Min(1024, compressed.Length))),
                Sha1Checksum or "SHA1/1K" => SHA1.HashData(compressed.AsSpan(0, Math.Min(1024, compressed.Length))),
                _ => throw new DocumentValidationException(
                    $"ODT checksum type '{info.ChecksumType}' is not supported."),
            };
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new DocumentValidationException("The ODT password is wrong or the file is damaged.");
            }

            return Decompress(compressed);
        }
        catch (FormatException exception)
        {
            throw new DocumentValidationException("The ODT encryption data is malformed.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new DocumentValidationException("The ODT password is wrong or the file is damaged.", exception);
        }
    }

    /// <summary>
    /// Returns the decrypted package, or <c>null</c> when the package is not encrypted. The
    /// manifest is rewritten without encryption data, so the reader and the preserved source see
    /// a plain ODF package.
    /// </summary>
    /// <exception cref="DocumentValidationException">A password is required or wrong.</exception>
    public static byte[]? TryDecryptPackage(ReadOnlyMemory<byte> source, string? password)
    {
        using var archive = new ZipArchive(new MemoryStream(source.ToArray(), writable: false), ZipArchiveMode.Read);
        if (archive.GetEntry("META-INF/manifest.xml") is not { } manifestEntry)
        {
            return null;
        }

        XDocument manifest;
        using (var stream = ReaderGuard.OpenBounded(manifestEntry))
        {
            manifest = ReaderGuard.LoadXml(stream);
        }

        var encrypted = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var entry in manifest.Descendants(Manifest + "file-entry"))
        {
            if ((string?)entry.Attribute(Manifest + "full-path") is { } name
                && entry.Element(Manifest + "encryption-data") is { } data)
            {
                encrypted[name] = data;
            }
        }

        if (encrypted.Count == 0)
        {
            return null;
        }

        // The manifest sets the PBKDF2 cost of every entry: bound the total before deriving any key.
        if (encrypted.Values.Sum(data => (long)ParseInfo(data).Iterations) > MaxTotalIterations)
        {
            throw new DocumentValidationException($"The ODT key derivation exceeds {MaxTotalIterations} iterations in total.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new DocumentValidationException("The ODT file is encrypted; a password is required.");
        }

        foreach (var entry in manifest.Descendants(Manifest + "file-entry"))
        {
            entry.Elements(Manifest + "encryption-data").Remove();
            entry.Attribute(Manifest + "size")?.Remove();
        }

        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (archive.GetEntry("mimetype") is { } mime)
            {
                WriteEntry(target, "mimetype", ReadEntry(mime), CompressionLevel.NoCompression);
            }

            using (var manifestStream = new MemoryStream())
            {
                manifest.Save(manifestStream, SaveOptions.DisableFormatting);
                WriteEntry(target, "META-INF/manifest.xml", manifestStream.ToArray(), CompressionLevel.Optimal);
            }

            var decoded = 0L;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName is "mimetype" or "META-INF/manifest.xml")
                {
                    continue;
                }

                var data = encrypted.TryGetValue(entry.FullName, out var info)
                    ? Decrypt(ReadEntry(entry), info, password)
                    : ReadEntry(entry);
                decoded += data.Length;
                ReaderGuard.CheckDecodedLength(decoded, "The decrypted ODT package");
                WriteEntry(target, entry.FullName, data, CompressionLevel.Optimal);
            }
        }

        return output.ToArray();
    }

    /// <summary>The highest PBKDF2 iteration count of one entry (LibreOffice writes 100,000).</summary>
    public const int MaxIterations = 1_000_000;

    /// <summary>The highest iteration count over all entries of a package (50 entries at the maximum).</summary>
    public const long MaxTotalIterations = 50_000_000;

    internal static OdtEncryptionInfo ParseInfo(XElement data)
    {
        var algorithm = data.Element(Manifest + "algorithm");
        var derivation = data.Element(Manifest + "key-derivation");
        var startKey = data.Element(Manifest + "start-key-generation");

        // Both values come from the file and size the work: the iterations the CPU time, the key size an allocation.
        var iterations = (int?)derivation?.Attribute(Manifest + "iteration-count") ?? 1024;
        if (iterations is < 1 or > MaxIterations)
        {
            throw new DocumentValidationException($"The ODT iteration count must be between 1 and {MaxIterations}.");
        }

        var keySize = (int?)derivation?.Attribute(Manifest + "key-size") ?? 32;
        if (keySize is < 1 or > 64)
        {
            throw new DocumentValidationException("The ODT key size is invalid.");
        }

        return new OdtEncryptionInfo(
            (string?)data.Attribute(Manifest + "checksum") ?? string.Empty,
            (string?)data.Attribute(Manifest + "checksum-type") ?? Sha256Checksum,
            (string?)algorithm?.Attribute(Manifest + "initialisation-vector") ?? string.Empty,
            (string?)derivation?.Attribute(Manifest + "salt") ?? string.Empty,
            iterations,
            keySize,
            (int?)startKey?.Attribute(Manifest + "key-size") ?? 0,
            (string?)algorithm?.Attribute(Manifest + "algorithm-name") ?? Aes256Cbc,
            (string?)derivation?.Attribute(Manifest + "key-derivation-name") ?? Pbkdf2,
            (string?)startKey?.Attribute(Manifest + "start-key-generation-name") ?? Sha256);
    }

    private static byte[] StartKey(string password, string algorithm)
        => algorithm switch
        {
            Sha256 or Sha256Manifest => SHA256.HashData(Encoding.UTF8.GetBytes(password)),
            Sha1 or Sha1Manifest => SHA1.HashData(Encoding.UTF8.GetBytes(password)),
            _ => throw new DocumentValidationException($"ODT start-key algorithm '{algorithm}' is not supported."),
        };

    private static byte[] Transform(bool cipherMode, byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var transform = cipherMode ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    // A decrypted part is inflated like any package part: bounded, and damaged data is a validation error.
    private static byte[] Decompress(byte[] data)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            int read;
            while ((read = deflate.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                ReaderGuard.CheckDecodedLength(output.Length, "A decrypted ODT part");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new DocumentValidationException("A decrypted ODT part is damaged.", exception);
        }

        return output.ToArray();
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry) => ReaderGuard.ReadBoundedBytes(entry, ReaderGuard.MaxDecodedBytes);

    private static void WriteEntry(ZipArchive archive, string name, byte[] data, CompressionLevel level)
    {
        var entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
