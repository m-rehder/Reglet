using Reglet;

// L09: consumer that references only the packed NuGet (local feed).
var document = PortableDocument.Create()
    .Heading("Paket", level: 1)
    .Paragraph("Produced from the NuGet package.")
    .Build();

var path = Path.Combine(Path.GetTempPath(), "reglet-package-" + Guid.NewGuid().ToString("N") + ".pdf");
var result = await DocumentWriter.WriteAsync(document, path);

if (result.HasWarnings)
{
    throw new InvalidOperationException("Unexpected warnings during package consumption.");
}

var bytes = File.ReadAllBytes(path);
if (bytes.Length < 8 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P')
{
    throw new InvalidOperationException("Smoke.Package: kein PDF-Header.");
}

Console.WriteLine($"Smoke.Package OK ({bytes.Length} Bytes).");
File.Delete(path);
