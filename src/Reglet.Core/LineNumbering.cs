namespace Reglet;

/// <summary>describes when the line numbering restarts.</summary>
public enum LineNumberRestart
{
    /// <summary>The numbering continues across pages and sections.</summary>
    Continuous,

    /// <summary>The numbering restarts on every page (the default, as in Word).</summary>
    NewPage,

    /// <summary>The numbering restarts with every section.</summary>
    NewSection,
}

/// <summary>
/// the line numbering of a section: the numbers are placed in the margin next to the text
/// lines (phase D). DOCX writes <c>w:lnNumType</c>, ODT the document-wide
/// <c>text:linenumbering-configuration</c>, the PDF draws the numbers in the left margin.
/// </summary>
public sealed record LineNumbering
{
    private int _countBy = 1;
    private int _start = 1;
    private Length _distance = Length.FromPoints(12);

    /// <summary>Every n-th line receives a number; 1 numbers every line.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is smaller than 1.</exception>
    public int CountBy
    {
        get => _countBy;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _countBy = value;
        }
    }

    /// <summary>The number of the first line.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Start
    {
        get => _start;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _start = value;
        }
    }

    /// <summary>The distance between the text and the numbers (default 12 pt).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The distance is negative.</exception>
    public Length Distance
    {
        get => _distance;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Points);
            _distance = value;
        }
    }

    /// <summary>When the numbering restarts (default: on every page).</summary>
    public LineNumberRestart Restart { get; init; } = LineNumberRestart.NewPage;
}
