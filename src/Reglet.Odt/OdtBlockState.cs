namespace Reglet;

/// <summary>
/// the mutable state of one ODT content pass (OF-B02): the running table number and the lists
/// that were already written, so a later list with the same numbering definition continues it
/// through <c>text:continue-list</c>.
/// </summary>
internal sealed class OdtBlockState(int tableIndexStart)
{
    private readonly Dictionary<(string Name, int Level), string> _lastListIds = [];
    private int _listId;

    /// <summary>The control ids of the form fields of the body (ADR-0024).</summary>
    public IReadOnlyDictionary<FormField, string> Forms { get; init; } = new Dictionary<FormField, string>();

    /// <summary>The object names of the charts of the body (ADR-0025).</summary>
    public IReadOnlyDictionary<Chart, string> Charts { get; init; } = new Dictionary<Chart, string>();

    /// <summary>The part names of the embedded objects of the body (ADR-0029).</summary>
    public IReadOnlyDictionary<EmbeddedObject, string> Objects { get; init; } = new Dictionary<EmbeddedObject, string>();

    /// <summary>The object names of the equations of the body (ADR-0031).</summary>
    public IReadOnlyDictionary<Equation, string> Equations { get; init; } = new Dictionary<Equation, string>();

    /// <summary>The index of the last table that was written; tables are named "Table{index}".</summary>
    public int TableIndex { get; private set; } = tableIndexStart;

    /// <summary>The next table number (tables are numbered per document, also across bands).</summary>
    public int NextTableIndex() => ++TableIndex;

    /// <summary>Reserves the ODT id of a list (a valid NCName).</summary>
    public string NextListId() => "List" + (++_listId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// the id of the last list with the given definition and level, or null when this is the
    /// first list of that definition.
    /// </summary>
    public string? LastListId(string definitionName, int level)
        => _lastListIds.TryGetValue((definitionName, level), out var id) ? id : null;

    /// <summary>Remembers the id of a written list, so the next one can continue it.</summary>
    public void SetLastListId(string definitionName, int level, string id)
        => _lastListIds[(definitionName, level)] = id;
}
