using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// fixed document models for golden and render tests: short text, long table, several
/// pages, image, Unicode and edge cases.
/// </summary>
internal static class RenderFixtures
{
    internal static IEnumerable<(string Name, PortableDocument Document)> All()
    {
        yield return ("short-text", ShortText());
        yield return ("long-table", LongTable());
        yield return ("multiple-pages", MultiplePages());
        yield return ("with-image", WithImage());
        yield return ("unicode", UnicodeText());
        yield return ("edge-cases", EdgeCases());
        yield return ("chart", ChartFixture());
    }

    private static PortableDocument ShortText()
        => PortableDocument.Create()
            .Metadata(title: "Kurz", author: "Reglet")
            .Heading("Heading", level: 1)
            .Paragraph(p => p.Text("Hallo ").Bold("Welt"))
            .Build();

    private static PortableDocument LongTable()
        => PortableDocument.Create()
            .Heading("Table", level: 2)
            .Table(table =>
            {
                table.Header("Kennzahl", "Wert", "Unit");
                for (var index = 1; index <= 20; index++)
                {
                    table.Row($"Kennzahl {index}", (index * 1000).ToString(), "Piece");
                }

                table.ColumnWidths(Length.FromCentimeters(5), Length.FromCentimeters(4), Length.FromCentimeters(3));
            })
            .Build();

    private static PortableDocument MultiplePages()
        => PortableDocument.Create()
            .Heading("Many pages", level: 1)
            .Paragraph(string.Join(' ', Enumerable.Repeat("A longer paragraph that is wrapped.", 12)))
            .PageBreak()
            .Heading("Second part", level: 2)
            .BulletList("one", "two", "three")
            .NumberedList("first", "second")
            .PageBreak()
            .Paragraph("Last page")
            .Build();

    private static PortableDocument WithImage()
        => PortableDocument.Create()
            .Heading("Image", level: 1)
            .Image(CreatePng(32, 16), ImageMediaType.Png, altText: "Quadrat")
            .Paragraph("After the image")
            .Build();

    private static PortableDocument UnicodeText()
        => PortableDocument.Create()
            .Metadata(title: "Greetings", language: "de-DE")
            .Paragraph("Greetings – Ω ≤ café 42")
            .Build();

    private static PortableDocument ChartFixture()
        => new(new DocumentMetadata(), new Section(new Block[] {
            new Chart(ChartKind.Column,
                new[] { new ChartSeries("2025", new[] { 3.0, 5, 4 }), new ChartSeries("2026", new[] { 4.0, 6, 7 }) },
                new[] { "Q1", "Q2", "Q3" }) { Title = "Umsatz", Minimum = 0, Maximum = 8 },
            new Chart(ChartKind.Pie, new[] { new ChartSeries("Anteil", new[] { 1.0, 2, 3 }) }, new[] { "A", "B", "C" }) }));

    private static PortableDocument EdgeCases()
        => PortableDocument.Create()
            .Paragraph(string.Empty)
            .Paragraph("leading  and   trailing spaces ")
            .Heading("Stufe 6", level: 6)
            .BulletList(string.Empty)
            .Build();

    internal static byte[] CreatePng(int width, int height, byte red = 0, byte green = 0, byte blue = 0)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;

        var pixels = new byte[height * (1 + width * 3)];
        var position = 0;
        for (var y = 0; y < height; y++)
        {
            pixels[position++] = 0;
            for (var x = 0; x < width; x++)
            {
                pixels[position++] = red;
                pixels[position++] = green;
                pixels[position++] = blue;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        AppendChunk(bytes, "IHDR", ihdr);
        AppendChunk(bytes, "IDAT", compressed.ToArray());
        AppendChunk(bytes, "IEND", []);
        return bytes.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(Encoding.ASCII.GetBytes(type));
        target.AddRange(data);
        target.AddRange(new byte[4]);
    }
}
