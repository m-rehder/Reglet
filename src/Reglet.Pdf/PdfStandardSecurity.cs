using System.Security.Cryptography;
using System.Text;

namespace Reglet;

/// <summary>
/// The PDF standard security handler. <see cref="Create"/> writes revision 6 with AES-256-CBC
/// (ISO 32000-2, 7.6.4: <c>/V 5 /R 6 /CFM /AESV3</c>): a random 256-bit file key, the
/// <c>/U</c>/<c>/UE</c> and <c>/O</c>/<c>/OE</c> entries from the hardened SHA-2 hash
/// (Algorithm 2.B) and the encrypted <c>/Perms</c>. The older revisions (RC4, AES-128 with the
/// MD5-based key of Algorithm 2) are only derived for reading; RC4 is implemented here because
/// <see cref="System.Security.Cryptography"/> does not expose it.
/// </summary>
internal sealed class PdfStandardSecurity
{
    /// <summary>The 32-byte padding string of ISO 32000-1, Algorithm 2, step (a).</summary>
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>The AESV2 key-mixing suffix of Algorithm 1 (<c>"sAlT"</c>).</summary>
    private static readonly byte[] AesSalt = "sAlT"u8.ToArray();

    private readonly byte[] _key;

    private PdfStandardSecurity(byte[] key, byte[] owner, byte[] user, byte[] ownerKey, byte[] userKey, byte[] perms, int permissions)
    {
        _key = key;
        O = owner;
        U = user;
        OE = ownerKey;
        UE = userKey;
        Perms = perms;
        Permissions = permissions;
    }

    /// <summary>The 48-byte <c>/O</c> entry (hash, validation salt, key salt).</summary>
    public byte[] O { get; }

    /// <summary>The 48-byte <c>/U</c> entry (hash, validation salt, key salt).</summary>
    public byte[] U { get; }

    /// <summary>The 32-byte <c>/OE</c> entry: the file key encrypted with the owner password.</summary>
    public byte[] OE { get; }

    /// <summary>The 32-byte <c>/UE</c> entry: the file key encrypted with the user password.</summary>
    public byte[] UE { get; }

    /// <summary>The 16-byte <c>/Perms</c> entry: the permissions encrypted with the file key.</summary>
    public byte[] Perms { get; }

    /// <summary>The signed 32-bit <c>/P</c> value.</summary>
    public int Permissions { get; }

    /// <summary>Creates the handler (revision 6, AES-256) from the passwords.</summary>
    public static PdfStandardSecurity Create(
        string? userPassword,
        string? ownerPassword,
        PdfPermissions permissions)
    {
        var user = PasswordBytes(userPassword);

        // Without an owner password of its own, anyone who can open the file would have full rights.
        var owner = string.IsNullOrEmpty(ownerPassword) ? RandomNumberGenerator.GetBytes(32) : PasswordBytes(ownerPassword);
        var permissionBits = ToPermissionBits(permissions);
        var fileKey = RandomNumberGenerator.GetBytes(32);

        // Algorithm 8: /U and /UE.
        var userValidationSalt = RandomNumberGenerator.GetBytes(8);
        var userKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] userEntry = [.. HashR6(user, userValidationSalt, []), .. userValidationSalt, .. userKeySalt];
        var userKey = Aes256EncryptNoPad(HashR6(user, userKeySalt, []), fileKey);

        // Algorithm 9: /O and /OE bind the complete /U entry.
        var ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        var ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        byte[] ownerEntry = [.. HashR6(owner, ownerValidationSalt, userEntry), .. ownerValidationSalt, .. ownerKeySalt];
        var ownerKey = Aes256EncryptNoPad(HashR6(owner, ownerKeySalt, userEntry), fileKey);

        // Algorithm 10: /Perms = AES-256-ECB(P (4 bytes LE), 0xFFFFFFFF, 'T', "adb", 4 random bytes).
        var perms = new byte[16];
        WriteLittleEndian(permissionBits, perms.AsSpan(0, 4));
        perms.AsSpan(4, 4).Fill(0xFF);
        perms[8] = (byte)'T';
        perms[9] = (byte)'a';
        perms[10] = (byte)'d';
        perms[11] = (byte)'b';
        RandomNumberGenerator.Fill(perms.AsSpan(12, 4));
        using (var aes = Aes.Create())
        {
            aes.Key = fileKey;
            perms = aes.EncryptEcb(perms, PaddingMode.None);
        }

