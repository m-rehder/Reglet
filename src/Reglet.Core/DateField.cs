namespace Reglet;

/// <summary>
/// inline placeholder for a date. In DOCX/ODT a date field is written (the value
/// serves as a cached result), in PDF the value is rendered directly.
/// </summary>
public sealed class DateField : Inline
{
    /// <summary>Creates a date field.</summary>
    /// <param name="value">The value; <c>null</c> means the current date at runtime.</param>
    public DateField(DateTimeOffset? value = null)
    {
        Value = value;
    }

    /// <summary>The value of the field; <c>null</c> means the current date.</summary>
    public DateTimeOffset? Value { get; }
}
