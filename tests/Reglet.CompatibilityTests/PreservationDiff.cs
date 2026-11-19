using System.IO.Compression;
using System.Security.Cryptography;

/// <summary>Classification of a package part between an original and a rewritten document.</summary>
internal enum PackagePartStatus
{
    /// <summary>Present in both with identical content.</summary>
    Unchanged,

    /// <summary>Present in both with different content.</summary>
    Changed,

    /// <summary>Only present in the rewritten document.</summary>
    Added,

    /// <summary>Only present in the original document — a loss.</summary>
    Removed,
}

/// <summary>A package part with its comparison status.</summary>
internal readonly record struct PackagePartDiff(string Name, PackagePartStatus Status)
{
    public override string ToString() => Status + ": " + Name;
}

/// <summary>
/// AF02: compares the package parts of an original and a rewritten document. For ZIP packages
/// (DOCX/ODT) it reports per part whether it survived unchanged, changed, was added or was
/// lost; for anything else it compares the two blobs as a whole. This is the acceptance tool
/// for the preserving round trip: a <see cref="PackagePartStatus.Removed"/> part is data loss.
/// </summary>
internal static class PreservationDiff
{
    /// <summary>Classifies every part of the original against the rewritten document.</summary>
    public static IReadOnlyList<PackagePartDiff> Compare(byte[] original, byte[] rewritten)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rewritten);

        if (IsZip(original) && IsZip(rewritten))
        {
            return ComparePackages(original, rewritten);
        }

        return
        [
            new PackagePartDiff(
                "<document>",
                original.AsSpan().SequenceEqual(rewritten) ? PackagePartStatus.Unchanged : PackagePartStatus.Changed),
        ];
    }

    /// <summary>The lost parts (present in the original, absent after the rewrite).</summary>
    public static IReadOnlyList<PackagePartDiff> Losses(IReadOnlyList<PackagePartDiff> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);
        return differences.Where(difference => difference.Status == PackagePartStatus.Removed).ToList();
    }

    private static IReadOnlyList<PackagePartDiff> ComparePackages(byte[] original, byte[] rewritten)
    {
        var before = ReadParts(original);
        var after = ReadParts(rewritten);
        var names = before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        var result = new List<PackagePartDiff>();
        foreach (var name in names)
        {
            if (!before.TryGetValue(name, out var originalHash))
            {
                result.Add(new PackagePartDiff(name, PackagePartStatus.Added));
            }
            else if (!after.TryGetValue(name, out var rewrittenHash))
            {
                result.Add(new PackagePartDiff(name, PackagePartStatus.Removed));
            }
            else
            {
                result.Add(new PackagePartDiff(
                    name,
                    originalHash == rewrittenHash ? PackagePartStatus.Unchanged : PackagePartStatus.Changed));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadParts(byte[] package)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            // Directory entries carry no content and are not interesting for preservation.
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            using var content = entry.Open();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            parts[entry.FullName] = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        }

        return parts;
    }

    private static bool IsZip(byte[] data)
        => data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04;
}