        return new PdfStandardSecurity(fileKey, ownerEntry, userEntry, ownerKey, userKey, perms, permissionBits);
    }

    /// <summary>
    /// The password as UTF-8, at most 127 bytes (ISO 32000-2, 7.6.4.3.2), after the mapping and
    /// normalization of SASLprep (RFC 4013) so that it matches other producers.
    /// </summary>
    /// <exception cref="DocumentValidationException">The password is not valid Unicode.</exception>
    internal static byte[] PasswordBytes(string? password)
    {
        var mapped = new StringBuilder();
        foreach (var character in password ?? string.Empty)
        {
            if (!IsMappedToNothing(character))
            {
                mapped.Append(IsNonAsciiSpace(character) ? ' ' : character);
            }
        }

        string normalized;
        try
        {
            normalized = mapped.ToString().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException exception)
        {
            throw new DocumentValidationException("The PDF password is not valid Unicode.", exception);
        }

        var bytes = Encoding.UTF8.GetBytes(normalized);
        return bytes.Length > 127 ? bytes[..127] : bytes;

        // RFC 3454, tables B.1 and C.1.2.
        static bool IsMappedToNothing(char c)
            => c is '\u00AD' or '\u034F' or '\u1806' or (>= '\u180B' and <= '\u180D') or (>= '\u200B' and <= '\u200D')
                or '\u2060' or (>= '\uFE00' and <= '\uFE0F') or '\uFEFF';

        static bool IsNonAsciiSpace(char c)
            => c is '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A') or '\u202F' or '\u205F' or '\u3000';
    }

    /// <summary>Algorithm 2.B (R6): the hardened hash of a password with salt and user vector.</summary>
    internal static byte[] HashR6(byte[] password, byte[] salt, byte[] userVector)
    {
        byte[] input = [.. password, .. salt, .. userVector];
        return Algorithm2B(SHA256.HashData(input), password, userVector);
    }

    /// <summary>Algorithm 2.B (R6): iterated SHA-256/384/512 with AES-128-CBC, at least 64 rounds.</summary>
    internal static byte[] Algorithm2B(byte[] initial, byte[] password, byte[] userVector)
    {
        var key = initial;
        var round = 0;
        byte[] encrypted;
        do
        {
            var k1 = new byte[64 * (password.Length + key.Length + userVector.Length)];
            var position = 0;
            for (var repetition = 0; repetition < 64; repetition++)
            {
                password.CopyTo(k1, position);
                position += password.Length;
                key.CopyTo(k1, position);
                position += key.Length;
                userVector.CopyTo(k1, position);
                position += userVector.Length;
            }

            using var aes = Aes.Create();
            aes.Key = key.AsSpan(0, 16).ToArray();
            encrypted = aes.EncryptCbc(k1, key.AsSpan(16, 16).ToArray(), PaddingMode.None);

            var selector = encrypted.AsSpan(0, 16).ToArray().Sum(value => (int)value) % 3;
            key = selector switch
            {
                0 => SHA256.HashData(encrypted),
                1 => SHA384.HashData(encrypted),
                _ => SHA512.HashData(encrypted),
            };
            round++;
        }
        while (round < 64 || encrypted[^1] > round - 32);

        return key[..32];
    }

    private static byte[] Aes256EncryptNoPad(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }

    /// <summary>Encrypts a string or stream: AES-256-CBC with the file key and a random IV (AESV3).</summary>
    public byte[] Encrypt(byte[] data, int objectNumber)
    {
        ArgumentNullException.ThrowIfNull(data);

        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = _key;

        var cipher = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
        var result = new byte[16 + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, 16);
        return result;
    }

    /// <summary>Maps the flags onto the bits of the <c>/P</c> entry (reserved bits stay set).</summary>
    internal static int ToPermissionBits(PdfPermissions permissions)
    {
        // Bits 3-32 are set (all allowed); bits 1-2 are reserved and stay 0.
        var value = unchecked((int)0xFFFFFFFC);
        Clear(PdfPermissions.Print, 1 << 2);
        Clear(PdfPermissions.ModifyContents, 1 << 3);
        Clear(PdfPermissions.Copy, 1 << 4);
        Clear(PdfPermissions.Annotate, 1 << 5);
        Clear(PdfPermissions.FillForms, 1 << 8);
        Clear(PdfPermissions.ExtractForAccessibility, 1 << 9);
        Clear(PdfPermissions.Assemble, 1 << 10);
        Clear(PdfPermissions.PrintHighQuality, 1 << 11);
        return value;

        void Clear(PdfPermissions permission, int bit)
        {
            if (!permissions.HasFlag(permission))
            {
                value &= ~bit;
            }
        }
    }

    /// <summary>Algorithm 2 (step a): pads or truncates a password to the 32-byte padding string.</summary>
    internal static byte[] PadPassword(string? password)
    {
        // R2-R4 passwords are PDFDocEncoded; a password outside that encoding cannot match.
        var bytes = PdfTextString.EncodePdfDoc(password ?? string.Empty)
            ?? throw new DocumentValidationException(PdfDecryptor.WrongPassword);
        var padded = new byte[32];
        var count = Math.Min(bytes.Length, 32);
        bytes.AsSpan(0, count).CopyTo(padded);
        Padding.AsSpan(0, 32 - count).CopyTo(padded.AsSpan(count));
        return padded;
    }

    /// <summary>
    /// Algorithm 3: computes the <c>/O</c> entry. Unlike the file key (Algorithm 2), the 50
    /// extra MD5 rounds hash the full digest; only the final result is truncated to n bytes.
    /// </summary>
    internal static byte[] ComputeOwnerEntry(string ownerPassword, string userPassword, int revision, int keyLength)
    {
        var digest = MD5.HashData(PadPassword(ownerPassword));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                digest = MD5.HashData(digest);
            }
        }

        var rc4Key = digest[..keyLength];
        var result = Rc4(rc4Key, PadPassword(userPassword));
        if (revision >= 3)
        {
            for (var i = 1; i <= 19; i++)
            {
                result = Rc4(XorKey(rc4Key, (byte)i), result);
            }
        }

        return result;
    }

    /// <summary>Algorithm 2: derives the file encryption key from the padded user password.</summary>
    internal static byte[] ComputeEncryptionKey(
        byte[] paddedUserPassword,
        byte[] ownerEntry,
        int permissions,
        byte[] id,
        int revision,
        int keyLength,
        bool encryptMetadata = true)
    {
        // Step (e): for R>=4 with /EncryptMetadata false, append 0xFFFFFFFF.
        var extra = revision >= 4 && !encryptMetadata ? 4 : 0;
        var input = new byte[paddedUserPassword.Length + ownerEntry.Length + 4 + id.Length + extra];
        paddedUserPassword.CopyTo(input, 0);
        ownerEntry.CopyTo(input, paddedUserPassword.Length);
        WriteLittleEndian(permissions, input.AsSpan(paddedUserPassword.Length + ownerEntry.Length, 4));
        id.CopyTo(input, paddedUserPassword.Length + ownerEntry.Length + 4);
        if (extra == 4)
        {
            input.AsSpan(input.Length - 4, 4).Fill(0xFF);
        }

        var digest = MD5.HashData(input);
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                digest = MD5.HashData(digest.AsSpan(0, keyLength));
            }
        }

        return digest[..keyLength];
    }

    /// <summary>Algorithms 4 and 5: computes the <c>/U</c> entry (always 32 bytes).</summary>
    internal static byte[] ComputeUserEntry(byte[] key, byte[] id, int revision)
    {
        var result = new byte[32];
        if (revision == 2)
        {
            Rc4(key, Padding).CopyTo(result, 0);
            return result;
        }

        var input = new byte[Padding.Length + id.Length];
        Padding.CopyTo(input, 0);
        id.CopyTo(input, Padding.Length);

        var digest = Rc4(key, MD5.HashData(input));
        for (var i = 1; i <= 19; i++)
        {
            digest = Rc4(XorKey(key, (byte)i), digest);
        }

        digest.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// Algorithm 1: derives the per-object key. For AES a <c>"sAlT"</c> suffix is mixed in;
    /// the result is truncated to <c>min(n+5, 16)</c> bytes either way.
    /// </summary>
    internal static byte[] ComputeObjectKey(byte[] fileKey, int objectNumber, int generation, bool aes)
    {
        var input = new byte[fileKey.Length + 5 + (aes ? 4 : 0)];
        fileKey.CopyTo(input, 0);
        input[fileKey.Length] = (byte)objectNumber;
        input[fileKey.Length + 1] = (byte)(objectNumber >> 8);
        input[fileKey.Length + 2] = (byte)(objectNumber >> 16);
        input[fileKey.Length + 3] = (byte)generation;
        input[fileKey.Length + 4] = (byte)(generation >> 8);
        if (aes)
        {
            AesSalt.CopyTo(input, fileKey.Length + 5);
        }

        var digest = MD5.HashData(input);
        return digest[..Math.Min(fileKey.Length + 5, 16)];
    }

    /// <summary>Algorithm 7: recovers the padded user password from <c>/O</c>.</summary>
    internal static byte[] RecoverUserPassword(string ownerPassword, byte[] ownerEntry, int revision, int keyLength)
    {
        var digest = MD5.HashData(PadPassword(ownerPassword));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                digest = MD5.HashData(digest);
            }
        }

        var rc4Key = digest[..keyLength];
        var result = ownerEntry[..Math.Min(ownerEntry.Length, 32)];
        if (revision == 2)
        {
            return Rc4(rc4Key, result);
        }

        for (var i = 19; i >= 1; i--)
        {
            result = Rc4(XorKey(rc4Key, (byte)i), result);
        }

        return Rc4(rc4Key, result);
    }

    /// <summary>RC4 (symmetric): the same call encrypts and decrypts.</summary>
    internal static byte[] Rc4(byte[] key, byte[] data)
    {
        var state = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var result = new byte[data.Length];
        int a = 0, b = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + state[a]) & 0xFF;
            (state[a], state[b]) = (state[b], state[a]);
            result[i] = (byte)(data[i] ^ state[(state[a] + state[b]) & 0xFF]);
        }

        return result;
    }

    internal static byte[] XorKey(byte[] key, byte value)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ value);
        }

        return result;
    }

    private static void WriteLittleEndian(int value, Span<byte> destination)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }
}

