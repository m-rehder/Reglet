namespace Reglet;

/// <summary>
/// A named character style (a reusable set of character formatting). A run references a
/// style by name (<see cref="TextRun.CharacterStyleName"/>); the formatting of the run
/// itself takes precedence.
/// </summary>
/// <remarks>
/// The toggles and enumerations are tri-state: <c>null</c> means the style does not set the
/// property, so the paragraph style provides it, while <c>false</c> or
/// <see cref="TextUnderlineStyle.None"/> switch an inherited property off.
/// </remarks>
public sealed class CharacterStyle
{
    /// <summary>Creates a character style.</summary>
    /// <param name="name">The name of the style.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public CharacterStyle(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>The name of the style (referenced by <see cref="TextRun.CharacterStyleName"/>).</summary>
    public string Name { get; }

    /// <summary>Optional name of the linked paragraph style (DOCX <c>w:link</c>); null means none.</summary>
    public string? LinkedStyleName { get; init; }

    /// <summary>Bold; <c>null</c> means "not set".</summary>
    public bool? Bold { get; init; }

    /// <summary>Italic; <c>null</c> means "not set".</summary>
    public bool? Italic { get; init; }

    /// <summary>Single underline; <c>null</c> means "not set".</summary>
    public bool? Underline { get; init; }

    /// <summary>
    /// Underline style; <c>null</c> means "not set", <see cref="TextUnderlineStyle.None"/> switches
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
    /// Strikethrough (single or double); <c>null</c> means "not set",
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
    /// Vertical position (superscript or subscript); <c>null</c> means "not set",
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

    /// <summary>All capitals; <c>null</c> means "not set".</summary>
    public bool? AllCaps { get; init; }

    /// <summary>Small capitals; <c>null</c> means "not set".</summary>
    public bool? SmallCaps { get; init; }

    /// <summary>Text effects (outline, shadow, relief); <c>null</c> means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.</summary>
    public TextEffects? Effects { get; init; }

    /// <summary>Hidden text; <c>null</c> means "not set".</summary>
    public bool? Hidden { get; init; }

    /// <summary>The text color.</summary>
    public Color? Color { get; init; }

    /// <summary>The background color of the text.</summary>
    public Color? Background { get; init; }

    /// <summary>The highlighting color.</summary>
    public Color? Highlight { get; init; }

    /// <summary>The font family.</summary>
    public string? FontFamily { get; init; }

    /// <summary>The font size.</summary>
    public Length? FontSize { get; init; }

    /// <summary>The border around the text.</summary>
    public ParagraphBorder? Border { get; init; }

    /// <summary>The minimum font size for the kerning.</summary>
    public Length? Kerning { get; init; }

    /// <summary>The spacing between the characters.</summary>
    public Length? CharacterSpacing { get; init; }

    /// <summary>The horizontal scaling in percent (100 = unchanged).</summary>
    public double? HorizontalScaling { get; init; }

    /// <summary>The vertical position (raised or lowered).</summary>
    public Length? Position { get; init; }

    private readonly TextUnderlineStyle? _underlineStyle;
    private readonly TextStrikethrough? _strikethrough;
    private readonly TextBaseline? _baseline;
}
