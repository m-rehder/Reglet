using System.Globalization;

namespace Reglet;

/// <summary>Formatting key for deduplicated automatic text styles.</summary>
internal readonly record struct OdtTextStyleKey(
    bool? Bold,
    bool? Italic,
    TextUnderlineStyle? UnderlineStyle,
    string? UnderlineColorHex,
    TextStrikethrough? Strikethrough,
    TextBaseline? Baseline,
    double? PositionPercent,
    bool? AllCaps,
    bool? SmallCaps,
    TextEffects? Effects,
    string? BackgroundHex,
    bool? Kerning,
    double? CharacterSpacingPoints,
    double? HorizontalScaling,
    string? FontFamily,
    string? EastAsianFontFamily,
    double? FontSizePoints,
    string? ColorHex,
    bool? Hidden,
    string? Border,
    string? StyleName);

/// <summary>Collects and deduplicates the automatic text styles of a document.</summary>
internal static class OdtAutomaticStyles
{
    public static IReadOnlyDictionary<OdtTextStyleKey, string> Collect(PortableDocument document, int nameStart = 0)
        => Collect(
            document.Sections.SelectMany(section => section.Blocks),
            "T",
            nameStart,
            document.CharacterStyles,
            document.Defaults.FontSize.Points);

    /// <summary>
    /// Collects the text styles of the blocks; names are <paramref name="prefix"/> plus a counter. The
    /// runs are resolved like the writer does (ADR-0010), so span styles and text agree.
    /// </summary>
    public static IReadOnlyDictionary<OdtTextStyleKey, string> Collect(
        IEnumerable<Block> blocks,
        string prefix,
        int nameStart = 0,
        IReadOnlyList<CharacterStyle>? characterStyles = null,
        double defaultFontSize = 11.0)
    {
        var styles = new Dictionary<OdtTextStyleKey, string>();

        foreach (var (run, style, baseFormat) in EnumerateTextRuns(blocks))
        {
            var resolved = run.CharacterStyleName is null && (style is null || !RunFormat.HasFormatting(style))
                ? run
                : RunFormatResolver.Resolve(
                    run,
                    style,
                    RunFormatResolver.Find(characterStyles ?? [], run.CharacterStyleName));
            var effective = run.CharacterStyleName is null && (style is null || !RunFormat.HasFormatting(style))
                ? run
                : RunFormat.Of(resolved).Difference(baseFormat).ToRun(run.Text, run.CharacterStyleName);
            // ODF states the baseline shift as a percentage of the font size. The run itself may
            // inherit the size from its paragraph style or the document defaults, so the resolved
            // size is used instead of assuming 11 pt.
            var fontSize = resolved.FontSize?.Points ?? defaultFontSize;
            if (KeyOf(effective, fontSize) is not { } key)
            {
                continue;
            }

            if (!styles.ContainsKey(key))
            {
                styles[key] = $"{prefix}{nameStart + styles.Count + 1}";
            }
        }

        return styles;
    }

    /// <summary>
    /// Whether the document sets a text highlight and a text background with different colors on the
    /// same text. ODF has one <c>fo:background-color</c>, so the writer can only keep one of them.
    /// </summary>
    public static bool HasDistinctHighlightAndBackground(PortableDocument document)
    {
        foreach (var style in document.Styles)
        {
            if (Distinct(style.Highlight, style.TextBackground))
            {
                return true;
            }
        }

        foreach (var style in document.CharacterStyles)
        {
            if (Distinct(style.Highlight, style.Background))
            {
                return true;
            }
        }

        foreach (var blocks in BlockGroups(document))
        {
            foreach (var (run, style, _) in EnumerateTextRuns(blocks))
            {
                var resolved = RunFormatResolver.Resolve(
                    run,
                    style,
                    RunFormatResolver.Find(document.CharacterStyles, run.CharacterStyleName));
                if (Distinct(resolved.Highlight, resolved.Background))
                {
                    return true;
                }
            }
        }

        return false;

        static bool Distinct(Color? highlight, Color? background)
            => highlight is { } high && background is { } back && high != back;
    }

