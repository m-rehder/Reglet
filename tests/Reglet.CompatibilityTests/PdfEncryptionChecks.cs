using System.Text;
using Reglet;

/// <summary>
/// Phase AD (AD02): PDF encryption with the standard security handler and AES-256 (R6).
/// Round trip, password handling, permissions and the encryption dictionary.
/// </summary>
internal static class PdfEncryptionChecks
{
    internal static int Run()
    {
        try
        {
            WritesEncryptDictionary();
            RoundTripsWithUserPassword();
            RoundTripsWithOwnerPassword();
            AllowsEmptyUserPassword();
            RejectsWrongPassword();
            ReadsAes256();
            EncryptsStringsAndStreams();
            AppliesPermissions();
            MixesAesSaltIntoObjectKey();
            RejectsEncryptionWithPdfA();
            GeneratesOwnerPasswordWhenMissing();
            MapsPasswordsLikeSaslPrep();
            ReadsIdentityCryptFilters();
            ForcesAes128KeyForAesV2();
            KeepsDataWithoutValidPadding();
            EncodesLegacyPasswordsInPdfDocEncoding();
            AcceptsLongKeyEntries();
            Console.WriteLine("PDF encryption (AD02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF encryption (AD): " + exception);
            return 1;
        }
    }

    private static void WritesEncryptDictionary()
    {
        var bytes = Write("Body", "user", "owner", PdfPermissions.All);
        var latin = Encoding.Latin1.GetString(bytes);

        Require(latin.Contains("/Encrypt ", StringComparison.Ordinal), "Trailer references /Encrypt.");
        Require(latin.Contains("/Filter /Standard", StringComparison.Ordinal), "Standard security handler.");
        Require(latin.Contains("/V 5 ", StringComparison.Ordinal), "Version V 5.");
        Require(latin.Contains("/R 6 ", StringComparison.Ordinal), "Revision R 6.");
        Require(latin.Contains("/CFM /AESV3", StringComparison.Ordinal), "AES-256 (AESV3).");
        Require(latin.Contains("/OE <", StringComparison.Ordinal) && latin.Contains("/UE <", StringComparison.Ordinal), "Encrypted file keys /OE and /UE.");
        Require(latin.Contains("/Perms <", StringComparison.Ordinal), "Encrypted permissions /Perms.");
        Require(latin.Contains("/ExtensionLevel 8 ", StringComparison.Ordinal), "Adobe extension level 8 for AES-256 in PDF 1.7.");
        Require(latin.Contains("/StmF /StdCF", StringComparison.Ordinal), "Streams use StdCF.");
        Require(latin.Contains("/StrF /StdCF", StringComparison.Ordinal), "Strings use StdCF.");
        Require(latin.Contains("/ID [<", StringComparison.Ordinal), "Trailer has a document ID.");
    }

    private static void RoundTripsWithUserPassword()
    {
        var bytes = Write("Body text", "user", "owner", PdfPermissions.All);
        var read = new PdfDocumentReader().Read(
            new MemoryStream(bytes), new PdfReadOptions { Password = "user" }).Document;
        Require(AllText(read).Contains("Body text", StringComparison.Ordinal), "User password reads the text.");
    }

    private static void RoundTripsWithOwnerPassword()
    {
        var bytes = Write("Body text", "user", "owner", PdfPermissions.All);
        var read = new PdfDocumentReader().Read(
            new MemoryStream(bytes), new PdfReadOptions { Password = "owner" }).Document;
        Require(AllText(read).Contains("Body text", StringComparison.Ordinal), "Owner password reads the text.");
    }

    private static void AllowsEmptyUserPassword()
    {
        var bytes = Write("EncryptedBody", string.Empty, "owner", PdfPermissions.All);
        var read = new PdfDocumentReader().Read(new MemoryStream(bytes)).Document;
        Require(AllText(read).Contains("EncryptedBody", StringComparison.Ordinal), "Empty user password opens the file.");

        var pages = DocumentReader.ReadPdfPages(new MemoryStream(bytes));
        Require(pages.Count == 1 && pages[0].Contains("EncryptedBody", StringComparison.Ordinal),
            "ReadPdfPages opens an encrypted file with the empty password.");
    }

