using System.Text;

namespace Reglet;

/// <summary>The error correction level of a QR code: the share of damaged codewords it survives.</summary>
public enum QrErrorCorrection
{
    /// <summary>About 7 %.</summary>
    Low,

    /// <summary>About 15 %.</summary>
    Medium,

    /// <summary>About 25 %.</summary>
    Quartile,

    /// <summary>About 30 %.</summary>
    High,
}

/// <summary>
/// QR code encoder (ISO/IEC 18004, model 2): versions 1-40, numeric, alphanumeric and byte (UTF-8)
/// mode in one segment, the mask with the lowest penalty.
/// </summary>
internal static class QrEncoder
{
    private const string AlphanumericSet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // Index: level (Low, Medium, Quartile, High), then version 1..40 (index 0 unused).
    private static readonly int[][] EccPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] BlockCount =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    /// <summary>The data codewords a version holds at a level.</summary>
    internal static int DataCodewords(int version, QrErrorCorrection level)
        => RawModules(version) / 8 - EccPerBlock[(int)level][version] * BlockCount[(int)level][version];

    /// <summary>Encodes the text and returns the module grid [row, column], true = dark.</summary>
    internal static bool[,] Encode(string text, QrErrorCorrection level)
    {
        var (mode, data) = Segment(text);
        var level0 = (int)level;
        var version = 1;
        int bits;
        while (true)
        {
            var countBits = CountBits(mode, version);
            var count = mode == 4 ? data.Length : text.Length;
            bits = 4 + countBits + DataBits(mode, text.Length, data.Length);
            if (count < 1 << countBits && bits <= DataCodewords(version, level) * 8)
            {
                break;
            }

            if (++version > 40)
            {
                throw new ArgumentException("The content does not fit into a QR code at this error correction level.", nameof(text));
            }
        }

        var buffer = new List<bool>();
        Append(buffer, mode, 4);
        Append(buffer, mode == 4 ? data.Length : text.Length, CountBits(mode, version));
        AppendData(buffer, mode, text, data);
        var capacity = DataCodewords(version, level) * 8;
        Append(buffer, 0, Math.Min(4, capacity - buffer.Count));
        Append(buffer, 0, (8 - buffer.Count % 8) % 8);
        for (var pad = 0xEC; buffer.Count < capacity; pad ^= 0xEC ^ 0x11)
        {
            Append(buffer, pad, 8);
        }

        var dataBytes = new byte[capacity / 8];
        for (var i = 0; i < buffer.Count; i++)
        {
            dataBytes[i >> 3] |= (byte)((buffer[i] ? 1 : 0) << (7 - (i & 7)));
        }

        var codewords = AddEccAndInterleave(dataBytes, version, level0);
        var size = version * 4 + 17;
        var function = new bool[size, size];
        var grid = new bool[size, size];
        DrawFunctionPatterns(grid, function, version);
        DrawCodewords(grid, function, codewords);

        var best = -1;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(grid, function, mask);
            DrawFormat(grid, function, level0, mask);
            var penalty = Penalty(grid);
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }

            ApplyMask(grid, function, mask); // XOR again: undo
        }

        ApplyMask(grid, function, best);
        DrawFormat(grid, function, level0, best);
        return grid;
    }

    private static (int Mode, byte[] Bytes) Segment(string text)
    {
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            return (1, Array.Empty<byte>());
        }

        if (text.Length > 0 && text.All(c => AlphanumericSet.Contains(c)))
        {
            return (2, Array.Empty<byte>());
        }

        return (4, new UTF8Encoding(false, true).GetBytes(text));
    }

    private static int CountBits(int mode, int version)
    {
        var band = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return mode switch
        {
            1 => new[] { 10, 12, 14 }[band],
            2 => new[] { 9, 11, 13 }[band],
            _ => new[] { 8, 16, 16 }[band],
        };
    }

    private static int DataBits(int mode, int chars, int bytes) => mode switch
    {
        1 => chars / 3 * 10 + (chars % 3 == 0 ? 0 : chars % 3 == 1 ? 4 : 7),
        2 => chars / 2 * 11 + chars % 2 * 6,
        _ => bytes * 8,
    };

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static void AppendData(List<bool> bits, int mode, string text, byte[] bytes)
    {
        switch (mode)
        {
            case 1:
                for (var i = 0; i < text.Length; i += 3)
                {
                    var n = Math.Min(3, text.Length - i);
                    Append(bits, int.Parse(text.AsSpan(i, n), System.Globalization.CultureInfo.InvariantCulture), n * 3 + 1);
                }

                break;
            case 2:
                for (var i = 0; i < text.Length; i += 2)
                {
                    if (i + 1 < text.Length)
                    {
                        Append(bits, AlphanumericSet.IndexOf(text[i]) * 45 + AlphanumericSet.IndexOf(text[i + 1]), 11);
                    }
                    else
                    {
                        Append(bits, AlphanumericSet.IndexOf(text[i]), 6);
                    }
                }

                break;
            default:
                foreach (var b in bytes)
                {
                    Append(bits, b, 8);
                }

                break;
        }
    }

    private static int RawModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7)
            {
                result -= 36;
            }
        }

        return result;
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, int level)
    {
        var blocks = BlockCount[level][version];
        var eccLength = EccPerBlock[level][version];
        var raw = RawModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;
        var divisor = ReedSolomonDivisor(eccLength);
        var dataBlocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var length = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            var block = data[k..(k + length)];
            k += length;
            dataBlocks.Add(block);
            eccBlocks.Add(ReedSolomonRemainder(block, divisor));
        }

        var result = new List<byte>();
        for (var i = 0; i < shortLength - eccLength + 1; i++)
        {
            for (var j = 0; j < blocks; j++)
            {
                // Short blocks lack the last data byte.
                if (i != shortLength - eccLength || j >= shortBlocks)
                {
                    result.Add(dataBlocks[j][i]);
                }
            }
        }

        for (var i = 0; i < eccLength; i++)
        {
            for (var j = 0; j < blocks; j++)
            {
                result.Add(eccBlocks[j][i]);
            }
        }

        return result.ToArray();
    }

    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static void SetFunction(bool[,] grid, bool[,] function, int x, int y, bool dark)
    {
        grid[y, x] = dark;
        function[y, x] = true;
    }

    private static void DrawFunctionPatterns(bool[,] grid, bool[,] function, int version)
    {
        var size = grid.GetLength(0);
        for (var i = 0; i < size; i++)
        {
            SetFunction(grid, function, 6, i, i % 2 == 0);
            SetFunction(grid, function, i, 6, i % 2 == 0);
        }

        DrawFinder(grid, function, 3, 3);
        DrawFinder(grid, function, size - 4, 3);
        DrawFinder(grid, function, 3, size - 4);
        var positions = AlignmentPositions(version);
        for (var i = 0; i < positions.Count; i++)
        {
            for (var j = 0; j < positions.Count; j++)
            {
                if (!(i == 0 && j == 0 || i == 0 && j == positions.Count - 1 || i == positions.Count - 1 && j == 0))
                {
                    for (var dy = -2; dy <= 2; dy++)
                    {
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            SetFunction(grid, function, positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                        }
                    }
                }
            }
        }

        DrawFormat(grid, function, 0, 0); // reserves the areas; drawn again after the mask
        if (version >= 7)
        {
            var rem = version;
            for (var i = 0; i < 12; i++)
            {
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            }

            var bits = version << 12 | rem;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                var a = size - 11 + i % 3;
                var b = i / 3;
                SetFunction(grid, function, a, b, dark);
                SetFunction(grid, function, b, a, dark);
            }
        }
    }

    private static void DrawFinder(bool[,] grid, bool[,] function, int cx, int cy)
    {
        var size = grid.GetLength(0);
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x >= 0 && x < size && y >= 0 && y < size)
                {
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFunction(grid, function, x, y, distance != 2 && distance != 4);
                }
            }
        }
    }

    internal static List<int> AlignmentPositions(int version)
    {
        var result = new List<int> { 6 };
        if (version == 1)
        {
            return new List<int>();
        }

        var count = version / 7 + 2;
        var size = version * 4 + 17;
        var step = version == 32 ? 26 : (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        for (int i = 0, pos = size - 7; i < count - 1; i++, pos -= step)
        {
            result.Insert(1, pos);
        }

        return result;
    }

    private static void DrawFormat(bool[,] grid, bool[,] function, int level, int mask)
    {
        var size = grid.GetLength(0);
        var data = new[] { 1, 0, 3, 2 }[level] << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        var bits = (data << 10 | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (var i = 0; i <= 5; i++)
        {
            SetFunction(grid, function, 8, i, Bit(i));
        }

        SetFunction(grid, function, 8, 7, Bit(6));
        SetFunction(grid, function, 8, 8, Bit(7));
        SetFunction(grid, function, 7, 8, Bit(8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(grid, function, 14 - i, 8, Bit(i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(grid, function, size - 1 - i, 8, Bit(i));
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(grid, function, 8, size - 15 + i, Bit(i));
        }

        SetFunction(grid, function, 8, size - 8, true);
    }

    private static void DrawCodewords(bool[,] grid, bool[,] function, byte[] codewords)
    {
        var size = grid.GetLength(0);
        var i = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var vertical = 0; vertical < size; vertical++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vertical : vertical;
                    if (!function[y, x] && i < codewords.Length * 8)
                    {
                        grid[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[,] grid, bool[,] function, int mask)
    {
        var size = grid.GetLength(0);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !function[y, x])
                {
                    grid[y, x] = !grid[y, x];
                }
            }
        }
    }

    // The four penalty rules of the standard (3, 3, 40, 10).
    private static int Penalty(bool[,] grid)
    {
        var size = grid.GetLength(0);
        var penalty = 0;
        var dark = 0;
        for (var a = 0; a < size; a++)
        {
            foreach (var rows in new[] { true, false })
            {
                var line = new bool[size];
                for (var b = 0; b < size; b++)
                {
                    line[b] = rows ? grid[a, b] : grid[b, a];
                }

                for (var b = 0; b < size;)
                {
                    var end = b;
                    while (end < size && line[end] == line[b])
                    {
                        end++;
                    }

                    if (end - b >= 5)
                    {
                        penalty += 3 + end - b - 5;
                    }

                    b = end;
                }

                penalty += 40 * CountFinderLike(line);
            }
        }

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                dark += grid[y, x] ? 1 : 0;
                if (x + 1 < size && y + 1 < size && grid[y, x] == grid[y, x + 1] && grid[y, x] == grid[y + 1, x] && grid[y, x] == grid[y + 1, x + 1])
                {
                    penalty += 3;
                }
            }
        }

        var total = size * size;
        var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return penalty + k * 10;
    }

    // 1:1:3:1:1 dark-light-dark-light-dark with four light modules on one side (the finder look-alike).
    private static int CountFinderLike(bool[] line)
    {
        var count = 0;
        for (var i = 0; i + 7 <= line.Length; i++)
        {
            if (line[i] && !line[i + 1] && line[i + 2] && line[i + 3] && line[i + 4] && !line[i + 5] && line[i + 6])
            {
                var before = i >= 4 && !line[i - 4] && !line[i - 3] && !line[i - 2] && !line[i - 1];
                var after = i + 11 <= line.Length && !line[i + 7] && !line[i + 8] && !line[i + 9] && !line[i + 10];
                count += (before ? 1 : 0) + (after ? 1 : 0);
            }
        }

        return count;
    }
}
