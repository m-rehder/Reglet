namespace Reglet;

/// <summary>Text in a cell with runs of their own font, such as a bold label followed by normal text.</summary>
public sealed class RichText
{
    /// <summary>Creates rich text from runs.</summary>
    /// <exception cref="ArgumentException">There is no run or a run is null.</exception>
    public RichText(IEnumerable<RichTextRun> runs)
    {
        Runs = ModelCollections.Snapshot(runs, nameof(runs));
        if (Runs.Count == 0)
        {
            throw new ArgumentException("Rich text needs at least one run.", nameof(runs));
        }
    }

    /// <summary>Creates rich text from runs.</summary>
    public RichText(params RichTextRun[] runs)
        : this((IEnumerable<RichTextRun>)runs)
    {
    }

    /// <summary>The runs.</summary>
    public IReadOnlyList<RichTextRun> Runs { get; }

    /// <summary>The plain text of all runs.</summary>
    public override string ToString() => string.Concat(Runs.Select(run => run.Text));
}

/// <summary>A run of rich text.</summary>
/// <param name="Text">The text.</param>
/// <param name="Font">The run's font; null keeps the cell's.</param>
public sealed record RichTextRun(string Text, CellFont? Font = null)
{
    /// <summary>The text.</summary>
    public string Text { get; init; } = Text ?? throw new ArgumentNullException(nameof(Text));
}
