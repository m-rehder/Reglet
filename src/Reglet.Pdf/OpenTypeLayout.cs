using System.Buffers.Binary;

namespace Reglet;

/// <summary>
/// validating reader of the OpenType layout tables <c>GSUB</c>, <c>GPOS</c> and <c>GDEF</c>
/// (phase P, P01). Supports the lookup types needed for text shaping:
/// GSUB 1 (single), 4 (ligature) and GPOS 1 (single), 2 (pair) and 4 (mark base), each
/// including extension lookups (GSUB 7, GPOS 9).
/// </summary>
/// <remarks>
/// the parser works exclusively on the font's raw data, creates no font files
/// and reports damaged tables as <see cref="FontException"/> instead of silently
/// scheitern.
/// </remarks>
internal sealed class OpenTypeLayout
{
    internal static readonly uint ScriptLatin = Tag("latn");
    internal static readonly uint ScriptArabic = Tag("arab");
    internal static readonly uint ScriptHebrew = Tag("hebr");
    internal static readonly uint ScriptDefault = Tag("DFLT");

    internal static readonly uint FeatureLiga = Tag("liga");
    internal static readonly uint FeatureRlig = Tag("rlig");
    internal static readonly uint FeatureKern = Tag("kern");
    internal static readonly uint FeatureIsol = Tag("isol");
    internal static readonly uint FeatureInit = Tag("init");
    internal static readonly uint FeatureMedi = Tag("medi");
    internal static readonly uint FeatureFina = Tag("fina");

    private static readonly int[] EmptyLookups = [];

    private readonly byte[] _data;
    private readonly LayoutTable? _gsub;
    private readonly LayoutTable? _gpos;
    private readonly ushort[]? _glyphClasses;
    private readonly int _glyphClassCount;

    private OpenTypeLayout(
        byte[] data,
        LayoutTable? gsub,
        LayoutTable? gpos,
        ushort[]? glyphClasses,
        int glyphClassCount)
    {
        _data = data;
        _gsub = gsub;
        _gpos = gpos;
        _glyphClasses = glyphClasses;
        _glyphClassCount = glyphClassCount;
    }

    /// <summary>True if a GSUB table is present and valid.</summary>
    public bool HasGsub => _gsub is not null;

    /// <summary>True if a GPOS table is present and valid.</summary>
    public bool HasGpos => _gpos is not null;

    /// <summary>True if a GDEF table with glyph classes is present.</summary>
    public bool HasGlyphClasses => _glyphClasses is not null;

    /// <summary>
    /// loads the layout tables of a font. Returns <c>null</c> if none of the tables
    /// is present; throws <see cref="FontException"/> for damaged tables.
    /// </summary>
    public static OpenTypeLayout? TryCreate(TrueTypeFont font)
    {
        ArgumentNullException.ThrowIfNull(font);

        var data = font.Data;
        var gsub = font.TryGetTable("GSUB", out var offset, out var length)
            ? LayoutTable.Parse(data, offset, length, "GSUB")
            : null;
        var gpos = font.TryGetTable("GPOS", out offset, out length)
            ? LayoutTable.Parse(data, offset, length, "GPOS")
            : null;
        ushort[]? glyphClasses = null;
        var classCount = 0;
        if (font.TryGetTable("GDEF", out offset, out length))
        {
            glyphClasses = ParseGlyphClasses(data, offset, length, out classCount);
        }

        if (gsub is null && gpos is null && glyphClasses is null)
        {
            return null;
        }

        return new OpenTypeLayout(data, gsub, gpos, glyphClasses, classCount);
    }

    /// <summary>GDEF glyph class (1 base, 2 ligature, 3 mark, 4 component; 0 no class).</summary>
    public int GetGlyphClass(ushort glyph)
        => _glyphClasses is not null && glyph < _glyphClassCount ? _glyphClasses[glyph] : 0;