    private static IEnumerable<IEnumerable<Block>> BlockGroups(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            yield return section.Blocks;
            foreach (var band in new[]
                     {
                         section.Header, section.Footer, section.FirstHeader,
                         section.FirstFooter, section.EvenHeader, section.EvenFooter,
                     })
            {
                if (band is not null)
                {
                    yield return band.Blocks;
                }
            }
        }
    }

    /// <summary>
    /// the automatic text style of a run: its direct formatting (ADR-0010). A property the run does
    /// not set is not part of the style, so the paragraph style or the named character style applies.
    /// </summary>
    public static OdtTextStyleKey? KeyOf(TextRun run, double? effectiveFontSize = null)
    {
        var underline = run.UnderlineStyle ?? (run.Underline switch
        {
            true => TextUnderlineStyle.Single,
            false => TextUnderlineStyle.None,
            null => null,
        });

        if (run.Bold is null && run.Italic is null && underline is null
            && run.Strikethrough is null && run.Baseline is null
            && run.Position is null && run.AllCaps is null && run.SmallCaps is null && run.Effects is null
            && run.Background is null && run.Highlight is null && run.Kerning is null
            && run.CharacterSpacing is null && run.HorizontalScaling is null
            && run.UnderlineColor is null
            && run.Hidden is null && run.Border is null
            && run.FontFamily is null && run.EastAsianFontFamily is null && run.FontSize is null && run.Color is null
            && run.CharacterStyleName is null)
        {
            return null;
        }

        return new OdtTextStyleKey(
            run.Bold,
            run.Italic,
            underline,
            run.UnderlineColor?.ToString(),
            run.Strikethrough,
            run.Baseline,
            // ODT specifies the baseline shift in percent of the font size, the model absolute.
            run.Position is { } position
                ? position.Points / (run.FontSize?.Points ?? effectiveFontSize ?? 11.0) * 100.0
                : null,
            run.AllCaps,
            run.SmallCaps,
            run.Effects,
            (run.Highlight ?? run.Background)?.ToString(),
            run.Kerning is null ? null : true,
            run.CharacterSpacing?.Points,
            run.HorizontalScaling,
            run.FontFamily,
            run.EastAsianFontFamily,
            run.FontSize?.Points,
            run.Color?.ToString(),
            run.Hidden,
            BorderText(run.Border),
            run.CharacterStyleName);
    }

    internal static string? BorderText(ParagraphBorder? border)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            return null;
        }

        var style = border.Style switch
        {
            BorderStyle.Double => "double",
            BorderStyle.Dotted => "dotted",
            BorderStyle.Dashed => "dashed",
            _ => "solid",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{border.Width.Points:0.##}pt {style} {border.Color}");
    }

    private static IEnumerable<(TextRun Run, ParagraphStyle? Style, RunFormat Base)> EnumerateTextRuns(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var inline in paragraph.Inlines)
                    {
                        if (inline is TextRun run)
                        {
                            yield return (run, paragraph.Style, RunFormat.HasFormatting(paragraph.Style) ? RunFormat.Of(paragraph.Style) : RunFormat.Empty);
                        }
                    }

                    break;
                case Heading heading:
                    foreach (var inline in heading.Inlines)
                    {
                        if (inline is TextRun run)
                        {
                            yield return (run, heading.Style, RunFormat.Empty);
                        }
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var entry in EnumerateTextRuns(item.Blocks))
                        {
                            yield return entry;
                        }
                    }

                    break;
                case DrawingShape { Content: { } content }:
                    foreach (var entry in EnumerateTextRuns(content))
                    {
                        yield return entry;
                    }

                    break;
                case Diagram diagram:
                    foreach (var entry in EnumerateTextRuns(OdtDiagrams.Shapes(diagram).SelectMany(placed => placed.Shape.Content ?? []).ToList()))
                    {
                        yield return entry;
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var entry in EnumerateTextRuns(cell.Blocks))
                            {
                                yield return entry;
                            }
                        }
                    }

                    break;
            }
        }
    }
}
