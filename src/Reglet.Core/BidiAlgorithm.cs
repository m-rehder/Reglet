namespace Reglet;

/// <summary>Result of the bidi resolution: embedding level per character.</summary>
internal sealed class BidiResult
{
    public required byte[] Levels { get; init; }

    public required bool[] Removed { get; init; }
}

/// <summary>
/// Unicode bidirectional algorithm (UAX #9) for direction-correct output
/// (phase P, P04). Works on code points and uses the generated Unicode data.
/// </summary>
internal static class BidiAlgorithm
{
    private const int MaxDepth = 125;
    private const int MaxBracketStack = 63;

    /// <summary>Determines the paragraph direction P2/P3 (0 = LTR, 1 = RTL); isolated text is skipped.</summary>
    public static int GetParagraphLevel(ReadOnlySpan<int> codePoints)
    {
        var isolateDepth = 0;
        foreach (var codePoint in codePoints)
        {
            var bidiClass = BidiData.GetBidiClass(codePoint);
            if (bidiClass is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
            {
                isolateDepth++;
                continue;
            }

            if (bidiClass == BidiClass.PDI)
            {
                isolateDepth = Math.Max(0, isolateDepth - 1);
                continue;
            }

            if (bidiClass == BidiClass.B)
            {
                break;
            }

            if (isolateDepth > 0)
            {
                continue;
            }

            if (bidiClass == BidiClass.L)
            {
                return 0;
            }

            if (bidiClass is BidiClass.R or BidiClass.AL)
            {
                return 1;
            }
        }

        return 0;
    }

    /// <summary>Resolves the embedding levels of a paragraph.</summary>
    public static BidiResult Resolve(ReadOnlySpan<int> codePoints, int paragraphLevel)
    {
        var count = codePoints.Length;
        var original = new BidiClass[count];
        for (var i = 0; i < count; i++)
        {
            original[i] = BidiData.GetBidiClass(codePoints[i]);
        }

        var types = (BidiClass[])original.Clone();
        var levels = new byte[count];
        var removed = new bool[count];

        var (matchingPdi, matchingIsolate) = MatchIsolates(original);
        ApplyExplicitLevels(codePoints, original, types, levels, removed, matchingPdi, paragraphLevel);

        var order = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            if (!removed[i])
            {
                order.Add(i);
            }
        }

        var runs = BuildRuns(order, levels);
        var runOf = new int[count];
        Array.Fill(runOf, -1);
        for (var r = 0; r < runs.Count; r++)
        {
            for (var s = runs[r].Start; s <= runs[r].End; s++)
            {
                runOf[order[s]] = r;
            }
        }

        // continuation of a sequence run across an isolate with a matching PDI.
        var next = new int[runs.Count];
        Array.Fill(next, -1);
        for (var r = 0; r < runs.Count; r++)
        {
            var lastIndex = order[runs[r].End];
            if (IsIsolate(original[lastIndex]) && matchingPdi[lastIndex] >= 0)
            {
                next[r] = runOf[matchingPdi[lastIndex]];
            }
        }

        for (var r = 0; r < runs.Count; r++)
        {
            var firstIndex = order[runs[r].Start];
            if (original[firstIndex] == BidiClass.PDI && matchingIsolate[firstIndex] >= 0)
            {
                continue; // a continuation, not the start of a sequence
            }

            var sequence = new List<int>();
            var cursor = r;
            while (cursor >= 0)
            {
                for (var s = runs[cursor].Start; s <= runs[cursor].End; s++)
                {
                    sequence.Add(order[s]);
                }

                cursor = next[cursor];
            }

            ResolveSequence(codePoints, original, types, sequence, runs[r].Level, order, levels, removed, paragraphLevel);
        }

        // I1/I2: implicit levels based on the resolved types.
        foreach (var index in order)
        {
            var level = levels[index];
            var type = types[index];
            if ((level & 1) == 0)
            {
                if (type == BidiClass.R)
                {
                    level++;
                }
                else if (type is BidiClass.EN or BidiClass.AN)
                {
                    level += 2;
                }
            }
            else if (type is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                level++;
            }

            levels[index] = (byte)level;
        }

        // X9 retained: a removed character takes the level of its neighbour so it never splits a run.
        for (var i = 0; i < count; i++)
        {
            if (removed[i])
            {
                var neighbour = i - 1;
                if (neighbour < 0)
                {
                    neighbour = Array.FindIndex(removed, value => !value);
                }

                levels[i] = neighbour >= 0 ? levels[neighbour] : (byte)paragraphLevel;
            }
        }

        ApplyL1(original, levels, removed, paragraphLevel);
        return new BidiResult { Levels = levels, Removed = removed };
    }

