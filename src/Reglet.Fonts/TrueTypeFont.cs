using System.Buffers.Binary;
using System.Text;

namespace Reglet;

/// <summary>
/// minimal TrueType parser (glyf-based). Reads offset table, table directory, the
/// metrics from head, hhea, maxp and hmtx, the names from name as well as style and
/// embedding rights from OS/2 and head. CFF/OTTO is rejected; a font of a collection (TTC) is first cut out of it
/// (<see cref="CollectionMembers"/>).
/// </summary>
internal sealed class TrueTypeFont
{
    private const uint SfntTrueType = 0x00010000;
    private const uint SfntAppleTrue = 0x74727565;
    private const uint SfntCff = 0x4F54544F;        // 'OTTO'
    private const uint SfntCollection = 0x74746366; // 'ttcf'

    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly int _hmtxOffset;
    private readonly int _numberOfHMetrics;
    private readonly int _cmapOffset;
    private readonly int _cmapFormat;
    private readonly int _cmapEnd;

    public int UnitsPerEm { get; }

    public int NumGlyphs { get; }

    public int Ascender { get; }

    public int Descender { get; }

    public int LineGap { get; }

    public short XMin { get; }

    public short YMin { get; }

    public short XMax { get; }

    public short YMax { get; }

    public string PostScriptName { get; }

    /// <summary>Family name (name ID 16, otherwise 1; failing that the PostScript name).</summary>
    public string FamilyName { get; }

    /// <summary>The style name, e.g. "Bold Italic" (name ID 17, else 2; "Regular" as fallback).</summary>
    public string SubfamilyName { get; }

    /// <summary>Weight per OS/2 <c>usWeightClass</c> (400 = normal, 700 = bold).</summary>
    public int WeightClass { get; }

    /// <summary>Bold style (OS/2 fsSelection BOLD, head.macStyle or weight ≥ 600).</summary>
    public bool IsBold { get; }

    /// <summary>Italic or oblique style (OS/2 fsSelection ITALIC/OBLIQUE or head.macStyle).</summary>
    public bool IsItalic { get; }

    /// <summary>Embedding rights per OS/2 <c>fsType</c> (0 if the table is missing: installable).</summary>
    public ushort EmbeddingFlags { get; }

    private TrueTypeFont(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        _data = data;
        _tables = tables;

        var head = Require(tables, "head");
        var hhea = Require(tables, "hhea");
        var maxp = Require(tables, "maxp");
        var hmtx = Require(tables, "hmtx");
        RequireLength(head, "head", 54);
        RequireLength(hhea, "hhea", 36);
        RequireLength(maxp, "maxp", 6);
        if (hmtx.Length < ReadU16(data, hhea.Offset + 34) * 4)
        {
            throw new FontException("Table 'hmtx' is too short for the horizontal metrics.");
        }

        UnitsPerEm = ReadU16(data, head.Offset + 18);
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw new FontException($"unitsPerEm {UnitsPerEm} is outside the valid range 16-16384.");
        }

        XMin = ReadI16(data, head.Offset + 36);
        YMin = ReadI16(data, head.Offset + 38);
        XMax = ReadI16(data, head.Offset + 40);
        YMax = ReadI16(data, head.Offset + 42);

        Ascender = ReadI16(data, hhea.Offset + 4);
        Descender = ReadI16(data, hhea.Offset + 6);
        LineGap = ReadI16(data, hhea.Offset + 8);
        _numberOfHMetrics = ReadU16(data, hhea.Offset + 34);

        NumGlyphs = ReadU16(data, maxp.Offset + 4);
        _hmtxOffset = hmtx.Offset;

        var names = tables.TryGetValue("name", out var name)
            ? ReadNames(data, name)
            : new Dictionary<int, string>();
        PostScriptName = names.GetValueOrDefault(6) ?? "EmbeddedFont";
        FamilyName = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? PostScriptName;
        SubfamilyName = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "Regular";

        var macStyle = ReadU16(data, head.Offset + 44);
        var fsSelection = 0;
        WeightClass = (macStyle & 0x1) != 0 ? 700 : 400;
        if (tables.TryGetValue("OS/2", out var os2))
        {
            // Version 0 is 78 bytes long; it is read up to fsSelection (offset 62).
            RequireLength(os2, "OS/2", 64);
            WeightClass = ReadU16(data, os2.Offset + 4);
            EmbeddingFlags = ReadU16(data, os2.Offset + 8);
            fsSelection = ReadU16(data, os2.Offset + 62);
        }

