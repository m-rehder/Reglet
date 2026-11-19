using System.Collections.ObjectModel;
using System.Globalization;

namespace Reglet;

/// <summary>
/// a template for DOCX and ODT: an existing file whose named placeholders
/// <c>{{Name}}</c> are bound with format-neutral content when writing. The writers
/// adopt the template unchanged and patch a copy, so that styles and layout
/// are preserved.
/// </summary>
/// <remarks>
/// <para>
/// The template is the immutable byte snapshot of an existing file; it
/// is not read into the document model. This keeps it a deliberate,
/// format-specific design source and not an uncontrolled round-trip promise.
/// </para>
/// <para>
/// placeholder names are compared ordinally and thus case-sensitively.
/// </para>
/// </remarks>
public sealed class DocumentTemplate
{
    /// <summary>Opening identifier of a placeholder (<c>{{</c>).</summary>
    public const string OpeningDelimiter = "{{";

    /// <summary>Closing identifier of a placeholder (<c>}}</c>).</summary>
    public const string ClosingDelimiter = "}}";

    private readonly byte[] _data;
    private readonly Dictionary<string, TemplateContent> _map;
    private readonly IReadOnlyDictionary<string, TemplateContent> _placeholders;
    private readonly IReadOnlyDictionary<string, bool> _conditions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> _repeats;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> _regions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> _nestedRegions;
    private readonly string? _bodyPlaceholder;
    private readonly IReadOnlyDictionary<string, TemplatePlaceholderType> _types;

