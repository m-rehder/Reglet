using Reglet;

namespace Gallery;

/// <summary>
/// One example of the gallery: a category, a name, one sentence of what it shows, and either a document that is written
/// to the given formats or a custom action that writes its own files.
/// </summary>
public sealed record Example(string Category, string Name, string Description)
{
    /// <summary>The document the example builds; written to every format in <see cref="Formats"/>.</summary>
    public Func<PortableDocument>? Document { get; init; }

    /// <summary>The formats the document is written to (file extensions).</summary>
    public IReadOnlyList<string> Formats { get; init; } = ["docx", "odt", "pdf"];

    /// <summary>Writer options per file extension (a template, a PDF page template, encryption ...).</summary>
    public Func<string, WriteOptions?>? OptionsFor { get; init; }

    /// <summary>
    /// A custom action instead of <see cref="Document"/>: it gets the output directory and returns the files it wrote
    /// (reading, converting, merging PDFs, mail merge).
    /// </summary>
    public Func<string, Task<IReadOnlyList<string>>>? Run { get; init; }

    /// <summary>The file name prefix of the output, e.g. <c>basics-hello-world</c>.</summary>
    public string FileName => $"{Category}-{Name}";
}
