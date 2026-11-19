using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// shared helpers for reading the compressed content streams from produced
/// PDF bytes (golden, writer and template tests).
/// </summary>
internal static class PdfStreams
{
    /// <summary>Decompresses all <c>/Filter /FlateDecode</c> streams and concatenates them.</summary>
    public static string Decompress(byte[] pdf)
    {
        var latin = Encoding.Latin1.GetString(pdf);
        var result = new StringBuilder();
        var index = 0;

        while (index < latin.Length)
        {
            var filter = latin.IndexOf("/Filter /FlateDecode", index, StringComparison.Ordinal);
            if (filter < 0)
            {
                break;
            }

            var streamStart = latin.IndexOf("stream\n", filter, StringComparison.Ordinal) + "stream\n".Length;
            var streamEnd = latin.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            zlib.CopyTo(decompressed);
            result.Append(Encoding.Latin1.GetString(decompressed.ToArray()));
            index = streamEnd;
        }

        return result.ToString();
    }

    /// <summary>
    /// Decompresses the page content streams in page order, so a test can tell where on which
    /// page a text piece was placed. Image streams are skipped: they hold binary pixel data and
    /// carry no text operators.
    /// </summary>
    public static IReadOnlyList<string> DecompressPages(byte[] pdf)
    {
        var latin = Encoding.Latin1.GetString(pdf);
        var pages = new List<string>();
        var index = 0;

        while (index < latin.Length)
        {
            var filter = latin.IndexOf("/Filter /FlateDecode", index, StringComparison.Ordinal);
            if (filter < 0)
            {
                break;
            }

            var streamStart = latin.IndexOf("stream\n", filter, StringComparison.Ordinal) + "stream\n".Length;
            var streamEnd = latin.IndexOf("\nendstream", streamStart, StringComparison.Ordinal);
            using var compressed = new MemoryStream(pdf[streamStart..streamEnd]);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            zlib.CopyTo(decompressed);
            var text = Encoding.Latin1.GetString(decompressed.ToArray());
            if (text.Contains(" Td", StringComparison.Ordinal) || text.Contains(" Tj", StringComparison.Ordinal))
            {
                pages.Add(text);
            }

            index = streamEnd;
        }

        return pages;
    }

    /// <summary>Extracts the text pieces of all pages shown via <c>( … ) Tj</c>.</summary>
    public static string ExtractText(byte[] pdf)
        => string.Join(" ", Regex.Matches(Decompress(pdf), @"\(([^)]*)\) Tj").Select(match => match.Groups[1].Value));
}
