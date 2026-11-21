using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// the ECMA-376 agile encryption of a DOCX package (OF-E01): the package is wrapped in a compound
/// file whose <c>EncryptionInfo</c> stream holds the XML descriptor and whose
/// <c>EncryptedPackage</c> stream holds the AES-256-CBC encrypted package in 4 KB segments
/// (MS-OFFCRYPTO 2.3.4.10–2.3.4.15, hard-wired to AES-256/SHA-512 as Word writes it).
/// </summary>
internal static class DocxAgileEncryption
{
    private const int SegmentLength = 4096;
    private const int SaltSize = 16;
    private const int BlockSize = 16;
    private const int KeyBits = 256;
    private const int HashSize = 64;

    /// <summary>The highest spin count MS-OFFCRYPTO allows (2.3.4.11); the file sets it, so it must be bounded.</summary>
    public const int MaxSpinCount = 10_000_000;
    private const string CipherAlgorithm = "AES";
    private const string HashAlgorithm = "SHA512";

    private static ReadOnlySpan<byte> VerifierHashInputBlockKey => [0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79];
    private static ReadOnlySpan<byte> VerifierHashValueBlockKey => [0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E];
    private static ReadOnlySpan<byte> KeyValueBlockKey => [0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6];
    private static ReadOnlySpan<byte> DataIntegrityKeyBlockKey => [0x5F, 0xB2, 0xAD, 0x01, 0x0C, 0xB9, 0xE1, 0xF6];
    private static ReadOnlySpan<byte> DataIntegrityValueBlockKey => [0xA0, 0x67, 0x7F, 0x02, 0xB2, 0x2C, 0x84, 0x33];

    private static readonly XNamespace Encryption = "http://schemas.microsoft.com/office/2006/encryption";
    private static readonly XNamespace Password = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

    /// <summary>
    /// encrypts the plaintext package and writes the compound file to <paramref name="output"/>.
    /// </summary>
    public static void Encrypt(
        ReadOnlySpan<byte> package,
        DocxEncryptionOptions options,
        Stream output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        cancellationToken.ThrowIfCancellationRequested();

        var passwordSalt = RandomNumberGenerator.GetBytes(SaltSize);
        var keySalt = RandomNumberGenerator.GetBytes(SaltSize);
        var iterated = IteratePassword(
            Encoding.Unicode.GetBytes(options.Password), passwordSalt, options.SpinCount);

        // The verifier proves the password: a random input, its hash and the key itself are
        // encrypted with keys derived from the iterated hash.
        var verifierInput = RandomNumberGenerator.GetBytes(SaltSize);
        var encryptedVerifierInput = EncryptAes(verifierInput, DeriveKey(iterated, VerifierHashInputBlockKey), passwordSalt);
        var verifierHash = SHA512.HashData(verifierInput);
        var encryptedVerifierHash = EncryptAes(verifierHash, DeriveKey(iterated, VerifierHashValueBlockKey), passwordSalt);

        // The intermediate key encrypts the package; it is random and normalised with 0x36.
        var secretKey = Normalize(RandomNumberGenerator.GetBytes(SaltSize), KeyBits / 8);
        var encryptedKeyValue = EncryptAes(secretKey, DeriveKey(iterated, KeyValueBlockKey), passwordSalt);

        // The package: an 8-byte size, then segments of 4 KB, each with its own IV.
        using var payload = new MemoryStream(package.Length + 8);
        payload.Write(BitConverter.GetBytes((long)package.Length));
        for (var index = 0; index * SegmentLength < Math.Max(1, package.Length); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index * SegmentLength;
            var count = Math.Min(SegmentLength, package.Length - offset);
            var segment = package.Slice(offset, count).ToArray();
            payload.Write(EncryptSegment(segment, secretKey, keySalt, index));
        }

        var encryptedPayload = payload.ToArray();

        // The integrity values: the HMAC key and the HMAC of the encrypted package.
        var hmacKey = RandomNumberGenerator.GetBytes(HashSize);
        var encryptedHmacKey = EncryptAes(hmacKey, secretKey, SegmentIv(keySalt, DataIntegrityKeyBlockKey));
        var hmacValue = HMACSHA512.HashData(hmacKey, encryptedPayload);
        var encryptedHmacValue = EncryptAes(hmacValue, secretKey, SegmentIv(keySalt, DataIntegrityValueBlockKey));

        var info = EncryptionInfo(package.Length, options, passwordSalt, keySalt,
            encryptedVerifierInput, encryptedVerifierHash, encryptedKeyValue, encryptedHmacKey, encryptedHmacValue);

        CompoundFile.Write([("EncryptionInfo", info), ("EncryptedPackage", encryptedPayload)], output);
    }