/// <summary>
/// Decrypts strings and streams of an encrypted PDF. Supports the standard security handler
/// with RC4 (R2/R3), AES-128-CBC (R4/<c>AESV2</c>) and AES-256-CBC (V5/R5/R6/<c>AESV3</c>) so that
/// files written by <see cref="PdfStandardSecurity"/> and by other producers can be read.
/// </summary>
internal sealed class PdfDecryptor
{
    internal const string WrongPassword = "The PDF is encrypted and the password is wrong or missing.";

    private enum Method
    {
        None,
        Rc4,
        Aes128,
        Aes256,
    }

    private readonly byte[] _key;
    private readonly Method _streams;
    private readonly Method _strings;
    private readonly Method _embeddedFiles;
    private readonly bool _encryptMetadata;

    private PdfDecryptor(byte[] key, PdfDictionary encrypt, int revision, bool encryptMetadata)
    {
        _key = key;
        _streams = MethodOf(encrypt, "StmF", revision);
        _strings = MethodOf(encrypt, "StrF", revision);
        _embeddedFiles = revision >= 4 && encrypt.Get("EFF") is not null ? MethodOf(encrypt, "EFF", revision) : _streams;
        _encryptMetadata = encryptMetadata;
    }

    /// <summary>The method of a crypt filter entry; an absent entry means Identity (no encryption).</summary>
    private static Method MethodOf(PdfDictionary encrypt, string entry, int revision)
    {
        if (revision < 4)
        {
            return Method.Rc4;
        }

        if (NameOf(encrypt.Get(entry)) is not { } name || name == "Identity")
        {
            return Method.None;
        }

        var filter = (encrypt.Get("CF") as PdfDictionary)?.Get(name) as PdfDictionary;
        return NameOf(filter?.Get("CFM")) switch
        {
            "None" => Method.None,
            "V2" => Method.Rc4,
            "AESV2" => Method.Aes128,
            "AESV3" => Method.Aes256,
            _ => revision >= 5 ? Method.Aes256 : Method.Rc4,
        };
    }

