using System.Runtime.InteropServices;

namespace Reglet;

/// <summary>
/// shapes text for embedded TrueType fonts (phase P): splits the text per UAX #9 into
/// runs, applies GSUB ligatures and Arabic contextual forms as well as GPOS kerning and
/// returns a glyph sequence in visual order.
/// </summary>
/// <remarks>
/// the shaper replaces the pure <c>cmap</c> mapping only if the font has matching
/// layout features or the text must be reordered; otherwise the fast
/// Pfad aktiv.
/// </remarks>
internal sealed class TextShaper
{
    private readonly TrueTypeFont _font;
    private readonly OpenTypeLayout? _layout;
    private readonly double _scale;

    /// <summary>Creates a shaper; without layout tables (GSUB/GPOS/GDEF) only the bidi order is applied.</summary>
    public TextShaper(TrueTypeFont font, OpenTypeLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        _layout = layout;
        _scale = 1000.0 / font.UnitsPerEm;
    }

    /// <summary>Script of a text (Arabic, Hebrew or Latin).</summary>
    public static uint DetectScript(ReadOnlySpan<int> codePoints)
    {
        foreach (var codePoint in codePoints)
        {
            if (ArabicJoining.IsArabicScript(codePoint))
            {
                return OpenTypeLayout.ScriptArabic;
            }

            if (codePoint is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F)
            {
                return OpenTypeLayout.ScriptHebrew;
            }
        }

        return OpenTypeLayout.ScriptLatin;
    }

    /// <summary>
    /// Shapes a text and arranges it according to UAX #9. <paramref name="levels"/> are the
    /// embedding levels of its code points from the surrounding line; without them the text is
    /// resolved on its own.
    /// </summary>
    public IReadOnlyList<ShapedGlyph>? Shape(string text, IReadOnlyList<byte>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return null;
        }