        IsBold = (fsSelection & 0x20) != 0 || (macStyle & 0x1) != 0 || WeightClass >= 600;
        IsItalic = (fsSelection & 0x201) != 0 || (macStyle & 0x2) != 0;

        var cmap = Require(tables, "cmap");
        (_cmapOffset, _cmapFormat) = SelectCmap(data, cmap);
        _cmapEnd = cmap.Offset + cmap.Length;
    }

    public IReadOnlyCollection<string> TableTags => _tables.Keys;

    /// <summary>Raw data of the font file (for OpenType layout evaluation).</summary>
    internal byte[] Data => _data;

    public bool TryGetTable(string tag, out int offset, out int length)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (_tables.TryGetValue(tag, out var table))
        {
            offset = table.Offset;
            length = table.Length;
            return true;
        }

        offset = 0;
        length = 0;
        return false;
    }

    /// <summary>Maps a Unicode code point to a glyph ID via the cmap.</summary>
    public ushort GetGlyphId(int codePoint)
        => _cmapFormat == 12 ? LookupFormat12(codePoint) : LookupFormat4(codePoint);

    /// <summary>Enumerates all (code point, glyph) pairs of the cmap.</summary>
    public IEnumerable<(int CodePoint, ushort GlyphId)> EnumerateMappings()
    {
        if (_cmapFormat == 12)
        {
            var groupCount = (int)ReadU32(_data, _cmapOffset + 12);
            for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
            {
                var group = _cmapOffset + 16 + groupIndex * 12;
                var start = (int)ReadU32(_data, group);
                var end = (int)ReadU32(_data, group + 4);
                var startGlyph = (int)ReadU32(_data, group + 8);

                for (var codePoint = start; codePoint <= end; codePoint++)
                {
                    yield return (codePoint, (ushort)(startGlyph + (codePoint - start)));
                }
            }

            yield break;
        }

        for (var codePoint = 0; codePoint <= 0xFFFF; codePoint++)
        {
            var glyph = LookupFormat4(codePoint);
            if (glyph != 0)
            {
                yield return (codePoint, glyph);
            }
        }
    }

    /// <summary>Returns the advance width of a glyph in font units.</summary>
    public int GetAdvanceWidth(ushort glyphId)
    {
        if (_numberOfHMetrics == 0)
        {
            return 0;
        }

        var index = glyphId < _numberOfHMetrics ? glyphId : _numberOfHMetrics - 1;
        return ReadU16(_data, _hmtxOffset + index * 4);
    }

    public static TrueTypeFont Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 12)
        {
            throw new FontException("File is too short for an sfnt structure.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        switch (version)
        {
            case SfntCff:
                throw new UnsupportedFeatureException(
                    "CFF-based OpenType fonts (OTTO) are not supported in version 1.");
            case SfntCollection:
                throw new UnsupportedFeatureException("A TrueType collection (ttcf) holds several fonts: load one of them by its family name or from the list of the collection.");
            case SfntTrueType:
            case SfntAppleTrue:
                break;
            default:
                throw new FontException($"Unknown sfnt version 0x{version:X8}.");
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));
        var directoryEnd = 12 + numTables * 16;
        if (directoryEnd > data.Length)
        {
            throw new FontException("Table directory extends beyond the end of the file.");
        }

        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(data, record, 4);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8, 4));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12, 4));

            if (offset < 0 || length < 0 || (long)offset + length > data.Length)
            {
                throw new FontException($"Table '{tag}' is outside the file.");
            }

            tables[tag] = (offset, length);
        }

        try
        {
            return new TrueTypeFont(data, tables);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FontException("A font table contains invalid offsets or lengths.", exception);
        }
    }

    /// <summary>
    /// The fonts of a file as standalone sfnt files: a TrueType collection (<c>ttcf</c>) gives one per font, each with
    /// only its own tables, so that only the chosen font is embedded; any other file is its only font.
    /// </summary>
    /// <exception cref="FontException">The collection header or a table lies outside the file.</exception>
    public static IReadOnlyList<byte[]> CollectionMembers(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) != SfntCollection)
        {
            return [data];
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        if (count == 0 || 12L + (count * 4L) > data.Length)
        {
            throw new FontException("The collection header lists more fonts than the file holds.");
        }

        var members = new List<byte[]>((int)count);
        for (var index = 0; index < count; index++)
        {
            members.Add(ExtractMember(data, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + (index * 4), 4))));
        }

        return members;
    }

    /// <summary>Copies one font of a collection, whose table offsets count from the start of the collection, into a file of its own.</summary>
    private static byte[] ExtractMember(byte[] data, uint start)
    {
        if (start > data.Length - 12L)
        {
            throw new FontException("A font of the collection lies outside the file.");
        }

        var offset = (int)start;
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4, 2));
        var directory = 12 + (numTables * 16);
        if (offset + (long)directory > data.Length)
        {
            throw new FontException("The table directory of a font of the collection lies outside the file.");
        }

        var tables = new List<(int Record, int Offset, int Length)>(numTables);
        long size = directory;
        for (var table = 0; table < numTables; table++)
        {
            var record = offset + 12 + (table * 16);
            var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12, 4));
            if ((long)tableOffset + length > data.Length)
            {
                throw new FontException($"Table '{Encoding.ASCII.GetString(data, record, 4)}' of a font of the collection lies outside the file.");
            }

            tables.Add((record, (int)tableOffset, (int)length));
            size += (length + 3) & ~3L;
        }

        var result = new byte[size];
        data.AsSpan(offset, 12).CopyTo(result);
        var position = directory;
        for (var table = 0; table < tables.Count; table++)
        {
            var (record, tableOffset, length) = tables[table];
            var target = 12 + (table * 16);
            data.AsSpan(record, 8).CopyTo(result.AsSpan(target, 8));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(target + 8, 4), (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(target + 12, 4), (uint)length);
            data.AsSpan(tableOffset, length).CopyTo(result.AsSpan(position));
            position += (length + 3) & ~3;
        }

        return result;
    }

    private static (int Offset, int Length) Require(
        Dictionary<string, (int Offset, int Length)> tables,
        string tag)
        => tables.TryGetValue(tag, out var table)
            ? table
            : throw new FontException($"Required table '{tag}' is missing.");

    private static void RequireLength((int Offset, int Length) table, string tag, int minimum)
    {
        if (table.Length < minimum)
        {
            throw new FontException($"Table '{tag}' is too short.");
        }
    }

    /// <summary>
    /// reads the name entries of the name table. Windows Unicode entries are preferred
    /// (platform 3) in US English, then arbitrary Unicode entries, then Mac Roman.
    /// </summary>
    private static Dictionary<int, string> ReadNames(byte[] data, (int Offset, int Length) name)
    {
        RequireLength(name, "name", 6);
        var baseOffset = name.Offset;
        var count = ReadU16(data, baseOffset + 2);
        var storageOffset = ReadU16(data, baseOffset + 4);
        if (6L + count * 12 > name.Length || storageOffset > name.Length)
        {
            throw new FontException("Table 'name' contains invalid offsets.");
        }

        var storage = baseOffset + storageOffset;
        var best = new Dictionary<int, (int Rank, string Value)>();

        for (var i = 0; i < count; i++)
        {
            var record = baseOffset + 6 + i * 12;
            var platformId = ReadU16(data, record);
            var languageId = ReadU16(data, record + 4);
            var nameId = ReadU16(data, record + 6);
            if (nameId is not (1 or 2 or 6 or 16 or 17))
            {
                continue;
            }

            var length = ReadU16(data, record + 8);
            var offset = ReadU16(data, record + 10);
            var start = storage + offset;
            if ((long)storageOffset + offset + length > name.Length)
            {
                throw new FontException("A name extends beyond the 'name' table.");
            }

            var rank = platformId switch
            {
                3 when languageId == 0x409 => 0,
                3 or 0 => 1,
                1 => 2,
                _ => -1,
            };
            if (rank < 0 || (best.TryGetValue(nameId, out var existing) && existing.Rank <= rank))
            {
                continue;
            }

            if (platformId != 1 && length % 2 != 0)
            {
                throw new FontException("A UTF-16 name has an odd byte count.");
            }

            var value = platformId == 1
                ? Encoding.Latin1.GetString(data, start, length)
                : Encoding.BigEndianUnicode.GetString(data, start, length);
            if (value.Length > 0)
            {
                best[nameId] = (rank, value);
            }
        }

        return best.ToDictionary(entry => entry.Key, entry => entry.Value.Value);
    }

    private static (int Offset, int Format) SelectCmap(byte[] data, (int Offset, int Length) cmap)
    {
        RequireLength(cmap, "cmap", 4);
        var baseOffset = cmap.Offset;
        var count = ReadU16(data, baseOffset + 2);
        if (4L + count * 8 > cmap.Length)
        {
            throw new FontException("The cmap directory extends beyond the table.");
        }

        int? format4 = null;
        int? format12 = null;

        for (var i = 0; i < count; i++)
        {
            var record = baseOffset + 4 + i * 8;
            var platformId = ReadU16(data, record);
            var relativeOffset = ReadU32(data, record + 4);
            if (relativeOffset > cmap.Length - 2)
            {
                throw new FontException("A cmap subtable is outside the table.");
            }

            var subtable = baseOffset + (int)relativeOffset;
            var format = ReadU16(data, subtable);

            if (format == 12)
            {
                if (relativeOffset > cmap.Length - 16)
                {
                    throw new FontException("cmap format 12 subtable is too short.");
                }

                var subtableLength = ReadU32(data, subtable + 4);
                var groupCount = ReadU32(data, subtable + 12);
                if (subtableLength > cmap.Length - relativeOffset
                    || 16L + (long)groupCount * 12 > subtableLength)
                {
                    throw new FontException("cmap format 12 subtable is invalid.");
                }

                for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
                {
                    var group = subtable + 16 + (int)groupIndex * 12;
                    var start = ReadU32(data, group);
                    var end = ReadU32(data, group + 4);
                    var startGlyph = ReadU32(data, group + 8);
                    if (start > end || end > 0x10FFFF || startGlyph + (long)end - start > ushort.MaxValue)
                    {
                        throw new FontException("cmap format 12 group contains invalid code points or glyphs.");
                    }
                }

                format12 ??= subtable;
            }
            else if (format == 4 && (platformId == 0 || platformId == 3))
            {
                if (relativeOffset > cmap.Length - 16)
                {
                    throw new FontException("cmap format 4 subtable is too short.");
                }

                var subtableLength = ReadU16(data, subtable + 2);
                var segmentBytes = ReadU16(data, subtable + 6);
                if (subtableLength > cmap.Length - relativeOffset
                    || subtableLength < 16
                    || segmentBytes == 0
                    || segmentBytes % 2 != 0
                    || 16L + segmentBytes / 2 * 8 > subtableLength)
                {
                    throw new FontException("cmap format 4 subtable is invalid.");
                }

                format4 ??= subtable;
            }
        }

        if (format12 is int twelve)
        {
            return (twelve, 12);
        }

        if (format4 is int four)
        {
            return (four, 4);
        }

        throw new UnsupportedFeatureException("No cmap subtable in format 4 or 12 found.");
    }

    private ushort LookupFormat4(int codePoint)
    {
        if (codePoint is < 0 or > 0xFFFF)
        {
            return 0;
        }

        var segCount = ReadU16(_data, _cmapOffset + 6) / 2;
        var endBase = _cmapOffset + 14;
        var startBase = endBase + segCount * 2 + 2;
        var deltaBase = startBase + segCount * 2;
        var rangeBase = deltaBase + segCount * 2;

        for (var i = 0; i < segCount; i++)
        {
            var end = ReadU16(_data, endBase + i * 2);
            if (codePoint > end)
            {
                continue;
            }

            var start = ReadU16(_data, startBase + i * 2);
            if (codePoint < start)
            {
                return 0;
            }

            var delta = ReadI16(_data, deltaBase + i * 2);
            var rangeOffset = ReadU16(_data, rangeBase + i * 2);
            if (rangeOffset == 0)
            {
                return (ushort)((codePoint + delta) & 0xFFFF);
            }

            // idRangeOffset is relative to its own field (not to the start of glyphIdArray).
            var glyphAddress = rangeBase + i * 2 + rangeOffset + (codePoint - start) * 2;
            if (glyphAddress + 2 > _cmapEnd)
            {
                return 0;
            }

            var glyph = ReadU16(_data, glyphAddress);
            return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
        }

        return 0;
    }

    private ushort LookupFormat12(int codePoint)
    {
        var groupCount = (int)ReadU32(_data, _cmapOffset + 12);
        var groupsBase = _cmapOffset + 16;

        var low = 0;
        var high = groupCount - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var group = groupsBase + mid * 12;
            var start = (int)ReadU32(_data, group);
            var end = (int)ReadU32(_data, group + 4);

            if (codePoint < start)
            {
                high = mid - 1;
            }
            else if (codePoint > end)
            {
                low = mid + 1;
            }
            else
            {
                var startGlyph = (int)ReadU32(_data, group + 8);
                return (ushort)(startGlyph + (codePoint - start));
            }
        }

        return 0;
    }

    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static short ReadI16(byte[] data, int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