    /// <summary>Derives the file key and verifies the password (user first, then owner).</summary>
    /// <exception cref="UnsupportedFeatureException">The encryption is not the standard handler or a supported revision.</exception>
    /// <exception cref="DocumentValidationException">The password is wrong or missing.</exception>
    public static PdfDecryptor Open(PdfDictionary encrypt, PdfDictionary trailer, string? password)
    {
        ArgumentNullException.ThrowIfNull(encrypt);
        ArgumentNullException.ThrowIfNull(trailer);

        if (NameOf(encrypt.Get("Filter")) is not "Standard")
        {
            throw new UnsupportedFeatureException(
                "Only the PDF standard security handler (/Filter /Standard) is supported.");
        }

        var version = (int)(Integer(encrypt.Get("V")) ?? 0);
        var revision = (int)(Integer(encrypt.Get("R")) ?? 2);
        if (version >= 5 || revision >= 5)
        {
            return OpenV5(encrypt, password, revision);
        }

        if (revision is < 2 or > 4)
        {
            throw new UnsupportedFeatureException(
                $"The PDF encryption revision R{revision} is not supported (R2-R4 or V5/R5-R6).");
        }

        var owner = Bytes(encrypt.Get("O"));
        var user = Bytes(encrypt.Get("U"));
        if (owner is null || user is null || owner.Length < 32 || user.Length < 32)
        {
            throw new UnsupportedFeatureException("The PDF encryption dictionary is incomplete (/O or /U is missing).");
        }

        var permissions = (int)(Integer(encrypt.Get("P")) ?? -1);

        // AESV2 is always AES-128, whatever a /Length entry claims.
        var keyLength = revision >= 4 && UsesAes128(encrypt) ? 16 : KeyLength(encrypt, revision);
        var encryptMetadata = encrypt.Get("EncryptMetadata") is not PdfBoolean { Value: false };
        var id = FirstId(trailer);

        // Try the given password as user password, then as owner password.
        var userKey = PdfStandardSecurity.ComputeEncryptionKey(
            PdfStandardSecurity.PadPassword(password), owner, permissions, id, revision, keyLength, encryptMetadata);
        if (Verify(userKey, user, id, revision))
        {
            return new PdfDecryptor(userKey, encrypt, revision, encryptMetadata);
        }

        var recovered = PdfStandardSecurity.RecoverUserPassword(password ?? string.Empty, owner, revision, keyLength);
        var ownerKey = PdfStandardSecurity.ComputeEncryptionKey(
            recovered, owner, permissions, id, revision, keyLength, encryptMetadata);
        if (Verify(ownerKey, user, id, revision))
        {
            return new PdfDecryptor(ownerKey, encrypt, revision, encryptMetadata);
        }

        throw new DocumentValidationException(WrongPassword);
    }