    /// <summary>
    /// decrypts the compound file and returns the plaintext package. A wrong password and a
    /// modified payload are rejected.
    /// </summary>
    /// <exception cref="DocumentValidationException">The container, the password or the integrity check failed.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> compoundFile, string password)
    {
        var info = CompoundFile.Read(compoundFile, "EncryptionInfo")
            ?? throw new DocumentValidationException("The encrypted DOCX has no encryption information.");
        var encryptedPayload = CompoundFile.Read(compoundFile, "EncryptedPackage")
            ?? throw new DocumentValidationException("The encrypted DOCX has no encrypted package.");

        // The stream starts with version 4.4 (two 16-bit numbers) and the flags (8 bytes in
        // total); the XML descriptor follows.
        if (info.Length < 9 || BitConverter.ToUInt16(info, 0) != 4 || BitConverter.ToUInt16(info, 2) != 4
            || BitConverter.ToUInt32(info, 4) != 0x40)
        {
            throw new DocumentValidationException(
                "The DOCX uses an encryption format other than ECMA-376 agile encryption.");
        }

        var descriptor = ParseDescriptor(Encoding.UTF8.GetString(info, 8, info.Length - 8));
        var iterated = IteratePassword(Encoding.Unicode.GetBytes(password), descriptor.PasswordSalt, descriptor.SpinCount);

        // The verifier: the hash of the decrypted input must equal the decrypted hash value.
        var verifierInput = DecryptAes(descriptor.EncryptedVerifierHashInput, DeriveKey(iterated, VerifierHashInputBlockKey), descriptor.PasswordSalt);
        var expectedHash = DecryptAes(descriptor.EncryptedVerifierHashValue, DeriveKey(iterated, VerifierHashValueBlockKey), descriptor.PasswordSalt);
        if (!SHA512.HashData(verifierInput).AsSpan().SequenceEqual(expectedHash.AsSpan(0, HashSize)))
        {
            throw new DocumentValidationException("The password of the encrypted DOCX is wrong.");
        }

        var secretKey = DecryptAes(descriptor.EncryptedKeyValue, DeriveKey(iterated, KeyValueBlockKey), descriptor.PasswordSalt);

        var totalSize = BitConverter.ToInt64(encryptedPayload, 0);
        if (totalSize < 0 || totalSize > int.MaxValue)
        {
            throw new DocumentValidationException("The encrypted DOCX states an invalid package size.");
        }

        var package = new byte[(int)totalSize];
        var written = 0;
        var offset = 8;
        var segment = 0;
        while (written < totalSize && offset < encryptedPayload.Length)
        {
            var count = Math.Min(SegmentLength, encryptedPayload.Length - offset);
            var decrypted = DecryptAes(encryptedPayload.AsSpan(offset, count).ToArray(), secretKey, SegmentIv(descriptor.KeySalt, segment));
            var take = (int)Math.Min(decrypted.Length, totalSize - written);
            Array.Copy(decrypted, 0, package, written, take);
            written += take;
            offset += count;
            segment++;
        }

        if (written != totalSize)
        {
            throw new DocumentValidationException("The encrypted DOCX package is truncated.");
        }

        // Integrity: the HMAC of the encrypted payload must match the stored value.
        var hmacKey = DecryptAes(descriptor.EncryptedHmacKey, secretKey, SegmentIv(descriptor.KeySalt, DataIntegrityKeyBlockKey));
        var hmacValue = DecryptAes(descriptor.EncryptedHmacValue, secretKey, SegmentIv(descriptor.KeySalt, DataIntegrityValueBlockKey));
        if (!HMACSHA512.HashData(hmacKey, encryptedPayload).AsSpan().SequenceEqual(hmacValue.AsSpan(0, HashSize)))
        {
            throw new DocumentValidationException("The encrypted DOCX was modified (the integrity check failed).");
        }

        return package;
    }

