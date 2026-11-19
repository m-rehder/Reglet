namespace Reglet;

/// <summary>
/// Unicode joining types and Arabic script for the contextual forms (phase P, P03).
/// Covered are the Arabic block (U+0600–U+06FF) and Arabic Supplement (U+0750–U+077F) as
/// well as ZWJ/ZWNJ and the combining marks, as in ArabicShaping.txt (Unicode 16.0.0).
/// </summary>
internal static class ArabicJoining
{
    /// <summary>Joining type of a character (U non-joining, R right, L left, D dual, C causing, T transparent).</summary>
    internal enum JoiningType
    {
        U,
        R,
        L,
        D,
        C,
        T,
    }

    internal const int ZeroWidthJoiner = 0x200D;
    internal const int ZeroWidthNonJoiner = 0x200C;

    /// <summary>True if the code point belongs to the Arabic script (basic block).</summary>
    public static bool IsArabicScript(int codePoint)
        => codePoint is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F;

    /// <summary>Joining type of a code point.</summary>
    public static JoiningType GetJoiningType(int codePoint)
    {
        if (codePoint is ZeroWidthJoiner or 0x0640)
        {
            return JoiningType.C;
        }

        // ZWNJ and the prepended number signs (U+0600–0605, U+06DD) are format characters, but
        // ArabicShaping.txt lists them as non-joining instead of transparent.
        if (codePoint is ZeroWidthNonJoiner or >= 0x0600 and <= 0x0605 or 0x06DD)
        {
            return JoiningType.U;
        }

        // combining marks are transparent (skipped during joining).
        var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.Format)
        {
            return JoiningType.T;
        }

        if (codePoint is >= 0x0750 and <= 0x077F)
        {
            return codePoint is >= 0x0759 and <= 0x075B or 0x076B or 0x076C or 0x0771 or 0x0773 or 0x0774 or 0x0778 or 0x0779
                ? JoiningType.R
                : JoiningType.D; // Arabic Supplement
        }

        if (codePoint is < 0x0600 or > 0x06FF)
        {
            return JoiningType.U;
        }

        return codePoint switch
        {
            0x0620 => JoiningType.D,
            0x0621 => JoiningType.U, // HAMZA does not connect
            0x0622 or 0x0623 or 0x0624 or 0x0625 => JoiningType.R,
            0x0626 => JoiningType.D,
            0x0627 => JoiningType.R, // ALEF
            0x0628 => JoiningType.D,
            0x0629 => JoiningType.R, // TEH MARBUTA
            >= 0x062A and <= 0x062E => JoiningType.D,
            >= 0x062F and <= 0x0632 => JoiningType.R, // DAL, THAL, REH, ZAIN
            >= 0x0633 and <= 0x063F => JoiningType.D,
            >= 0x0641 and <= 0x0647 => JoiningType.D,
            0x0648 => JoiningType.R, // WAW
            0x0649 or 0x064A => JoiningType.D,
            >= 0x066E and <= 0x066F => JoiningType.D,
            0x0671 or 0x0672 or 0x0673 => JoiningType.R,
            0x0674 => JoiningType.U,
            >= 0x0675 and <= 0x0677 => JoiningType.R,
            >= 0x0678 and <= 0x0687 => JoiningType.D,
            >= 0x0688 and <= 0x0699 => JoiningType.R,
            >= 0x069A and <= 0x06BF => JoiningType.D,
            0x06C0 => JoiningType.R,
            0x06C1 or 0x06C2 => JoiningType.D,
            0x06C3 => JoiningType.R,
            >= 0x06C4 and <= 0x06CB => JoiningType.R,
            0x06CC => JoiningType.D,
            0x06CD => JoiningType.R,
            0x06CE => JoiningType.D,
            0x06CF => JoiningType.R,
            0x06D0 or 0x06D1 => JoiningType.D,
            0x06D2 or 0x06D3 => JoiningType.R,
            0x06D5 => JoiningType.R,
            >= 0x06D6 and <= 0x06DC => JoiningType.T,
            >= 0x06DF and <= 0x06E4 => JoiningType.T,
            >= 0x06E7 and <= 0x06E8 => JoiningType.T,
            >= 0x06EA and <= 0x06ED => JoiningType.T,
            0x06EE or 0x06EF => JoiningType.R,
            >= 0x06FA and <= 0x06FC => JoiningType.D,
            0x06FF => JoiningType.D,
            _ => JoiningType.U,
        };
    }

    /// <summary>
    /// computes the contextual form per character according to the joining algorithm. Letters without
    /// a joining neighbour get <see cref="ContextForm.Isolated"/>.
    /// </summary>
    public static ContextForm[] ResolveForms(ReadOnlySpan<int> codePoints)
    {
        var forms = new ContextForm[codePoints.Length];
        for (var i = 0; i < codePoints.Length; i++)
        {
            var type = GetJoiningType(codePoints[i]);
            if (type is not (JoiningType.D or JoiningType.R))
            {
                forms[i] = ContextForm.None;
                continue;
            }

            var previous = PreviousJoining(codePoints, i);
            var next = NextJoining(codePoints, i);

            var joinsPrevious = (type is JoiningType.D or JoiningType.R)
                && (previous is JoiningType.D or JoiningType.L or JoiningType.C);
            var joinsNext = (type is JoiningType.D or JoiningType.L)
                && (next is JoiningType.D or JoiningType.R or JoiningType.C);

            forms[i] = (joinsPrevious, joinsNext) switch
            {
                (true, true) => ContextForm.Medial,
                (true, false) => ContextForm.Final,
                (false, true) => ContextForm.Initial,
                _ => ContextForm.Isolated,
            };
        }

        return forms;
    }

    private static JoiningType PreviousJoining(ReadOnlySpan<int> codePoints, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var type = GetJoiningType(codePoints[i]);
            if (type != JoiningType.T)
            {
                return type;
            }
        }

        return JoiningType.U;
    }

    private static JoiningType NextJoining(ReadOnlySpan<int> codePoints, int index)
    {
        for (var i = index + 1; i < codePoints.Length; i++)
        {
            var type = GetJoiningType(codePoints[i]);
            if (type != JoiningType.T)
            {
                return type;
            }
        }

        return JoiningType.U;
    }

    /// <summary>Contextual form of an Arabic letter.</summary>
    internal enum ContextForm
    {
        /// <summary>No Arabic letter (no substitution).</summary>
        None,

        Isolated,
        Initial,
        Medial,
        Final,
    }
}
