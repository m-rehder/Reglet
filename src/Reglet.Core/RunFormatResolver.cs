namespace Reglet;

/// <summary>
/// resolves the run formatting of a run: the paragraph style provides the base, a character style
/// of the run overrides it and the formatting of the run itself wins (ADR-0010). Everything is
/// tri-state, so a level can switch an inherited property off.
/// </summary>
internal static class RunFormatResolver
{
    /// <summary>Finds a character style by name.</summary>
    public static CharacterStyle? Find(IReadOnlyList<CharacterStyle> styles, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var style in styles)
        {
            if (string.Equals(style.Name, name, StringComparison.Ordinal))
            {
                return style;
            }
        }

        return null;
    }

    /// <summary>
    /// A style's font name as a run takes it: styles accept any text, a run no empty name, so a blank one (from a
    /// foreign file) means "not set".
    /// </summary>
    internal static string? FontName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>
    /// the effective formatting of a run: paragraph style, then the character style, then the run
    /// itself. Toggles and enumerations that no level sets stay at their "off" value, so the
    /// result can be used by a writer without further checks.
    /// </summary>
    public static TextRun Resolve(TextRun run, ParagraphStyle? paragraphStyle, CharacterStyle? characterStyle)
    {
        ArgumentNullException.ThrowIfNull(run);

        var underline = Underline(run.UnderlineStyle, run.Underline)
            ?? Underline(characterStyle?.UnderlineStyle, characterStyle?.Underline)
            ?? Underline(paragraphStyle?.UnderlineStyle, paragraphStyle?.Underline)
            ?? TextUnderlineStyle.None;

        return new TextRun(run.Text)
        {
            Bold = run.Bold ?? characterStyle?.Bold ?? paragraphStyle?.Bold ?? false,
            Italic = run.Italic ?? characterStyle?.Italic ?? paragraphStyle?.Italic ?? false,
            Underline = underline != TextUnderlineStyle.None,
            UnderlineStyle = underline,
            UnderlineColor = run.UnderlineColor ?? characterStyle?.UnderlineColor ?? paragraphStyle?.UnderlineColor,
            Strikethrough = run.Strikethrough
                ?? characterStyle?.Strikethrough
                ?? paragraphStyle?.Strikethrough
                ?? TextStrikethrough.None,
            Baseline = run.Baseline ?? characterStyle?.Baseline ?? paragraphStyle?.Baseline ?? TextBaseline.Normal,
            AllCaps = run.AllCaps ?? characterStyle?.AllCaps ?? paragraphStyle?.AllCaps ?? false,
            SmallCaps = run.SmallCaps ?? characterStyle?.SmallCaps ?? paragraphStyle?.SmallCaps ?? false,
            Effects = run.Effects ?? characterStyle?.Effects ?? paragraphStyle?.Effects,
            Hidden = run.Hidden ?? characterStyle?.Hidden ?? paragraphStyle?.Hidden ?? false,
            Background = run.Background ?? characterStyle?.Background ?? paragraphStyle?.TextBackground,
            Highlight = run.Highlight ?? characterStyle?.Highlight ?? paragraphStyle?.Highlight,
            Kerning = run.Kerning ?? characterStyle?.Kerning ?? paragraphStyle?.Kerning,
            CharacterSpacing = run.CharacterSpacing
                ?? characterStyle?.CharacterSpacing
                ?? paragraphStyle?.CharacterSpacing,
            HorizontalScaling = run.HorizontalScaling
                ?? characterStyle?.HorizontalScaling
                ?? paragraphStyle?.HorizontalScaling,
            Position = run.Position ?? characterStyle?.Position ?? paragraphStyle?.Position,
            FontFamily = run.FontFamily ?? FontName(characterStyle?.FontFamily) ?? FontName(paragraphStyle?.FontFamily),
            FontSize = run.FontSize ?? characterStyle?.FontSize ?? paragraphStyle?.FontSize,
            Border = run.Border ?? characterStyle?.Border ?? paragraphStyle?.TextBorder,
            Color = run.Color ?? characterStyle?.Color ?? paragraphStyle?.TextColor,
            CharacterStyleName = run.CharacterStyleName,
        };
    }

    /// <summary>
    /// the underline of one level: an underline style wins over the single-underline shorthand,
    /// <c>null</c> means the level does not set the underline.
    /// </summary>
    private static TextUnderlineStyle? Underline(TextUnderlineStyle? style, bool? shorthand)
        => style ?? (shorthand switch
        {
            true => TextUnderlineStyle.Single,
            false => TextUnderlineStyle.None,
            null => null,
        });
}
