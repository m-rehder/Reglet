namespace Reglet;

/// <summary>
/// Resolves the paragraph formatting of a paragraph that was read from a source package.
/// The readers keep the resolved style on the paragraph (for the public model) and store the
/// formatting that came from the referenced named style separately. When the named style is
/// edited in <see cref="PortableDocument.Styles"/> after reading, the change reaches the
/// paragraph: every property that still carries the inherited value follows the style, while
/// direct formatting of the paragraph wins. The writers compare the resolved style with its
/// base and state only the direct formatting, so the output keeps the style reference
/// (ADR-0011).
/// </summary>
internal static class ParagraphStyleResolver
{
    /// <summary>
    /// Computes the effective paragraph style after a possible edit of the named style.
    /// </summary>
    /// <param name="paragraph">The resolved style stored on the paragraph.</param>
    /// <param name="sourceBase">
    /// The formatting the paragraph inherited from its named style when it was read, or
    /// <c>null</c> when the paragraph does not reference a preserved named style.
    /// </param>
    /// <param name="styles">The current named styles of the document.</param>
    /// <param name="sourceStyles">The named styles as they were read.</param>
    /// <param name="effective">The style to use for layout and writing.</param>
    /// <param name="inheritedBase">
    /// The current base the paragraph inherits; <c>null</c> when there is no reference. The
    /// writers state a property only when the effective style differs from this base.
    /// </param>
    /// <returns>Whether the paragraph references a named style that can be resolved.</returns>
    public static bool TryResolve(
        ParagraphStyle paragraph,
        ParagraphStyle? sourceBase,
        IReadOnlyList<ParagraphStyle>? styles,
        IReadOnlyList<ParagraphStyle>? sourceStyles,
        out ParagraphStyle effective,
        out ParagraphStyle? inheritedBase)
    {
        effective = paragraph;
        inheritedBase = null;
        if (sourceBase is null
            || paragraph.StyleName is not { Length: > 0 } name
            || Find(styles, name) is not { } current
            || Find(sourceStyles, name) is not { } source)
        {
            return false;
        }

        inheritedBase = Apply(sourceBase, sourceBase, source, current);
        effective = Apply(paragraph, sourceBase, source, current);

        // The break flags are tri-state: null means "inherits", so the effective value is the
        // current inherited one. A non-null value is handled by Apply (direct, or equal to the
        // source base and therefore following a style edit).
        effective = effective with
        {
            Alignment = paragraph.Alignment is null ? inheritedBase.Alignment : effective.Alignment,
            SpaceBefore = paragraph.SpaceBefore is null ? inheritedBase.SpaceBefore : effective.SpaceBefore,
            SpaceAfter = paragraph.SpaceAfter is null ? inheritedBase.SpaceAfter : effective.SpaceAfter,
            Borders = paragraph.Borders is null ? inheritedBase.Borders : effective.Borders,
            RightToLeft = paragraph.RightToLeft is null ? inheritedBase.RightToLeft : effective.RightToLeft,
            TabStops = paragraph.TabStops is null ? inheritedBase.TabStops : effective.TabStops,
            LeftIndent = paragraph.LeftIndent is null ? inheritedBase.LeftIndent : effective.LeftIndent,
            RightIndent = paragraph.RightIndent is null ? inheritedBase.RightIndent : effective.RightIndent,
            FirstLineIndent = paragraph.FirstLineIndent is null ? inheritedBase.FirstLineIndent : effective.FirstLineIndent,
            KeepWithNext = paragraph.KeepWithNext is null ? inheritedBase.KeepWithNext : effective.KeepWithNext,
            KeepTogether = paragraph.KeepTogether is null ? inheritedBase.KeepTogether : effective.KeepTogether,
            PageBreakBefore = paragraph.PageBreakBefore is null ? inheritedBase.PageBreakBefore : effective.PageBreakBefore,
        };
        return true;
    }

    /// <summary>Whether the paragraph carries direct formatting beyond its named style.</summary>
    public static bool HasDirectFormatting(ParagraphStyle style, ParagraphStyle inheritedBase)
        => !SameLayout(style, inheritedBase)
            || !SameRunFormatting(style, inheritedBase);

