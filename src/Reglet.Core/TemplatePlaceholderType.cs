namespace Reglet;

/// <summary>The expected value of a typed template placeholder.</summary>
public enum TemplatePlaceholderType
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>An ISO date (yyyy-MM-dd) or a date field with a value.</summary>
    Date,

    /// <summary>An invariant decimal number.</summary>
    Number,

    /// <summary>A single image block.</summary>
    Image,

    /// <summary>A single field inline.</summary>
    Field,
}
