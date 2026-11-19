using System.Text;
using System.Text.RegularExpressions;

namespace Reglet;

/// <summary>What a read PDF says about itself and later versions of the file must keep.</summary>
internal static partial class PdfAIdentity
{
    /// <summary>The claimed PDF/A part; the XMP stream of a PDF/A file is never filtered.</summary>
    public static int? Part(PdfFile file)
    {
        if (file.Dictionary(file.Trailer.Get("Root"))?.Get("Metadata") is not PdfReference reference
            || file.GetObject(reference.ObjectNumber) is not PdfStreamValue stream)
        {
            return null;
        }

        var match = PartPattern().Match(Encoding.UTF8.GetString(stream.Raw));
        return match.Success ? match.Groups[1].Value[0] - '0' : null;
    }

    /// <summary>The conformance to write again.</summary>
    /// <remarks>shortcut: only the parts this library writes keep their claim; add part 1 and 4 to BuildXmp when they are needed.</remarks>
    public static PdfAConformance Conformance(PdfFile file) => Part(file) switch
    {
        2 => PdfAConformance.PdfA2b,
        3 => PdfAConformance.PdfA3b,
        _ => PdfAConformance.None,
    };

    /// <summary>The file identifier of the trailer, so a new version stays the same document.</summary>
    public static (byte[]? Original, byte[]? Changing) FileId(PdfFile file)
        => file.Trailer.Get("ID") is PdfArray { Items.Count: 2 } id && id.Items[0] is PdfString original && id.Items[1] is PdfString changing
            ? (original.Bytes, changing.Bytes)
            : (null, null);

    [GeneratedRegex("pdfaid:part(?:>|=[\"\'])\\s*(\\d)")]
    private static partial Regex PartPattern();
}
