using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// creates a ToUnicode CMap (CMapType 2) that maps glyph IDs to Unicode so that
/// text remains searchable and copyable in the PDF.
/// </summary>
internal static class PdfToUnicodeCmap
{
    public static byte[] Build(IEnumerable<(ushort GlyphId, int CodePoint)> mappings)
        => Build(mappings
            .Where(mapping => mapping.CodePoint is >= 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF))
            .Select(mapping => (mapping.GlyphId, char.ConvertFromUtf32(mapping.CodePoint))));

    /// <summary>Builds the CMap from glyph → text (a ligature glyph maps to several characters).</summary>
    public static byte[] Build(IEnumerable<(ushort GlyphId, string Text)> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var byGlyph = new SortedDictionary<ushort, string>();
        foreach (var (glyphId, text) in mappings)
        {
            if (text.Length > 0)
            {
                byGlyph.TryAdd(glyphId, text);
            }
        }

        var builder = new StringBuilder();
        builder.Append("/CIDInit /ProcSet findresource begin\n");
        builder.Append("12 dict begin\nbegincmap\n");
        builder.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        builder.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        builder.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        foreach (var chunk in byGlyph.Chunk(100))
        {
            builder.Append(chunk.Length.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (glyphId, text) in chunk)
            {
                var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
                builder
                    .Append('<').Append(glyphId.ToString("X4", CultureInfo.InvariantCulture))
                    .Append("> <").Append(Convert.ToHexString(utf16)).Append(">\n");
            }

            builder.Append("endbfchar\n");
        }

        builder.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
