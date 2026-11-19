using System.Buffers.Binary;
using System.Text;

namespace Reglet;

/// <summary>Builds synthetic sfnt structures for the TrueType tests.</summary>
internal static class SyntheticTrueTypeFont
{
    public static byte[] Build(params (string Tag, byte[] Data)[] tables)
        => Build(0x00010000, tables);

    public static byte[] Build(uint version, params (string Tag, byte[] Data)[] tables)
    {
        var font = new byte[12 + tables.Length * 16 + tables.Sum(table => table.Data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0, 4), version);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4, 2), (ushort)tables.Length);

        var offset = 12 + tables.Length * 16;
        for (var i = 0; i < tables.Length; i++)
        {
            var record = 12 + i * 16;
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(font, record);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8, 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 12, 4), (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(font, offset);
            offset += tables[i].Data.Length;
        }

        return font;
    }

    public static byte[] WithVersion(uint version) => Build(version, ("head", new byte[54]));

    public static byte[] Empty(int length) => new byte[length];

    public static byte[] Head(int unitsPerEm, short xMin, short yMin, short xMax, short yMax)
    {
        var head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18, 2), (ushort)unitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36, 2), xMin);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38, 2), yMin);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40, 2), xMax);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42, 2), yMax);
        return head;
    }

    public static byte[] Hhea(short ascender, short descender, short lineGap, ushort numberOfHMetrics)
    {
        var hhea = new byte[36];
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4, 2), ascender);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6, 2), descender);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(8, 2), lineGap);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34, 2), numberOfHMetrics);
        return hhea;
    }

    public static byte[] Maxp(ushort numGlyphs)
    {
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4, 2), numGlyphs);
        return maxp;
    }

    public static byte[] Hmtx(params (ushort Advance, short Lsb)[] metrics)
    {
        var hmtx = new byte[metrics.Length * 4];
        for (var i = 0; i < metrics.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4, 2), metrics[i].Advance);
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2, 2), metrics[i].Lsb);
        }

        return hmtx;
    }

    public static byte[] Name(string postScriptName)
    {
        var text = Encoding.BigEndianUnicode.GetBytes(postScriptName);
        var name = new byte[18 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(4, 2), 18);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(10, 2), 0x409);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(12, 2), 6);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(14, 2), (ushort)text.Length);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(16, 2), 0);
        text.CopyTo(name, 18);
        return name;
    }

    public static byte[] Cmap4()
    {
        var cmap = new byte[46];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14, 2), 34);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(18, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(26, 2), 'A');
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(28, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(32, 2), 'A');
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(34, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(38, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(40, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(44, 2), 3);
        return cmap;
    }

    public static byte[] Cmap12()
    {
        var cmap = new byte[52];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16, 4), 40);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28, 4), 'A');
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32, 4), 'A');
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(40, 4), 0x1F600);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(44, 4), 0x1F600);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(48, 4), 4);
        return cmap;
    }

    public static byte[] Cmap12(params (int Code, ushort Glyph)[] mappings)
    {
        var ordered = mappings.OrderBy(mapping => mapping.Code).ToArray();
        var cmap = new byte[12 + 16 + ordered.Length * 12];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16, 4), (uint)(16 + ordered.Length * 12));
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(20, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24, 4), (uint)ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var group = 28 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(group, 4), (uint)ordered[i].Code);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(group + 4, 4), (uint)ordered[i].Code);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(group + 8, 4), ordered[i].Glyph);
        }

        return cmap;
    }

    public static byte[] U16(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), values[i]);
        }

        return bytes;
    }

    public static byte[] I16(params short[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), values[i]);
        }

        return bytes;
    }
}
