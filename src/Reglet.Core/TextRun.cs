namespace Reglet;

/// <summary>A text run with optional formatting.</summary>
public sealed class TextRun : Inline
{
    private string? _fontFamily;
    private Length? _fontSize;
    private TextBaseline? _baseline;
    private TextStrikethrough? _strikethrough;
    private TextUnderlineStyle? _underlineStyle;
    private Length? _kerning;
    private double? _horizontalScaling;

    /// <summary>Creates a text run.</summary>
    /// <param name="text">The text content.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public TextRun(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The text content.</summary>
    public string Text { get; }

    /// <summary>
    /// bold. <c>null</c> means "not set", so a style provides the value; <c>false</c> switches an
    /// inherited bold off.
    /// </summary>
    public bool? Bold { get; init; }

    /// <summary>italic; <c>null</c> means "not set" (see <see cref="Bold"/>).</summary>
    public bool? Italic { get; init; }

    /// <summary>
    /// single underline; <c>null</c> means "not set". Corresponds to
    /// <see cref="UnderlineStyle"/> = Single.
    /// </summary>
    public bool? Underline { get; init; }

    /// <summary>
    /// underline style; <c>null</c> means "not set", <see cref="TextUnderlineStyle.None"/> switches
    /// an inherited underline off. Overrides <see cref="Underline"/> when not null.
    /// </summary>
    public TextUnderlineStyle? UnderlineStyle
    {
        get => _underlineStyle;
        init
        {
            if (value is { } style && !Enum.IsDefined(style))
            {
                throw new ArgumentOutOfRangeException(nameof(UnderlineStyle), style, "Unknown underline style.");
            }

            _underlineStyle = value;
        }
    }

    /// <summary>Underline color; null means the text color.</summary>
    public Color? UnderlineColor { get; init; }

    /// <summary>
    /// strikethrough (single or double); <c>null</c> means "not set",
    /// <see cref="TextStrikethrough.None"/> switches an inherited one off.
    /// </summary>
    public TextStrikethrough? Strikethrough
    {
        get => _strikethrough;
        init
        {
            if (value is { } strikethrough && !Enum.IsDefined(strikethrough))
            {
                throw new ArgumentOutOfRangeException(nameof(Strikethrough), strikethrough, "Unknown strikethrough.");
            }

            _strikethrough = value;
        }
    }

    /// <summary>
    /// vertical position (superscript or subscript); <c>null</c> means "not set",
    /// <see cref="TextBaseline.Normal"/> switches an inherited one off.
    /// </summary>
    public TextBaseline? Baseline
    {
        get => _baseline;
        init
        {
            if (value is { } baseline && !Enum.IsDefined(baseline))
            {
                throw new ArgumentOutOfRangeException(nameof(Baseline), baseline, "Unknown baseline position.");
            }

            _baseline = value;
        }
    }

    /// <summary>all letters as uppercase; <c>null</c> means "not set" (see <see cref="Bold"/>).</summary>
    public bool? AllCaps { get; init; }

    /// <summary>small capitals; <c>null</c> means "not set" (see <see cref="Bold"/>).</summary>
    public bool? SmallCaps { get; init; }

    /// <summary>Text effects (outline, shadow, relief); <c>null</c> means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.</summary>
    public TextEffects? Effects { get; init; }

    /// <summary>Background color behind the text (shading).</summary>
    public Color? Background { get; init; }

    /// <summary>Text highlighter color (highlight).</summary>
    public Color? Highlight { get; init; }

    /// <summary>Minimum font size from which kerning is applied; null means no specification.</summary>
    public Length? Kerning
    {
        get => _kerning;
        init
        {
            if (value is { Points: < 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(Kerning), "Kerning threshold must not be negative.");
            }

            _kerning = value;
        }
    }

    /// <summary>Additional character spacing (positive = wider, negative = tighter).</summary>
    public Length? CharacterSpacing { get; init; }

    /// <summary>Horizontale Skalierung in Prozent (100 = normal).</summary>
    public double? HorizontalScaling
    {
        get => _horizontalScaling;
        init
        {
            if (value is { } scaling && (!double.IsFinite(scaling) || scaling <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(HorizontalScaling), "Scale must be positive and finite.");
            }

            _horizontalScaling = value;
        }
    }

    /// <summary>Baseline shift (positive = up).</summary>
    public Length? Position { get; init; }

    /// <summary>Font family; null means the document default.</summary>
    public string? FontFamily
    {
        get => _fontFamily;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Font family must not be empty.", nameof(FontFamily));
            }

            _fontFamily = value;
        }
    }

    /// <summary>Font size; null means the document default.</summary>
    public Length? FontSize
    {
        get => _fontSize;
        init
        {
            if (value is { Points: <= 0 })
            {
                throw new ArgumentOutOfRangeException(nameof(FontSize), "Font size must be positive.");
            }

            _fontSize = value;
        }
    }

    /// <summary>hidden text; <c>null</c> means "not set" (see <see cref="Bold"/>).</summary>
    public bool? Hidden { get; init; }

    /// <summary>Border around the run (character border); null means none.</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>Text color; null means the document default.</summary>
    public Color? Color { get; init; }

    /// <summary>
    /// The name of the character style (see <see cref="CharacterStyle"/>); null means no style.
    /// The formatting of the run itself takes precedence over the style.
    /// </summary>
    public string? CharacterStyleName { get; init; }

    /// <summary>
    /// whether the run is underlined: an underline style other than
    /// <see cref="TextUnderlineStyle.None"/> or, when no style is set, the
    /// <see cref="Underline"/> shorthand.
    /// </summary>
    public bool IsUnderlined => UnderlineStyle is { } style
        ? style != TextUnderlineStyle.None
        : Underline == true;
}
