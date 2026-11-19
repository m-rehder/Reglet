namespace Reglet;

/// <summary>
/// inline placeholder for a document property (DOCPROPERTY), e.g. the title or author.
/// In DOCX/ODT the field is written; the PDF resolves it from <see cref="DocumentMetadata"/>
/// (Title, Subject, Author, Keywords, CreateTime or a custom property).
/// </summary>
public sealed class DocPropertyField : Inline
{
    /// <summary>Creates a document property field.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public DocPropertyField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>Name of the document property (e.g. <c>Title</c>, <c>Author</c>).</summary>
    public string Name { get; }
}