    private static void RejectsWrongPassword()
    {
        var bytes = Write("Body text", "user", "owner", PdfPermissions.All);
        Throws<DocumentValidationException>(
            () => new PdfDocumentReader().Read(new MemoryStream(bytes), new PdfReadOptions { Password = "wrong" }),
            "Wrong password is rejected.");
        Throws<DocumentValidationException>(
            () => new PdfDocumentReader().Read(new MemoryStream(bytes)),
            "Missing password is rejected.");
    }

    private static void EncryptsStringsAndStreams()
    {
        var bytes = Write("SecretBodyText", "user", "owner", PdfPermissions.All);
        var latin = Encoding.Latin1.GetString(bytes);
        Require(!latin.Contains("SecretBodyText", StringComparison.Ordinal), "Streams are encrypted.");
        Require(!latin.Contains("SecretTitle", StringComparison.Ordinal), "Strings are encrypted.");
        Require(!latin.Contains("xpacket", StringComparison.Ordinal), "Metadata stream is encrypted.");
    }

    private static void AppliesPermissions()
    {
        var bytes = Write("Body", "user", "owner", PdfPermissions.Print);
        var file = PdfFile.Load(bytes, "user");
        var encrypt = file.Dictionary(file.Trailer.Get("Encrypt"))
            ?? throw new InvalidOperationException("Encryption dictionary is missing.");
        var permissions = file.Integer(encrypt.Get("P")) ?? throw new InvalidOperationException("/P is missing.");
        var expected = PdfStandardSecurity.ToPermissionBits(PdfPermissions.Print);

        Require(permissions == expected, $"Permission bits differ ({permissions:X8} != {expected:X8}).");
        Require((permissions & (1 << 2)) != 0, "Printing is allowed.");
        Require((permissions & (1 << 4)) == 0, "Copying is forbidden.");
        Require((permissions & 0xC0) == 0xC0, "Reserved bits 7-8 stay set.");
        Require((permissions & unchecked((int)0xFFFFF000)) == unchecked((int)0xFFFFF000), "Reserved upper bits stay set.");
    }

    /// <summary>AD02: the AES per-object key mixes in the "sAlT" suffix (Algorithm 1).</summary>
    private static void MixesAesSaltIntoObjectKey()
    {
        var fileKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var rc4 = PdfStandardSecurity.ComputeObjectKey(fileKey, 42, 0, aes: false);
        var aes = PdfStandardSecurity.ComputeObjectKey(fileKey, 42, 0, aes: true);

        // Known answers computed independently: MD5(fileKey || obj(3 LE) || gen(2 LE) [|| "sAlT"]).
        Require(Convert.ToHexStringLower(rc4) == "063ab06f16f31162b6d841f9f65dc7ca", "RC4 object key.");
        Require(Convert.ToHexStringLower(aes) == "2919ac0df76d4bb433a76f28b3c63f40", "AES object key with sAlT.");
    }

