using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Adobe glyph names to Unicode for fonts whose <c>/Encoding</c> lists <c>/Differences</c> (LaTeX and dvips output:
/// ligatures, Greek letters, dashes, math symbols). A subset of the Adobe Glyph List; unknown names give <c>null</c>.
/// </summary>
internal static class PdfGlyphNames
{
    private static readonly Dictionary<string, string> Names = Build();

    public static string? ToUnicode(string name)
    {
        if (Names.TryGetValue(name, out var text))
        {
            return text;
        }

        if (name.Length == 1)
        {
            return name;
        }

        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7
            && int.TryParse(name.AsSpan(3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return char.ConvertFromUtf32(code);
        }

        // A letter with an accent: Aacute, odieresis, ccedilla ...
        var accents = new (string Name, char Mark)[]
        {
            ("grave", '̀'), ("acute", '́'), ("circumflex", '̂'), ("tilde", '̃'), ("macron", '̄'),
            ("breve", '̆'), ("dotaccent", '̇'), ("dieresis", '̈'), ("ring", '̊'), ("hungarumlaut", '̋'),
            ("caron", '̌'), ("cedilla", '̧'), ("ogonek", '̨'),
        };
        foreach (var (accent, mark) in accents)
        {
            if (name.Length == accent.Length + 1 && char.IsAsciiLetter(name[0]) && name.EndsWith(accent, StringComparison.Ordinal))
            {
                return (name[0].ToString() + mark).Normalize(NormalizationForm.FormC);
            }
        }

        return null;
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["space"] = " ", ["exclam"] = "!", ["quotedbl"] = "\"", ["numbersign"] = "#", ["dollar"] = "$", ["percent"] = "%",
            ["ampersand"] = "&", ["quoteright"] = "’", ["quoteleft"] = "‘", ["quotesingle"] = "'", ["parenleft"] = "(",
            ["parenright"] = ")", ["asterisk"] = "*", ["plus"] = "+", ["comma"] = ",", ["hyphen"] = "-", ["period"] = ".",
            ["slash"] = "/", ["colon"] = ":", ["semicolon"] = ";", ["less"] = "<", ["equal"] = "=", ["greater"] = ">",
            ["question"] = "?", ["at"] = "@", ["bracketleft"] = "[", ["backslash"] = "\\", ["bracketright"] = "]",
            ["asciicircum"] = "^", ["underscore"] = "_", ["grave"] = "`", ["braceleft"] = "{", ["bar"] = "|", ["braceright"] = "}",
            ["asciitilde"] = "~", ["tilde"] = "~", ["dieresis"] = "¨", ["acute"] = "´", ["circumflex"] = "^",
            ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6",
            ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
            ["ff"] = "ff", ["fi"] = "fi", ["fl"] = "fl", ["ffi"] = "ffi", ["ffl"] = "ffl", ["dotlessi"] = "i", ["dotlessj"] = "j",
            ["germandbls"] = "ß", ["ae"] = "æ", ["AE"] = "Æ", ["oe"] = "œ", ["OE"] = "Œ",
            ["oslash"] = "ø", ["Oslash"] = "Ø", ["aring"] = "å", ["Aring"] = "Å",
            ["endash"] = "–", ["emdash"] = "—", ["quotedblleft"] = "“", ["quotedblright"] = "”",
            ["quotesinglbase"] = "‚", ["quotedblbase"] = "„", ["guillemotleft"] = "«", ["guillemotright"] = "»",
            ["bullet"] = "•", ["ellipsis"] = "…", ["dagger"] = "†", ["daggerdbl"] = "‡", ["section"] = "§",
            ["paragraph"] = "¶", ["degree"] = "°", ["copyright"] = "©", ["registered"] = "®",
            ["minus"] = "−", ["plusminus"] = "±", ["minusplus"] = "∓", ["multiply"] = "×", ["divide"] = "÷",
            ["periodcentered"] = "·", ["asteriskmath"] = "∗", ["lessequal"] = "≤", ["greaterequal"] = "≥",
            ["notequal"] = "≠", ["approxequal"] = "≈", ["equivalence"] = "≡", ["similar"] = "∼",
            ["proportional"] = "∝", ["infinity"] = "∞", ["integral"] = "∫", ["summation"] = "∑",
            ["product"] = "∏", ["radical"] = "√", ["partialdiff"] = "∂", ["nabla"] = "∇", ["element"] = "∈",
            ["emptyset"] = "∅", ["universal"] = "∀", ["existential"] = "∃", ["logicaland"] = "∧",
            ["logicalor"] = "∨", ["logicalnot"] = "¬", ["union"] = "∪", ["intersection"] = "∩",
            ["propersubset"] = "⊂", ["propersuperset"] = "⊃", ["reflexsubset"] = "⊆", ["reflexsuperset"] = "⊇",
            ["arrowleft"] = "←", ["arrowright"] = "→", ["arrowup"] = "↑", ["arrowdown"] = "↓",
            ["arrowboth"] = "↔", ["arrowdblleft"] = "⇐", ["arrowdblright"] = "⇒", ["arrowdblboth"] = "⇔",
            ["prime"] = "′", ["aleph"] = "ℵ", ["circleplus"] = "⊕", ["circlemultiply"] = "⊗",
            ["angbracketleft"] = "⟨", ["angbracketright"] = "⟩", ["floorleft"] = "⌊", ["floorright"] = "⌋",
            ["ceilingleft"] = "⌈", ["ceilingright"] = "⌉", ["bardbl"] = "‖", ["lessmuch"] = "≪",
            ["greatermuch"] = "≫", ["epsilon1"] = "ε", ["theta1"] = "ϑ", ["phi1"] = "ϕ", ["rho1"] = "ϱ",
            ["sigma1"] = "ς", ["omega1"] = "ϖ", ["pi1"] = "ϖ", ["Euro"] = "€", ["sterling"] = "£",
            ["yen"] = "¥", ["cent"] = "¢", ["trademark"] = "™", ["onehalf"] = "½", ["onequarter"] = "¼",
            ["threequarters"] = "¾", ["mu1"] = "µ",
        };
        string[] lower = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega"];
        const string LowerLetters = "αβγδεζηθικλμνξοπρστυφχψω";
        const string Capitals = "ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ";
        for (var index = 0; index < lower.Length; index++)
        {
            map[lower[index]] = LowerLetters[index].ToString();
            map[char.ToUpperInvariant(lower[index][0]) + lower[index][1..]] = Capitals[index].ToString();
        }

        map["Delta"] = "Δ"; // not the increment sign U+2206
        map["Omega"] = "Ω";
        return map;
    }
}
