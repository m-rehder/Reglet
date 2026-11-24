using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// Moves the relative A1 references of a formula, as Excel does for the cells of a shared formula: <c>A2*B2</c> one row
/// down is <c>A3*B3</c>; <c>$A$2</c> stays. Text in quotes, quoted sheet names and bracketed parts are left alone.
/// shortcut: whole-column and whole-row references (<c>A:A</c>, <c>1:3</c>) are not moved; add it when a workbook needs it.
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
