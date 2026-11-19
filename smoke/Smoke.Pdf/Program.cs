using Reglet;

// consumer smoke test: references only the built DLLs and produces a PDF.
var document = PortableDocument.Create().Heading("Smoke", level: 1).Paragraph("Hallo").Build();
var path = Path.Combine(Path.GetTempPath(), "reglet-smoke-" + Guid.NewGuid().ToString("N") + ".pdf");

using (var stream = File.Create(path))
{
    new PdfDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult();
}

var bytes = File.ReadAllBytes(path);
if (bytes.Length < 8 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
{
    throw new InvalidOperationException("Smoke.Pdf: kein PDF-Header.");
}

Console.WriteLine($"Smoke.Pdf OK ({bytes.Length} Bytes).");
File.Delete(path);