    /// <summary>
    /// Applies the document paragraph defaults to a style built from the model. A property that is
    /// still at the built-in default inherits the configured document default. The readers already
    /// resolve the source defaults into the paragraph style, so this is only used for a document
    /// created from the model.
    /// </summary>
    public static ParagraphStyle ApplyDefaults(ParagraphStyle style, ParagraphStyle defaults)
    {
        if (defaults == ParagraphStyle.Default)
        {
            return style;
        }

        var result = style;
        if (style.Alignment is null && defaults.Alignment is { } alignment)
        {
            result = result with { Alignment = alignment };
        }

        if (style.SpaceBefore is null && defaults.SpaceBefore is { } spaceBefore)
        {
            result = result with { SpaceBefore = spaceBefore };
        }

        if (style.SpaceAfter is null && defaults.SpaceAfter is { } spaceAfter)
        {
            result = result with { SpaceAfter = spaceAfter };
        }

        if (style.LeftIndent is null && defaults.LeftIndent is { } leftIndent)
        {
            result = result with { LeftIndent = leftIndent };
        }

        if (style.RightIndent is null && defaults.RightIndent is { } rightIndent)
        {
            result = result with { RightIndent = rightIndent };
        }

        if (style.FirstLineIndent is null && defaults.FirstLineIndent is { } firstLineIndent)
        {
            result = result with { FirstLineIndent = firstLineIndent };
        }

        if (style.LineSpacing is null)
        {
            result = result with { LineSpacing = defaults.LineSpacing };
        }

        if (style.Borders is null)
        {
            result = result with { Borders = defaults.Borders };
        }

        if (style.Background is null)
        {
            result = result with { Background = defaults.Background };
        }

        if (style.KeepWithNext is null && defaults.KeepWithNext is { } keepWithNext)
        {
            result = result with { KeepWithNext = keepWithNext };
        }

        if (style.KeepTogether is null && defaults.KeepTogether is { } keepTogether)
        {
            result = result with { KeepTogether = keepTogether };
        }

        if (style.PageBreakBefore is null && defaults.PageBreakBefore is { } pageBreakBefore)
        {
            result = result with { PageBreakBefore = pageBreakBefore };
        }

        if (style.TabStops is null)
        {
            result = result with { TabStops = defaults.TabStops };
        }

        if (style.RightToLeft is null && defaults.RightToLeft is { } rightToLeft)
        {
            result = result with { RightToLeft = rightToLeft };
        }

        return result;
    }

