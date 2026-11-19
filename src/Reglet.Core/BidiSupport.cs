namespace Reglet;

/// <summary>Unicode bidi class (UAX #9). The numeric values correspond to the generated table.</summary>
internal enum BidiClass : byte
{
    L = 0,
    R = 1,
    AL = 2,
    EN = 3,
    ES = 4,
    ET = 5,
    AN = 6,
    CS = 7,
    NSM = 8,
    BN = 9,
    B = 10,
    S = 11,
    WS = 12,
    ON = 13,
    LRE = 14,
    RLE = 15,
    LRO = 16,
    RLO = 17,
    PDF = 18,
    LRI = 19,
    RLI = 20,
    FSI = 21,
    PDI = 22,
}

/// <summary>Access to the generated Unicode bidi data (classes and bracket pairs).</summary>
internal static partial class BidiData
{
    /// <summary>Bidi class of a code point (default L).</summary>
    public static BidiClass GetBidiClass(int codePoint)
    {
        var low = 0;
        var high = Ranges.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (start, end, value) = Ranges[mid];
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
                return (BidiClass)value;
            }
        }

        return BidiClass.L;
    }

    /// <summary>Klammerpaar (UAX #9 N0).</summary>
    public static bool TryGetBracket(int codePoint, out int paired, out bool isOpen)
    {
        var low = 0;
        var high = Brackets.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (code, pair, open) = Brackets[mid];
            if (codePoint < code)
            {
                high = mid - 1;
            }
            else if (codePoint > code)
            {
                low = mid + 1;
            }
            else
            {
                paired = pair;
                isOpen = open;
                return true;
            }
        }

        paired = 0;
        isOpen = false;
        return false;
    }

    /// <summary>The mirrored glyph of a character at a right-to-left level (UAX #9 L4, BidiMirroring.txt).</summary>
    public static bool TryGetMirror(int codePoint, out int mirror)
    {
        var index = Array.BinarySearch(Mirrors, (codePoint, 0), MirrorComparer);
        mirror = index >= 0 ? Mirrors[index].Mirror : codePoint;
        return index >= 0;
    }

    private static readonly Comparer<(int Code, int Mirror)> MirrorComparer =
        Comparer<(int Code, int Mirror)>.Create((left, right) => left.Code.CompareTo(right.Code));
}