    /// <summary>Whether the data is a compound file that may hold an encrypted DOCX.</summary>
    public static bool IsEncrypted(ReadOnlySpan<byte> data) => CompoundFile.IsCompoundFile(data);

    internal sealed record Descriptor(
        int SpinCount,
        byte[] PasswordSalt,
        byte[] KeySalt,
        byte[] EncryptedVerifierHashInput,
        byte[] EncryptedVerifierHashValue,
        byte[] EncryptedKeyValue,
        byte[] EncryptedHmacKey,
        byte[] EncryptedHmacValue);

    internal static Descriptor ParseDescriptor(string xml)
    {
        XDocument document;
        try
        {
            document = ReaderGuard.ParseXml(xml);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new DocumentValidationException("The encryption information of the DOCX is not valid XML.", exception);
        }

        var keyData = document.Descendants(Encryption + "keyData").FirstOrDefault()
            ?? throw new DocumentValidationException("The encryption information has no key data.");
        var encryptedKey = document.Descendants(Password + "encryptedKey").FirstOrDefault()
            ?? throw new DocumentValidationException("The encryption information has no password key.");
        var integrity = document.Descendants(Encryption + "dataIntegrity").FirstOrDefault()
            ?? throw new DocumentValidationException("The encryption information has no integrity data.");

        if ((string?)keyData.Attribute("cipherAlgorithm") != CipherAlgorithm
            || (string?)keyData.Attribute("hashAlgorithm") != HashAlgorithm
            || (string?)keyData.Attribute("keyBits") != KeyBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new DocumentValidationException(
                "The DOCX is encrypted with an algorithm other than AES-256/SHA-512; only the agile default is supported.");
        }

        return new Descriptor(
            SpinCount: int.TryParse((string?)encryptedKey.Attribute("spinCount"), out var spin) && spin >= 0
                ? spin <= MaxSpinCount ? spin : throw new DocumentValidationException($"The DOCX spin count exceeds {MaxSpinCount}.")
                : 100000,
            PasswordSalt: Base64Value(encryptedKey, "saltValue"),
            KeySalt: Base64Value(keyData, "saltValue"),
            EncryptedVerifierHashInput: Base64Value(encryptedKey, "encryptedVerifierHashInput"),
            EncryptedVerifierHashValue: Base64Value(encryptedKey, "encryptedVerifierHashValue"),
            EncryptedKeyValue: Base64Value(encryptedKey, "encryptedKeyValue"),
            EncryptedHmacKey: Base64Value(integrity, "encryptedHmacKey"),
            EncryptedHmacValue: Base64Value(integrity, "encryptedHmacValue"));
    }

