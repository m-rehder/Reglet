using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// compares the named styles of a model with the styles that were read from the source
/// package. The writers use the result to keep the source definitions when the reader has
/// not changed them (phase AC, AC01).
/// </summary>
internal static class NamedStyleComparer
{
    /// <summary>Whether the named styles of the model are the ones of the source package.</summary>
    public static bool Unchanged(IReadOnlyList<ParagraphStyle> model, IReadOnlyList<ParagraphStyle>? source)
    {
        if (source is null || model.Count != source.Count)
        {
            return false;
        }

        for (var index = 0; index < model.Count; index++)
        {
            if (Key(model[index]) != Key(source[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the character styles of the model are the ones of the source package. The style
    /// objects are immutable, so a changed style is always a new instance.
    /// </summary>
    public static bool CharacterStylesUnchanged(
        IReadOnlyList<CharacterStyle> model, IReadOnlyList<CharacterStyle>? source)
    {
        if (source is null || model.Count != source.Count)
        {
            return false;
        }

        for (var index = 0; index < model.Count; index++)
        {
            if (!ReferenceEquals(model[index], source[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The comparable content of a style.</summary>
    private static string Key(ParagraphStyle style) => string.Join(
        "|",
        style.StyleName ?? string.Empty,
        style.LinkedStyleName ?? string.Empty,
        style.Alignment.ToString(),
        style.SpaceBefore?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.SpaceAfter?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.LeftIndent?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.RightIndent?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.FirstLineIndent?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.LineSpacing?.ToString() ?? string.Empty,
        style.Borders?.ToString() ?? string.Empty,
        style.Background?.ToString() ?? string.Empty,
        style.KeepWithNext.ToString(),
        style.KeepTogether.ToString(),
        style.PageBreakBefore.ToString(),
        style.RightToLeft.ToString(),
        TabStopsKey(style.TabStops),
        // ADR-0010: the run formatting of the style belongs to its definition.
        style.Bold?.ToString() ?? string.Empty,
        style.Italic?.ToString() ?? string.Empty,
        style.Underline?.ToString() ?? string.Empty,
        style.UnderlineStyle?.ToString() ?? string.Empty,
        style.UnderlineColor?.ToString() ?? string.Empty,
        style.Strikethrough?.ToString() ?? string.Empty,
        style.Baseline?.ToString() ?? string.Empty,
        style.AllCaps?.ToString() ?? string.Empty,
        style.SmallCaps?.ToString() ?? string.Empty,
        style.Effects?.ToString() ?? string.Empty,
        style.Hidden?.ToString() ?? string.Empty,
        style.TextColor?.ToString() ?? string.Empty,
        style.TextBackground?.ToString() ?? string.Empty,
        style.Highlight?.ToString() ?? string.Empty,
        style.FontFamily ?? string.Empty,
        style.FontSize?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.TextBorder?.ToString() ?? string.Empty,
        style.Kerning?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.CharacterSpacing?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.HorizontalScaling?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.Position?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
        style.MarkFontFamily ?? string.Empty,
        style.MarkFontSize?.Points.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);

    /// <summary>Comparable content of the tab stops, keeping "unset" and an empty list apart.</summary>
    private static string TabStopsKey(IReadOnlyList<TabStop>? tabStops)
    {
        if (tabStops is null)
        {
            return "null";
        }

        var builder = new StringBuilder();
        builder.Append(tabStops.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
        foreach (var tab in tabStops)
        {
            builder
                .Append(tab.Position.Points.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(tab.Alignment).Append(':')
                .Append(tab.Leader).Append(';');
        }

        return builder.ToString();
    }
}
