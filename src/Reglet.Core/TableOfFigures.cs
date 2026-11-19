namespace Reglet;

/// <summary>
/// a list of the captions of one label, for example "Figure" or "Table" (a table of figures). A
/// caption is a paragraph with a <c>SEQ</c> field of the label (<see cref="Field"/>, for example
/// <c>SEQ Figure \* ARABIC</c>); its text is the entry. DOCX writes a <c>TOC \c</c> field and ODT an
/// illustration or table index, so the application fills in the page numbers; PDF renders the
/// entries with page numbers and links.
/// </summary>
public sealed class TableOfFigures : Block
{
    private string _label = "Figure";

    /// <summary>Optional title above the entries.</summary>
    public string? Title { get; init; }

    /// <summary>The label of the captions that are listed (default "Figure").</summary>
    /// <exception cref="ArgumentException">The label is empty or contains a quotation mark, a backslash or a control character.</exception>
    public string Label
    {
        get => _label;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Any(character => character is '"' or '\\' || char.IsControl(character)))
            {
                throw new ArgumentException("The label must not contain a quotation mark, a backslash or a control character.", nameof(value));
            }

            _label = value.Trim();
        }
    }
}
