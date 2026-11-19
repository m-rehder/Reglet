using System.Collections.ObjectModel;

namespace Reglet;

/// <summary>A repeated-region row and its per-row nested data sets.</summary>
public sealed class TemplateRegionRow
{
    /// <summary>Creates a row; nested lists are snapshotted and value dictionaries are revalidated when writing.</summary>
    public TemplateRegionRow(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>? regions = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
        var children = new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(StringComparer.Ordinal);
        if (regions is not null)
        {
            foreach (var (name, rows) in regions)
            {
                _ = DocumentTemplate.Marker(name);
                ArgumentNullException.ThrowIfNull(rows);
                children[name] = ModelCollections.Snapshot(rows, nameof(regions));
            }
        }

        Regions = new ReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>(children);
    }

    /// <summary>Field values for this row.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>Per-row data sets for nested regions; names are case-sensitive.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> Regions { get; }
}
