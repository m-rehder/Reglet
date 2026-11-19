namespace Reglet;

/// <summary>Shared format detection from a file extension for readers and writers.</summary>
internal static class DocumentFormatExtensions
{
    /// <summary>Determines the format from a file extension (normalized).</summary>
    /// <exception cref="UnsupportedFeatureException">The extension is missing or not allowed.</exception>
    public static DocumentFormat FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            throw new UnsupportedFeatureException(
                "The file has no extension. Allowed are .docx, .odt, .pdf and .html.");
        }

        return extension.ToLowerInvariant() switch
        {
            ".docx" => DocumentFormat.Docx,
            ".odt" => DocumentFormat.Odt,
            ".pdf" => DocumentFormat.Pdf,
            ".html" or ".htm" => DocumentFormat.Html,
            _ => throw new UnsupportedFeatureException(
                $"Unknown file extension '{extension.ToLowerInvariant()}'. Allowed are .docx, .odt, .pdf and .html."),
        };
    }
}
