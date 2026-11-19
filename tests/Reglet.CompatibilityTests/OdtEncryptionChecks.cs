using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>OF-E05: ODT package encryption (AES-256-CBC, PBKDF2, manifest encryption data).</summary>
internal static class OdtEncryptionChecks
{
    private const string Password = "correct horse battery staple";

    internal static int Run()
    {
        try
        {
            RoundTripsEncryptedPackage();
            RequiresPassword();
            RejectsWrongPassword();
            WritesPlainAfterEncryptedRead();
            Console.WriteLine("ODT encryption (OF-E05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ODT encryption (OF-E05): " + exception);
            return 1;
        }
    }

    private static void RoundTripsEncryptedPackage()
    {
        var bytes = Write(Document(), Password);

        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            Require(zip.Entries[0].FullName == "mimetype", "The mimetype entry is first.");
            Require(zip.Entries[0].CompressedLength == zip.Entries[0].Length, "The mimetype entry is stored.");
            var manifest = Encoding.UTF8.GetString(Read(zip, "META-INF/manifest.xml"));
            Require(manifest.Contains("manifest:encryption-data", StringComparison.Ordinal)
                && manifest.Contains("http://www.w3.org/2001/04/xmlenc#aes256-cbc", StringComparison.Ordinal)
                && manifest.Contains("PBKDF2", StringComparison.Ordinal)
                && manifest.Contains("manifest:checksum-type", StringComparison.Ordinal),
                "The manifest carries the encryption parameters.");
            var content = zip.GetEntry("content.xml") ?? throw new InvalidOperationException("content.xml is missing.");
            Require(content.CompressedLength == content.Length, "The encrypted content is stored.");
            // Ciphertext is random: its first byte is '<' once in 256 runs, so look for the XML itself, not for a '<'.
            var stored = Encoding.Latin1.GetString(Read(zip, "content.xml"));
            Require(!stored.Contains("office:document-content", StringComparison.Ordinal) && !stored.Contains("<?xml", StringComparison.Ordinal),
                "The encrypted content is not readable XML.");
        }

        var read = new OdtDocumentReader().Read(
            new MemoryStream(bytes), new OdtReadOptions { Password = Password });
        Require(AllText(read.Document).Contains("Secret text", StringComparison.Ordinal),
            "The encrypted ODT is read with the password.");

        var throughFacade = DocumentReader.Read(
            new MemoryStream(bytes), DocumentFormat.Odt, Password);
        Require(AllText(throughFacade.Document).Contains("Secret text", StringComparison.Ordinal),
            "The DocumentReader facade opens the encrypted ODT.");
    }

    private static void RequiresPassword()
        => Throws<DocumentValidationException>(() =>
        {
            var bytes = Write(Document(), Password);
            _ = new OdtDocumentReader().Read(new MemoryStream(bytes));
        });

    private static void RejectsWrongPassword()
        => Throws<DocumentValidationException>(() =>
        {
            var bytes = Write(Document(), Password);
            _ = new OdtDocumentReader().Read(
                new MemoryStream(bytes), new OdtReadOptions { Password = "wrong" });
        });

    private static void WritesPlainAfterEncryptedRead()
    {
        var encrypted = Write(Document(), Password);
        var read = new OdtDocumentReader().Read(
            new MemoryStream(encrypted), new OdtReadOptions { Password = Password });

        using var output = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(read.Document, output).GetAwaiter().GetResult();
        var plain = output.ToArray();
        using (var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read))
        {
            var manifest = Encoding.UTF8.GetString(Read(zip, "META-INF/manifest.xml"));
            Require(!manifest.Contains("encryption-data", StringComparison.Ordinal),
                "A plain write of the decrypted document has no encryption data.");
        }

        var again = new OdtDocumentReader().Read(new MemoryStream(plain));
        Require(AllText(again.Document).Contains("Secret text", StringComparison.Ordinal),
            "The decrypted source can be written and read without a password.");
    }

    private static PortableDocument Document()
        => PortableDocument.Create()
            .Heading("Encrypted", level: 1)
            .Paragraph("Secret text")
            .BulletList("one", "two")
            .Build();

    private static byte[] Write(PortableDocument document, string password)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(
            document,
            buffer,
            new OdtWriteOptions { Encryption = new OdtEncryptionOptions(password) }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string AllText(PortableDocument document)
    {
        var builder = new StringBuilder();
        foreach (var section in document.Sections)
        {
            Append(builder, section.Blocks);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Append(builder, paragraph.Inlines);
                    break;
                case Heading heading:
                    Append(builder, heading.Inlines);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Append(builder, item.Blocks);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            Append(builder, cell.Blocks);
                        }
                    }

                    break;
            }
        }
    }

    private static void Append(StringBuilder builder, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is TextRun run)
            {
                builder.Append(run.Text);
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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
