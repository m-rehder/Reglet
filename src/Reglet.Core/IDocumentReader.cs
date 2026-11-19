namespace Reglet;

/// <summary>
/// contract for a format reader. The reader reads a file (stream) into a
/// <see cref="PortableDocument"/> content model. Layout is not preserved; the
/// reader represents only a documented subset (see feature matrix). The passed
/// stream is not closed.
/// </summary>
public interface IDocumentReader
{
    /// <summary>Reads a document from the stream into the content model.</summary>
    /// <param name="input">Source stream; it remains open after reading.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ReadResult Read(Stream input, CancellationToken cancellationToken = default);
}
