using System.IO.Compression;
using Reglet;

/// <summary>
/// editing protection (phase AD, AD03): <c>w:documentProtection</c> in settings.xml without
/// encryption. Encryption (AD01/AD02) and signatures (AD04) are evaluated in the documentation.
/// </summary>
internal static class ProtectionChecks
{
    internal static int Run()
    {
        try
        {
            WritesProtection(DocxProtection.ReadOnly, "readOnly");
            WritesProtection(DocxProtection.Comments, "comments");
            WritesProtection(DocxProtection.TrackedChanges, "trackedChanges");
            WritesProtection(DocxProtection.Forms, "forms");
            WritesNoProtection();
            WritesPasswordProtection();
            KeepsProtectionOnEdit();
            RemovesProtectionOnRequest();
            Console.WriteLine("Protection (AD): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Protection (AD): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
        => PortableDocument.Create().Paragraph("Body").Build();

    private static string Settings(DocxProtection protection)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer, new DocxWriteOptions { Protection = protection })
            .GetAwaiter().GetResult();
        return ReadZipEntry(buffer.ToArray(), "word/settings.xml");
    }

    private static void WritesProtection(DocxProtection protection, string expected)
    {
        var settings = Settings(protection);
        Require(settings.Contains("<w:documentProtection", StringComparison.Ordinal), $"{protection}: element.");
        Require(settings.Contains($"w:edit=\"{expected}\"", StringComparison.Ordinal), $"{protection}: edit value.");
        Require(settings.Contains("w:enforcement=\"1\"", StringComparison.Ordinal), $"{protection}: enforced.");
    }

    /// <summary>
    /// The password is stored as a salted hash (ISO/IEC 29500-1, 17.15.1.29); the test
    /// recomputes the hash independently.
    /// </summary>
    private static void WritesPasswordProtection()
    {
        const string password = "Secret123";
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(
                Document(),
                buffer,
                new DocxWriteOptions { Protection = DocxProtection.ReadOnly, ProtectionPassword = password })
            .GetAwaiter()
            .GetResult();

        var package = buffer.ToArray();
        var settings = ReadZipEntry(package, "word/settings.xml");
        Require(settings.Contains("w:algorithmName=\"SHA-512\"", StringComparison.Ordinal), "Password: the algorithm.");
        Require(settings.Contains("w:spinCount=\"100000\"", StringComparison.Ordinal), "Password: the iteration count.");
        Require(!settings.Contains(password, StringComparison.Ordinal), "Password: the password is not in clear text.");

        var salt = Attribute(settings, "saltValue");
        var hash = Attribute(settings, "hashValue");
        Require(salt is not null && hash is not null, "Password: salt and hash are written.");
        Require(
            hash == ExpectedHash(password, Convert.FromBase64String(salt!), 100_000),
            "Password: the hash value matches the password and the salt.");
    }

    /// <summary>The value of an attribute of the protection element.</summary>
    private static string? Attribute(string settings, string name)
    {
        var marker = $"w:{name}=\"";
        var start = settings.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = settings.IndexOf('"', start);
        return end < 0 ? null : settings[start..end];
    }

    /// <summary>The hash of a password as Word computes it (an independent implementation).</summary>
    private static string ExpectedHash(string password, byte[] salt, int spinCount)
    {
        var hash = System.Security.Cryptography.SHA512.HashData([.. salt, .. System.Text.Encoding.Unicode.GetBytes(password)]);
        for (var round = 0; round < spinCount; round++)
        {
            hash = System.Security.Cryptography.SHA512.HashData(
            [
                .. hash,
                (byte)(round & 0xFF),
                (byte)((round >> 8) & 0xFF),
                (byte)((round >> 16) & 0xFF),
                (byte)((round >> 24) & 0xFF),
            ]);
        }

        return Convert.ToBase64String(hash);
    }


    private static void KeepsProtectionOnEdit()
    {
        var settings = SettingsAfterEdit(DocxProtection.None);
        Require(settings.Contains("documentProtection", StringComparison.Ordinal), "An edit keeps the source protection.");
    }

    private static void RemovesProtectionOnRequest()
    {
        var settings = SettingsAfterEdit(DocxProtection.Remove);
        Require(!settings.Contains("documentProtection", StringComparison.Ordinal), "Remove deletes the source protection.");
    }

    private static string SettingsAfterEdit(DocxProtection protection)
    {
        var package = SettingsPackage();
        var read = new DocxDocumentReader().Read(new MemoryStream(package)).Document;
        var sections = read.Sections.ToList();
        var blocks = sections[0].Blocks.ToList();
        blocks.Add(new Paragraph(new TextRun("added")));
        sections[0] = sections[0] with { Blocks = blocks };
        var modified = read with { Sections = sections };

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(modified, buffer, new DocxWriteOptions { Protection = protection })
            .GetAwaiter()
            .GetResult();
        return ReadZipEntry(buffer.ToArray(), "word/settings.xml");
    }

    private static byte[] SettingsPackage()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(), buffer, new DocxWriteOptions { Protection = DocxProtection.ReadOnly, ProtectionPassword = "secret" })
            .GetAwaiter()
            .GetResult();
        return buffer.ToArray();
    }

    private static void WritesNoProtection()
    {
        Require(!Settings(DocxProtection.None).Contains("documentProtection", StringComparison.Ordinal), "None: no protection.");
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
