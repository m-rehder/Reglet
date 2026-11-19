namespace Reglet;

/// <summary>Behavior for unsupported features.</summary>
public enum UnsupportedContentBehavior
{
    /// <summary>Collect a warning and omit the feature (default).</summary>
    Warn,

    /// <summary>Throw an error.</summary>
    Fail,
}

/// <summary>A warning that arose when writing.</summary>
public sealed record WriteWarning(string Message);

/// <summary>
/// optional metrics of a write operation. They are only filled if
/// <see cref="WriteOptions.CollectMetrics"/> is enabled.
/// </summary>
public sealed record WriteMetrics
{
    /// <summary>Duration of the write operation (build and transfer).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// page count if the format knows it (PDF). DOCX and ODT flow freely and report
    /// <c>null</c>.
    /// </summary>
    public int? Pages { get; init; }

    /// <summary>Bytes written to the target.</summary>
    public long Bytes { get; init; }
}

/// <summary>Result of a write operation with the collected warnings.</summary>
public sealed record WriteResult
{
    /// <summary>Creates a result from a snapshot of the warnings.</summary>
    public WriteResult(IEnumerable<WriteWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        Warnings = Array.AsReadOnly(warnings.ToArray());
    }

    /// <summary>Immutable snapshot of the warnings.</summary>
    public IReadOnlyList<WriteWarning> Warnings { get; }

    /// <summary>
    /// optional metrics; <c>null</c> if <see cref="WriteOptions.CollectMetrics"/> is not
    /// is set.
    /// </summary>
    public WriteMetrics? Metrics { get; init; }

    /// <summary>Result without warnings.</summary>
    public static WriteResult Empty { get; } = new([]);

    /// <summary>True if at least one warning was collected.</summary>
    public bool HasWarnings => Warnings.Count > 0;
}
