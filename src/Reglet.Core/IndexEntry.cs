namespace Reglet;

/// <summary>
/// a zero-width mark for an entry of the <see cref="AlphabeticalIndex"/> at this position of the
/// text. DOCX writes an <c>XE</c> field, ODT an alphabetical index mark; PDF records the page.
/// </summary>
public sealed class IndexEntry : Inline
{
    /// <summary>Creates an index entry, optionally below a main entry.</summary>
    /// <param name="text">The entry (or the main entry when <paramref name="subText"/> is given).</param>
    /// <param name="subText">An entry below the main entry; null for a main entry.</param>
    /// <exception cref="ArgumentException">A text is empty or contains a control character.</exception>
    public IndexEntry(string text, string? subText = null)
    {
        Check(text, nameof(text));
        if (subText is not null)
        {
            Check(subText, nameof(subText));
        }

        Text = text.Trim();
        SubText = subText?.Trim();
    }

    /// <summary>The entry; the main entry when <see cref="SubText"/> is set.</summary>
    public string Text { get; }

    /// <summary>The entry below the main entry, or null.</summary>
    public string? SubText { get; }

    private static void Check(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("An index entry must not contain a control character.", name);
        }
    }
}
