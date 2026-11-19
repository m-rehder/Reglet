namespace Reglet;

/// <summary>Result of a font selection: the matching style and the attributes still to be simulated.</summary>
/// <param name="Face">The chosen style.</param>
/// <param name="SyntheticBold">Bold is requested, but the style is not bold (is simulated).</param>
/// <param name="SyntheticItalic">Italic is requested, but the style is not italic (is simulated).</param>
public sealed record FontMatch(FontFace Face, bool SyntheticBold, bool SyntheticItalic);

/// <summary>
/// an immutable collection of caller-provided fonts, grouped by family
/// and style (normal, bold, italic, bold-italic). Family names are compared without regard
/// case-insensitively compared; the empty name stands for the default font of text
/// without its own family. Every change yields a new collection.
/// </summary>
/// <example>
/// <code>
/// var fonts = FontCollection.Empty
///     .Add(FontFace.LoadFile("NotoSans-Regular.ttf"))
///     .Add(FontFace.LoadFile("NotoSans-Bold.ttf"))
///     .AddDefault(FontFace.LoadFile("NotoSans-Regular.ttf"));
/// </code>
/// </example>
public sealed class FontCollection
{
    private readonly Dictionary<string, Dictionary<(bool Bold, bool Italic), FontFace>> _families;

    private FontCollection(Dictionary<string, Dictionary<(bool Bold, bool Italic), FontFace>> families)
    {
        _families = families;
    }

    /// <summary>An empty collection.</summary>
    public static FontCollection Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    /// <summary>The family names of the collection (the empty name stands for the default font).</summary>
    public IReadOnlyCollection<string> Families => _families.Keys;

    /// <summary>Number of styles across all families.</summary>
    public int Count => _families.Values.Sum(faces => faces.Count);

    /// <summary>Loads all <c>.ttf</c> files of a directory (non-recursive) under their family name.</summary>
    /// <exception cref="FontException">A file is corrupted, or two files use the same style.</exception>
    public static FontCollection FromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.ttf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FontException("The font directory could not be read.", exception);
        }

        // sorted so that the result does not depend on the file system order.
        Array.Sort(files, StringComparer.Ordinal);
        var collection = Empty;
        foreach (var file in files)
        {
            collection = collection.Add(FontFace.LoadFile(file));
        }

        return collection;
    }

    /// <summary>
    /// Loads the installed TrueType fonts from the standard font directories of the platform.
    /// Files that are not TrueType outlines, are corrupted or repeat an existing family/style are
    /// skipped; directories that cannot be read are ignored.
    /// </summary>
    public static FontCollection FromSystem() => FromDirectories(SystemFontDirectories());

    internal static FontCollection FromDirectories(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var collection = Empty;
        foreach (var directory in directories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.ttf", SearchOption.AllDirectories);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    collection = collection.Add(FontFace.LoadFile(file));
                }
                catch (Exception exception) when (exception is FontException or DocumentValidationException or IOException)
                {
                    // A system directory may contain non-TrueType outlines, broken files or a
                    // second file for an already loaded family/style.
                }
            }
        }

        return collection;
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(fonts))
            {
                yield return fonts;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                yield return Path.Combine(local, "Microsoft", "Windows", "Fonts");
            }

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
            yield break;
        }

        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".fonts");
        yield return Path.Combine(home, ".local", "share", "fonts");
    }

    /// <summary>Adds a style under its own family name.</summary>
    /// <exception cref="DocumentValidationException">The style is already taken in the family.</exception>
    public FontCollection Add(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        return Add(face.FamilyName, face);
    }

    /// <summary>Adds a style under a freely chosen family name (e.g. the name used in the document).</summary>
    /// <exception cref="DocumentValidationException">The style is already taken in the family.</exception>
    public FontCollection Add(string family, FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        return Add(family, face, face.IsBold, face.IsItalic);
    }

    /// <summary>
    /// adds a style with an explicitly specified style, for example when the information in
    /// the font file do not match.
    /// </summary>
    /// <exception cref="DocumentValidationException">The style is already taken in the family.</exception>
    public FontCollection Add(string family, FontFace face, bool bold, bool italic)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(face);

        var families = new Dictionary<string, Dictionary<(bool Bold, bool Italic), FontFace>>(_families.Comparer);
        foreach (var (name, faces) in _families)
        {
            families[name] = faces;
        }

        var updated = families.TryGetValue(family, out var existing)
            ? new Dictionary<(bool Bold, bool Italic), FontFace>(existing)
            : [];
        if (!updated.TryAdd((bold, italic), face))
        {
            throw new DocumentValidationException(
                $"The family '{family}' already has a style {StyleText(bold, italic)}.", nameof(face));
        }

        families[family] = updated;
        return new FontCollection(families);
    }

    /// <summary>A collection with only the faces that satisfy <paramref name="predicate"/>.</summary>
    internal FontCollection Where(Func<FontFace, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var families = new Dictionary<string, Dictionary<(bool Bold, bool Italic), FontFace>>(_families.Comparer);
        foreach (var (name, faces) in _families)
        {
            var kept = faces.Where(entry => predicate(entry.Value)).ToDictionary(entry => entry.Key, entry => entry.Value);
            if (kept.Count > 0)
            {
                families[name] = kept;
            }
        }

        return new FontCollection(families);
    }

    /// <summary>Sets the default font for text without its own family (family name "").</summary>
    /// <exception cref="DocumentValidationException">The style is already taken.</exception>
    public FontCollection AddDefault(FontFace face) => Add(string.Empty, face);

    /// <summary>Whether the family is contained in the collection.</summary>
    public bool Contains(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return _families.ContainsKey(family);
    }

    /// <summary>The styles of a family (empty if the family is missing).</summary>
    public IReadOnlyList<FontFace> GetFaces(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return _families.TryGetValue(family, out var faces)
            ? faces.OrderBy(entry => entry.Key.Bold).ThenBy(entry => entry.Key.Italic).Select(entry => entry.Value).ToList()
            : [];
    }

    /// <summary>
    /// chooses the best style of a family. Order: exact style; for bold-italic
    /// first bold, then italic; then normal; finally any style of the family. Missing
    /// attributes are marked as synthetic in the result.
    /// </summary>
    /// <param name="family">Family name; <see langword="null"/> or empty for the default font.</param>
    /// <param name="bold">Bold requested.</param>
    /// <param name="italic">Italic requested.</param>
    /// <returns>The selection, or <see langword="null"/> if the family is missing.</returns>
    public FontMatch? Match(string? family, bool bold, bool italic)
    {
        if (!_families.TryGetValue(family ?? string.Empty, out var faces))
        {
            return null;
        }

        (bool Bold, bool Italic)[] preferences =
        [
            (bold, italic),
            (bold, false),
            (false, italic),
            (false, false),
            (true, false),
            (false, true),
            (true, true),
        ];

        foreach (var slot in preferences)
        {
            if (faces.TryGetValue(slot, out var face))
            {
                return new FontMatch(face, bold && !slot.Bold, italic && !slot.Italic);
            }
        }

        return null;
    }

    private static string StyleText(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => "fett-kursiv",
        (true, false) => "fett",
        (false, true) => "kursiv",
        _ => "normal",
    };
}