    private const string Aes256Fixture =
        "JVBERi0xLjMKJeLjz9MKMSAwIG9iago8PAovVHlwZSAvQ2F0YWxvZwovUGFnZXMgMiAwIFIKL1N0cnVjdFRyZWVSb290IDcgMCBS" +
        "Ci9NYXJrSW5mbyA8PAovTWFya2VkIHRydWUKPj4KL1ZpZXdlclByZWZlcmVuY2VzIDw8Ci9EaXNwbGF5RG9jVGl0bGUgdHJ1ZQo+" +
        "PgovTWV0YWRhdGEgMTEgMCBSCj4+CmVuZG9iagoyIDAgb2JqCjw8Ci9UeXBlIC9QYWdlcwovS2lkcyBbIDMgMCBSIF0KL0NvdW50" +
        "IDEKPj4KZW5kb2JqCjMgMCBvYmoKPDwKL1R5cGUgL1BhZ2UKL1BhcmVudCAyIDAgUgovUmVzb3VyY2VzIDQgMCBSCi9NZWRpYUJv" +
        "eCBbIDAgMCA1OTUuMjc1NTkgODQxLjg4OTc2IF0KL1N0cnVjdFBhcmVudHMgMAovQ29udGVudHMgNiAwIFIKPj4KZW5kb2JqCjQg" +
        "MCBvYmoKPDwKL0ZvbnQgPDwKL0YxIDUgMCBSCj4+Cj4+CmVuZG9iago1IDAgb2JqCjw8Ci9UeXBlIC9Gb250Ci9TdWJ0eXBlIC9U" +
        "eXBlMQovQmFzZUZvbnQgL0hlbHZldGljYQovRW5jb2RpbmcgL1dpbkFuc2lFbmNvZGluZwo+PgplbmRvYmoKNiAwIG9iago8PAov" +
        "RmlsdGVyIC9GbGF0ZURlY29kZQovTGVuZ3RoIDExMgo+PgpzdHJlYW0KYR1nTSvHa1OV1oTlZjbMC1Qk3EuVxnTTeHUrsJO6m/JX" +
        "GDkDieWuSVPVlLUr+bg/dJKGB775A/3FhskNDzHj3y732qc+5ZwA3GTEinGZRpAbqdBIH6JBs11SKbe4ogTIqfhAKbcAIuyfXr4T" +
        "7pwjmQplbmRzdHJlYW0KZW5kb2JqCjcgMCBvYmoKPDwKL1R5cGUgL1N0cnVjdFRyZWVSb290Ci9LIFsgOCAwIFIgXQovUGFyZW50" +
        "VHJlZSAxMCAwIFIKL1BhcmVudFRyZWVOZXh0S2V5IDEKPj4KZW5kb2JqCjggMCBvYmoKPDwKL1R5cGUgL1N0cnVjdEVsZW0KL1Mg" +
        "L0RvY3VtZW50Ci9QIDcgMCBSCi9LIFsgOSAwIFIgXQo+PgplbmRvYmoKOSAwIG9iago8PAovVHlwZSAvU3RydWN0RWxlbQovUyAv" +
        "UAovUCA4IDAgUgovUGcgMyAwIFIKL0sgPDwKL1R5cGUgL01DUgovUGcgMyAwIFIKL01DSUQgMAo+Pgo+PgplbmRvYmoKMTAgMCBv" +
        "YmoKPDwKL051bXMgWyAwIFsgOSAwIFIgXSBdCj4+CmVuZG9iagoxMSAwIG9iago8PAovVHlwZSAvTWV0YWRhdGEKL1N1YnR5cGUg" +
        "L1hNTAovTGVuZ3RoIDQ4MAo+PgpzdHJlYW0KrzA8kPMy77YxS23rgpuxvRZ7nbqL146ya9xHfts7KRjUa1ipdND/9aGJBXw9uxnx" +
        "+NGgVLlN4+Q4EQBEvIHnLxZt4tyw3r/A4kQ7PNHXJoqBIcjBzoS0jFso6wV37BFrA8bOFGxZYkr3OxlyzudpBOl5PeQt23bZgNis" +
        "Rv3UhwBFG2nwX/K385RpPwxjc5dYK3nyesAevhFtCkeKs2kNxgotyA0Nc78bUwhkXo3G3+0uN0h9KGduWE0YQJeNknUyYicoAXIt" +
        "NBy4bOnqnsTtDE9Fxw+jo71HcCpaS4wGBb/c2ZZ6MqJsXHZF/ln5M9gJnDjvqrtdSIwXaHjTiGclxbSz4/jeroKfWIjiPfX0DfGZ" +
        "D4nUwbZaZQnnln6bDjMJ72rlNKYlgs1dljfUkLHWZpIC45FBJhhQlJYyeFAZNelwAioKmpuf5s8B98NZwNlp1CvI1UonpEioET6K" +
        "skr1u2DOA3wXisRzrzv9yneQxPcRffg1ob7GbhkFyVvKapz4BNaEejMypdVmoffzQBgEaPTLUzAV8un0rCLHmUYJabYForvegX6t" +
        "9sIn1ivtOe76f5yLTDbDsaYPO/n7KRI0XMFY4tYiFOabqnOGUXPIKJteB8F6ZzxX8+v5YH9COHuNCmVuZHN0cmVhbQplbmRvYmoK" +
        "MTIgMCBvYmoKPDwKL1RpdGxlIDw3NmQxZWEzODkwNmE2YTEyZmQyMTY3M2U4Y2EyYzdmOGQ1YzcyYTczNGMxYzUwZWU1OTRlNjBk" +
        "NTUwYWUxODdjPgovUHJvZHVjZXIgPDg1ZjU3M2Y0MDNiNzE4OTZjZWY4MDE3MGQ2ZGY2ZjFhYzdjNGM5ZWRjYjIxYjUyNWYzYWU0" +
        "ZjIwOGVlNzVkNzQ+Cj4+CmVuZG9iagoxMyAwIG9iago8PAovViA1Ci9SIDYKL0xlbmd0aCAyNTYKL1AgNDI5NDk2NzI5MgovRmls" +
        "dGVyIC9TdGFuZGFyZAovTyA8NzI3YTcwZjIyYzRmOTgyNmY3NzZhNjA3YTYzMjExM2ZhNmJhYTRjMTI0ZjI4Zjc4ZThmZmMzYTRm" +
        "Nzk4YzIyMjNkYTE5MmQxOTg4MTc4MTExZDY0N2IyMDQxOTA4ZWYzPgovVSA8OGM3YWIzZGJkNmM1NjE4MjBiZWI5NGNlMDM0YzRl" +
        "YzUxOTI2Nzk1OTM3ZWMwODc1YzYxZTU0OWVjMjM0OGQyZTE1MDcyMjgxNDQxMDg0ZDJhNTg0Y2U3OGY5NWM2NzNhPgovQ0YgPDwK" +
        "L1N0ZENGIDw8Ci9BdXRoRXZlbnQgL0RvY09wZW4KL0NGTSAvQUVTVjMKL0xlbmd0aCAzMgo+Pgo+PgovU3RtRiAvU3RkQ0YKL1N0" +
        "ckYgL1N0ZENGCi9PRSA8ZmE3YjljN2NhZDcyN2NkZDI2YzZkMjg4YTE2YjIyZDgyYjRjYjUzN2U4Nzg4NjNkYmUyYWJmZjNiMjk3" +
        "OWQzMj4KL1VFIDxhMDZkMTEyOTUyN2Q3ZjgyMjhmOWZmMGZkYmRiNWRhNDhkMjI2YWI4MjQ1M2VkMWZlMzA2YzE0ODhiYzcxMWFj" +
        "PgovUGVybXMgPGM0YjA0MzgyMTM1MDFlYzcxNzI4ODI4Mjg4OTdlYWM2Pgo+PgplbmRvYmoKeHJlZgowIDE0CjAwMDAwMDAwMDAg" +
        "NjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMTc5IDAwMDAwIG4gCjAwMDAwMDAyMzggMDAwMDAgbiAKMDAwMDAw" +
        "MDM3MyAwMDAwMCBuIAowMDAwMDAwNDE2IDAwMDAwIG4gCjAwMDAwMDA1MTMgMDAwMDAgbiAKMDAwMDAwMDY5NyAwMDAwMCBuIAow" +
        "MDAwMDAwNzkzIDAwMDAwIG4gCjAwMDAwMDA4NjcgMDAwMDAgbiAKMDAwMDAwMDk2OSAwMDAwMCBuIAowMDAwMDAxMDEzIDAwMDAw" +
        "IG4gCjAwMDAwMDE1NzUgMDAwMDAgbiAKMDAwMDAwMTc0OCAwMDAwMCBuIAp0cmFpbGVyCjw8Ci9TaXplIDE0Ci9Sb290IDEgMCBS" +
        "Ci9JbmZvIDEyIDAgUgovSUQgWyA8NjY2MTM4MzMzODYzMzYzNjM4NjQzNTMwNjIzMTM4MzI2MjY0MzczNTMyMzE2MjY2MzI2MjY0" +
        "MzYzNzM3NjQzNz4gPDY2NjEzODMzMzg2MzM2MzYzODY0MzUzMDYyMzEzODMyNjI2NDM3MzUzMjMxNjI2NjMyNjI2NDM2MzczNzY0" +
        "Mzc+IF0KL0VuY3J5cHQgMTMgMCBSCj4+CnN0YXJ0eHJlZgoyMzA0CiUlRU9GCg==";

