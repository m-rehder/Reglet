namespace Reglet;

/// <summary>A warning that arose when reading (unmappable content element).</summary>
public sealed record ReadWarning(string Message);

/// <summary>Result of a read operation: content model plus collected warnings.</summary>
public sealed record ReadResult
{
    /// <summary>Creates a read result from the model and a snapshot of the warnings.</summary>
    public ReadResult(PortableDocument document, IEnumerable<ReadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(warnings);
        Document = document;
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }

    /// <summary>The read content model.</summary>
    public PortableDocument Document { get; }

    /// <summary>Immutable snapshot of the warnings.</summary>
    public IReadOnlyList<ReadWarning> Warnings { get; }

    /// <summary>True if at least one warning arose when reading.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Result without warnings.</summary>
    public static ReadResult Create(PortableDocument document) => new(document, []);
}
