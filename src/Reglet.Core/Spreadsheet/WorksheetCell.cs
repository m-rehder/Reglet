using System.Globalization;

namespace Reglet;

/// <summary>A cell of a worksheet: a value or a formula, and a style.</summary>
public sealed class WorksheetCell
{
    /// <summary>Excel's limit of characters in a cell.</summary>
    private const int MaxText = 32_767;

    private object? _value;
    private string? _formula;

    internal WorksheetCell(int row, int column, Worksheet sheet)
    {
        Row = row;
        Column = column;
        Sheet = sheet;
    }

    /// <summary>The sheet the cell belongs to.</summary>
    internal Worksheet Sheet { get; }

    /// <summary>
    /// Calculates the cell (ADR-0046): the formula when there is one, otherwise the value as a number. The formula may
    /// use numbers, cell references (also <c>Sheet2!B3</c> and <c>'My sheet'!$B$3</c>), <c>SUM</c> and <c>PRODUCT</c>
    /// over references and ranges, <c>+ - * / ^</c>, a sign, <c>%</c> and parentheses; referenced formula cells are
    /// calculated too, an empty cell counts as 0. Meant for checking a workbook in tests: Excel and LibreOffice calculate
    /// the file themselves when they open it.
    /// </summary>
    /// <exception cref="FormulaException">The formula uses something outside that scope, refers in a circle, divides
    /// by zero or uses a text that is no number.</exception>
    public double Evaluate() => FormulaEvaluator.Evaluate(this);

    /// <summary>The 1-based row.</summary>
    public int Row { get; }

    /// <summary>The 1-based column.</summary>
    public int Column { get; }

    /// <summary>The A1 reference, such as <c>B3</c>.</summary>
    public string Reference => CellReference.Format(Row, Column);

    /// <summary>
    /// The value: null, a <see cref="string"/>, <see cref="RichText"/>, a number (stored as <see cref="double"/>),
    /// a <see cref="DateTime"/> (a <see cref="DateOnly"/> becomes one) or a <see cref="bool"/>. Setting a value clears
    /// the formula. A read formula cell carries the value Excel cached, if any.
    /// </summary>
    /// <exception cref="ArgumentException">The value has another type, a number is not finite or a text is longer than Excel's 32,767 characters.</exception>
    public object? Value
    {
        get => _value;
        set
        {
            _value = Normalize(value, checkLength: true);
            _formula = null;
        }
    }

    /// <summary>
    /// A formula without the leading <c>=</c>, such as <c>SUM(A1:A3)</c>; null means none. It is written without a cached
    /// value and the workbook asks Excel to calculate on load. Setting a formula clears the value.
    /// </summary>
    public string? Formula
    {
        get => _formula;
        set
        {
            _formula = value is null ? null : value.StartsWith('=') ? value[1..] : value;
            _value = null;
        }
    }

    /// <summary>The style; <see cref="CellStyle.Default"/> means none.</summary>
    public CellStyle Style
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = CellStyle.Default;

    /// <summary>
    /// The value as Excel shows it with the cell's number format (Excel's TEXT of the value), in
    /// <paramref name="culture"/> (default: the current culture). Rich text gives its plain text; a formula without a
    /// cached value gives an empty string.
    /// </summary>
    public string GetFormattedText(CultureInfo? culture = null)
        => CellFormatter.Format(_value, Style.NumberFormat, culture ?? CultureInfo.CurrentCulture);

    /// <summary>Sets the formula and the value Excel cached for it (used by the reader).</summary>
    internal void SetFormulaResult(string formula, object? cached)
    {
        _formula = formula;
        _value = Normalize(cached, checkLength: false);
    }

    /// <summary>Sets a value read from a file, also a text longer than Excel's limit (used by the reader).</summary>
    internal void SetReadValue(object? value)
    {
        _value = Normalize(value, checkLength: false);
        _formula = null;
    }

    /// <summary>Whether the value is a text longer than Excel's 32,767 characters (possible only for read cells).</summary>
    internal bool IsTooLong => _value is string text && text.Length > MaxText || _value is RichText rich && rich.ToString().Length > MaxText;

    private static object? Normalize(object? value, bool checkLength) => value switch
    {
        string text when checkLength && text.Length > MaxText => throw new ArgumentException($"A cell holds at most {MaxText} characters.", nameof(value)),
        RichText rich when checkLength && rich.ToString().Length > MaxText => throw new ArgumentException($"A cell holds at most {MaxText} characters.", nameof(value)),
        null or string or RichText or bool or DateTime => value,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        double number => double.IsFinite(number) ? number : throw new ArgumentException("A cell number must be finite.", nameof(value)),
        float number => Normalize((double)number, checkLength),
        int or long or short or byte or sbyte or uint or ulong or ushort => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        decimal number => (double)number,
        _ => throw new ArgumentException($"A cell cannot hold a {value.GetType().Name}.", nameof(value)),
    };
}