    /// <summary>AD01/AF: a pypdf-generated AES-256 (V5/R6, AESV3) PDF is read with user/owner password.</summary>
    private static void ReadsAes256()
    {
        var pdf = Convert.FromBase64String(Aes256Fixture);

        var user = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { Password = "user" }).Document;
        Require(AllText(user).Contains("Aes256BodyText", StringComparison.Ordinal), "AES-256 (R6): user password reads the text.");

        var owner = new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { Password = "owner" }).Document;
        Require(AllText(owner).Contains("Aes256BodyText", StringComparison.Ordinal), "AES-256 (R6): owner password reads the text.");

        Throws<DocumentValidationException>(
            () => new PdfDocumentReader().Read(new MemoryStream(pdf), new PdfReadOptions { Password = "wrong" }),
            "AES-256 (R6): wrong password is rejected.");
    }

    private static void RejectsEncryptionWithPdfA()
    {
        var document = PortableDocument.Create().Paragraph("Body").Build();
        using var buffer = new MemoryStream();
        Throws<ArgumentException>(
            () => new PdfDocumentWriter().WriteAsync(
                document,
                buffer,
                new PdfWriteOptions
                {
                    PdfA = PdfAConformance.PdfA2b,
                    Encryption = new PdfEncryptionOptions { UserPassword = "user" },
                }).GetAwaiter().GetResult(),
            "Encryption and PDF/A are mutually exclusive.");
    }

    private static void GeneratesOwnerPasswordWhenMissing()
    {
        foreach (var user in new[] { string.Empty, "user" })
        {
            var security = PdfStandardSecurity.Create(user, null, PdfPermissions.Print);
            var hash = PdfStandardSecurity.HashR6(PdfStandardSecurity.PasswordBytes(user), security.O[32..40], security.U[..48]);
            Require(!hash.AsSpan().SequenceEqual(security.O.AsSpan(0, 32)), "Without an owner password the user password has no owner rights.");
        }
    }

    private static void MapsPasswordsLikeSaslPrep()
    {
        static string Mapped(string password) => Encoding.UTF8.GetString(PdfStandardSecurity.PasswordBytes(password));

        Require(Mapped("a\u00ADb") == "ab" && Mapped("a\u200Db") == "ab", "SASLprep: characters mapped to nothing.");
        Require(Mapped("a\u1680b") == "a b" && Mapped("a\u3000b") == "a b", "SASLprep: non-ASCII spaces.");
        Require(Mapped("\uFB01x") == "fix", "SASLprep: compatibility normalization.");
        Require(PdfStandardSecurity.PasswordBytes(new string('\u00E4', 100)).Length == 127, "The password is cut to 127 bytes.");
        Throws<DocumentValidationException>(
            () => PdfStandardSecurity.Create("a\uD800b", null, PdfPermissions.All), "A lone surrogate is reported.");
    }

    private static void ReadsIdentityCryptFilters()
        => Require(ReadLegacy(LegacyPdf("userpw", "/StmF /Identity /StrF /Identity", encrypted: false), "userpw").Contains("HELLOWORLD", StringComparison.Ordinal), "Identity: the content is not decrypted.");

    private static void ForcesAes128KeyForAesV2()
        => Require(ReadLegacy(LegacyPdf("userpw", "/CF << /StdCF << /CFM /AESV2 /Length 5 >> >> /StmF /StdCF /StrF /StdCF", encrypted: true), "userpw").Contains("HELLOWORLD", StringComparison.Ordinal), "AESV2 ignores a wrong /Length.");

    private static void KeepsDataWithoutValidPadding()
    {
        var pdf = LegacyPdf("userpw", "/CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF", encrypted: true, unpadded: true);
        Require(ReadLegacy(pdf, "userpw").Contains("HELLOWORLD", StringComparison.Ordinal), "Data without a valid padding is kept.");
    }

    private static void EncodesLegacyPasswordsInPdfDocEncoding()
    {
        const string Filters = "/StmF /Identity /StrF /Identity";
        Require(ReadLegacy(LegacyPdf("\u20ACuro", Filters, encrypted: false), "\u20ACuro").Contains("HELLOWORLD", StringComparison.Ordinal), "The euro sign is PDFDocEncoded.");
        Require(PdfStandardSecurity.PadPassword("\u20AC")[0] == 0xA0 && PdfStandardSecurity.PadPassword("\u017D")[0] == 0x99, "PDFDocEncoding bytes.");
        var pdf = LegacyPdf("?uro", Filters, encrypted: false);
        Throws<DocumentValidationException>(() => ReadLegacy(pdf, "\u20ACuro"), "The euro sign is not a question mark.");
        Throws<DocumentValidationException>(() => ReadLegacy(pdf, "\u0416uro"), "A password outside PDFDocEncoding cannot match.");
    }

    private static void AcceptsLongKeyEntries()
    {
        var latin = Encoding.Latin1.GetString(Convert.FromBase64String(Aes256Fixture));
        var at = latin.IndexOf("/UE <", StringComparison.Ordinal) + 5 + 64;
        var padded = (latin[..at] + new string('0', 32) + latin[at..]).Replace("startxref\n2304", "startxref\n2336", StringComparison.Ordinal);
        var document = new PdfDocumentReader().Read(new MemoryStream(Encoding.Latin1.GetBytes(padded)), new PdfReadOptions { Password = "user" }).Document;
        Require(AllText(document).Contains("Aes256BodyText", StringComparison.Ordinal), "/UE longer than 32 bytes is read.");
    }

    private static string ReadLegacy(byte[] pdf, string password)
        => string.Concat(DocumentReader.ReadPdfPages(new MemoryStream(pdf), password));

    /// <summary>A minimal V4/R4 file: the encryption entries are derived here, the crypt filters are given.</summary>
    private static byte[] LegacyPdf(string password, string filters, bool encrypted, bool unpadded = false)
    {
        var id = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var owner = PdfStandardSecurity.ComputeOwnerEntry("owner", password, 4, 16);
        var key = PdfStandardSecurity.ComputeEncryptionKey(PdfStandardSecurity.PadPassword(password), owner, -1028, id, 4, 16);
        var user = PdfStandardSecurity.ComputeUserEntry(key, id, 4);

        // 80 bytes: without padding the data still fills whole AES blocks.
        var content = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (HELLOWORLD) Tj ET" + new string(' ', 39));
        if (encrypted)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = PdfStandardSecurity.ComputeObjectKey(key, 5, 0, aes: true);
            var iv = new byte[16];
            content = [.. iv, .. aes.EncryptCbc(content, iv, unpadded ? System.Security.Cryptography.PaddingMode.None : System.Security.Cryptography.PaddingMode.PKCS7)];
        }

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{Encoding.Latin1.GetString(content)}\nendstream",
            $"<< /Filter /Standard /V 4 /R 4 /Length 128 /P -1028 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> {filters} >>",
        ];

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(builder.Length);
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append("xref\n0 7\n0000000000 65535 f \n");
        offsets.ForEach(offset => builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n"));
        var hex = Convert.ToHexString(id);
        builder.Append($"trailer << /Size 7 /Root 1 0 R /Encrypt 6 0 R /ID [<{hex}><{hex}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static byte[] Write(string text, string user, string owner, PdfPermissions permissions)
    {
        var document = PortableDocument.Create()
            .Metadata(title: "SecretTitle")
            .Paragraph(text)
            .Build();

        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions
            {
                Encryption = new PdfEncryptionOptions
                {
                    UserPassword = user,
                    OwnerPassword = owner,
                    Permissions = permissions,
                },
            }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static string AllText(PortableDocument document)
    {
        var builder = new StringBuilder();
        foreach (var section in document.Sections)
        {
            foreach (var block in section.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    foreach (var run in paragraph.Inlines.OfType<TextRun>())
                    {
                        builder.Append(run.Text);
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
