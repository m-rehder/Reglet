namespace Reglet;

/// <summary>
/// the run formatting a model object provides, as the writers see it (ADR-0010). Every value is
/// tri-state: <c>null</c> means "not set" and writes nothing, <c>false</c>/<c>None</c>/<c>Normal</c>
/// switch an inherited property off and are written as an explicit off.
/// </summary>
internal sealed record RunFormat
{
    /// <summary>The empty formatting, shared so a writer does not allocate it per block.</summary>
    public static RunFormat Empty { get; } = new();

    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public TextUnderlineStyle? UnderlineStyle { get; init; }
    public Color? UnderlineColor { get; init; }
    public TextStrikethrough? Strikethrough { get; init; }
    public TextBaseline? Baseline { get; init; }
    public bool? AllCaps { get; init; }
    public bool? SmallCaps { get; init; }

    /// <summary>Text effects (outline, shadow, relief); <c>null</c> means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.</summary>
    public TextEffects? Effects { get; init; }
    public bool? Hidden { get; init; }
    public Color? Color { get; init; }
    public Color? Background { get; init; }
    public Color? Highlight { get; init; }
    public string? FontFamily { get; init; }
    public Length? FontSize { get; init; }
    public ParagraphBorder? Border { get; init; }
    public Length? Kerning { get; init; }
    public Length? CharacterSpacing { get; init; }
    public double? HorizontalScaling { get; init; }
    public Length? Position { get; init; }

    /// <summary>The formatting of a run (its direct formatting, without the character style).</summary>
    public static RunFormat Of(TextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new RunFormat
        {
            Bold = run.Bold,
            Italic = run.Italic,
            Underline = run.Underline,
            UnderlineStyle = run.UnderlineStyle,
            UnderlineColor = run.UnderlineColor,
            Strikethrough = run.Strikethrough,
            Baseline = run.Baseline,
            AllCaps = run.AllCaps,
            SmallCaps = run.SmallCaps,
            Effects = run.Effects,
            Hidden = run.Hidden,
            Color = run.Color,
            Background = run.Background,
            Highlight = run.Highlight,
            FontFamily = run.FontFamily,
            FontSize = run.FontSize,
            Border = run.Border,
            Kerning = run.Kerning,
            CharacterSpacing = run.CharacterSpacing,
            HorizontalScaling = run.HorizontalScaling,
            Position = run.Position,
        };
    }

    /// <summary>The formatting of a named character style.</summary>
    public static RunFormat Of(CharacterStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new RunFormat
        {
            Bold = style.Bold,
            Italic = style.Italic,
            Underline = style.Underline,
            UnderlineStyle = style.UnderlineStyle,
            UnderlineColor = style.UnderlineColor,
            Strikethrough = style.Strikethrough,
            Baseline = style.Baseline,
            AllCaps = style.AllCaps,
            SmallCaps = style.SmallCaps,
            Effects = style.Effects,
            Hidden = style.Hidden,
            Color = style.Color,
            Background = style.Background,
            Highlight = style.Highlight,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            Border = style.Border,
            Kerning = style.Kerning,
            CharacterSpacing = style.CharacterSpacing,
            HorizontalScaling = style.HorizontalScaling,
            Position = style.Position,
        };
    }

    /// <summary>The run formatting of a paragraph style.</summary>
    public static RunFormat Of(ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new RunFormat
        {
            Bold = style.Bold,
            Italic = style.Italic,
            Underline = style.Underline,
            UnderlineStyle = style.UnderlineStyle,
            UnderlineColor = style.UnderlineColor,
            Strikethrough = style.Strikethrough,
            Baseline = style.Baseline,
            AllCaps = style.AllCaps,
            SmallCaps = style.SmallCaps,
            Effects = style.Effects,
            Hidden = style.Hidden,
            Color = style.TextColor,
            Background = style.TextBackground,
            Highlight = style.Highlight,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            Border = style.TextBorder,
            Kerning = style.Kerning,
            CharacterSpacing = style.CharacterSpacing,
            HorizontalScaling = style.HorizontalScaling,
            Position = style.Position,
        };
    }

