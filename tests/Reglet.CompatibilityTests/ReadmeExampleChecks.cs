using Reglet;

/// <summary>
/// compile test of the five-minute example shown in the README. The structure matches exactly
/// the example and is then written to all three formats.
/// </summary>
internal static class ReadmeExampleChecks
{
    internal static int Run()
    {
        try
        {
            var document = BuildReadmeExample();

            if (document.Sections.Count != 1 || document.Sections[0].Blocks.Count != 4)
            {
                throw new InvalidOperationException("README example does not produce the expected structure.");
            }

            foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
            {
                using var stream = new MemoryStream();
                DocumentWriter.WriteAsync(document, stream, format).GetAwaiter().GetResult();
                if (stream.Length == 0)
                {
                    throw new InvalidOperationException($"README example wrote no {format}.");
                }
            }

            Console.WriteLine("README example (E10): compiles and writes DOCX, ODT and PDF.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("README example (E10): " + exception);
            return 1;
        }
    }

    private static PortableDocument BuildReadmeExample()
        => PortableDocument.Create()
            .Metadata(title: "Monthly report", author: "Example Ltd")
            .Heading("March 2026")
            .Paragraph(p => p.Text("Status: ").Bold("on track"))
            .BulletList("Layout finished", "Proofs sent", "Printing next week")
            .Table(t => t.Header("Metric", "Value").Row("Pages", "40").Row("Pictures", "12"))
            .Build();
}
