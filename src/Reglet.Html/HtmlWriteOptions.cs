namespace Reglet;

/// <summary>Options for the HTML writer (ADR-0032).</summary>
public sealed record HtmlWriteOptions : WriteOptions
{
    /// <summary>The default options.</summary>
    public static HtmlWriteOptions Default { get; } = new();

    /// <summary>Whether images are written as <c>data:</c> URIs (default); false drops them with a report.</summary>
    public bool EmbedImages { get; init; } = true;
}