    /// <summary>Decrypts a string with the method of <c>/StrF</c>.</summary>
    public byte[] DecryptString(byte[] data, int objectNumber, int generation)
        => Decrypt(_strings, data, objectNumber, generation);

    /// <summary>Decrypts stream data with the method of <c>/StmF</c> or, for an embedded file, <c>/EFF</c>.</summary>
    public byte[] DecryptStream(PdfDictionary dictionary, byte[] data, int objectNumber, int generation)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var type = NameOf(dictionary.Get("Type"));
        if (!_encryptMetadata && type == "Metadata")
        {
            return data;
        }

        return Decrypt(type == "EmbeddedFile" ? _embeddedFiles : _streams, data, objectNumber, generation);
    }

    private byte[] Decrypt(Method method, byte[] data, int objectNumber, int generation)
    {
        ArgumentNullException.ThrowIfNull(data);
        return method switch
        {
            Method.None => data,
            Method.Aes256 => AesCbcDecrypt(_key, data),
            Method.Aes128 => AesCbcDecrypt(PdfStandardSecurity.ComputeObjectKey(_key, objectNumber, generation, aes: true), data),
            _ => PdfStandardSecurity.Rc4(PdfStandardSecurity.ComputeObjectKey(_key, objectNumber, generation, aes: false), data),
        };
    }

    /// <summary>V5 (AES-256, R5/R6): the file key is stored encrypted in /UE (user) or /OE (owner).</summary>
    private static PdfDecryptor OpenV5(PdfDictionary encrypt, string? password, int revision)
    {
        if (revision is < 5 or > 6)
        {
            throw new UnsupportedFeatureException(
                $"The PDF AES-256 revision R{revision} is not supported (R5/R6).");
        }

        var owner = Bytes(encrypt.Get("O"));
        var user = Bytes(encrypt.Get("U"));
        var ownerEncrypted = Bytes(encrypt.Get("OE"));
        var userEncrypted = Bytes(encrypt.Get("UE"));
        if (owner is null || user is null || ownerEncrypted is null || userEncrypted is null
            || owner.Length < 48 || user.Length < 48 || ownerEncrypted.Length < 32 || userEncrypted.Length < 32)
        {
            throw new UnsupportedFeatureException("The PDF AES-256 encryption dictionary is incomplete (/O, /U, /OE or /UE).");
        }

        var encryptMetadata = encrypt.Get("EncryptMetadata") is not PdfBoolean { Value: false };
        var passwordBytes = PdfStandardSecurity.PasswordBytes(password);

        // User password: validate against /U[0..32], recover the file key from /UE.
        var userValidationSalt = user.AsSpan(32, 8).ToArray();
        if (HashV5(passwordBytes, userValidationSalt, [], revision).AsSpan(0, 32).SequenceEqual(user.AsSpan(0, 32)))
        {
            var intermediate = HashV5(passwordBytes, user.AsSpan(40, 8).ToArray(), [], revision);
            return new PdfDecryptor(Aes256DecryptNoPad(intermediate, userEncrypted), encrypt, revision, encryptMetadata);
        }

        // Owner password: the hash input additionally binds the first 48 bytes of /U.
        var userVector = user.AsSpan(0, 48).ToArray();
        if (HashV5(passwordBytes, owner.AsSpan(32, 8).ToArray(), userVector, revision).AsSpan(0, 32).SequenceEqual(owner.AsSpan(0, 32)))
        {
            var intermediate = HashV5(passwordBytes, owner.AsSpan(40, 8).ToArray(), userVector, revision);
            return new PdfDecryptor(Aes256DecryptNoPad(intermediate, ownerEncrypted), encrypt, revision, encryptMetadata);
        }

        throw new DocumentValidationException(WrongPassword);
    }

    /// <summary>Algorithm 2.A/2.B: hashes the password with the salt (R6 is hardened).</summary>
    private static byte[] HashV5(byte[] password, byte[] salt, byte[] userVector, int revision)
        => revision == 5
            ? SHA256.HashData([.. password, .. salt, .. userVector])
            : PdfStandardSecurity.HashR6(password, salt, userVector);

    private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
    {
        if (data.Length == 0)
        {
            return data;
        }

        if (data.Length < 32 || data.Length % 16 != 0)
        {
            throw new DocumentValidationException("PDF encrypted data is corrupted (invalid AES block length).");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(data.AsSpan(16).ToArray(), data.AsSpan(0, 16).ToArray(), PaddingMode.None);

        // Some producers write no valid padding; the data is still usable, so the bytes are kept.
        var padding = plain[^1];
        return padding is >= 1 and <= 16 && plain.AsSpan(plain.Length - padding).IndexOfAnyExcept(padding) < 0
            ? plain[..^padding]
            : plain;
    }

    /// <summary>AES-256-CBC with a zero IV and no padding (decrypts the 32 key bytes of /UE and /OE).</summary>
    private static byte[] Aes256DecryptNoPad(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data.AsSpan(0, 32).ToArray(), new byte[16], PaddingMode.None);
    }

    private static bool Verify(byte[] key, byte[] user, byte[] id, int revision)
    {
        var computed = PdfStandardSecurity.ComputeUserEntry(key, id, revision);
        return revision == 2
            ? computed.AsSpan(0, 32).SequenceEqual(user.AsSpan(0, 32))
            : computed.AsSpan(0, 16).SequenceEqual(user.AsSpan(0, 16));
    }

    private static int KeyLength(PdfDictionary encrypt, int revision)
    {
        // For V>=4 the crypt-filter /Length is in bytes (5-16); the document /Length is in bits.
        if (revision >= 4
            && encrypt.Get("CF") is PdfDictionary cf
            && NameOf(encrypt.Get("StmF")) is { } name
            && cf.Get(name) is PdfDictionary filter
            && Integer(filter.Get("Length")) is { } bytes)
        {
            return (int)Math.Clamp(bytes, 5, 16);
        }

        return Math.Clamp((int)(Integer(encrypt.Get("Length")) ?? 40) / 8, 5, 16);
    }

    private static bool UsesAes128(PdfDictionary encrypt)
        => MethodOf(encrypt, "StmF", 4) == Method.Aes128 || MethodOf(encrypt, "StrF", 4) == Method.Aes128;

    private static byte[] FirstId(PdfDictionary trailer)
        => trailer.Get("ID") is PdfArray { Items.Count: > 0 } array && array.Items[0] is PdfString id
            ? id.Bytes
            : [];

    private static byte[]? Bytes(PdfValue? value) => value as PdfString is { } text ? text.Bytes : null;

    private static long? Integer(PdfValue? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when Math.Abs(real.Value % 1) < 1e-9 => (long)real.Value,
        _ => null,
    };

    private static string? NameOf(PdfValue? value) => value as PdfName is { } name ? name.Name : null;
}