    /// <summary>Lookup indices of a feature for a script (in application order).</summary>
    public IReadOnlyList<int> GetFeatureLookups(bool gpos, uint scriptTag, uint featureTag)
    {
        var table = gpos ? _gpos : _gsub;
        if (table is null)
        {
            return EmptyLookups;
        }

        var featureIndices = table.GetScriptFeatureIndices(_data, scriptTag);
        List<int>? lookups = null;
        foreach (var featureIndex in featureIndices)
        {
            if (featureIndex < 0 || featureIndex >= table.Features.Length)
            {
                continue;
            }

            var feature = table.Features[featureIndex];
            if (feature.Tag != featureTag)
            {
                continue;
            }

            var count = U16(feature.Offset + 2, table.Limit, "Feature");
            lookups ??= new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                lookups.Add(U16(feature.Offset + 4 + i * 2, table.Limit, "Feature-Lookup"));
            }
        }

        return lookups ?? (IReadOnlyList<int>)EmptyLookups;
    }

    /// <summary>Applies a single substitution (GSUB 1).</summary>
    public bool TrySingleSubstitute(int lookupIndex, ushort glyph, out ushort substitute)
    {
        substitute = glyph;
        if (!TryGetLookup(_gsub, lookupIndex, out var lookupOffset, out var lookupType))
        {
            return false;
        }

        var count = U16(lookupOffset + 4, _gsub!.Limit, "Lookup");
        for (var i = 0; i < count; i++)
        {
            var subtable = U16(lookupOffset + 6 + i * 2, _gsub.Limit, "Lookup-Subtabelle");
            var (type, offset) = ResolveSubtable(_data, _gsub.Limit, lookupOffset + subtable, lookupType, gpos: false);
            if (type != 1 || !TryCoverageIndex(offset + U16(offset + 2, _gsub.Limit, "SingleSubst"), _gsub.Limit, glyph, out var index))
            {
                continue;
            }

            var format = U16(offset, _gsub.Limit, "SingleSubst");
            substitute = format == 2
                ? U16(offset + 6 + index * 2, _gsub.Limit, "SingleSubst-Glyphe")
                : (ushort)((glyph + I16(offset + 4, _gsub.Limit, "SingleSubst-Delta")) & 0xFFFF);
            return true;
        }

        return false;
    }

    /// <summary>
    /// searches for a ligature (GSUB 4) whose components match the glyphs
    /// <paramref name="start"/>. <paramref name="componentCount"/> is the total number
    /// the used glyphs including the first.
    /// </summary>
    public bool TryLigature(ReadOnlySpan<ushort> glyphs, int start, int lookupIndex, out ushort ligature, out int componentCount)
    {
        ligature = glyphs[start];
        componentCount = 0;
        if (!TryGetLookup(_gsub, lookupIndex, out var lookupOffset, out var lookupType))
        {
            return false;
        }

        var count = U16(lookupOffset + 4, _gsub!.Limit, "Lookup");
        for (var i = 0; i < count; i++)
        {
            var subtable = U16(lookupOffset + 6 + i * 2, _gsub.Limit, "Lookup-Subtabelle");
            var (type, offset) = ResolveSubtable(_data, _gsub.Limit, lookupOffset + subtable, lookupType, gpos: false);
            if (type != 4)
            {
                continue;
            }

            var coverage = offset + U16(offset + 2, _gsub.Limit, "LigatureSubst");
            if (!TryCoverageIndex(coverage, _gsub.Limit, glyphs[start], out var coverageIndex))
            {
                continue;
            }

            var setCount = U16(offset + 4, _gsub.Limit, "LigatureSet");
            if (coverageIndex >= setCount)
            {
                continue;
            }

            var set = offset + U16(offset + 6 + coverageIndex * 2, _gsub.Limit, "LigatureSet");
            var ligatureCount = U16(set, _gsub.Limit, "LigatureSet");
            for (var ligatureIndex = 0; ligatureIndex < ligatureCount; ligatureIndex++)
            {
                var candidate = set + U16(set + 2 + ligatureIndex * 2, _gsub.Limit, "Ligatur");
                var glyph = U16(candidate, _gsub.Limit, "Ligatur");
                var components = U16(candidate + 2, _gsub.Limit, "Ligatur");
                if (components == 0 || start + components > glyphs.Length)
                {
                    continue;
                }

                var matches = true;
                for (var component = 1; component < components; component++)
                {
                    if (U16(candidate + 2 + component * 2, _gsub.Limit, "Ligatur-Komponente") != glyphs[start + component])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    ligature = glyph;
                    componentCount = components;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Applies a kerning pair adjustment (GPOS 2). Values in font units.</summary>
    public bool TryKern(int lookupIndex, ushort first, ushort second, out double xAdvance, out double xPlacement)
    {
        xAdvance = 0;
        xPlacement = 0;
        if (!TryGetLookup(_gpos, lookupIndex, out var lookupOffset, out var lookupType))
        {
            return false;
        }

        var count = U16(lookupOffset + 4, _gpos!.Limit, "Lookup");
        for (var i = 0; i < count; i++)
        {
            var subtable = U16(lookupOffset + 6 + i * 2, _gpos.Limit, "Lookup-Subtabelle");
            var (type, offset) = ResolveSubtable(_data, _gpos.Limit, lookupOffset + subtable, lookupType, gpos: true);
            if (type != 2 || !TryPairAdjustment(offset, first, second, out xAdvance, out xPlacement))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>Applies mark positioning over a base glyph (GPOS 4).</summary>
    public bool TryMarkBase(int lookupIndex, ushort mark, ushort baseGlyph, out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        if (!TryGetLookup(_gpos, lookupIndex, out var lookupOffset, out var lookupType))
        {
            return false;
        }

        var count = U16(lookupOffset + 4, _gpos!.Limit, "Lookup");
        for (var i = 0; i < count; i++)
        {
            var subtable = U16(lookupOffset + 6 + i * 2, _gpos.Limit, "Lookup-Subtabelle");
            var (type, offset) = ResolveSubtable(_data, _gpos.Limit, lookupOffset + subtable, lookupType, gpos: true);
            if (type != 4 || !TryMarkToBase(offset, mark, baseGlyph, out dx, out dy))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryPairAdjustment(int subtable, ushort first, ushort second, out double xAdvance, out double xPlacement)
    {
        xAdvance = 0;
        xPlacement = 0;
        var limit = _gpos!.Limit;
        var format = U16(subtable, limit, "PairPos");
        var coverage = subtable + U16(subtable + 2, limit, "PairPos");
        if (!TryCoverageIndex(coverage, limit, first, out var coverageIndex))
        {
            return false;
        }

        var valueFormat1 = U16(subtable + 4, limit, "PairPos");
        var valueFormat2 = U16(subtable + 6, limit, "PairPos");

        if (format == 1)
        {
            var pairSetCount = U16(subtable + 8, limit, "PairPos");
            if (coverageIndex >= pairSetCount)
            {
                return false;
            }

            var pairSet = subtable + U16(subtable + 10 + coverageIndex * 2, limit, "PairSet");
            var pairValueCount = U16(pairSet, limit, "PairSet");
            var valueSize = ValueSize(valueFormat1) + ValueSize(valueFormat2);
            var position = pairSet + 2;
            for (var i = 0; i < pairValueCount; i++)
            {
                if (U16(position, limit, "PairValue") == second)
                {
                    var value = ReadValueRecord(position + 2, limit, valueFormat1);
                    xAdvance = value.XAdvance;
                    xPlacement = value.XPlacement;
                    return true;
                }

                position += 2 + valueSize;
            }

            return false;
        }

        if (format != 2)
        {
            return false;
        }

        var classDef1 = subtable + U16(subtable + 8, limit, "PairPos");
        var classDef2 = subtable + U16(subtable + 10, limit, "PairPos");
        var class1Count = U16(subtable + 12, limit, "PairPos");
        var class2Count = U16(subtable + 14, limit, "PairPos");
        var class1 = GetClass(classDef1, limit, first);
        var class2 = GetClass(classDef2, limit, second);
        if (class1 >= class1Count || class2 >= class2Count)
        {
            return false;
        }

        var recordSize = ValueSize(valueFormat1) + ValueSize(valueFormat2);
        var record = subtable + 16 + (class1 * class2Count + class2) * recordSize;
        var pairValue = ReadValueRecord(record, limit, valueFormat1);
        xAdvance = pairValue.XAdvance;
        xPlacement = pairValue.XPlacement;
        return true;
    }

    private bool TryMarkToBase(int subtable, ushort mark, ushort baseGlyph, out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        var limit = _gpos!.Limit;
        var markCoverage = subtable + U16(subtable + 2, limit, "MarkBasePos");
        var baseCoverage = subtable + U16(subtable + 4, limit, "MarkBasePos");
        var markClassCount = U16(subtable + 6, limit, "MarkBasePos");
        var markArray = subtable + U16(subtable + 8, limit, "MarkBasePos");
        var baseArray = subtable + U16(subtable + 10, limit, "MarkBasePos");

        if (!TryCoverageIndex(markCoverage, limit, mark, out var markIndex)
            || !TryCoverageIndex(baseCoverage, limit, baseGlyph, out var baseIndex))
        {
            return false;
        }

        var markClass = U16(markArray + 2 + markIndex * 4, limit, "MarkArray");
        var markAnchorOffset = U16(markArray + 2 + markIndex * 4 + 2, limit, "MarkArray");
        var baseAnchorOffset = U16(baseArray + 2 + (baseIndex * markClassCount + markClass) * 2, limit, "BaseArray");

        var markAnchor = ReadAnchor(markArray + markAnchorOffset, limit);
        var baseAnchor = ReadAnchor(baseArray + baseAnchorOffset, limit);
        dx = baseAnchor.X - markAnchor.X;
        dy = baseAnchor.Y - markAnchor.Y;
        return true;
    }

    private bool TryGetLookup(LayoutTable? table, int lookupIndex, out int lookupOffset, out int lookupType)
    {
        lookupOffset = 0;
        lookupType = 0;
        if (table is null || lookupIndex < 0 || lookupIndex >= table.Lookups.Length)
        {
            return false;
        }

        lookupOffset = table.Lookups[lookupIndex];
        lookupType = U16(lookupOffset, table.Limit, "Lookup");
        return true;
    }

    private (int Type, int Offset) ResolveSubtable(byte[] data, int limit, int subtable, int lookupType, bool gpos)
    {
        var extensionType = gpos ? 9 : 7;
        if (lookupType != extensionType)
        {
            return (lookupType, subtable);
        }

        var type = U16(subtable + 2, limit, "Extension-Lookup");
        var offset = (int)U32(subtable + 4, limit, "Extension-Lookup");
        return (type, subtable + offset);
    }

    private bool TryCoverageIndex(int coverage, int limit, ushort glyph, out int index)
    {
        index = 0;
        if (coverage == 0)
        {
            return false;
        }

        var format = U16(coverage, limit, "Coverage");
        if (format == 1)
        {
            var count = U16(coverage + 2, limit, "Coverage");
            for (var i = 0; i < count; i++)
            {
                if (U16(coverage + 4 + i * 2, limit, "Coverage-Glyphe") == glyph)
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }

        if (format != 2)
        {
            throw new FontException($"Coverage format {format} is not supported.");
        }

        var rangeCount = U16(coverage + 2, limit, "Coverage");
        for (var i = 0; i < rangeCount; i++)
        {
            var range = coverage + 4 + i * 6;
            var start = U16(range, limit, "Coverage-Bereich");
            var end = U16(range + 2, limit, "Coverage-Bereich");
            if (glyph >= start && glyph <= end)
            {
                index = U16(range + 4, limit, "Coverage-Bereich") + (glyph - start);
                return true;
            }
        }

        return false;
    }

    private int GetClass(int classDef, int limit, ushort glyph)
    {
        if (classDef == 0)
        {
            return 0;
        }

        var format = U16(classDef, limit, "ClassDef");
        if (format == 1)
        {
            var start = U16(classDef + 2, limit, "ClassDef");
            var count = U16(classDef + 4, limit, "ClassDef");
            return glyph >= start && glyph < start + count
                ? U16(classDef + 6 + (glyph - start) * 2, limit, "ClassDef")
                : 0;
        }

        if (format != 2)
        {
            throw new FontException($"ClassDef format {format} is not supported.");
        }

        var rangeCount = U16(classDef + 2, limit, "ClassDef");
        for (var i = 0; i < rangeCount; i++)
        {
            var range = classDef + 4 + i * 6;
            var start = U16(range, limit, "ClassDef-Bereich");
            var end = U16(range + 2, limit, "ClassDef-Bereich");
            if (glyph >= start && glyph <= end)
            {
                return U16(range + 4, limit, "ClassDef-Bereich");
            }
        }

        return 0;
    }

    private (double X, double Y) ReadAnchor(int anchor, int limit)
    {
        var format = U16(anchor, limit, "Anchor");
        if (format is < 1 or > 3)
        {
            throw new FontException($"Anchor format {format} is not supported.");
        }

        return (I16(anchor + 2, limit, "Anchor"), I16(anchor + 4, limit, "Anchor"));
    }

    private (double XPlacement, double YPlacement, double XAdvance, double YAdvance) ReadValueRecord(int offset, int limit, int format)
    {
        double xPlacement = 0, yPlacement = 0, xAdvance = 0, yAdvance = 0;
        var position = offset;
        if ((format & 0x0001) != 0)
        {
            xPlacement = I16(position, limit, "ValueRecord");
            position += 2;
        }

        if ((format & 0x0002) != 0)
        {
            yPlacement = I16(position, limit, "ValueRecord");
            position += 2;
        }

        if ((format & 0x0004) != 0)
        {
            xAdvance = I16(position, limit, "ValueRecord");
            position += 2;
        }

        if ((format & 0x0008) != 0)
        {
            yAdvance = I16(position, limit, "ValueRecord");
            position += 2;
        }

        // device offsets (0x10..0x80) are not evaluated but occupy 2 bytes each.
        for (var bit = 0x0010; bit <= 0x0080; bit <<= 1)
        {
            if ((format & bit) != 0)
            {
                position += 2;
            }
        }

        return (xPlacement, yPlacement, xAdvance, yAdvance);
    }

    private static int ValueSize(int format)
    {
        var size = 0;
        for (var bit = 0x0001; bit <= 0x0080; bit <<= 1)
        {
            if ((format & bit) != 0)
            {
                size += 2;
            }
        }

        return size;
    }

    private static ushort[]? ParseGlyphClasses(byte[] data, int offset, int length, out int classCount)
    {
        classCount = 0;
        if (length < 12)
        {
            throw new FontException("GDEF is too short.");
        }

        ushort U16(int at, int end, string what) => ReadU16(data, at, end, what);

        var limit = offset + length;
        var classDefOffset = U16(offset + 4, limit, "GDEF");
        if (classDefOffset == 0)
        {
            return null;
        }

        var classDef = offset + classDefOffset;
        var format = U16(classDef, limit, "GDEF-GlyphClassDef");
        var classes = new Dictionary<int, ushort>();
        var maxGlyph = -1;

        if (format == 1)
        {
            var start = U16(classDef + 2, limit, "GDEF-GlyphClassDef");
            var count = U16(classDef + 4, limit, "GDEF-GlyphClassDef");
            for (var i = 0; i < count; i++)
            {
                var glyph = start + i;
                classes[glyph] = U16(classDef + 6 + i * 2, limit, "GDEF-GlyphClassDef");
                maxGlyph = Math.Max(maxGlyph, glyph);
            }
        }
        else if (format == 2)
        {
            var rangeCount = U16(classDef + 2, limit, "GDEF-GlyphClassDef");
            for (var i = 0; i < rangeCount; i++)
            {
                var range = classDef + 4 + i * 6;
                var start = U16(range, limit, "GDEF-GlyphClassDef");
                var end = U16(range + 2, limit, "GDEF-GlyphClassDef");
                var value = U16(range + 4, limit, "GDEF-GlyphClassDef");
                // int, not ushort: a range ending at 0xFFFF would otherwise wrap around forever.
                for (int glyph = start; glyph <= end; glyph++)
                {
                    classes[glyph] = value;
                }

                maxGlyph = Math.Max(maxGlyph, end);
            }
        }
        else
        {
            throw new FontException($"GDEF GlyphClassDef format {format} is not supported.");
        }

        classCount = maxGlyph + 1;
        var result = new ushort[classCount];
        foreach (var (glyph, value) in classes)
        {
            result[glyph] = value;
        }

        return result;
    }

    internal static uint Tag(string value)
        => ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    internal static string TagToString(uint tag)
        => new([(char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF)]);

    private static FontException Malformed(string what)
        => new($"OpenType layout is invalid: {what} outside the table.");

    // instance readers work on the font's raw data and check the table boundary.
    private ushort U16(int offset, int limit, string what) => ReadU16(_data, offset, limit, what);

    private static ushort ReadU16(byte[] data, int offset, int limit, string what)
    {
        if (offset < 0 || limit < offset + 2)
        {
            throw Malformed(what);
        }

        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private short I16(int offset, int limit, string what)
    {
        if (offset < 0 || limit < offset + 2)
        {
            throw Malformed(what);
        }

        return BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2));
    }

    private uint U32(int offset, int limit, string what)
    {
        if (offset < 0 || limit < offset + 4)
        {
            throw Malformed(what);
        }

        return BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
    }

    /// <summary>Script, feature and lookup directory of a GSUB/GPOS table.</summary>
    private sealed class LayoutTable
    {
        private LayoutTable(
            (uint Tag, int Offset)[] scripts,
            (uint Tag, int Offset)[] features,
            int[] lookups,
            int limit)
        {
            Scripts = scripts;
            Features = features;
            Lookups = lookups;
            Limit = limit;
        }

        public (uint Tag, int Offset)[] Scripts { get; }

        public (uint Tag, int Offset)[] Features { get; }

        public int[] Lookups { get; }

        public int Limit { get; }

        public static LayoutTable Parse(byte[] data, int offset, int length, string tag)
        {
            if (length < 10)
            {
                throw new FontException($"Table '{tag}' is too short.");
            }

            var limit = offset + length;
            var major = ReadU16(data, offset, limit, tag);
            if (major != 1)
            {
                throw new FontException($"Table '{tag}' has unknown version {major}.");
            }

            var scriptList = offset + ReadU16(data, offset + 4, limit, tag);
            var featureList = offset + ReadU16(data, offset + 6, limit, tag);
            var lookupList = offset + ReadU16(data, offset + 8, limit, tag);

            var scripts = ReadRecords(data, scriptList, limit, tag, "ScriptList");
            var features = ReadRecords(data, featureList, limit, tag, "FeatureList");
            var lookups = ReadOffsets(data, lookupList, limit, tag, "LookupList");
            var result = new LayoutTable(scripts, features, lookups, limit);
            result.Validate(data, tag);
            return result;
        }

        /// <summary>Lookup indices of a script's default LangSys (with DFLT fallback).</summary>
        public IReadOnlyList<int> GetScriptFeatureIndices(byte[] data, uint scriptTag)
        {
            var script = FindScript(scriptTag);
            if (script is null)
            {
                return Array.Empty<int>();
            }

            var scriptOffset = script.Value.Offset;
            var defaultLangSys = ReadU16(data, scriptOffset, Limit, "Script");
            var langSysCount = ReadU16(data, scriptOffset + 2, Limit, "Script");
            var langSys = defaultLangSys != 0
                ? scriptOffset + defaultLangSys
                : langSysCount > 0
                    ? scriptOffset + ReadU16(data, scriptOffset + 8, Limit, "LangSys")
                    : 0;
            if (langSys == 0)
            {
                return Array.Empty<int>();
            }

            var required = ReadU16(data, langSys + 2, Limit, "LangSys");
            var count = ReadU16(data, langSys + 4, Limit, "LangSys");
            var indices = new List<int>(count + 1);
            if (required != 0xFFFF)
            {
                indices.Add(required);
            }

            for (var i = 0; i < count; i++)
            {
                indices.Add(ReadU16(data, langSys + 6 + i * 2, Limit, "LangSys"));
            }

            return indices;
        }

        private (uint Tag, int Offset)? FindScript(uint scriptTag)
        {
            (uint Tag, int Offset)? first = null;
            (uint Tag, int Offset)? fallback = null;
            foreach (var script in Scripts)
            {
                first ??= script;
                if (script.Tag == scriptTag)
                {
                    return script;
                }

                if (script.Tag == ScriptDefault)
                {
                    fallback = script;
                }
            }

            return fallback ?? first;
        }

        private void Validate(byte[] data, string tag)
        {
            foreach (var feature in Features)
            {
                _ = ReadU16(data, feature.Offset + 2, Limit, tag + " Feature");
            }

            foreach (var lookup in Lookups)
            {
                var type = ReadU16(data, lookup, Limit, tag + " Lookup");
                var subtableCount = ReadU16(data, lookup + 4, Limit, tag + " Lookup");
                for (var i = 0; i < subtableCount; i++)
                {
                    var subtable = lookup + ReadU16(data, lookup + 6 + i * 2, Limit, tag + " Lookup");
                    ValidateSubtable(data, subtable, type, tag);
                }
            }
        }

        private void ValidateSubtable(byte[] data, int subtable, int type, string tag)
        {
            // check only the header structure; the content evaluation happens on apply.
            _ = ReadU16(data, subtable, Limit, tag + " Subtable");
            if ((tag == "GSUB" && type == 7) || (tag == "GPOS" && type == 9))
            {
                var extensionOffset = (int)ReadU32(data, subtable + 4, Limit, tag + " Extension");
                _ = ReadU16(data, subtable + extensionOffset, Limit, tag + " Extension");
            }
        }

        private static (uint Tag, int Offset)[] ReadRecords(byte[] data, int list, int limit, string tag, string what)
        {
            var count = ReadU16(data, list, limit, tag + " " + what);
            var records = new (uint, int)[count];
            for (var i = 0; i < count; i++)
            {
                var record = list + 2 + i * 6;
                var scriptTag = ((uint)ReadU16(data, record, limit, tag) << 16) | ReadU16(data, record + 2, limit, tag);
                records[i] = (scriptTag, list + ReadU16(data, record + 4, limit, tag));
            }

            return records;
        }

        private static int[] ReadOffsets(byte[] data, int list, int limit, string tag, string what)
        {
            var count = ReadU16(data, list, limit, tag + " " + what);
            var offsets = new int[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = list + ReadU16(data, list + 2 + i * 2, limit, tag + " " + what);
            }

            return offsets;
        }

        private static ushort ReadU16(byte[] data, int offset, int limit, string what) => OpenTypeLayout.ReadU16(data, offset, limit, what);

        private static uint ReadU32(byte[] data, int offset, int limit, string what)
        {
            if (offset < 0 || limit < offset + 4)
            {
                throw Malformed(what);
            }

            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        }
    }
}