        var codePoints = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            codePoints.Add(rune.Value);
        }

        var span = CollectionsMarshal.AsSpan(codePoints);
        return levels is not null && levels.Count == codePoints.Count
            ? Shape(span, [.. levels], rightToLeftBase: false)
            : Shape(span, BidiAlgorithm.GetParagraphLevel(span));
    }

    /// <summary>Shapes code points with the given paragraph direction (0 = LTR, 1 = RTL).</summary>
    public IReadOnlyList<ShapedGlyph>? Shape(ReadOnlySpan<int> codePoints, int baseLevel)
        => Shape(codePoints, BidiAlgorithm.Resolve(codePoints, baseLevel).Levels, rightToLeftBase: (baseLevel & 1) == 1);

    private IReadOnlyList<ShapedGlyph>? Shape(ReadOnlySpan<int> codePoints, byte[] levels, bool rightToLeftBase)
    {
        var runs = BuildRuns(levels);

        var hasWork = rightToLeftBase;
        foreach (var run in runs)
        {
            hasWork |= (run.Level & 1) == 1 || HasFeatures(DetectScript(codePoints[run.Start..(run.End + 1)]));
        }

        if (!hasWork)
        {
            return null;
        }

        var shaped = new List<IReadOnlyList<ShapedGlyph>>(runs.Count);
        foreach (var run in runs)
        {
            var slice = codePoints[run.Start..(run.End + 1)];
            shaped.Add(ShapeRun(slice, DetectScript(slice), (run.Level & 1) == 1, run.Start));
        }

        var runLevels = new byte[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            runLevels[i] = (byte)runs[i].Level;
        }

        var order = BidiAlgorithm.ReorderVisual(runLevels);
        var result = new List<ShapedGlyph>();
        foreach (var runIndex in order)
        {
            result.AddRange(shaped[runIndex]);
        }

        return result;
    }

    /// <summary>Total width of a shaped sequence in 1000ths of an em.</summary>
    public double Measure(IReadOnlyList<ShapedGlyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        var width = 0.0;
        foreach (var glyph in glyphs)
        {
            width += glyph.Advance + glyph.Kern;
        }

        return width;
    }

    private static List<(int Start, int End, int Level)> BuildRuns(byte[] levels)
    {
        var runs = new List<(int Start, int End, int Level)>();
        var start = 0;
        for (var i = 1; i <= levels.Length; i++)
        {
            if (i == levels.Length || levels[i] != levels[start])
            {
                runs.Add((start, i - 1, levels[start]));
                start = i;
            }
        }

        return runs;
    }

    private bool HasFeatures(uint script)
    {
        if (_layout is null)
        {
            return false;
        }

        if (_layout.GetFeatureLookups(gpos: true, script, OpenTypeLayout.FeatureKern).Count > 0)
        {
            return true;
        }

        if (_layout.GetFeatureLookups(gpos: false, script, OpenTypeLayout.FeatureLiga).Count > 0)
        {
            return true;
        }

        if (script != OpenTypeLayout.ScriptArabic)
        {
            return false;
        }

        foreach (var feature in new[] { OpenTypeLayout.FeatureIsol, OpenTypeLayout.FeatureInit, OpenTypeLayout.FeatureMedi, OpenTypeLayout.FeatureFina, OpenTypeLayout.FeatureRlig })
        {
            if (_layout.GetFeatureLookups(gpos: false, script, feature).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<ShapedGlyph> ShapeRun(ReadOnlySpan<int> codePoints, uint script, bool rightToLeft, int runStart)
    {
        var glyphs = new List<ushort>(codePoints.Length);

        // The source code point of every glyph (index into the whole text); a ligature keeps the
        // index of its first component.
        var sources = new List<int>(codePoints.Length);
        for (var index = 0; index < codePoints.Length; index++)
        {
            // L4: right to left a mirrored character ("(", "<", "«") takes its mirror glyph.
            var codePoint = codePoints[index];
            var glyph = rightToLeft && BidiData.TryGetMirror(codePoint, out var mirror) ? _font.GetGlyphId(mirror) : (ushort)0;
            glyphs.Add(glyph != 0 ? glyph : _font.GetGlyphId(codePoint));
            sources.Add(runStart + index);
        }

        var formLookups = script == OpenTypeLayout.ScriptArabic && _layout is not null ? ResolveFormLookups() : null;
        if (formLookups is not null && formLookups.Count > 0)
        {
            ApplyForms(codePoints, glyphs, formLookups);
        }

        if (_layout is not null)
        {
            foreach (var lookup in ResolveLigatureLookups(script))
            {
                ApplyLigature(glyphs, sources, lookup);
            }
        }

        var kern = ComputeKerning(glyphs, _layout?.GetFeatureLookups(gpos: true, script, OpenTypeLayout.FeatureKern) ?? []);
        return BuildResult(glyphs, sources, kern, rightToLeft);
    }

    private Dictionary<ArabicJoining.ContextForm, IReadOnlyList<int>> ResolveFormLookups()
    {
        var features = new Dictionary<ArabicJoining.ContextForm, uint>
        {
            [ArabicJoining.ContextForm.Isolated] = OpenTypeLayout.FeatureIsol,
            [ArabicJoining.ContextForm.Initial] = OpenTypeLayout.FeatureInit,
            [ArabicJoining.ContextForm.Medial] = OpenTypeLayout.FeatureMedi,
            [ArabicJoining.ContextForm.Final] = OpenTypeLayout.FeatureFina,
        };

        var result = new Dictionary<ArabicJoining.ContextForm, IReadOnlyList<int>>();
        foreach (var (form, feature) in features)
        {
            var lookups = _layout!.GetFeatureLookups(gpos: false, OpenTypeLayout.ScriptArabic, feature);
            if (lookups.Count > 0)
            {
                result[form] = lookups;
            }
        }

        return result;
    }

    private IReadOnlyList<int> ResolveLigatureLookups(uint script)
    {
        var result = new List<int>();
        if (script == OpenTypeLayout.ScriptArabic)
        {
            result.AddRange(_layout!.GetFeatureLookups(gpos: false, script, OpenTypeLayout.FeatureRlig));
        }

        result.AddRange(_layout!.GetFeatureLookups(gpos: false, script, OpenTypeLayout.FeatureLiga));
        return result;
    }

    private void ApplyForms(
        ReadOnlySpan<int> codePoints,
        List<ushort> glyphs,
        Dictionary<ArabicJoining.ContextForm, IReadOnlyList<int>> formLookups)
    {
        var forms = ArabicJoining.ResolveForms(codePoints);
        for (var i = 0; i < glyphs.Count; i++)
        {
            if (forms[i] == ArabicJoining.ContextForm.None
                || !formLookups.TryGetValue(forms[i], out var lookups))
            {
                continue;
            }

            foreach (var lookup in lookups)
            {
                if (_layout!.TrySingleSubstitute(lookup, glyphs[i], out var substitute))
                {
                    glyphs[i] = substitute;
                }
            }
        }
    }

    private void ApplyLigature(List<ushort> glyphs, List<int> sources, int lookup)
    {
        var index = 0;
        while (index < glyphs.Count)
        {
            if (_layout!.TryLigature(CollectionsMarshal.AsSpan(glyphs), index, lookup, out var ligature, out var components)
                && components >= 2)
            {
                glyphs[index] = ligature;
                glyphs.RemoveRange(index + 1, components - 1);
                sources.RemoveRange(index + 1, components - 1);
            }

            index++;
        }
    }

    private double[] ComputeKerning(List<ushort> glyphs, IReadOnlyList<int> kernLookups)
    {
        var kern = new double[glyphs.Count];
        if (kernLookups.Count == 0)
        {
            return kern;
        }

        for (var i = 0; i + 1 < glyphs.Count; i++)
        {
            foreach (var lookup in kernLookups)
            {
                if (_layout!.TryKern(lookup, glyphs[i], glyphs[i + 1], out var advance, out _))
                {
                    kern[i] += advance;
                }
            }
        }

        return kern;
    }

    private IReadOnlyList<ShapedGlyph> BuildResult(List<ushort> glyphs, List<int> sources, double[] kern, bool rightToLeft)
    {
        var result = new List<ShapedGlyph>(glyphs.Count);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var advance = _font.GetAdvanceWidth(glyphs[i]) * _scale;
            // kern[i] spaces the pair (i, i+1). Right to left, glyph i is followed visually by glyph
            // i-1, so after reversal glyph i must carry the value of the pair (i-1, i).
            var pair = rightToLeft ? i - 1 : i;
            var kernAfter = pair >= 0 && pair < kern.Length ? kern[pair] * _scale : 0;
            result.Add(new ShapedGlyph(glyphs[i], advance, kernAfter, 0, 0, sources[i]));
        }

        if (rightToLeft)
        {
            result.Reverse();
        }

        return result;
    }
}
