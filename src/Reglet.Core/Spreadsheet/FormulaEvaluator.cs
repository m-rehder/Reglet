using System.Globalization;

namespace Reglet;

/// <summary>
/// Calculates worksheet formulas in a deliberately small scope (ADR-0046): numbers, cell references with an optional
/// sheet (<c>Sheet2!B3</c>, <c>'My sheet'!$B$3</c>), ranges inside <c>SUM</c> and <c>PRODUCT</c>, <c>+ - * / ^</c>, a
/// sign, <c>%</c> and parentheses, with Excel's precedence (a sign binds tighter than <c>^</c>). Referenced formula
/// cells are calculated in turn; a circle, too deep a chain, a division by zero, a text that is no number and anything
/// outside the scope throw a <see cref="FormulaException"/> instead of giving a wrong number.
/// </summary>
internal sealed class FormulaEvaluator
{
    /// <summary>The longest chain of formula cells referring to each other that is followed.</summary>
    private const int MaxDepth = 500;

    private readonly HashSet<WorksheetCell> _active = [];

    // Each formula cell is calculated once per evaluation, so cells that share inputs cost no more than a chain.
    private readonly Dictionary<WorksheetCell, double> _results = [];

    private FormulaEvaluator()
    {
    }

    public static double Evaluate(WorksheetCell cell) => new FormulaEvaluator().Value(cell);

    /// <summary>The number of a cell: its formula calculated, or its value.</summary>
    private double Value(WorksheetCell cell)
    {
        if (cell.Formula is not { } formula)
        {
            return Number(cell.Value, cell);
        }

        if (_results.TryGetValue(cell, out var known))
        {
            return known;
        }

        if (!_active.Add(cell))
        {
            throw new FormulaException($"{Name(cell)} refers to itself through its formula (a circular reference).");
        }

        if (_active.Count > MaxDepth)
        {
            throw new FormulaException($"The formulas refer to each other more than {MaxDepth} cells deep.");
        }

        ReaderGuard.EnsureStack();
        try
        {
            var result = new Parser(formula, cell.Sheet, this).ParseFormula();
            _results[cell] = result;
            return result;
        }
        finally
        {
            _active.Remove(cell);
        }
    }

    /// <summary>A cell value in arithmetic: empty is 0, a boolean 1 or 0, a date its serial, a text only when it is a number.</summary>
    private static double Number(object? value, WorksheetCell cell) => value switch
    {
        null => 0,
        double number => number,
        bool flag => flag ? 1 : 0,
        DateTime date => date.ToOADate(),
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new FormulaException($"{Name(cell)} holds the text '{Shorten(value.ToString() ?? string.Empty)}', which is no number (#VALUE!)."),
    };

    private static string Name(WorksheetCell cell) => $"{cell.Sheet.Name}!{cell.Reference}";

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;

    /// <summary>
    /// A number, or a reference: a single cell is its number in arithmetic and a one-cell range in SUM and PRODUCT (which
    /// skip text there); a range of several cells only SUM and PRODUCT take.
    /// </summary>
    private readonly record struct Operand(double Number, (Worksheet Sheet, CellRange Range)? Range);

    /// <summary>A recursive-descent parser that calculates while it parses.</summary>
    private sealed class Parser(string formula, Worksheet sheet, FormulaEvaluator evaluator)
    {
        private const int MaxNesting = 200;
        private int _position;
        private int _nesting;

        public double ParseFormula()
        {
            var result = Additive();
            SkipSpaces();
            return _position == formula.Length ? Scalar(result) : throw Error($"unexpected '{formula[_position..Math.Min(formula.Length, _position + 10)]}'");
        }

