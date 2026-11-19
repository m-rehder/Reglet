namespace Reglet;

/// <summary>
/// one level of a <see cref="NumberingDefinition"/>: the kind, the number format, the start
/// value and the formatting of the number or bullet.
/// </summary>
public sealed record NumberingLevel
{
    private ListKind _kind = ListKind.Number;
    private ListNumberFormat _format = ListNumberFormat.Decimal;
    private int _start = 1;

    /// <summary>Bullet or numbered level (default: <see cref="ListKind.Number"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is unknown.</exception>
    public ListKind Kind
    {
        get => _kind;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown list kind.");
            }

            _kind = value;
        }
    }

    /// <summary>
    /// Number format of a numbered level (default: <see cref="ListNumberFormat.Decimal"/>).
    /// Ignored for bullet levels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is unknown.</exception>
    public ListNumberFormat Format
    {
        get => _format;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown list number format.");
            }

            _format = value;
        }
    }

    /// <summary>First number of a numbered level (default 1). Ignored for bullet levels.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The start value is less than 1.</exception>
    public int Start
    {
        get => _start;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Start value must be at least 1.");
            }

            _start = value;
        }
    }

    /// <summary>
    /// the color of the number or of the bullet (phase AJ, AJ04); null means the color of the
    /// text. The formats that have no list styles per list (ODT) report the value.
    /// </summary>
    public Color? NumberColor { get; init; }

    /// <summary>
    /// the number or the bullet is written in bold (phase AJ, AJ04); false means normal.
    /// </summary>
    public bool NumberBold { get; init; }

    /// <summary>
    /// what follows the marker before the text: a tab (default), a space or nothing. DOCX
    /// (<c>w:suff</c>) and ODT (<c>text:label-followed-by</c>) keep it; the PDF always starts the
    /// text at the item indent.
    /// </summary>
    public ListSuffix Suffix { get; init; }
}
