using System.Buffers.Binary;

namespace Reglet;

/// <summary>
/// reduces a glyf-based TrueType font to the glyphs that were actually drawn (phase P, P05).
/// The glyph IDs are retained: unused glyphs keep their slot with an empty outline, so the
/// content streams (Identity-H, CIDToGIDMap /Identity) need no remapping and fonts with
/// layout tables (GSUB/GPOS) can be reduced as well — the shaping already happened when the
/// glyph IDs were written. The layout tables themselves are not needed in the PDF and dropped.
/// </summary>
/// <remarks>
/// subsetting is an optimization and must never damage a document. The call therefore
/// returns <c>null</c> if the structure is unexpected, if there is no saving or if the check
/// of the result fails; in these cases the complete file is embedded.
/// </remarks>
internal static class TrueTypeSubsetter
{
    private static readonly string[] RetainedTables = ["name", "OS/2", "cvt ", "fpgm", "prep", "gasp"];

    /// <summary>Builds the reduced font file, or <c>null</c> if the complete font must be embedded.</summary>
    public static byte[]? TrySubset(TrueTypeFont font, IReadOnlySet<ushort> usedGlyphs)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(usedGlyphs);

        foreach (var tag in new[] { "glyf", "loca", "head", "hhea", "hmtx", "maxp" })
        {
            if (!font.TryGetTable(tag, out _, out _))
            {
                return null;
            }
        }

        _ = font.TryGetTable("glyf", out var glyfOffset, out var glyfLength);
        _ = font.TryGetTable("head", out var headOffset, out var headLength);
        if (headLength < 54)
        {
            return null;
        }

        var data = font.Data;
        if (!TryReadGlyphOffsets(font, data, out var offsets))
        {
            return null;
        }

        var used = CollectUsedGlyphs(font, data, offsets, glyfOffset, glyfLength, usedGlyphs);
        if (used is null)
        {
            return null;
        }

