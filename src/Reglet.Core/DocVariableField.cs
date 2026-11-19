namespace Reglet;

/// <summary>
/// inline placeholder for a document variable (DOCVARIABLE). In DOCX/ODT the field is
/// written; the PDF resolves it from <see cref="DocumentMetadata.Variables"/>.
/// </summary>
public sealed class DocVariableField : Inline
{
    /// <summary>Creates a document variable field.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public DocVariableField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>Name of the document variable.</summary>
    public string Name { get; }
}