    private static ParagraphStyle? Find(IReadOnlyList<ParagraphStyle>? styles, string name)
    {
        if (styles is null)
        {
            return null;
        }

        foreach (var style in styles)
        {
            if (string.Equals(style.StyleName, name, StringComparison.Ordinal))
            {
                return style;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the difference between the old and the edited style definition to
    /// <paramref name="paragraph"/>. A property that differs from <paramref name="sourceBase"/>
    /// is direct formatting and stays.
    /// </summary>
    private static ParagraphStyle Apply(
        ParagraphStyle paragraph,
        ParagraphStyle sourceBase,
        ParagraphStyle source,
        ParagraphStyle current)
    {
        var result = paragraph;

        if (Inherited(paragraph.Alignment, sourceBase.Alignment) && !Equals(current.Alignment, source.Alignment))
        {
            result = result with { Alignment = current.Alignment };
        }

        if (Inherited(paragraph.SpaceBefore, sourceBase.SpaceBefore) && !Equals(current.SpaceBefore, source.SpaceBefore))
        {
            result = result with { SpaceBefore = current.SpaceBefore };
        }

        if (Inherited(paragraph.SpaceAfter, sourceBase.SpaceAfter) && !Equals(current.SpaceAfter, source.SpaceAfter))
        {
            result = result with { SpaceAfter = current.SpaceAfter };
        }

        if (Inherited(paragraph.LeftIndent, sourceBase.LeftIndent) && !Equals(current.LeftIndent, source.LeftIndent))
        {
            result = result with { LeftIndent = current.LeftIndent };
        }

        if (Inherited(paragraph.RightIndent, sourceBase.RightIndent) && !Equals(current.RightIndent, source.RightIndent))
        {
            result = result with { RightIndent = current.RightIndent };
        }

        if (Inherited(paragraph.FirstLineIndent, sourceBase.FirstLineIndent)
            && !Equals(current.FirstLineIndent, source.FirstLineIndent))
        {
            result = result with { FirstLineIndent = current.FirstLineIndent };
        }

        if (Inherited(paragraph.LineSpacing, sourceBase.LineSpacing) && !Equals(current.LineSpacing, source.LineSpacing))
        {
            result = result with { LineSpacing = current.LineSpacing };
        }

        if (Inherited(paragraph.Borders, sourceBase.Borders) && !Equals(current.Borders, source.Borders))
        {
            result = result with { Borders = current.Borders };
        }

        if (Inherited(paragraph.Background, sourceBase.Background) && !Equals(current.Background, source.Background))
        {
            result = result with { Background = current.Background };
        }

        if (Inherited(paragraph.KeepWithNext, sourceBase.KeepWithNext) && !Equals(current.KeepWithNext, source.KeepWithNext))
        {
            result = result with { KeepWithNext = current.KeepWithNext };
        }

        if (Inherited(paragraph.KeepTogether, sourceBase.KeepTogether) && !Equals(current.KeepTogether, source.KeepTogether))
        {
            result = result with { KeepTogether = current.KeepTogether };
        }

        if (Inherited(paragraph.PageBreakBefore, sourceBase.PageBreakBefore)
            && !Equals(current.PageBreakBefore, source.PageBreakBefore))
        {
            result = result with { PageBreakBefore = current.PageBreakBefore };
        }

        if (SameTabStops(paragraph.TabStops, sourceBase.TabStops) && !SameTabStops(current.TabStops, source.TabStops))
        {
            result = result with { TabStops = current.TabStops };
        }

        if (Inherited(paragraph.RightToLeft, sourceBase.RightToLeft) && !Equals(current.RightToLeft, source.RightToLeft))
        {
            result = result with { RightToLeft = current.RightToLeft };
        }

        if (Inherited(paragraph.Bold, sourceBase.Bold) && !Equals(current.Bold, source.Bold))
        {
            result = result with { Bold = current.Bold };
        }

        if (Inherited(paragraph.Italic, sourceBase.Italic) && !Equals(current.Italic, source.Italic))
        {
            result = result with { Italic = current.Italic };
        }

        if (Inherited(paragraph.Underline, sourceBase.Underline) && !Equals(current.Underline, source.Underline))
        {
            result = result with { Underline = current.Underline };
        }

        if (Inherited(paragraph.UnderlineStyle, sourceBase.UnderlineStyle)
            && !Equals(current.UnderlineStyle, source.UnderlineStyle))
        {
            result = result with { UnderlineStyle = current.UnderlineStyle };
        }

        if (Inherited(paragraph.UnderlineColor, sourceBase.UnderlineColor)
            && !Equals(current.UnderlineColor, source.UnderlineColor))
        {
            result = result with { UnderlineColor = current.UnderlineColor };
        }

        if (Inherited(paragraph.Strikethrough, sourceBase.Strikethrough)
            && !Equals(current.Strikethrough, source.Strikethrough))
        {
            result = result with { Strikethrough = current.Strikethrough };
        }

        if (Inherited(paragraph.Baseline, sourceBase.Baseline) && !Equals(current.Baseline, source.Baseline))
        {
            result = result with { Baseline = current.Baseline };
        }

        if (Inherited(paragraph.AllCaps, sourceBase.AllCaps) && !Equals(current.AllCaps, source.AllCaps))
        {
            result = result with { AllCaps = current.AllCaps };
        }

        if (Inherited(paragraph.SmallCaps, sourceBase.SmallCaps) && !Equals(current.SmallCaps, source.SmallCaps))
        {
            result = result with { SmallCaps = current.SmallCaps };
        }

        if (Inherited(paragraph.Effects, sourceBase.Effects) && !Equals(current.Effects, source.Effects))
        {
            result = result with { Effects = current.Effects };
        }

        if (Inherited(paragraph.Hidden, sourceBase.Hidden) && !Equals(current.Hidden, source.Hidden))
        {
            result = result with { Hidden = current.Hidden };
        }

        if (Inherited(paragraph.TextColor, sourceBase.TextColor) && !Equals(current.TextColor, source.TextColor))
        {
            result = result with { TextColor = current.TextColor };
        }

        if (Inherited(paragraph.TextBackground, sourceBase.TextBackground)
            && !Equals(current.TextBackground, source.TextBackground))
        {
            result = result with { TextBackground = current.TextBackground };
        }

        if (Inherited(paragraph.Highlight, sourceBase.Highlight) && !Equals(current.Highlight, source.Highlight))
        {
            result = result with { Highlight = current.Highlight };
        }

        if (Inherited(paragraph.FontFamily, sourceBase.FontFamily) && !Equals(current.FontFamily, source.FontFamily))
        {
            result = result with { FontFamily = current.FontFamily };
        }

        if (Inherited(paragraph.FontSize, sourceBase.FontSize) && !Equals(current.FontSize, source.FontSize))
        {
            result = result with { FontSize = current.FontSize };
        }

        if (Inherited(paragraph.TextBorder, sourceBase.TextBorder) && !Equals(current.TextBorder, source.TextBorder))
        {
            result = result with { TextBorder = current.TextBorder };
        }

        if (Inherited(paragraph.Kerning, sourceBase.Kerning) && !Equals(current.Kerning, source.Kerning))
        {
            result = result with { Kerning = current.Kerning };
        }

        if (Inherited(paragraph.CharacterSpacing, sourceBase.CharacterSpacing)
            && !Equals(current.CharacterSpacing, source.CharacterSpacing))
        {
            result = result with { CharacterSpacing = current.CharacterSpacing };
        }

        if (Inherited(paragraph.HorizontalScaling, sourceBase.HorizontalScaling)
            && !Equals(current.HorizontalScaling, source.HorizontalScaling))
        {
            result = result with { HorizontalScaling = current.HorizontalScaling };
        }

        if (Inherited(paragraph.Position, sourceBase.Position) && !Equals(current.Position, source.Position))
        {
            result = result with { Position = current.Position };
        }

        return result;
    }

    private static bool Inherited<T>(T paragraph, T sourceBase)
        => EqualityComparer<T>.Default.Equals(paragraph, sourceBase);

    private static bool SameLayout(ParagraphStyle left, ParagraphStyle right)
        => left.Alignment == right.Alignment
            && left.SpaceBefore == right.SpaceBefore
            && left.SpaceAfter == right.SpaceAfter
            && left.LeftIndent == right.LeftIndent
            && left.RightIndent == right.RightIndent
            && left.FirstLineIndent == right.FirstLineIndent
            && Equals(left.LineSpacing, right.LineSpacing)
            && Equals(left.Borders, right.Borders)
            && Equals(left.Background, right.Background)
            && left.KeepWithNext == right.KeepWithNext
            && left.KeepTogether == right.KeepTogether
            && left.PageBreakBefore == right.PageBreakBefore
            && SameTabStops(left.TabStops, right.TabStops)
            && left.RightToLeft == right.RightToLeft;

    private static bool SameRunFormatting(ParagraphStyle left, ParagraphStyle right)
        => left.Bold == right.Bold
            && left.Italic == right.Italic
            && left.Underline == right.Underline
            && Equals(left.UnderlineStyle, right.UnderlineStyle)
            && Equals(left.UnderlineColor, right.UnderlineColor)
            && Equals(left.Strikethrough, right.Strikethrough)
            && Equals(left.Baseline, right.Baseline)
            && left.AllCaps == right.AllCaps
            && left.SmallCaps == right.SmallCaps
            && left.Effects == right.Effects
            && left.Hidden == right.Hidden
            && Equals(left.TextColor, right.TextColor)
            && Equals(left.TextBackground, right.TextBackground)
            && Equals(left.Highlight, right.Highlight)
            && string.Equals(left.FontFamily, right.FontFamily, StringComparison.Ordinal)
            && Equals(left.FontSize, right.FontSize)
            && Equals(left.TextBorder, right.TextBorder)
            && Equals(left.Kerning, right.Kerning)
            && Equals(left.CharacterSpacing, right.CharacterSpacing)
            && Equals(left.HorizontalScaling, right.HorizontalScaling)
            && Equals(left.Position, right.Position)
            && string.Equals(left.MarkFontFamily, right.MarkFontFamily, StringComparison.Ordinal)
            && Equals(left.MarkFontSize, right.MarkFontSize);

    /// <summary>Whether two tab-stop lists are equal in content.</summary>
    public static bool SameTabStops(IReadOnlyList<TabStop>? left, IReadOnlyList<TabStop>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
