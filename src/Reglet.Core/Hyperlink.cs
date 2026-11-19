namespace Reglet;

/// <summary>
/// a hyperlink with target and visible text. The target is a URL (http, https, mailto
/// or a file path).
/// </summary>
public sealed class Hyperlink : Inline
{
    private readonly string _target;

    /// <summary>Creates a hyperlink with plain text.</summary>
    /// <exception cref="ArgumentNullException">Target or text is null.</exception>
    /// <exception cref="ArgumentException">The target is empty.</exception>
    public Hyperlink(string target, string text)
        : this(target, new Inline[] { new TextRun(text) })
    {
    }

    /// <summary>Creates a hyperlink with several inline elements.</summary>
    /// <exception cref="ArgumentNullException">Target or content is null.</exception>
    /// <exception cref="ArgumentException">The target is empty.</exception>
    public Hyperlink(string target, IEnumerable<Inline> inlines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(inlines);

        _target = target;
        Inlines = ModelCollections.Snapshot(inlines, nameof(inlines));
    }

    /// <summary>The target (URL or path).</summary>
    public string Target => _target;

    /// <summary>The visible content of the hyperlink.</summary>
    public IReadOnlyList<Inline> Inlines { get; }
}
