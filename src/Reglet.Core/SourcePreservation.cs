using System.Runtime.CompilerServices;

namespace Reglet;

/// <summary>
/// Tracks which <see cref="PortableDocument"/> instances were returned as-is by a reader. The
/// model is immutable, so a document that is still the exact instance the reader produced cannot
/// have been modified. The DOCX writer uses this to emit the original package verbatim
/// (lossless open+save); a document rebuilt with <c>with</c> or a builder is a different
/// instance and is regenerated instead.
/// </summary>
/// <remarks>
/// The keys are held weakly, so registering a document does not keep it (or its package) alive.
/// </remarks>
internal static class SourcePreservation
{
    private static readonly ConditionalWeakTable<PortableDocument, object> Pristine = new();

    /// <summary>Remembers a document as unmodified since it was read.</summary>
    public static void MarkPristine(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Pristine.AddOrUpdate(document, new object());
    }

    /// <summary>Whether the document is still the exact instance a reader returned.</summary>
    public static bool IsPristine(PortableDocument? document)
        => document is not null && Pristine.TryGetValue(document, out _);

    /// <summary>
    /// Whether only parts a writer patches changed: the section list is untouched (reference
    /// equality), the defaults are unchanged, no named styles were added and the language is the
    /// same. Then the original package can be adopted for the body and only metadata/settings
    /// patched.
    /// </summary>
    public static bool IsBodyUnchanged(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.SourceSections is { } sections
            && ReferenceEquals(document.Sections, sections)
            && document.Defaults == document.SourceDefaults
            && document.Styles.Count == 0
            && NamedStyleComparer.CharacterStylesUnchanged(document.CharacterStyles, document.SourceCharacterStyles)
            && document.Metadata.Language == document.SourceMetadata?.Language;
    }

    /// <summary>A short summary of the reader warnings for an actionable loss report.</summary>
    public static string? UnmodelledContentDetails(PortableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.SourceHasUnmodelledContent || document.SourceUnmodelledWarnings is not { Count: > 0 } warnings)
        {
            return null;
        }

        const int Max = 5;
        var summary = string.Join("; ", warnings.Take(Max));
        return warnings.Count > Max ? summary + "; …" : summary;
    }
}