        private Operand Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                if (Accept('+'))
                {
                    left = new(Scalar(left) + Scalar(Multiplicative()), null);
                }
                else if (Accept('-'))
                {
                    left = new(Scalar(left) - Scalar(Multiplicative()), null);
                }
                else
                {
                    return left;
                }
            }
        }

        private Operand Multiplicative()
        {
            var left = Power();
            while (true)
            {
                if (Accept('*'))
                {
                    left = new(Scalar(left) * Scalar(Power()), null);
                }
                else if (Accept('/'))
                {
                    var divisor = Scalar(Power());
                    left = divisor == 0 ? throw Error("division by zero (#DIV/0!)") : new(Scalar(left) / divisor, null);
                }
                else
                {
                    return left;
                }
            }
        }

        private Operand Power()
        {
            var left = Unary();
            while (Accept('^'))
            {
                var result = Math.Pow(Scalar(left), Scalar(Unary()));
                left = double.IsFinite(result) ? new(result, null) : throw Error("a power without a number result (#NUM!)");
            }

            return left;
        }

        private Operand Unary()
        {
            if (Accept('-'))
            {
                return new(-Scalar(Nested(Unary)), null);
            }

            if (Accept('+'))
            {
                return Nested(Unary);
            }

            var value = Primary();
            while (Accept('%'))
            {
                value = new(Scalar(value) / 100, null);
            }

            return value;
        }

        private Operand Primary()
        {
            SkipSpaces();
            if (_position >= formula.Length)
            {
                throw Error("the formula ends too early");
            }

            var character = formula[_position];
            if (character == '(')
            {
                _position++;
                var inner = Nested(Additive);
                return Accept(')') ? inner : throw Error("a parenthesis is not closed");
            }

            if (char.IsAsciiDigit(character) || character == '.')
            {
                return new(NumberLiteral(), null);
            }

            if (character == '"')
            {
                throw Error("text in a formula is not supported");
            }

            // A function name, or a reference with an optional sheet.
            var start = _position;
            var sheetName = SheetPrefix();
            if (sheetName is null)
            {
                var name = Word();
                SkipSpaces();
                if (name.Length > 0 && _position < formula.Length && formula[_position] == '(')
                {
                    _position++;
                    return Function(name);
                }

                _position = start;
            }

            return Reference(sheetName);
        }

        private Operand Function(string name)
        {
            var upper = name.ToUpperInvariant();
            if (upper is not ("SUM" or "PRODUCT"))
            {
                throw Error($"the function {upper} is not supported (only SUM and PRODUCT)");
            }

            var numbers = new List<double>();
            if (!Accept(')'))
            {
                do
                {
                    var argument = Nested(Additive);
                    if (argument.Range is { } range)
                    {
                        numbers.AddRange(RangeNumbers(range.Sheet, range.Range));
                    }
                    else
                    {
                        numbers.Add(argument.Number);
                    }
                }
                while (Accept(','));

                if (!Accept(')'))
                {
                    throw Error($"{upper}( is not closed");
                }
            }

            // Excel: PRODUCT of no numbers is 0, like SUM.
            return new(upper == "SUM" ? numbers.Sum() : numbers.Count == 0 ? 0 : numbers.Aggregate(1.0, (product, number) => product * number), null);
        }

        /// <summary>The numbers of a range as SUM and PRODUCT take them: text, booleans and empty cells are left out.</summary>
        private IEnumerable<double> RangeNumbers(Worksheet target, CellRange range)
        {
            foreach (var cell in target.Cells.Where(cell => cell.Row >= range.First.Row && cell.Row <= range.Last.Row
                && cell.Column >= range.First.Column && cell.Column <= range.Last.Column).ToList())
            {
                if (cell.Formula is not null)
                {
                    yield return evaluator.Value(cell);
                }
                else if (cell.Value is double or DateTime)
                {
                    yield return Number(cell.Value, cell);
                }
            }
        }

        private Operand Reference(string? sheetName)
        {
            var target = sheetName is null
                ? sheet
                : sheet.Workbook.Worksheets.FirstOrDefault(candidate => string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw Error($"there is no sheet '{sheetName}' (#REF!)");
            var first = CellAddress();
            if (Accept(':'))
            {
                var second = CellAddress();
                var range = new CellRange((Math.Min(first.Row, second.Row), Math.Min(first.Column, second.Column)), (Math.Max(first.Row, second.Row), Math.Max(first.Column, second.Column)));
                return new(0, (target, range));
            }

            return new(0, (target, new CellRange(first, first)));
        }

        private (int Row, int Column) CellAddress()
        {
            SkipSpaces();
            var start = _position;
            while (_position < formula.Length && (char.IsAsciiLetterOrDigit(formula[_position]) || formula[_position] == '$'))
            {
                _position++;
            }

            var text = formula[start.._position];
            return CellReference.TryParse(text.Replace("$", string.Empty, StringComparison.Ordinal), out var row, out var column)
                ? (row, column)
                : throw Error(text.Length == 0 ? "a cell reference is missing" : $"'{text}' is not a cell reference or is not supported (names, whole columns and rows are not)");
        }

        /// <summary>A sheet prefix: <c>Name!</c> or <c>'Quoted name'!</c>; null when there is none.</summary>
        private string? SheetPrefix()
        {
            SkipSpaces();
            var start = _position;
            if (_position < formula.Length && formula[_position] == '\'')
            {
                var name = new System.Text.StringBuilder();
                _position++;
                while (_position < formula.Length)
                {
                    if (formula[_position] == '\'')
                    {
                        if (_position + 1 < formula.Length && formula[_position + 1] == '\'')
                        {
                            name.Append('\'');
                            _position += 2;
                            continue;
                        }

                        _position++;
                        break;
                    }

                    name.Append(formula[_position++]);
                }

                return Accept('!') ? name.ToString() : throw Error("a quoted sheet name is not followed by '!'");
            }

            var word = Word();
            if (word.Length > 0 && _position < formula.Length && formula[_position] == '!')
            {
                _position++;
                return word;
            }

            _position = start;
            return null;
        }

        private string Word()
        {
            var start = _position;
            while (_position < formula.Length && (char.IsLetterOrDigit(formula[_position]) || formula[_position] is '_' or '.'))
            {
                _position++;
            }

            return formula[start.._position];
        }

        private double NumberLiteral()
        {
            var start = _position;
            while (_position < formula.Length && (char.IsAsciiDigit(formula[_position]) || formula[_position] == '.'))
            {
                _position++;
            }

            if (_position < formula.Length && formula[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < formula.Length && formula[_position] is '+' or '-')
                {
                    _position++;
                }

                while (_position < formula.Length && char.IsAsciiDigit(formula[_position]))
                {
                    _position++;
                }
            }

            return double.TryParse(formula.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw Error($"'{formula[start.._position]}' is not a number");
        }

        private double Scalar(Operand operand) => operand.Range switch
        {
            null => operand.Number,
            ({ } target, { } range) when range.First == range.Last
                => target.Find(range.First.Row, range.First.Column) is { } cell ? evaluator.Value(cell) : 0,
            _ => throw Error("a range stands outside SUM or PRODUCT (#VALUE!)"),
        };

        private Operand Nested(Func<Operand> parse)
        {
            if (++_nesting > MaxNesting)
            {
                throw Error($"the formula is nested deeper than {MaxNesting} levels");
            }

            try
            {
                return parse();
            }
            finally
            {
                _nesting--;
            }
        }

        private bool Accept(char expected)
        {
            SkipSpaces();
            if (_position < formula.Length && formula[_position] == expected)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void SkipSpaces()
        {
            while (_position < formula.Length && formula[_position] is ' ' or '\n' or '\r' or '\t')
            {
                _position++;
            }
        }

        private FormulaException Error(string what) => new($"{sheet.Name}: the formula '={Shorten(formula)}' cannot be calculated: {what}.");
    }
}
