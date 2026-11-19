using System.Collections.Concurrent;
using System.Reflection;

namespace Reglet;

/// <summary>
/// the hyphenation patterns that ship with this package: the TeX patterns of the <c>hyph-utf8</c>
/// project for several languages, unmodified. Pass the result to
/// <c>PdfWriteOptions.Hyphenation</c>. The licences of the patterns are in
/// <c>THIRD-PARTY-NOTICES.md</c> of the package.
/// </summary>
public static class HyphenationLanguages
{
    /// <summary>language key, resource name, least characters before and after a break.</summary>
    private static readonly (string Key, int Left, int Right)[] Languages =
    [
        ("de-1996", 2, 2),
        ("de-1901", 2, 2),
        ("de-CH-1901", 2, 2),
        ("en-US", 2, 3),
        ("en-GB", 2, 3),
        ("fr", 2, 2),
        ("es", 2, 2),
        ("it", 2, 2),
        ("nl", 2, 2),
        ("pt", 2, 3),
        ("pl", 2, 2),
        ("da", 2, 2),
        ("nb", 2, 2),
    ];

    /// <summary>Regional and short names that map to a language of the package.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "de-1996",
        ["de-DE"] = "de-1996",
        ["de-AT"] = "de-1996",
        ["de-CH"] = "de-CH-1901",
        ["en"] = "en-US",
        ["fr-FR"] = "fr",
        ["es-ES"] = "es",
        ["it-IT"] = "it",
        ["nl-NL"] = "nl",
        ["pt-PT"] = "pt",
        ["pt-BR"] = "pt",
        ["pl-PL"] = "pl",
        ["da-DK"] = "da",
        ["nb-NO"] = "nb",
        ["no"] = "nb",
    };

    private static readonly ConcurrentDictionary<string, HyphenationPatterns> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The language keys of the package, for example <c>de-1996</c> and <c>en-US</c>.</summary>
    public static IReadOnlyList<string> Available { get; } = [.. Languages.Select(language => language.Key)];

    /// <summary>
    /// whether patterns exist for the language. Case does not matter; <c>de</c>, <c>de-DE</c> and
    /// <c>en</c> name the usual variant of the language.
    /// </summary>
    public static bool IsAvailable(string language) => Resolve(language) is not null;

    /// <summary>Loads the patterns of a language (parsed once, then shared).</summary>
    /// <exception cref="ArgumentException">The language is empty or not part of the package.</exception>
    public static HyphenationPatterns Load(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        var key = Resolve(language)
            ?? throw new ArgumentException($"No hyphenation patterns for '{language}'. Available: {string.Join(", ", Available)}.", nameof(language));
        return Cache.GetOrAdd(key, Read);
    }

    private static string? Resolve(string language)
    {
        var name = language.Trim().Replace('_', '-');
        foreach (var (key, _, _) in Languages)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return Aliases.TryGetValue(name, out var alias) ? alias : null;
    }

    private static HyphenationPatterns Read(string key)
    {
        var (_, left, right) = Languages.Single(language => language.Key == key);
        var resource = $"Reglet.Hyphenation.hyph-{key.ToLowerInvariant()}.pat.txt";
        using var stream = typeof(HyphenationLanguages).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The pattern resource '{resource}' is missing.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return HyphenationPatterns.Parse(reader, left, right);
    }
}
