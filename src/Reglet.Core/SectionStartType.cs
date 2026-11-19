namespace Reglet;

/// <summary>How a section starts (the break before the section).</summary>
public enum SectionStartType
{
    /// <summary>The section starts on the next page (the default).</summary>
    NextPage = 0,

    /// <summary>The section continues on the same page (no page break).</summary>
    Continuous = 1,

    /// <summary>The section starts on the next odd page (a blank page is inserted if needed).</summary>
    OddPage = 2,

    /// <summary>The section starts on the next even page (a blank page is inserted if needed).</summary>
    EvenPage = 3,
}
