using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// OF-E01: ECMA-376 agile encryption of a DOCX. The package is wrapped in a compound file and
/// opens with the password only; a wrong password and a modified payload are rejected.
/// </summary>
internal static class DocxEncryptionChecks
{
    private const string Password = "Secret123";

    internal static int Run()
    {
        try
        {
            RoundTrips();
            WritesTheCompoundFileStreams();
            RejectsWrongPasswordAndMissingPassword();
            DetectsAModifiedPayload();
            IgnoresThePasswordOfAPlainDocument();
            EncryptsAPreservedSource();
            ValidatesWithMsoffcryptoWhenConfigured();
            Console.WriteLine("DOCX encryption (OF-E01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DOCX encryption (OF-E01): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
        => PortableDocument.Create()
            .Paragraph("Secret")
            .Paragraph("Confidential content with special characters: äöü ß €")
            .Build();

    private static byte[] Write(PortableDocument document, string? password, int spinCount = 1000)
    {
        using var buffer = new MemoryStream();
        var options = password is null
            ? new DocxWriteOptions()
            : new DocxWriteOptions { Encryption = new DocxEncryptionOptions(password) { SpinCount = spinCount } };
        new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static PortableDocument Read(byte[] package, string? password)
    {
        using var buffer = new MemoryStream(package);
        return new DocxDocumentReader().Read(buffer, new DocxReadOptions { Password = password }).Document;
    }

    private static void RoundTrips()
    {
        var encrypted = Write(Document(), Password);

        Require(encrypted.Length > 0, "The encrypted package is written.");
        Require(
            encrypted[0] == 0xD0 && encrypted[1] == 0xCF && encrypted[2] == 0x11 && encrypted[3] == 0xE0,
            "The output is a compound file.");

        var read = Read(encrypted, Password);
        var text = string.Concat(read.Sections[0].Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<TextRun>())
            .Select(run => run.Text));
        Require(text.Contains("Secret", StringComparison.Ordinal), "The text survives: " + text);
        Require(text.Contains("äöü ß €", StringComparison.Ordinal), "The umlauts survive: " + text);
    }

    /// <summary>The container holds the two streams the format requires.</summary>
    private static void WritesTheCompoundFileStreams()
    {
        var encrypted = Write(Document(), Password);
        var names = Encoding.Unicode.GetString(encrypted);
        Require(names.Contains("EncryptionInfo", StringComparison.Ordinal), "The container holds EncryptionInfo.");
        Require(names.Contains("EncryptedPackage", StringComparison.Ordinal), "The container holds EncryptedPackage.");

        // The encrypted package is not a ZIP any more: the plaintext magic appears nowhere.
        var latin = Encoding.Latin1.GetString(encrypted);
        Require(!latin.Contains("word/document.xml", StringComparison.Ordinal), "The part names are not readable.");
    }

    private static void RejectsWrongPasswordAndMissingPassword()
    {
        var encrypted = Write(Document(), Password);
        Throws<DocumentValidationException>(() => Read(encrypted, "wrong"));
        Throws<DocumentValidationException>(() => Read(encrypted, null));
    }

    /// <summary>A changed payload fails the encrypted HMAC.</summary>
    private static void DetectsAModifiedPayload()
    {
        // A package larger than the mini stream cutoff sits in regular sectors.
        var large = new PortableDocument(
            new DocumentMetadata(),
            new Section(Enumerable.Range(0, 400)
                .Select(index => (Block)new Paragraph(new TextRun("Paragraph " + index.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                .ToList()));
        var plain = Write(large, password: null);
        var encrypted = Write(large, Password, spinCount: 100);

        // The encrypted package starts with the little-endian length of the plaintext, at the
        // boundary of a sector (512 bytes) or of a mini sector (64 bytes).
        var marker = BitConverter.GetBytes((long)plain.Length);
        var start = -1;
        for (var offset = 64; offset + marker.Length < encrypted.Length; offset += 64)
        {
            if (encrypted.AsSpan(offset, marker.Length).SequenceEqual(marker))
            {
                start = offset;
                break;
            }
        }

        Require(start > 0, "The encrypted package is found in the container.");

        // A change inside the first segment must fail the integrity check.
        var tampered = (byte[])encrypted.Clone();
        tampered[start + 16] ^= 0xFF;
        Throws<DocumentValidationException>(() => Read(tampered, Password));
    }

    private static void IgnoresThePasswordOfAPlainDocument()
    {
        var plain = Write(Document(), password: null);
        var read = Read(plain, "any");
        Require(read.Sections[0].Blocks.Count > 0, "A plain document is read although a password was given.");
    }

    /// <summary>A document read from a package can be encrypted again (the source is preserved).</summary>
    private static void EncryptsAPreservedSource()
    {
        var plain = Write(Document(), password: null);
        var source = Read(plain, null);

        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(source, buffer, new DocxWriteOptions
        {
            Encryption = new DocxEncryptionOptions(Password) { SpinCount = 1000 },
        }).GetAwaiter().GetResult();

        var read = Read(buffer.ToArray(), Password);
        Require(read.Sections[0].Blocks.Count > 0, "The encrypted source stays readable.");
    }

    /// <summary>
    /// OF-E04 (optional): an independent implementation decrypts the file. Set
    /// <c>REGLET_MSOFFCRYPTO</c> to a Python interpreter with <c>msoffcrypto-tool</c>.
    /// </summary>
    private static void ValidatesWithMsoffcryptoWhenConfigured()
    {
        var python = Environment.GetEnvironmentVariable("REGLET_MSOFFCRYPTO");
        if (string.IsNullOrEmpty(python) || !File.Exists(python))
        {
            Console.WriteLine("DOCX encryption validator skipped (REGLET_MSOFFCRYPTO not set).");
            return;
        }

        var encryptedPath = Path.Combine(Path.GetTempPath(), "reglet-enc-" + Guid.NewGuid().ToString("N") + ".docx");
        var plainPath = Path.Combine(Path.GetTempPath(), "reglet-plain-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            File.WriteAllBytes(encryptedPath, Write(Document(), Password, spinCount: 100));

            var script = string.Join(
                "\n",
                "import sys, msoffcrypto",
                "f = open(sys.argv[1], 'rb')",
                "office = msoffcrypto.OfficeFile(f)",
                "assert office.is_encrypted(), 'not encrypted'",
                "assert office.type == 'agile', office.type",
                "office.load_key(password=sys.argv[2], verify_password=True)",
                "out = open(sys.argv[3], 'wb')",
                "office.decrypt(out)",
                "out.close()");

            var startInfo = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(encryptedPath);
            startInfo.ArgumentList.Add(Password);
            startInfo.ArgumentList.Add(plainPath);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The validator could not be started.");
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "msoffcrypto cannot decrypt the file: " + errors);

            using var zip = new ZipArchive(new MemoryStream(File.ReadAllBytes(plainPath)), ZipArchiveMode.Read);
            Require(zip.GetEntry("word/document.xml") is not null, "The decrypted package is a DOCX.");
        }
        finally
        {
            if (File.Exists(encryptedPath))
            {
                File.Delete(encryptedPath);
            }

            if (File.Exists(plainPath))
            {
                File.Delete(plainPath);
            }
        }
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
