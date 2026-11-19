namespace Reglet;

/// <summary>Applies a <see cref="PdfRevisionMode"/> to the document before it is laid out (ADR-0027).</summary>
internal static class PdfRevisions
{
    private static readonly Color[] Palette =
    [
        Color.FromHex("#C00000"), Color.FromHex("#0070C0"), Color.FromHex("#00A050"),
        Color.FromHex("#7030A0"), Color.FromHex("#ED7D31"), Color.FromHex("#008B8B"),
    ];

    public static PortableDocument Apply(PortableDocument document, PdfRevisionMode mode, ResourceLimits limits)
    {
        // The scan recurses: the nesting limit comes first (the builder checks it again later).
        DocumentLimits.Enforce(document, limits);
        if (RevisionScan.Collect(document).Body.Count == 0)
        {
            return document;
        }

        switch (mode)
        {
            case PdfRevisionMode.Original:
                return DocumentRevisions.RejectAll(document);
            case PdfRevisionMode.Markup:
                var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
                return DocumentRevisions.Markup(document, (run, change) =>
                {
                    if (!colors.TryGetValue(change.Author, out var color))
                    {
                        color = Palette[colors.Count % Palette.Length];
                        colors[change.Author] = color;
                    }

                    return change.Kind == TrackedChangeKind.Insertion
                        ? DocumentSearch.Copy(run, run.Text, TextUnderlineStyle.Single, run.Strikethrough, color)
                        : DocumentSearch.Copy(run, run.Text, run.UnderlineStyle, TextStrikethrough.Single, color);
                });
            default:
                return DocumentRevisions.AcceptAll(document);
        }
    }
}
