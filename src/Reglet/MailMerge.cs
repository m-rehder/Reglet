namespace Reglet;

/// <summary>
/// mail merge (phase AB): binds a template with data rows and writes one document per row.
/// </summary>
public static class MailMerge
{
    /// <summary>
    /// Binds each row (phase AB, AB01/AB03) and writes one document per row into
    /// <paramref name="directory"/>. Only DOCX and ODT templates are supported (PDF uses page
    /// templates). The files are named <c>merge-001.docx</c> and so on; a file with that name in
    /// the directory is replaced.
    /// </summary>
    /// <returns>The paths of the written documents, in row order.</returns>
    /// <exception cref="ArgumentNullException">Template or rows are null.</exception>
    /// <exception cref="ArgumentException">The directory is empty.</exception>
    /// <exception cref="UnsupportedFeatureException">The format is not DOCX or ODT.</exception>
    public static async Task<IReadOnlyList<string>> MergeAsync(
        DocumentTemplate template,
        IEnumerable<IReadOnlyDictionary<string, string?>> rows,
        string directory,
        DocumentFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var extension = format switch
        {
            DocumentFormat.Docx => "docx",
            DocumentFormat.Odt => "odt",
            _ => throw new UnsupportedFeatureException(
                "Mail merge is only supported for DOCX and ODT templates."),
        };

        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var index = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            // A column whose name cannot be a placeholder has nothing to fill.
            var bound = template.Bind(row.Where(column => DocumentTemplate.IsValidName(column.Key)).ToDictionary(StringComparer.Ordinal));
            WriteOptions options = format == DocumentFormat.Docx
                ? new DocxWriteOptions { Template = bound }
                : new OdtWriteOptions { Template = bound };
            var document = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
            var path = Path.Combine(directory, $"merge-{index:D3}.{extension}");
            await DocumentWriter.WriteAsync(document, path, options, cancellationToken).ConfigureAwait(false);
            paths.Add(path);
        }

        return paths;
    }
}
