using System.IO.Compression;

namespace Reglet;

/// <summary>
/// compresses streams with System.IO.Compression. Uses ZLibStream, which produces the
/// the zlib frame (RFC 1950) required by FlateDecode.
/// </summary>
internal static class PdfStreamCompressor
{
    public static byte[] Compress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
