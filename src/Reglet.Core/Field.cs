namespace Reglet;

/// <summary>
/// a field with a field code, for example <c>SEQ Figure \* ARABIC</c>, <c>IF</c> or
/// <c>MERGEFIELD Name</c>. The typed fields (<see cref="PageNumberField"/>,
/// <see cref="DateField"/>, <see cref="DocPropertyField"/>, …) cover the common cases;
/// this type carries every other field code (phase Z).
///
/// <para>DOCX writes the field code, so the application calculates it. ODF has no element for
/// a general field, therefore the ODT writer keeps the cached result as text and reports the
/// field. The PDF renders the cached result; a field without a result is reported and not
/// drawn.</para>
/// </summary>
public sealed class Field : Inline
{
    /// <summary>Creates a field.</summary>
    /// <param name="instruction">The field code without the surrounding braces.</param>
    /// <param name="result">The cached result of the field (optional).</param>
    /// <exception cref="ArgumentException">The field code is empty.</exception>
    public Field(string instruction, string? result = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        Instruction = instruction;
        Result = result;
    }

    /// <summary>The field code without the surrounding braces.</summary>
    public string Instruction { get; }

    /// <summary>
    /// The cached result of the field: the text that an application shows until it calculates
    /// the field. Null means that the field has no result.
    /// </summary>
    public string? Result { get; }
}
