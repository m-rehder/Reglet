namespace Reglet;

/// <summary>Options for the XLSX writer.</summary>
public sealed record XlsxWriteOptions : WriteOptions
{
    /// <summary>Default options.</summary>
    public static XlsxWriteOptions Default { get; } = new();
}
