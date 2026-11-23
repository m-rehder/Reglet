namespace Reglet;

/// <summary>Whether the paged HTML is a whole document or an element to embed in a host page.</summary>
public enum HtmlOutput
{
    /// <summary>A complete HTML document.</summary>
    Document,

    /// <summary>
    /// Only the root element (a <c>div</c> with the class <c>{ClassPrefix}document</c>) and its scoped styles, without
    /// <c>html</c>, <c>head</c> and <c>body</c>.
    /// </summary>
    Fragment,
}

/// <summary>
/// Options of the paged HTML (ADR-0039): the pages of the PDF writer as HTML, one element per page, drawn as SVG with real
/// text and the fonts of <see cref="PdfWriteOptions.FontCollection"/> embedded as <c>@font-face</c>.
/// </summary>
public sealed record PdfHtmlOptions
{
    private string _classPrefix = "reglet-";

    /// <summary>The default options.</summary>
    public static PdfHtmlOptions Default { get; } = new();

    /// <summary>A complete document (default) or a fragment for a host page.</summary>
    public HtmlOutput Output { get; init; }

    /// <summary>
    /// The prefix of every class, font family and id (default <c>reglet-</c>). The root element has the class
    /// <c>{prefix}document</c>, every page the class <c>{prefix}page</c> and a <c>data-page</c> number. The styles select
    /// only these classes, so a host page and other fragments are not changed.
    /// </summary>
    /// <exception cref="ArgumentException">The prefix is not a letter followed by letters, digits, hyphens or underscores.</exception>
    public string ClassPrefix
    {
        get => _classPrefix;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0 || !char.IsAsciiLetter(value[0]) || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                throw new ArgumentException("The class prefix must start with a letter and contain only letters, digits, '-' and '_'.", nameof(ClassPrefix));
            }

            _classPrefix = value;
        }
    }
}