    private static byte[] Base64Value(XElement element, string name)
    {
        var text = (string?)element.Attribute(name)
            ?? throw new DocumentValidationException($"The encryption information has no '{name}'.");
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException exception)
        {
            throw new DocumentValidationException($"The encryption information has an invalid '{name}'.", exception);
        }
    }

    /// <summary>the iterated SHA-512 hash of the password (MS-OFFCRYPTO 2.3.4.11).</summary>
    private static byte[] IteratePassword(byte[] password, byte[] salt, int spinCount)
    {
        var hash = SHA512.HashData([.. salt, .. password]);
        var buffer = new byte[4 + hash.Length];
        hash.CopyTo(buffer, 4);
        for (var index = 0; index < spinCount; index++)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), index);
            hash = SHA512.HashData(buffer);
            hash.CopyTo(buffer, 4);
        }

        return hash;
    }

    /// <summary>the key of a block key: the iterated hash plus the block key, truncated.</summary>
    private static byte[] DeriveKey(byte[] iterated, ReadOnlySpan<byte> blockKey)
        => SHA512.HashData([.. iterated, .. blockKey])[..(KeyBits / 8)];

    /// <summary>the IV of a segment or of an integrity value: the hash of the salt and the block.</summary>
    private static byte[] SegmentIv(byte[] salt, int segment)
        => SHA512.HashData([.. salt, .. BitConverter.GetBytes(segment)])[..BlockSize];

    private static byte[] SegmentIv(byte[] salt, ReadOnlySpan<byte> blockKey)
        => SHA512.HashData([.. salt, .. blockKey])[..BlockSize];

    private static byte[] EncryptSegment(byte[] segment, byte[] key, byte[] salt, int index)
    {
        var padded = Normalize(segment, (segment.Length + BlockSize - 1) / BlockSize * BlockSize);
        return EncryptAes(padded, key, SegmentIv(salt, index));
    }

    /// <summary>AES-256-CBC without padding; the caller pads the data with zeros.</summary>
    private static byte[] EncryptAes(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>AES-256-CBC without padding; the caller trims the data.</summary>
    private static byte[] DecryptAes(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>Grows the data to the given length, filling with <paramref name="fill"/>.</summary>
    private static byte[] Normalize(byte[] data, int length, byte fill = 0x36)
    {
        if (data.Length == length)
        {
            return data;
        }

        var result = new byte[length];
        Array.Fill(result, fill);
        Array.Copy(data, result, Math.Min(data.Length, length));
        return result;
    }

    private static byte[] EncryptionInfo(
        int packageLength,
        DocxEncryptionOptions options,
        byte[] passwordSalt,
        byte[] keySalt,
        byte[] encryptedVerifierInput,
        byte[] encryptedVerifierHash,
        byte[] encryptedKeyValue,
        byte[] encryptedHmacKey,
        byte[] encryptedHmacValue)
    {
        var xml = new XDocument(
            new XElement(
                Encryption + "encryption",
                new XAttribute(XNamespace.Xmlns + "p", Password.NamespaceName),
                new XElement(
                    Encryption + "keyData",
                    new XAttribute("saltSize", SaltSize),
                    new XAttribute("blockSize", BlockSize),
                    new XAttribute("keyBits", KeyBits),
                    new XAttribute("hashSize", HashSize),
                    new XAttribute("cipherAlgorithm", CipherAlgorithm),
                    new XAttribute("cipherChaining", "ChainingModeCBC"),
                    new XAttribute("hashAlgorithm", HashAlgorithm),
                    new XAttribute("saltValue", Convert.ToBase64String(keySalt))),
                new XElement(
                    Encryption + "dataIntegrity",
                    new XAttribute("encryptedHmacKey", Convert.ToBase64String(encryptedHmacKey)),
                    new XAttribute("encryptedHmacValue", Convert.ToBase64String(encryptedHmacValue))),
                new XElement(
                    Encryption + "keyEncryptors",
                    new XElement(
                        Encryption + "keyEncryptor",
                        new XAttribute("uri", Password.NamespaceName),
                        new XElement(
                            Password + "encryptedKey",
                            new XAttribute("spinCount", options.SpinCount),
                            new XAttribute("saltSize", SaltSize),
                            new XAttribute("blockSize", BlockSize),
                            new XAttribute("keyBits", KeyBits),
                            new XAttribute("hashSize", HashSize),
                            new XAttribute("cipherAlgorithm", CipherAlgorithm),
                            new XAttribute("cipherChaining", "ChainingModeCBC"),
                            new XAttribute("hashAlgorithm", HashAlgorithm),
                            new XAttribute("saltValue", Convert.ToBase64String(passwordSalt)),
                            new XAttribute("encryptedVerifierHashInput", Convert.ToBase64String(encryptedVerifierInput)),
                            new XAttribute("encryptedVerifierHashValue", Convert.ToBase64String(encryptedVerifierHash)),
                            new XAttribute("encryptedKeyValue", Convert.ToBase64String(encryptedKeyValue)))))));

        var xmlBytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        // Version 4.4 (two 16-bit numbers) and the agile flag, 8 bytes in total (as POI and
        // msoffcrypto expect); the XML descriptor follows.
        var result = new byte[8 + xmlBytes.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 2), (ushort)4);
        BitConverter.TryWriteBytes(result.AsSpan(2, 2), (ushort)4);
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), 0x40u);
        xmlBytes.CopyTo(result, 8);
        _ = packageLength;
        return result;
    }
}
