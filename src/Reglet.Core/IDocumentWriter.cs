namespace Reglet;

/// <summary>
/// contract for a format writer. The writer reads the immutable model and
/// writes it into the specified stream. The foreign stream is not closed.
/// </summary>
/// <remarks>
/// The writers work synchronously; the returned <see cref="Task{WriteResult}"/> is already
/// completed. The asynchronous signature unifies the writer interface.
/// </remarks>
public interface IDocumentWriter
{
    /// <summary>Writes the document to the stream and returns collected warnings.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="output">Target stream; it remains open after writing.</param>
    /// <param name="cancellationToken">Abbruchsignal.</param>
    Task<WriteResult> WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken = default);
}