        var glyf = BuildGlyf(data, offsets, glyfOffset, used, out var loca);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = glyf,
            ["loca"] = loca,
            ["head"] = BuildHead(data, headOffset),
            ["hhea"] = Copy(font, data, "hhea"),
            ["hmtx"] = Copy(font, data, "hmtx"),
            ["maxp"] = Copy(font, data, "maxp"),
            ["cmap"] = BuildCmap(font, used),
            ["post"] = BuildPost(),
        };

        foreach (var tag in RetainedTables)
        {
            if (font.TryGetTable(tag, out _, out _))
            {
                tables[tag] = Copy(font, data, tag);
            }
        }

        var result = Assemble(tables);
        if (result.Length >= data.Length)
        {
            return null; // no saving
        }

        // validate the result: same glyph count and metrics for every used glyph.
        try
        {
            var check = TrueTypeFont.Load(result);
            if (check.NumGlyphs != font.NumGlyphs || check.UnitsPerEm != font.UnitsPerEm)
            {
                return null;
            }

            foreach (var glyph in used)
            {
                if (check.GetAdvanceWidth((ushort)glyph) != font.GetAdvanceWidth((ushort)glyph))
                {
                    return null;
                }
            }
        }
        catch (Exception exception) when (exception is FontException or UnsupportedFeatureException or ArgumentOutOfRangeException)
        {
            return null;
        }

        return result;
    }

    private static byte[] Copy(TrueTypeFont font, byte[] data, string tag)
    {
        _ = font.TryGetTable(tag, out var offset, out var length);
        return data[offset..(offset + length)];
    }

    private static bool TryReadGlyphOffsets(TrueTypeFont font, byte[] data, out int[] offsets)
    {
        offsets = [];
        if (!font.TryGetTable("loca", out var locaOffset, out var locaLength)
            || !font.TryGetTable("head", out var headOffset, out _))
        {
            return false;
        }

        var count = font.NumGlyphs + 1;
        var longFormat = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(headOffset + 50, 2)) != 0;
        var entrySize = longFormat ? 4 : 2;
        if (locaLength < count * entrySize)
        {
            return false;
        }

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = longFormat
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(locaOffset + i * 4, 4))
                : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(locaOffset + i * 2, 2)) * 2;
        }

        for (var i = 0; i < count - 1; i++)
        {
            if (result[i] > result[i + 1])
            {
                return false;
            }
        }

        // Ascending order bounds every entry by the first and last; offsets >= 2^31 turn negative above.
        _ = font.TryGetTable("glyf", out _, out var glyfLength);
        if (result[0] < 0 || result[^1] > glyfLength)
        {
            return false;
        }

        offsets = result;
        return true;
    }

    private static SortedSet<int>? CollectUsedGlyphs(
        TrueTypeFont font,
        byte[] data,
        int[] offsets,
        int glyfOffset,
        int glyfLength,
        IReadOnlySet<ushort> usedGlyphs)
    {
        var used = new SortedSet<int> { 0 };
        foreach (var glyph in usedGlyphs)
        {
            if (glyph < font.NumGlyphs)
            {
                used.Add(glyph);
            }
        }

        // Composite glyphs: include the referenced components.
        var pending = new Stack<int>(used);
        var seen = new HashSet<int>(used);
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph >= offsets.Length - 1)
            {
                return null;
            }

            var start = glyfOffset + offsets[glyph];
            var end = glyfOffset + offsets[glyph + 1];
            if (end > glyfOffset + glyfLength || start > end)
            {
                return null;
            }

            foreach (var component in ReadComponents(data, start, end))
            {
                if (component >= font.NumGlyphs)
                {
                    return null;
                }

                if (seen.Add(component))
                {
                    used.Add(component);
                    pending.Push(component);
                }
            }
        }

        return used;
    }

    private static IEnumerable<int> ReadComponents(byte[] data, int start, int end)
    {
        if (start + 10 > end || BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(start, 2)) >= 0)
        {
            yield break;
        }

        var position = start + 10;
        while (position + 4 <= end)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
            var glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2, 2));
            yield return glyphIndex;
            position += 4;
            position += (flags & 0x0001) != 0 ? 4 : 2;
            if ((flags & 0x0008) != 0)
            {
                position += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                position += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                position += 8;
            }

            if ((flags & 0x0020) == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>glyf/loca with the original glyph IDs; unused glyphs get an empty outline.</summary>
    private static byte[] BuildGlyf(byte[] data, int[] offsets, int glyfOffset, SortedSet<int> used, out byte[] loca)
    {
        var count = offsets.Length - 1;
        var glyf = new MemoryStream();
        var locaBuffer = new byte[(count + 1) * 4];
        for (var glyph = 0; glyph < count; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaBuffer.AsSpan(glyph * 4, 4), (uint)glyf.Length);
            if (used.Contains(glyph))
            {
                var start = glyfOffset + offsets[glyph];
                var end = glyfOffset + offsets[glyph + 1];
                glyf.Write(data, start, end - start);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(locaBuffer.AsSpan(count * 4, 4), (uint)glyf.Length);
        loca = locaBuffer;
        return glyf.ToArray();
    }

    private static byte[] BuildHead(byte[] data, int headOffset)
    {
        var head = data[headOffset..(headOffset + 54)];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8, 4), 0); // checksumAdjustment
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50, 2), 1); // indexToLocFormat = long
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(52, 2), 0); // glyphDataFormat
        return head;
    }

    /// <summary>A cmap limited to the used glyphs (the PDF addresses glyphs by ID; the cmap keeps the font valid).</summary>
    private static byte[] BuildCmap(TrueTypeFont font, SortedSet<int> used)
    {
        var bmp = new SortedDictionary<int, ushort>();
        var supplementary = new SortedDictionary<int, ushort>();
        foreach (var (codePoint, glyph) in font.EnumerateMappings())
        {
            if (glyph == 0 || !used.Contains(glyph) || codePoint is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            if (codePoint < 0xFFFF)
            {
                bmp.TryAdd(codePoint, glyph);
            }
            else
            {
                supplementary.TryAdd(codePoint, glyph);
            }
        }

        var format4 = BuildFormat4(bmp);
        var format12 = supplementary.Count > 0 ? BuildFormat12(bmp, supplementary) : null;

        var records = new List<(ushort Platform, ushort Encoding, byte[] Table)>();
        if (format12 is not null)
        {
            records.Add((3, 10, format12));
        }

        records.Add((3, 1, format4));

        var headerSize = 4 + (records.Count * 8);
        var total = headerSize + records.Sum(record => record.Table.Length);
        var cmap = new byte[total];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), (ushort)records.Count);
        var position = headerSize;
        for (var i = 0; i < records.Count; i++)
        {
            var record = 4 + (i * 8);
            BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(record, 2), records[i].Platform);
            BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(record + 2, 2), records[i].Encoding);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(record + 4, 4), (uint)position);
            records[i].Table.CopyTo(cmap, position);
            position += records[i].Table.Length;
        }

        return cmap;
    }

    /// <summary>cmap format 4: runs of consecutive codes with consecutive glyphs share a segment.</summary>
    private static byte[] BuildFormat4(SortedDictionary<int, ushort> bmp)
    {
        var segments = new List<(int Start, int End, ushort Glyph)>();
        foreach (var (code, glyph) in bmp)
        {
            if (segments.Count > 0
                && segments[^1] is var last
                && code == last.End + 1
                && glyph == last.Glyph + (code - last.Start))
            {
                segments[^1] = (last.Start, code, last.Glyph);
            }
            else
            {
                segments.Add((code, code, glyph));
            }
        }

        // The subtable length is a 16-bit value; a larger mapping stays in format 12 only.
        if (16 + ((segments.Count + 1) * 8) > ushort.MaxValue)
        {
            segments.Clear();
        }

        var segCount = segments.Count + 1;
        var length = 16 + (segCount * 8);
        var format4 = new byte[length];

        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(2, 2), (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(6, 2), (ushort)(segCount * 2));

        var power = 1;
        var selector = 0;
        while (power * 2 <= segCount)
        {
            power *= 2;
            selector++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(8, 2), (ushort)(power * 2));
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(10, 2), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(12, 2), (ushort)((segCount * 2) - (power * 2)));

        var endBase = 14;
        var startBase = endBase + (segCount * 2) + 2;
        var deltaBase = startBase + (segCount * 2);
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end, glyph) = segments[i];
            BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(endBase + (i * 2), 2), (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(startBase + (i * 2), 2), (ushort)start);
            BinaryPrimitives.WriteInt16BigEndian(format4.AsSpan(deltaBase + (i * 2), 2), unchecked((short)(glyph - start)));
        }

        var lastIndex = segments.Count;
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(endBase + (lastIndex * 2), 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(startBase + (lastIndex * 2), 2), 0xFFFF);
        BinaryPrimitives.WriteInt16BigEndian(format4.AsSpan(deltaBase + (lastIndex * 2), 2), 1);
        return format4;
    }

    private static byte[] BuildFormat12(SortedDictionary<int, ushort> bmp, SortedDictionary<int, ushort> supplementary)
    {
        var all = new SortedDictionary<int, ushort>(bmp);
        foreach (var (code, glyph) in supplementary)
        {
            all[code] = glyph;
        }

        var groups = new List<(int Start, int End, ushort StartGlyph)>();
        foreach (var (code, glyph) in all)
        {
            if (groups.Count > 0)
            {
                var previous = groups[^1];
                if (code == previous.End + 1 && glyph == previous.StartGlyph + (previous.End - previous.Start) + 1)
                {
                    groups[^1] = (previous.Start, code, previous.StartGlyph);
                    continue;
                }
            }

            groups.Add((code, code, glyph));
        }

        var format12 = new byte[16 + groups.Count * 12];
        BinaryPrimitives.WriteUInt16BigEndian(format12.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(format12.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(4, 4), (uint)format12.Length);
        BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(12, 4), (uint)groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = 16 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(group, 4), (uint)groups[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(group + 4, 4), (uint)groups[i].End);
            BinaryPrimitives.WriteUInt32BigEndian(format12.AsSpan(group + 8, 4), groups[i].StartGlyph);
        }

        return format12;
    }

    private static byte[] BuildPost()
    {
        var post = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(0, 4), 0x00030000);
        return post;
    }

    private static byte[] Assemble(Dictionary<string, byte[]> tables)
    {
        var sorted = tables.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        var count = sorted.Length;

        var headerSize = 12 + count * 16;
        var offset = headerSize;
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = offset;
            offset += (sorted[i].Value.Length + 3) & ~3;
        }

        var font = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4, 2), (ushort)count);

        var power = 1;
        var selector = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            selector++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6, 2), (ushort)(power * 16));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8, 2), (ushort)selector);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10, 2), (ushort)(count * 16 - power * 16));

        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var tag = sorted[i].Key;
            for (var c = 0; c < 4; c++)
            {
                font[record + c] = (byte)tag[c];
            }

            var checksum = Checksum(sorted[i].Value);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 4, 4), checksum);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8, 4), (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 12, 4), (uint)sorted[i].Value.Length);
            sorted[i].Value.CopyTo(font, offsets[i]);
        }

        // head.checksumAdjustment = 0xB1B0AFBA − checksum of the whole file (computed with the
        // adjustment set to 0; the head table's directory checksum also excludes it).
        var adjustment = unchecked(0xB1B0AFBA - Checksum(font));
        var headIndex = Array.FindIndex(sorted, entry => entry.Key == "head");
        if (headIndex >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(offsets[headIndex] + 8, 4), adjustment);
        }

        return font;
    }

    private static uint Checksum(byte[] table)
    {
        uint sum = 0;
        var padded = (table.Length + 3) & ~3;
        for (var i = 0; i < padded; i += 4)
        {
            uint value = 0;
            for (var b = 0; b < 4; b++)
            {
                value <<= 8;
                if (i + b < table.Length)
                {
                    value |= table[i + b];
                }
            }

            sum = unchecked(sum + value);
        }

        return sum;
    }
}