    /// <summary>Establishes the visual order (UAX #9, L2).</summary>
    public static int[] ReorderVisual(ReadOnlySpan<byte> levels)
    {
        var order = new int[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            order[i] = i;
        }

        var maxLevel = 0;
        var lowestOdd = byte.MaxValue;
        foreach (var level in levels)
        {
            maxLevel = Math.Max(maxLevel, level);
            if ((level & 1) == 1)
            {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        if (lowestOdd == byte.MaxValue)
        {
            return order;
        }

        for (var level = maxLevel; level >= lowestOdd; level--)
        {
            for (var i = 0; i < order.Length; i++)
            {
                if (levels[order[i]] < level)
                {
                    continue;
                }

                var start = i;
                while (i < order.Length && levels[order[i]] >= level)
                {
                    i++;
                }

                Array.Reverse(order, start, i - start);
            }
        }

        return order;
    }

    private static (int[] MatchingPdi, int[] MatchingIsolate) MatchIsolates(BidiClass[] original)
    {
        var matchingPdi = new int[original.Length];
        var matchingIsolate = new int[original.Length];
        Array.Fill(matchingPdi, -1);
        Array.Fill(matchingIsolate, -1);
        var stack = new Stack<int>();
        for (var i = 0; i < original.Length; i++)
        {
            if (IsIsolate(original[i]))
            {
                stack.Push(i);
            }
            else if (original[i] == BidiClass.PDI && stack.Count > 0)
            {
                var open = stack.Pop();
                matchingPdi[open] = i;
                matchingIsolate[i] = open;
            }
        }

        return (matchingPdi, matchingIsolate);
    }

    private static void ApplyExplicitLevels(
        ReadOnlySpan<int> codePoints,
        BidiClass[] original,
        BidiClass[] types,
        byte[] levels,
        bool[] removed,
        int[] matchingPdi,
        int paragraphLevel)
    {
        var stack = new List<(int Level, BidiClass Override, bool Isolate)>
        {
            (paragraphLevel, BidiClass.ON, false),
        };
        var overflowIsolate = 0;
        var overflowEmbedding = 0;
        var validIsolate = 0;

        for (var i = 0; i < original.Length; i++)
        {
            var current = stack[^1];
            switch (original[i])
            {
                case BidiClass.RLE:
                case BidiClass.LRE:
                case BidiClass.RLO:
                case BidiClass.LRO:
                {
                    removed[i] = true;
                    types[i] = BidiClass.BN;
                    levels[i] = (byte)current.Level;
                    var newLevel = original[i] is BidiClass.RLE or BidiClass.RLO
                        ? NextOdd(current.Level)
                        : NextEven(current.Level);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        var over = original[i] == BidiClass.RLO ? BidiClass.R
                            : original[i] == BidiClass.LRO ? BidiClass.L
                            : BidiClass.ON;
                        stack.Add((newLevel, over, false));
                    }
                    else if (overflowIsolate == 0)
                    {
                        overflowEmbedding++;
                    }

                    break;
                }

                case BidiClass.RLI:
                case BidiClass.LRI:
                case BidiClass.FSI:
                {
                    levels[i] = (byte)current.Level;
                    types[i] = BidiClass.ON;
                    var rightToLeft = original[i] == BidiClass.RLI
                        || (original[i] == BidiClass.FSI && FsiIsRightToLeft(codePoints, original, matchingPdi, i));
                    var newLevel = rightToLeft ? NextOdd(current.Level) : NextEven(current.Level);
                    if (newLevel <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        stack.Add((newLevel, BidiClass.ON, true));
                        validIsolate++;
                    }
                    else
                    {
                        overflowIsolate++;
                    }

                    break;
                }

                case BidiClass.PDI:
                {
                    if (overflowIsolate > 0)
                    {
                        overflowIsolate--;
                    }
                    else if (validIsolate > 0)
                    {
                        overflowEmbedding = 0;
                        while (!stack[^1].Isolate)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        stack.RemoveAt(stack.Count - 1);
                        validIsolate--;
                    }

                    levels[i] = (byte)stack[^1].Level;
                    types[i] = BidiClass.ON;
                    break;
                }

                case BidiClass.PDF:
                {
                    removed[i] = true;
                    types[i] = BidiClass.BN;
                    levels[i] = (byte)current.Level;
                    if (overflowIsolate > 0)
                    {
                        // nothing
                    }
                    else if (overflowEmbedding > 0)
                    {
                        overflowEmbedding--;
                    }
                    else if (!stack[^1].Isolate && stack.Count > 1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    break;
                }

                case BidiClass.B:
                {
                    levels[i] = (byte)paragraphLevel;
                    stack.Clear();
                    stack.Add((paragraphLevel, BidiClass.ON, false));
                    overflowIsolate = 0;
                    overflowEmbedding = 0;
                    validIsolate = 0;
                    break;
                }

                default:
                {
                    if (original[i] == BidiClass.BN)
                    {
                        removed[i] = true;
                        levels[i] = (byte)current.Level;
                        types[i] = BidiClass.BN;
                        break;
                    }

                    levels[i] = (byte)current.Level;
                    types[i] = current.Override switch
                    {
                        BidiClass.L => BidiClass.L,
                        BidiClass.R => BidiClass.R,
                        _ => original[i],
                    };
                    break;
                }
            }
        }
    }

    private static bool FsiIsRightToLeft(
        ReadOnlySpan<int> codePoints,
        BidiClass[] original,
        int[] matchingPdi,
        int index)
    {
        var end = matchingPdi[index] >= 0 ? matchingPdi[index] : codePoints.Length;
        var i = index + 1;
        while (i < end)
        {
            if (IsIsolate(original[i]))
            {
                if (matchingPdi[i] < 0)
                {
                    break; // an unmatched nested isolate runs to the end of the paragraph
                }

                i = matchingPdi[i] + 1;
                continue;
            }

            if (original[i] == BidiClass.L)
            {
                return false;
            }

            if (original[i] is BidiClass.R or BidiClass.AL)
            {
                return true;
            }

            i++;
        }

        return false;
    }

    private static List<(int Start, int End, int Level)> BuildRuns(List<int> order, byte[] levels)
    {
        var runs = new List<(int Start, int End, int Level)>();
        var start = 0;
        for (var s = 1; s <= order.Count; s++)
        {
            if (s == order.Count || levels[order[s]] != levels[order[start]])
            {
                runs.Add((start, s - 1, levels[order[start]]));
                start = s;
            }
        }

        return runs;
    }

    /// <summary>Applies W1–N2 to an isolating run sequence and writes the types back.</summary>
    private static void ResolveSequence(
        ReadOnlySpan<int> codePoints,
        BidiClass[] original,
        BidiClass[] types,
        List<int> sequence,
        int sequenceLevel,
        List<int> order,
        byte[] levels,
        bool[] removed,
        int paragraphLevel)
    {
        var length = sequence.Count;
        var work = new BidiClass[length];
        for (var i = 0; i < length; i++)
        {
            work[i] = types[sequence[i]];
        }

        var sos = SequenceDirection(sequenceLevel, PrevLevel(sequence[0], levels, removed, paragraphLevel));
        var lastIndex = sequence[^1];
        var eos = IsIsolate(original[lastIndex])
            ? DirectionOf(paragraphLevel)
            : SequenceDirection(sequenceLevel, NextLevel(sequence[^1], levels, removed, paragraphLevel));

        ApplyWeak(work, original, sequence, sos);
        ApplyBrackets(codePoints, work, original, sequence, sequenceLevel, sos);
        ApplyNeutrals(work, sos, eos, DirectionOf(sequenceLevel));

        for (var i = 0; i < length; i++)
        {
            types[sequence[i]] = work[i];
        }
    }

    private static void ApplyWeak(BidiClass[] work, BidiClass[] original, List<int> sequence, BidiClass sos)
    {
        // W1
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] != BidiClass.NSM)
            {
                continue;
            }

            work[i] = i == 0
                ? sos
                : IsIsolate(original[sequence[i - 1]]) || original[sequence[i - 1]] == BidiClass.PDI
                    ? BidiClass.ON
                    : work[i - 1];
        }

