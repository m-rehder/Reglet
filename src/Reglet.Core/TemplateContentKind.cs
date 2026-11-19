namespace Reglet;

/// <summary>
/// kind of content that is bound to a template placeholder. The kind decides
/// whether the placeholder is replaced within the surrounding paragraph (inline) or
/// receives its own area (block).
/// </summary>
public enum TemplateContentKind
{
    /// <summary>Inline content such as text or a page number field; the surrounding paragraph remains.</summary>
    Inline,

    /// <summary>Block content such as paragraphs, headings, lists, tables, images or page breaks.</summary>
    Block,
}
