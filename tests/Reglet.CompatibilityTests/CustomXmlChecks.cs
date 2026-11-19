using Reglet;

/// <summary>Custom XML parts: model validation, DOCX package layout, read-back, and that other formats do not break.</summary>
internal static class CustomXmlChecks
{
    internal static int Run()
    {
        try
        {
            RejectsInvalidParts();
            RoundTripsDocx();
            Console.WriteLine("Custom XML: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Custom XML: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RejectsInvalidParts()
    {
        foreach (var make in new Func<CustomXmlPart>[]
        {
            () => new CustomXmlPart { Xml = "<a>" },
            () => new CustomXmlPart { Xml = " " },
            () => new CustomXmlPart { Xml = "<a/>", ItemId = "not a guid" },
        })
        {
            var threw = false;
            try
            {
                make();
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Require(threw, "An invalid custom XML part must be rejected.");
        }
    }

    private static void RoundTripsDocx()
    {
        var parts = new[]
        {
            new CustomXmlPart { Xml = "<data xmlns=\"urn:t\"><name>Ä &amp; Ö</name></data>", ItemId = "{11111111-2222-3333-4444-555555555555}" },
            new CustomXmlPart { Xml = "<other/>" },
        };
        var document = new PortableDocument(
            new DocumentMetadata { Title = "T", CustomXmlParts = parts },
            new Section(new Paragraph(new TextRun("Body"))));

        byte[] Write(PortableDocument source)
        {
            using var buffer = new MemoryStream();
            new DocxDocumentWriter().WriteAsync(source, buffer).GetAwaiter().GetResult();
            return buffer.ToArray();
        }

        var package = Write(document);
        Require(DocxPackageValidator.Validate(package).Count == 0, "DOCX: valid package: " + string.Join("; ", DocxPackageValidator.Validate(package)));
        Require(package.AsSpan().SequenceEqual(Write(document)), "DOCX: deterministic output.");

        var read = DocumentReader.Read(new MemoryStream(package), DocumentFormat.Docx).Document.Metadata.CustomXmlParts;
        Require(read is { Count: 2 }, "DOCX read: two parts.");
        Require(read![0].Xml == parts[0].Xml && read[0].ItemId == "{11111111-2222-3333-4444-555555555555}", "DOCX read: first part and id.");
        Require(read[1].Xml == "<other/>" && Guid.TryParse(read[1].ItemId, out _), "DOCX read: derived id.");

        using var odt = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, odt).GetAwaiter().GetResult();
        Require(odt.Length > 0, "ODT ignores the parts.");
    }
}
