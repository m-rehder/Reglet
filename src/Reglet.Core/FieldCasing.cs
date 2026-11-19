namespace Reglet;

/// <summary>The Word format switches that change capitals but keep all other letters.</summary>
internal static class FieldCasing
{
    /// <summary><c>\* FirstCap</c>: capitalizes the first letter of the first word.</summary>
    public static string FirstCap(string text)
    {
        var index = 0;
        while (index < text.Length && !char.IsLetter(text[index]))
        {
            index++;
        }

        return index < text.Length ? text[..index] + char.ToUpperInvariant(text[index]) + text[(index + 1)..] : text;
    }

    /// <summary><c>\* Caps</c>: capitalizes the first letter of each word.</summary>
    public static string Caps(string text)
        => string.Create(text.Length, text, static (target, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                target[index] = index == 0 || char.IsWhiteSpace(source[index - 1]) ? char.ToUpperInvariant(source[index]) : source[index];
            }
        });
}
