namespace Reglet;

/// <summary>
/// A spreadsheet workbook (ADR-0043): named worksheets in a fixed order. Unlike the document model it is filled step by
/// step; <c>XlsxWorkbookWriter</c> (package Reglet.Xlsx) writes it.
/// </summary>
public sealed class Workbook
{
    private readonly List<Worksheet> _worksheets = [];

    /// <summary>The worksheets in the order they were added.</summary>
    public IReadOnlyList<Worksheet> Worksheets => _worksheets;

    /// <summary>Adds a worksheet at the end.</summary>
    /// <exception cref="ArgumentException">The name is not a valid sheet name (see <see cref="IsValidSheetName"/>) or is already used, ignoring case.</exception>
    public Worksheet AddWorksheet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsValidSheetName(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid sheet name: 1 to 31 characters, none of [ ] : * ? / \\, not starting or ending with an apostrophe.",
                nameof(name));
        }

        if (_worksheets.Any(sheet => string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The workbook already has a sheet named '{name}'.", nameof(name));
        }

        var worksheet = new Worksheet(name);
        _worksheets.Add(worksheet);
        return worksheet;
    }

    /// <summary>
    /// Adds a sheet read from a file as it is named there, without the name rules: a reader keeps what the file holds;
    /// the writer checks the names.
    /// </summary>
    internal Worksheet AddReadWorksheet(string name)
    {
        var worksheet = new Worksheet(name);
        _worksheets.Add(worksheet);
        return worksheet;
    }

    /// <summary>The worksheet with this name, ignoring case.</summary>
    /// <exception cref="KeyNotFoundException">There is no such sheet.</exception>
    public Worksheet this[string name]
        => _worksheets.FirstOrDefault(sheet => string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"The workbook has no sheet named '{name}'.");

    /// <summary>
    /// Whether Excel accepts the name for a sheet: 1 to 31 characters, none of <c>[ ] : * ? / \</c>, not starting or
    /// ending with an apostrophe. Uniqueness within a workbook is checked by <see cref="AddWorksheet"/>.
    /// </summary>
    public static bool IsValidSheetName(string? name)
        => name is { Length: > 0 and <= 31 }
            && name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) < 0
            && name[0] != '\'' && name[^1] != '\''
            && !name.Any(char.IsControl);
}