    private static readonly IReadOnlyDictionary<string, bool> EmptyConditions =
        new ReadOnlyDictionary<string, bool>(new Dictionary<string, bool>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> EmptyRepeats =
        new ReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.Ordinal));

    private DocumentTemplate(
        byte[] data,
        Dictionary<string, TemplateContent> map,
        string? bodyPlaceholder,
        IReadOnlyDictionary<string, bool> conditions,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> repeats,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> regions,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> nestedRegions,
        IReadOnlyDictionary<string, TemplatePlaceholderType> types)
    {
        _data = data;
        _map = map;
        _placeholders = new ReadOnlyDictionary<string, TemplateContent>(map);
        _conditions = conditions;
        _repeats = repeats;
        _regions = regions;
        _nestedRegions = nestedRegions;
        _bodyPlaceholder = bodyPlaceholder;
        _types = types;
    }

    /// <summary>Creates a template from the bytes of an existing DOCX or ODT file.</summary>
    /// <param name="data">The template bytes; adopted as a snapshot.</param>
    /// <exception cref="ArgumentException">The bytes are empty.</exception>
    public static DocumentTemplate FromBytes(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("Template data must not be empty.", nameof(data));
        }

        return new DocumentTemplate(data.ToArray(), new Dictionary<string, TemplateContent>(StringComparer.Ordinal), null, EmptyConditions, EmptyRepeats, EmptyRepeats,
            new ReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>(new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, TemplatePlaceholderType>(new Dictionary<string, TemplatePlaceholderType>(StringComparer.Ordinal)));
    }

    /// <summary>The template bytes as a defensive copy.</summary>
    public ReadOnlyMemory<byte> Data => _data.ToArray();

    /// <summary>
    /// The template bytes without a copy for the writers. The caller must not modify the returned
    /// memory view; the bytes remain immutable.
    /// </summary>
    internal ReadOnlyMemory<byte> RawData => _data;

    /// <summary>
    /// the name of the placeholder that receives the document's blocks; null means
    /// that the document blocks are appended at the end of the template content.
    /// </summary>
    public string? BodyPlaceholder => _bodyPlaceholder;

    /// <summary>
    /// The bound placeholders (name → content). The collection is immutable.
    /// </summary>
    public IReadOnlyDictionary<string, TemplateContent> Placeholders => _placeholders;

    /// <summary>Declared placeholder types; names are compared ordinally.</summary>
    public IReadOnlyDictionary<string, TemplatePlaceholderType> PlaceholderTypes => _types;

    /// <summary>Declares a type and validates existing bindings and repeat rows for the name.</summary>
    /// <exception cref="ArgumentException">The name is invalid, is the body placeholder, or an existing value does not match the type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The type is not defined.</exception>
    public DocumentTemplate Declare(string name, TemplatePlaceholderType type)
    {
        name = ValidateName(name);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (name == _bodyPlaceholder)
        {
            throw new ArgumentException("A body placeholder cannot have a declared type.", nameof(name));
        }

        if (_map.TryGetValue(name, out var content))
        {
            ValidateContent(name, type, content);
        }

        ValidateRows(_repeats.Values.Concat(_regions.Values).SelectMany(rows => rows), new Dictionary<string, TemplatePlaceholderType> { [name] = type });
        ValidateNestedRows(_nestedRegions.Values.SelectMany(rows => rows), new Dictionary<string, TemplatePlaceholderType> { [name] = type });

        var types = new Dictionary<string, TemplatePlaceholderType>(_types, StringComparer.Ordinal) { [name] = type };
        return new DocumentTemplate(_data, Copy(), _bodyPlaceholder, _conditions, _repeats, _regions, _nestedRegions,
            new ReadOnlyDictionary<string, TemplatePlaceholderType>(types));
    }

    /// <summary>The conditions of the <c>{{If Name}}</c> regions (phase AB, AB02).</summary>
    public IReadOnlyDictionary<string, bool> Conditions => _conditions;

    /// <summary>
    /// The row repetitions (phase AB, AB03): placeholder → data rows. The table row containing
    /// the placeholder is repeated once per data row and the <c>{{Field}}</c> values are bound.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Repeats => _repeats;

    /// <summary>Named data sets for repeated regions.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> RegionRepeats => _regions;

    /// <summary>Named repeated-region data with per-row nested data sets.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> NestedRegionRepeats => _nestedRegions;

    /// <summary>
    /// repeats the table row that contains <paramref name="name"/> once per data row
    /// (phase AB, AB03).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is invalid or a declared type does not match a row value.</exception>
    public DocumentTemplate RepeatRows(string name, IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var snapshot = rows.ToList();
        ValidateRows(snapshot, _types);
        var repeats = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(_repeats, StringComparer.Ordinal)
        {
            [ValidateName(name)] = snapshot,
        };
        return new DocumentTemplate(_data, Copy(), _bodyPlaceholder, _conditions, repeats, _regions, _nestedRegions, _types);
    }

    /// <summary>
    /// Sets the rows for a region delimited by standalone <c>{{Repeat:Name}}</c> and
    /// <c>{{EndRepeat}}</c> paragraphs or table rows. Regions may be nested.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or rows are null.</exception>
    /// <exception cref="ArgumentException">The name is invalid or a declared type does not match a row value.</exception>
    public DocumentTemplate RepeatRegion(string name, IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        name = ValidateName(name);
        ArgumentNullException.ThrowIfNull(rows);
        var snapshot = rows.ToList();
        ValidateRows(snapshot, _types);
        var regions = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(_regions, StringComparer.Ordinal)
        {
            [name] = snapshot.AsReadOnly(),
        };
        var nested = new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(_nestedRegions, StringComparer.Ordinal);
        nested.Remove(name);
        return new DocumentTemplate(_data, Copy(), _bodyPlaceholder, _conditions, _repeats,
            new ReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(regions),
            new ReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>(nested), _types);
    }

    /// <summary>
    /// Sets rows whose nested regions can have different data for each parent row. A nested
    /// data set overrides a shared one with the same name; an absent set falls back to it.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or rows are null.</exception>
    /// <exception cref="ArgumentException">A name, row or declared value is invalid.</exception>
    public DocumentTemplate RepeatNestedRegion(string name, IEnumerable<TemplateRegionRow> rows)
    {
        name = ValidateName(name);
        var snapshot = ModelCollections.Snapshot(rows, nameof(rows));
        ValidateNestedRows(snapshot, _types);
        var nested = new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(_nestedRegions, StringComparer.Ordinal) { [name] = snapshot };
        var regions = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(_regions, StringComparer.Ordinal);
        regions.Remove(name);
        return new DocumentTemplate(_data, Copy(), _bodyPlaceholder, _conditions, _repeats,
            new ReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(regions),
            new ReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>(nested), _types);
    }

    /// <summary>
    /// sets the condition of an <c>{{If Name}}…{{EndIf}}</c> region (phase AB, AB02). A region
    /// whose condition is false (or absent) is removed when writing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate Condition(string name, bool value)
    {
        var conditions = new Dictionary<string, bool>(_conditions, StringComparer.Ordinal)
        {
            [ValidateName(name)] = value,
        };
        return new DocumentTemplate(_data, Copy(), _bodyPlaceholder, conditions, _repeats, _regions, _nestedRegions, _types);
    }

    /// <summary>Binds a placeholder to empty block content, removing its paragraph (phase AB, AB02).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate BindEmpty(string name) => Bind(name, TemplateContent.FromBlocks(Array.Empty<Block>()));

    /// <summary>
    /// sets which placeholder receives the document's blocks. If the template contains the
    /// placeholder once in each of several sections, the writers bind the model section at the
    /// same position instead of merging all sections into one area.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="placeholderName"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate Body(string placeholderName)
    {
        placeholderName = ValidateName(placeholderName);
        if (_types.ContainsKey(placeholderName))
        {
            throw new ArgumentException("A typed placeholder cannot receive the document body.", nameof(placeholderName));
        }

        return new(_data, Copy(), placeholderName, _conditions, _repeats, _regions, _nestedRegions, _types);
    }

    /// <summary>Binds a placeholder to content; an already bound name is replaced.</summary>
    /// <exception cref="ArgumentNullException">Name or content is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate Bind(string name, TemplateContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        name = ValidateName(name);
        if (_types.TryGetValue(name, out var type))
        {
            ValidateContent(name, type, content);
        }

        var copy = Copy();
        copy[name] = content;
        return new DocumentTemplate(_data, copy, _bodyPlaceholder, _conditions, _repeats, _regions, _nestedRegions, _types);
    }

    /// <summary>Binds a placeholder to exactly one inline element.</summary>
    /// <exception cref="ArgumentNullException">Name or element is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate Bind(string name, Inline inline)
        => Bind(name, TemplateContent.FromInline(inline));

    /// <summary>Binds a placeholder to inline content.</summary>
    /// <exception cref="ArgumentNullException">Name or collection is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, contains braces, or the collection contains null elements.</exception>
    public DocumentTemplate Bind(string name, IEnumerable<Inline> inlines)
        => Bind(name, TemplateContent.FromInlines(inlines));

    /// <summary>Binds a placeholder to exactly one block element.</summary>
    /// <exception cref="ArgumentNullException">Name or element is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate Bind(string name, Block block)
        => Bind(name, TemplateContent.FromBlock(block));

    /// <summary>Binds a placeholder to block content.</summary>
    /// <exception cref="ArgumentNullException">Name or collection is null.</exception>
    /// <exception cref="ArgumentException">The name is empty, contains braces, or the collection contains null elements.</exception>
    public DocumentTemplate Bind(string name, IEnumerable<Block> blocks)
        => Bind(name, TemplateContent.FromBlocks(blocks));

    /// <summary>Binds a placeholder to plain text.</summary>
    /// <exception cref="ArgumentNullException">Name or text is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public DocumentTemplate BindText(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Bind(name, new TextRun(text));
    }

    /// <summary>
    /// Binds all placeholders named in <paramref name="values"/> to their text value
    /// (phase AB, mail merge). A name with a null value is skipped.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or contains braces.</exception>
    public DocumentTemplate Bind(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = Copy();
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            var content = TemplateContent.FromInline(new TextRun(value));
            if (_types.TryGetValue(name, out var type))
            {
                ValidateContent(name, type, content);
            }

            copy[ValidateName(name)] = content;
        }

        return new DocumentTemplate(_data, copy, _bodyPlaceholder, _conditions, _repeats, _regions, _nestedRegions, _types);
    }

    /// <summary>Forms the identifier of a placeholder, e.g. <c>{{Title}}</c>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is empty or contains braces.</exception>
    public static string Marker(string name) => OpeningDelimiter + ValidateName(name) + ClosingDelimiter;

    private Dictionary<string, TemplateContent> Copy()
        => new(_map, StringComparer.Ordinal);

    internal void ValidateTypes()
    {
        ValidateRows(_repeats.Values.Concat(_regions.Values).SelectMany(rows => rows), _types);
        ValidateNestedRows(_nestedRegions.Values.SelectMany(rows => rows), _types);
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> RegionData()
    {
        var result = new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(_nestedRegions, StringComparer.Ordinal);
        foreach (var (name, rows) in _regions)
        {
            result[name] = rows.Select(row => new TemplateRegionRow(row)).ToArray();
        }

        return result;
    }

    private static void ValidateNestedRows(IEnumerable<TemplateRegionRow> rows, IReadOnlyDictionary<string, TemplatePlaceholderType> types)
    {
        if (types.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            ReaderGuard.EnsureStack();
            ValidateRows([row.Values], types);
            ValidateNestedRows(row.Regions.Values.SelectMany(children => children), types);
        }
    }

    private static void ValidateRows(
        IEnumerable<IReadOnlyDictionary<string, string?>> rows,
        IReadOnlyDictionary<string, TemplatePlaceholderType> types)
    {
        if (types.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            foreach (var (name, type) in types)
            {
                if (row.TryGetValue(name, out var value) && value is not null)
                {
                    ValidateContent(name, type, TemplateContent.FromInline(new TextRun(value)));
                }
            }
        }
    }

    private static void ValidateContent(string name, TemplatePlaceholderType type, TemplateContent content)
    {
        var text = content.Kind == TemplateContentKind.Inline && content.Inlines.All(inline => inline is TextRun)
            ? string.Concat(content.Inlines.Cast<TextRun>().Select(run => run.Text)) : null;
        var valid = type switch
        {
            TemplatePlaceholderType.Text => text is not null,
            TemplatePlaceholderType.Date => text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || content.Inlines.Count == 1 && content.Inlines[0] is DateField { Value: not null },
            TemplatePlaceholderType.Number => text is not null && decimal.TryParse(text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _),
            TemplatePlaceholderType.Image => content.Blocks.Count == 1 && content.Blocks[0] is ImageBlock,
            TemplatePlaceholderType.Field => content.Inlines.Count == 1 && content.Inlines[0] is
                Field or DateField or PageNumberField or PageCountField or PageRefField or DocPropertyField or DocVariableField,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"Placeholder '{name}' requires a {type} value.", nameof(content));
        }
    }

    /// <summary>Whether a data column can name a placeholder at all.</summary>
    internal static bool IsValidName(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains('{') && !name.Contains('}');

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('{') || name.Contains('}'))
        {
            throw new ArgumentException("A placeholder name must not contain curly braces.", nameof(name));
        }

        return name;
    }
}
