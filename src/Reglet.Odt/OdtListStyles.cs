using System.Globalization;

namespace Reglet;

/// <summary>
/// maps the numbering definitions of a document to ODF list styles (phase AJ, AJ04). OF-B01:
/// one <c>text:list-style</c> per <see cref="NumberingDefinition"/>, so all levels of a definition
/// survive a read/write cycle and every list that uses the definition shares one style. ODF
/// carries the number format, the start value, the color and the weight per level, so nothing has
/// to be derived from the appearance.
/// </summary>
internal static class OdtListStyles
{
    /// <summary>
    /// the ODF style name of a numbering definition: the name of the definition. The model keeps
    /// it as a unique NCName, so it is a valid style name; a definition keeps its name across a
    /// read/write cycle, so the style name stays stable.
    /// </summary>
    public static string StyleName(NumberingDefinition definition) => definition.Name;

    /// <summary>The <c>style:num-format</c> of a number format.</summary>
    public static string NumberedFormat(ListNumberFormat format) => format switch
    {
        ListNumberFormat.LowerRoman => "i",
        ListNumberFormat.UpperRoman => "I",
        ListNumberFormat.LowerLetter => "a",
        ListNumberFormat.UpperLetter => "A",
        _ => "1",
    };

    /// <summary>The bullet character of a level (three characters in rotation, as in DOCX).</summary>
    public static string BulletChar(int level) => ((level - 1) % 3) switch
    {
        0 => "\u2022",
        1 => "\u25E6",
        _ => "\u25AA",
    };

    /// <summary>A number the way ODF writes it.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
