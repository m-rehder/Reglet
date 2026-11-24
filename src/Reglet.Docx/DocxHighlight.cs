namespace Reglet;

/// <summary>The 16 named marker colours of Word's <c>w:highlight</c> (ST_HighlightColor).</summary>
internal static class DocxHighlight
{
    private static readonly (string Name, Color Color)[] Colors =
    [
        ("black", Color.FromHex("000000")), ("blue", Color.FromHex("0000FF")),
        ("cyan", Color.FromHex("00FFFF")), ("green", Color.FromHex("00FF00")),
        ("magenta", Color.FromHex("FF00FF")), ("red", Color.FromHex("FF0000")),
        ("yellow", Color.FromHex("FFFF00")), ("white", Color.FromHex("FFFFFF")),
        ("darkBlue", Color.FromHex("000080")), ("darkCyan", Color.FromHex("008080")),
        ("darkGreen", Color.FromHex("008000")), ("darkMagenta", Color.FromHex("800080")),
        ("darkRed", Color.FromHex("800000")), ("darkYellow", Color.FromHex("808000")),
        ("darkGray", Color.FromHex("808080")), ("lightGray", Color.FromHex("C0C0C0")),
    ];

    /// <summary>The marker name of exactly this colour, or null when Word has none for it.</summary>
    public static string? NameOf(Color color)
        => Array.Find(Colors, entry => entry.Color == color).Name;

    /// <summary>The colour of a marker name; null for <c>none</c> or an unknown name.</summary>
    public static Color? ColorOf(string? name)
        => Array.Find(Colors, entry => entry.Name == name) is { Name: not null } match ? match.Color : null;
}
