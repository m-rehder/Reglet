using System.Text;

namespace Reglet;

/// <summary>Behavior for characters that XML 1.0 does not allow.</summary>
public enum InvalidXmlCharacterBehavior
{
    /// <summary>Replace invalid characters with U+FFFD (default).</summary>
    Replace,

    /// <summary>Throw an error.</summary>
    Fail,
}

/// <summary>
/// cleans text for XML 1.0. Allowed are #x9, #xA, #xD, #x20–#xD7FF, #xE000–#xFFFD
/// and #x10000–#x10FFFF.
/// </summary>
internal static class XmlText
{
    public static string Clean(string text, InvalidXmlCharacterBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (IsClean(text))
        {
            return text;
        }

        if (behavior == InvalidXmlCharacterBehavior.Fail)
        {
            throw new DocumentValidationException(
                "The text contains characters that are not allowed in XML 1.0.");
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (TryReadCodePoint(text, index, out var codePoint, out var length) && IsValid(codePoint))
            {
                builder.Append(text, index, length);
            }
            else
            {
                builder.Append('\uFFFD');
            }

            index += length - 1;
        }

        return builder.ToString();
    }

    public static bool IsValid(int codePoint)
        => codePoint is 0x9 or 0xA or 0xD
            || (codePoint >= 0x20 && codePoint <= 0xD7FF)
            || (codePoint >= 0xE000 && codePoint <= 0xFFFD)
            || (codePoint >= 0x10000 && codePoint <= 0x10FFFF);

    // EnumerateRunes silently yields U+FFFD for individual surrogates; these would
    // slip through as valid and only fail in the XmlWriter. Therefore it is checked itself
    // decoded and an unpaired surrogate is treated as invalid.
    private static bool IsClean(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!TryReadCodePoint(text, index, out var codePoint, out var length) || !IsValid(codePoint))
            {
                return false;
            }

            index += length - 1;
        }

        return true;
    }

    private static bool TryReadCodePoint(string text, int index, out int codePoint, out int length)
    {
        var character = text[index];
        length = 1;
        codePoint = character;

        if (char.IsHighSurrogate(character))
        {
            if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                codePoint = char.ConvertToUtf32(character, text[index + 1]);
                length = 2;
                return true;
            }

            return false;
        }

        return !char.IsLowSurrogate(character);
    }
}
