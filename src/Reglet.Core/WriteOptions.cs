namespace Reglet;

/// <summary>
/// base class for format-specific writer options. Concrete options remain optional;
/// if none is passed, the respective writer's defaults apply.
/// </summary>
public abstract record WriteOptions
{
    private UnsupportedContentBehavior _unsupportedContent = UnsupportedContentBehavior.Warn;
    private InvalidXmlCharacterBehavior _invalidXmlCharacters = InvalidXmlCharacterBehavior.Replace;

    /// <summary>
    /// behavior for unsupported features. Default is <see cref="UnsupportedContentBehavior.Warn"/>.
    /// </summary>
    public UnsupportedContentBehavior UnsupportedContent
    {
        get => _unsupportedContent;
        init => _unsupportedContent = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(UnsupportedContent), value, "Unknown fallback behavior.");
    }

    /// <summary>
    /// behavior for characters that XML 1.0 does not allow (DOCX/ODT). Default is
    /// <see cref="InvalidXmlCharacterBehavior.Replace"/>.
    /// </summary>
    public InvalidXmlCharacterBehavior InvalidXmlCharacters
    {
        get => _invalidXmlCharacters;
        init => _invalidXmlCharacters = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(InvalidXmlCharacters), value, "Unknown behavior for invalid XML characters.");
    }

    /// <summary>Configurable resource and document limits.</summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// whether <see cref="WriteResult.Metrics"/> (duration, pages, bytes) is collected. Default is
    /// <c>false</c>.
    /// </summary>
    public bool CollectMetrics { get; init; }

    /// <summary>
    /// whether a non-seekable target is written directly instead of building the output completely in
    /// memory build-up. Benefit: no complete intermediate buffer (important for large
    /// documents). Prerequisite: the target allows synchronous write access; if the build aborts
    /// off, already-written data can remain in the target. Default is <c>false</c>.
    /// </summary>
    public bool StreamNonSeekableOutput { get; init; }

    /// <summary>
    /// optional progress receiver (phase AE, AE01); the writers report the completed sections.
    /// </summary>
    public IProgress<WriteProgress>? Progress { get; init; }
}
