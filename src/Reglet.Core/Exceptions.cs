namespace Reglet;

/// <summary>
/// marker for all errors raised by Reglet. This allows its own
/// catch errors together while the concrete types remain distinguishable.
/// </summary>
public interface IRegletError
{
}

/// <summary>Invalid input or model values.</summary>
public sealed class DocumentValidationException : ArgumentException, IRegletError
{
    /// <summary>Creates the exception with a message.</summary>
    public DocumentValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a parameter name.</summary>
    public DocumentValidationException(string message, string? paramName)
        : base(message, paramName)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public DocumentValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A feature cannot be rendered in the target format (or in version 1).</summary>
public sealed class UnsupportedFeatureException : NotSupportedException, IRegletError
{
    /// <summary>Creates the exception with a message.</summary>
    public UnsupportedFeatureException(string message)
        : base(message)
    {
    }
}

/// <summary>A font file is corrupted or unsupported.</summary>
public sealed class FontException : Exception, IRegletError
{
    /// <summary>Creates the exception with a message.</summary>
    public FontException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public FontException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Image data is corrupted, incomplete or unsupported.</summary>
public sealed class ImageException : Exception, IRegletError
{
    /// <summary>Creates the exception with a message.</summary>
    public ImageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Error when writing to the target file or stream.</summary>
public sealed class DocumentWriteException : IOException, IRegletError
{
    /// <summary>Creates the exception with a message.</summary>
    public DocumentWriteException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public DocumentWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