        // W2
        var strong = sos;
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] == BidiClass.EN && strong == BidiClass.AL)
            {
                work[i] = BidiClass.AN;
            }

            if (work[i] is BidiClass.L or BidiClass.R or BidiClass.AL)
            {
                strong = work[i];
            }
        }

        // W3
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] == BidiClass.AL)
            {
                work[i] = BidiClass.R;
            }
        }

        // W4
        for (var i = 1; i < work.Length - 1; i++)
        {
            if (work[i] == BidiClass.ES && work[i - 1] == BidiClass.EN && work[i + 1] == BidiClass.EN)
            {
                work[i] = BidiClass.EN;
            }
            else if (work[i] == BidiClass.CS
                && ((work[i - 1] == BidiClass.EN && work[i + 1] == BidiClass.EN)
                    || (work[i - 1] == BidiClass.AN && work[i + 1] == BidiClass.AN)))
            {
                work[i] = work[i - 1];
            }
        }

        // W5
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] != BidiClass.ET)
            {
                continue;
            }

            var start = i;
            while (i < work.Length && work[i] == BidiClass.ET)
            {
                i++;
            }

            var adjacentEn = (start > 0 && work[start - 1] == BidiClass.EN)
                || (i < work.Length && work[i] == BidiClass.EN);
            if (adjacentEn)
            {
                for (var j = start; j < i; j++)
                {
                    work[j] = BidiClass.EN;
                }
            }
        }

        // W6
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                work[i] = BidiClass.ON;
            }
        }

        // W7
        strong = sos;
        for (var i = 0; i < work.Length; i++)
        {
            if (work[i] == BidiClass.EN && strong == BidiClass.L)
            {
                work[i] = BidiClass.L;
            }

            if (work[i] is BidiClass.L or BidiClass.R)
            {
                strong = work[i];
            }
        }
    }

    private static void ApplyBrackets(
        ReadOnlySpan<int> codePoints,
        BidiClass[] work,
        BidiClass[] original,
        List<int> sequence,
        int sequenceLevel,
        BidiClass sos)
    {
        var stack = new List<(int Index, int Paired)>();
        var pairs = new List<(int Open, int Close)>();
        for (var i = 0; i < work.Length; i++)
        {
            if (!BidiData.TryGetBracket(codePoints[sequence[i]], out var paired, out var isOpen))
            {
                continue;
            }

            if (isOpen)
            {
                // BD16: the bracket stack is limited to 63 elements; on overflow the
                // the pairing is aborted for the rest of the sequence.
                if (stack.Count >= MaxBracketStack)
                {
                    break;
                }

                stack.Add((i, CanonicalBracket(paired)));
                continue;
            }

            // Closing bracket: search for a matching opening one. If none is found,
            // the stack unchanged (the bracket is ignored).
            var closer = CanonicalBracket(codePoints[sequence[i]]);
            var match = -1;
            for (var s = stack.Count - 1; s >= 0; s--)
            {
                if (stack[s].Paired == closer)
                {
                    match = s;
                    break;
                }
            }

            if (match >= 0)
            {
                pairs.Add((stack[match].Index, i));
                stack.RemoveRange(match, stack.Count - match);
            }
        }

        pairs.Sort((left, right) => left.Open.CompareTo(right.Open));
        var embedding = DirectionOf(sequenceLevel);
        var opposite = embedding == BidiClass.L ? BidiClass.R : BidiClass.L;

        foreach (var (open, close) in pairs)
        {
            var (insideEmbedding, insideOpposite) = StrongInside(work, open + 1, close - 1, embedding, opposite);
            BidiClass? resolved = null;
            if (insideEmbedding)
            {
                resolved = embedding;
            }
            else if (insideOpposite)
            {
                var before = StrongBefore(work, open, sos);
                resolved = before == opposite ? opposite : embedding;
            }

            if (resolved is null)
            {
                continue;
            }

            work[open] = resolved.Value;
            work[close] = resolved.Value;

            // Following NSM follow the bracket.
            for (var i = open + 1; i < close && original[sequence[i]] == BidiClass.NSM; i++)
            {
                work[i] = resolved.Value;
            }

            for (var i = close + 1; i < work.Length && original[sequence[i]] == BidiClass.NSM; i++)
            {
                work[i] = resolved.Value;
            }
        }
    }

    private static (bool Embedding, bool Opposite) StrongInside(
        BidiClass[] work,
        int start,
        int end,
        BidiClass embedding,
        BidiClass opposite)
    {
        var foundEmbedding = false;
        var foundOpposite = false;
        for (var i = start; i <= end; i++)
        {
            var strong = AsStrong(work[i]);
            if (strong == embedding)
            {
                foundEmbedding = true;
            }
            else if (strong == opposite)
            {
                foundOpposite = true;
            }
        }

        return (foundEmbedding, foundOpposite);
    }

    private static int CanonicalBracket(int codePoint)
        => codePoint switch
        {
            0x2329 => 0x3008,
            0x232A => 0x3009,
            _ => codePoint,
        };

    private static BidiClass StrongBefore(BidiClass[] work, int index, BidiClass sos)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var strong = AsStrong(work[i]);
            if (strong != BidiClass.ON)
            {
                return strong;
            }
        }

        return sos == BidiClass.L ? BidiClass.L : BidiClass.R;
    }

    private static void ApplyNeutrals(BidiClass[] work, BidiClass sos, BidiClass eos, BidiClass embedding)
    {
        for (var i = 0; i < work.Length; i++)
        {
            if (!IsNeutral(work[i]))
            {
                continue;
            }

            var start = i;
            while (i < work.Length && IsNeutral(work[i]))
            {
                i++;
            }

            var before = start == 0 ? sos : AsStrong(work[start - 1]);
            var after = i == work.Length ? eos : AsStrong(work[i]);
            if (before == BidiClass.ON)
            {
                before = sos;
            }

            if (after == BidiClass.ON)
            {
                after = eos;
            }

            var resolved = before == after ? before : embedding;
            for (var j = start; j < i; j++)
            {
                work[j] = resolved;
            }
        }
    }

    private static BidiClass AsStrong(BidiClass type)
        => type switch
        {
            BidiClass.L => BidiClass.L,
            BidiClass.R or BidiClass.AL or BidiClass.EN or BidiClass.AN => BidiClass.R,
            _ => BidiClass.ON,
        };

    private static bool IsNeutral(BidiClass type)
        => type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON;

    private static BidiClass SequenceDirection(int level, int neighborLevel)
        => DirectionOf(Math.Max(level, neighborLevel));

    private static int PrevLevel(int index, byte[] levels, bool[] removed, int paragraphLevel)
        => index - 1 >= 0 ? levels[index - 1] : paragraphLevel;

    private static int NextLevel(int index, byte[] levels, bool[] removed, int paragraphLevel)
        => index + 1 < levels.Length ? levels[index + 1] : paragraphLevel;

    private static BidiClass DirectionOf(int level) => (level & 1) == 0 ? BidiClass.L : BidiClass.R;

    private static bool IsIsolate(BidiClass type)
        => type is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static int NextOdd(int level) => (level + 1) | 1;

    private static int NextEven(int level) => (level + 2) & ~1;

    private static void ApplyL1(BidiClass[] original, byte[] levels, bool[] removed, int paragraphLevel)
    {
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] is BidiClass.B or BidiClass.S)
            {
                levels[i] = (byte)paragraphLevel;
                for (var j = i - 1; j >= 0 && (original[j] is BidiClass.WS || IsIsolate(original[j]) || original[j] == BidiClass.PDI || removed[j]); j--)
                {
                    levels[j] = (byte)paragraphLevel;
                }
            }
        }

        for (var j = original.Length - 1; j >= 0 && (original[j] is BidiClass.WS || IsIsolate(original[j]) || original[j] == BidiClass.PDI || removed[j]); j--)
        {
            levels[j] = (byte)paragraphLevel;
        }
    }
}
