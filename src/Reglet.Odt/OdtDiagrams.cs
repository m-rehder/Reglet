namespace Reglet;

/// <summary>
/// ADR-0030: the shapes a diagram is written with in ODT. A diagram wider than its place (the text area, a table cell)
/// is shrunk with its text (<see cref="DiagramLayout.Scale"/>); the style collectors and the content writer ask here so
/// that they see the same runs and paragraphs.
/// </summary>
internal static class OdtDiagrams
{
    private static readonly AsyncLocal<Dictionary<Diagram, IReadOnlyList<PlacedShape>>?> Current = new();

    /// <summary>The shapes to write for a diagram: shrunk to its place while a write is running, else its own.</summary>
    internal static IReadOnlyList<PlacedShape> Shapes(Diagram diagram)
        => Current.Value is { } map && map.TryGetValue(diagram, out var shapes) ? shapes : diagram.Shapes;

    /// <summary>Works out the place of every diagram of the document for the time of one write.</summary>
    internal static IDisposable Use(PortableDocument document)
    {
        var map = new Dictionary<Diagram, IReadOnlyList<PlacedShape>>(ReferenceEqualityComparer.Instance);
        foreach (var section in document.Sections)
        {
            var landscape = section.Orientation == PageOrientation.Landscape;
            var width = (landscape ? section.PageSize.Height.Points : section.PageSize.Width.Points) - section.Margins.Left.Points - section.Margins.Right.Points;
            Collect(section.Blocks, width, map, 0);
        }

        var previous = Current.Value;
        Current.Value = map;
        return new Scope(previous);
    }

    private static void Collect(IReadOnlyList<Block> blocks, double available, Dictionary<Diagram, IReadOnlyList<PlacedShape>> map, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case Diagram diagram when !map.ContainsKey(diagram):
                    map[diagram] = DiagramLayout.Scale(diagram.Shapes, Math.Min(1, Math.Max(1, available) / diagram.Width.Points));
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        Collect(item.Blocks, available, map, depth + 1);
                    }

                    break;
                case TableBlock table:
                    var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Cells.Sum(cell => Math.Max(1, cell.ColumnSpan)));
                    if (columns == 0)
                    {
                        break;
                    }

                    var widths = TableLayout.ResolveColumnWidthsWithPercent(table, columns, Math.Max(1, available));
                    foreach (var row in table.Rows)
                    {
                        var column = 0;
                        foreach (var cell in row.Cells)
                        {
                            var cellWidth = widths.Skip(column).Take(Math.Max(1, cell.ColumnSpan)).Sum();
                            var padding = cell.Padding?.Points ?? Typography.TableCellPaddingHorizontal.Points;
                            Collect(cell.Blocks, cellWidth - 2 * padding, map, depth + 1);
                            column += Math.Max(1, cell.ColumnSpan);
                        }
                    }

                    break;
            }
        }
    }

    private sealed class Scope(Dictionary<Diagram, IReadOnlyList<PlacedShape>>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