    /// <summary>
    /// what a writer has to state next to an inherited base: the resolved formatting where it differs
    /// from <paramref name="inherited"/>, so an inherited toggle that the text switches off becomes an
    /// explicit off and a value the base already provides is not repeated (ADR-0010).
    /// </summary>
    public RunFormat Difference(RunFormat inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        return new RunFormat
        {
            Bold = Toggle(Bold, inherited.Bold),
            Italic = Toggle(Italic, inherited.Italic),
            AllCaps = Toggle(AllCaps, inherited.AllCaps),
            SmallCaps = Toggle(SmallCaps, inherited.SmallCaps),
            Effects = Effects == inherited.Effects ? null : Effects,
            Hidden = Toggle(Hidden, inherited.Hidden),
            UnderlineStyle = Underline(EffectiveUnderline, inherited.EffectiveUnderline),
            Underline = null,
            Strikethrough = EnumToggle(Strikethrough, inherited.Strikethrough),
            Baseline = EnumToggle(Baseline, inherited.Baseline),
            UnderlineColor = Own(UnderlineColor, inherited.UnderlineColor),
            Color = Own(Color, inherited.Color),
            Background = Own(Background, inherited.Background),
            Highlight = Own(Highlight, inherited.Highlight),
            FontFamily = Own(FontFamily, inherited.FontFamily),
            FontSize = Own(FontSize, inherited.FontSize),
            Border = Own(Border, inherited.Border),
            Kerning = Own(Kerning, inherited.Kerning),
            CharacterSpacing = Own(CharacterSpacing, inherited.CharacterSpacing),
            HorizontalScaling = Own(HorizontalScaling, inherited.HorizontalScaling),
            Position = Own(Position, inherited.Position),
        };

        // A value the base already provides is not repeated; a toggle of the base that does not
        // apply has to be switched off explicitly.
        static bool? Toggle(bool? value, bool? inherited)
            => value == true
                ? inherited == true ? null : true
                : inherited == true ? false : null;

        // The same for an enumeration: "None"/"Normal" of the base becomes an explicit off.
        static T? EnumToggle<T>(T? value, T? inherited) where T : struct, Enum
        {
            var own = value is { } current && !EqualityComparer<T>.Default.Equals(current, default!)
                ? value
                : default(T?);
            var parent = inherited is { } parentValue && !EqualityComparer<T>.Default.Equals(parentValue, default!)
                ? inherited
                : default(T?);
            return EqualityComparer<T?>.Default.Equals(own, parent) ? default : own;
        }

        static TextUnderlineStyle? Underline(TextUnderlineStyle? value, TextUnderlineStyle? inherited)
            => value is not (null or TextUnderlineStyle.None)
                ? value
                : inherited is not (null or TextUnderlineStyle.None) ? TextUnderlineStyle.None : null;

        static T? Own<T>(T? value, T? inherited)
            => value is null || EqualityComparer<T?>.Default.Equals(value, inherited) ? default : value;
    }

    /// <summary>Whether a paragraph style provides any run formatting at all (no allocation).</summary>
    public static bool HasFormatting(ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Bold is not null || style.Italic is not null || style.Underline is not null
            || style.UnderlineStyle is not null || style.UnderlineColor is not null
            || style.Strikethrough is not null || style.Baseline is not null
            || style.AllCaps is not null || style.SmallCaps is not null || style.Effects is not null || style.Hidden is not null
            || style.TextColor is not null || style.TextBackground is not null
            || style.Highlight is not null || style.FontFamily is not null || style.FontSize is not null
            || style.TextBorder is not null || style.Kerning is not null
            || style.CharacterSpacing is not null || style.HorizontalScaling is not null
            || style.Position is not null;
    }

    /// <summary>Whether a run sets formatting of its own at all (no allocation).</summary>
    public static bool HasFormatting(TextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Bold is not null || run.Italic is not null || run.Underline is not null
            || run.UnderlineStyle is not null || run.UnderlineColor is not null
            || run.Strikethrough is not null || run.Baseline is not null
            || run.AllCaps is not null || run.SmallCaps is not null || run.Effects is not null || run.Hidden is not null
            || run.Color is not null || run.Background is not null || run.Highlight is not null
            || run.FontFamily is not null || run.FontSize is not null || run.Kerning is not null
            || run.CharacterSpacing is not null || run.HorizontalScaling is not null
            || run.Position is not null || run.Border is not null;
    }

    /// <summary>a run that carries this formatting (used where a writer works on runs).</summary>
    public TextRun ToRun(string text, string? characterStyleName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TextRun(text)
        {
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            UnderlineStyle = UnderlineStyle,
            UnderlineColor = UnderlineColor,
            Strikethrough = Strikethrough,
            Baseline = Baseline,
            AllCaps = AllCaps,
            SmallCaps = SmallCaps,
            Effects = Effects,
            Hidden = Hidden,
            Color = Color,
            Background = Background,
            Highlight = Highlight,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Border = Border,
            Kerning = Kerning,
            CharacterSpacing = CharacterSpacing,
            HorizontalScaling = HorizontalScaling,
            Position = Position,
            CharacterStyleName = characterStyleName,
        };
    }

    /// <summary>
    /// the paragraph style with this run formatting: the readers attach the run formatting of a
    /// style to the paragraph, so the writers can apply it as the base of its runs (ADR-0010).
    /// </summary>
    public ParagraphStyle ApplyTo(ParagraphStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style with
        {
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            UnderlineStyle = UnderlineStyle,
            UnderlineColor = UnderlineColor,
            Strikethrough = Strikethrough,
            Baseline = Baseline,
            AllCaps = AllCaps,
            SmallCaps = SmallCaps,
            Effects = Effects,
            Hidden = Hidden,
            TextColor = Color,
            TextBackground = Background,
            Highlight = Highlight,
            FontFamily = FontFamily,
            FontSize = FontSize,
            TextBorder = Border,
            Kerning = Kerning,
            CharacterSpacing = CharacterSpacing,
            HorizontalScaling = HorizontalScaling,
            Position = Position,
        };
    }

    /// <summary>
    /// the effective underline: an underline style wins over the single-underline shorthand,
    /// <c>null</c> means "not set".
    /// </summary>
    public TextUnderlineStyle? EffectiveUnderline
        => UnderlineStyle ?? (Underline switch
        {
            true => TextUnderlineStyle.Single,
            false => TextUnderlineStyle.None,
            null => null,
        });

    /// <summary>Whether the format sets anything at all.</summary>
    public bool IsEmpty
        => Bold is null && Italic is null && Underline is null && UnderlineStyle is null
            && UnderlineColor is null && Strikethrough is null && Baseline is null
            && AllCaps is null && SmallCaps is null && Effects is null && Hidden is null
            && Color is null && Background is null && Highlight is null
            && FontFamily is null && FontSize is null && Border is null
            && Kerning is null && CharacterSpacing is null && HorizontalScaling is null
            && Position is null;
}
