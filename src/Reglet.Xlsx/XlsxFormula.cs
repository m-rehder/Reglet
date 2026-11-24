using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Moves the relative A1 references of a formula, as Excel does for the cells of a shared formula: <c>A2*B2</c> one row
/// down is <c>A3*B3</c>; <c>$A$2</c> stays. Text in quotes, quoted sheet names and bracketed parts are left alone.
/// Whole-column and whole-row references (<c>A:C</c>, <c>$1:$3</c>) move along their axis.
/// </summary>
internal static class XlsxFormula
{
    public static string Shift(string formula, int rows, int columns)
    {
        var builder = new StringBuilder(formula.Length + 8);
        var index = 0;
        while (index < formula.Length)
        {
            var character = formula[index];
            if (character is '"' or '\'' or '[')
            {
                var close = character == '[' ? ']' : character;
                var end = index + 1;
                while (end < formula.Length && !(formula[end] == close && (close == ']' || end + 1 >= formula.Length || formula[end + 1] != close)))
                {
                    end += formula[end] == close && close != ']' ? 2 : 1;
                }

                end = Math.Min(end + 1, formula.Length);
                builder.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if ((index == 0 || !IsNameCharacter(formula[index - 1])) && TryLineRange(formula, index, rows, columns, out var lineLength, out var moved))
            {
                builder.Append(moved);
                index += lineLength;
                continue;
            }

            if ((index == 0 || !IsNameCharacter(formula[index - 1])) && TryReference(formula, index, out var length, out var reference))
            {
                builder.Append(Move(reference, rows, columns));
                index += length;
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>A whole-column (<c>A:C</c>, <c>$A:$A</c>) or whole-row (<c>1:3</c>) reference, moved.</summary>
    private static bool TryLineRange(string formula, int start, int rows, int columns, out int length, out string moved)
    {
        length = 0;
        moved = string.Empty;
        if (!TryLine(formula, start, out var firstEnd, out var firstAbsolute, out var first, out var isColumn)
            || firstEnd >= formula.Length || formula[firstEnd] != ':'
            || !TryLine(formula, firstEnd + 1, out var end, out var lastAbsolute, out var last, out var lastIsColumn)
            || lastIsColumn != isColumn
            || (end < formula.Length && (IsNameCharacter(formula[end]) || formula[end] is '(' or '!')))
        {
            return false;
        }

        var offset = isColumn ? columns : rows;
        var limit = isColumn ? CellReference.MaxColumn : CellReference.MaxRow;
        var from = firstAbsolute ? first : first + offset;
        var to = lastAbsolute ? last : last + offset;
        length = end - start;
        if (from < 1 || to < 1 || from > limit || to > limit)
        {
            moved = "#REF!";
            return true;
        }

        string Text(bool absolute, int value) => (absolute ? "$" : string.Empty)
            + (isColumn ? CellReference.ColumnName(value) : value.ToString(CultureInfo.InvariantCulture));
        moved = Text(firstAbsolute, from) + ":" + Text(lastAbsolute, to);
        return true;
    }

    /// <summary>One side of a line range: <c>$?letters</c> (a column) or <c>$?digits</c> (a row), nothing else.</summary>
    private static bool TryLine(string formula, int start, out int end, out bool absolute, out int value, out bool isColumn)
    {
        end = start;
        value = 0;
        absolute = end < formula.Length && formula[end] == '$';
        if (absolute) end++;
        var begin = end;
        isColumn = end < formula.Length && char.IsAsciiLetter(formula[end]);
        while (end < formula.Length && (isColumn ? char.IsAsciiLetter(formula[end]) : char.IsAsciiDigit(formula[end])) && end - begin < 7)
        {
            value = isColumn ? (value * 26) + (char.ToUpperInvariant(formula[end]) - 'A' + 1) : (value * 10) + (formula[end] - '0');
            end++;
        }

        var limit = isColumn ? CellReference.MaxColumn : CellReference.MaxRow;
        return end > begin && (!isColumn || end - begin <= 3) && value is >= 1 && value <= limit
            && (end >= formula.Length || !char.IsAsciiLetterOrDigit(formula[end]));
    }

    private static bool IsNameCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '$';

    private readonly record struct Reference(bool AbsoluteColumn, int Column, bool AbsoluteRow, int Row);

    private static bool TryReference(string formula, int start, out int length, out Reference reference)
    {
        length = 0;
        reference = default;
        var index = start;
        var absoluteColumn = index < formula.Length && formula[index] == '$';
        if (absoluteColumn) index++;
        var letters = index;
        while (index < formula.Length && char.IsAsciiLetter(formula[index]) && index - letters < 3) index++;
        if (index == letters) return false;
        var columnText = formula[letters..index];
        var absoluteRow = index < formula.Length && formula[index] == '$';
        if (absoluteRow) index++;
        var digits = index;
        while (index < formula.Length && char.IsAsciiDigit(formula[index])) index++;
        if (index == digits || (index < formula.Length && (IsNameCharacter(formula[index]) || formula[index] is '(' or '!')))
        {
            return false;
        }

        var column = 0;
        foreach (var letter in columnText)
        {
            column = (column * 26) + (char.ToUpperInvariant(letter) - 'A' + 1);
        }

        if (!int.TryParse(formula.AsSpan(digits, index - digits), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || column > CellReference.MaxColumn || row is < 1 or > CellReference.MaxRow)
        {
            return false;
        }

        length = index - start;
        reference = new Reference(absoluteColumn, column, absoluteRow, row);
        return true;
    }

    private static string Move(Reference reference, int rows, int columns)
    {
        var column = reference.AbsoluteColumn ? reference.Column : reference.Column + columns;
        var row = reference.AbsoluteRow ? reference.Row : reference.Row + rows;
        if (column is < 1 or > CellReference.MaxColumn || row is < 1 or > CellReference.MaxRow)
        {
            return "#REF!";
        }

        return (reference.AbsoluteColumn ? "$" : string.Empty) + CellReference.ColumnName(column)
            + (reference.AbsoluteRow ? "$" : string.Empty) + row.ToString(CultureInfo.InvariantCulture);
    }
}
