namespace Reglet;

/// <summary>
/// Resolves styles over their parent chains (<c>w:basedOn</c>, <c>style:parent-style-name</c>) once
/// each, iteratively and memoized: a crafted long chain neither exhausts the stack nor costs
/// quadratic time. The result for a key equals the recursive definition
/// <c>resolve(k) = combine(resolve(parent(k)) ?? base, k)</c> with a per-key cycle guard.
/// </summary>
internal static class StyleChain
{
    /// <summary>Resolves every key; a key without a link resolves to <paramref name="baseValue"/>.</summary>
    /// <param name="keys">The keys to resolve.</param>
    /// <param name="parent">The parent key of a key, null at the root.</param>
    /// <param name="has">Whether a key has a link.</param>
    /// <param name="baseValue">The value below the root, also for a missing parent or a cycle.</param>
    /// <param name="combine">The value of a key from the value of its parent.</param>
    /// <param name="stop">A parent that ends the chain with its own value, or null to continue.</param>
    public static Dictionary<string, T> ResolveAll<T>(
        IEnumerable<string> keys,
        Func<string, string?> parent,
        Func<string, bool> has,
        T baseValue,
        Func<T, string, T> combine,
        Func<string, (bool Stops, T Value)>? stop = null)
    {
        // memo holds results that do not depend on where a walk starts and can seed other walks;
        // the root of a walk that hit a cycle only goes into result.
        var memo = new Dictionary<string, T>(StringComparer.Ordinal);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (result.ContainsKey(key))
            {
                continue;
            }

            if (!has(key))
            {
                result[key] = baseValue;
                continue;
            }

            var path = new List<string> { key };
            var onPath = new HashSet<string>(StringComparer.Ordinal) { key };
            var start = baseValue;
            var cycle = false;
            var current = key;
            while (parent(current) is { } next)
            {
                if (stop?.Invoke(next) is { Stops: true } stopped)
                {
                    start = stopped.Value;
                    break;
                }

                if (memo.TryGetValue(next, out var known))
                {
                    start = known;
                    break;
                }

                if (!has(next))
                {
                    break;
                }

                if (!onPath.Add(next))
                {
                    cycle = true;
                    break;
                }

                path.Add(next);
                current = next;
            }

            var value = start;
            for (var index = path.Count - 1; index >= 0; index--)
            {
                value = combine(value, path[index]);
                // Inside a cycle the result depends on where the walk started; keep only the root.
                if (!cycle)
                {
                    memo[path[index]] = value;
                    result[path[index]] = value;
                }
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Whether the chain of any key ends at a missing parent or in a cycle (each key has at most one
    /// parent, so this is a linear walk over all keys).
    /// </summary>
    public static bool HasBrokenChain(IEnumerable<string> keys, Func<string, string?> parent, Func<string, bool> has)
    {
        // 1 = on the current walk, 2 = finished.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (state.ContainsKey(key) || !has(key))
            {
                continue;
            }

            var path = new List<string>();
            var current = key;
            var broken = false;
            while (true)
            {
                state[current] = 1;
                path.Add(current);
                if (parent(current) is not { } next)
                {
                    break;
                }

                if (!has(next) || state.TryGetValue(next, out var seen) && seen == 1)
                {
                    broken = true;
                    break;
                }

                if (seen == 2)
                {
                    break;
                }

                current = next;
            }

            if (broken)
            {
                return true;
            }

            foreach (var node in path)
            {
                state[node] = 2;
            }
        }

        return false;
    }
}
