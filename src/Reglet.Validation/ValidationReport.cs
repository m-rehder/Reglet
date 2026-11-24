namespace Reglet;

/// <summary>How serious a validation finding is.</summary>
public enum ValidationSeverity
{
    /// <summary>The file opens, but something may be lost or need a newer program (for example Office 2010 or later).</summary>
    Warning,

    /// <summary>A program may refuse the file or offer to repair it.</summary>
    Error,
}

/// <summary>One finding of the <see cref="DocumentValidator"/>.</summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Part">Where it is: a package part (<c>word/document.xml</c>), <c>[package]</c>, or a PDF object.</param>
/// <param name="Line">The line in the part, when it is XML and known.</param>
/// <param name="Message">What is wrong.</param>
public sealed record ValidationIssue(ValidationSeverity Severity, string Part, int? Line, string Message)
{
    /// <summary>The finding as one line, such as <c>Error word/document.xml:12: …</c>.</summary>
    public override string ToString() => $"{Severity} {Part}{(Line is { } line ? ":" + line : string.Empty)}: {Message}";
}

/// <summary>The result of a validation (ADR-0044).</summary>
public sealed record ValidationReport
{
    /// <summary>Creates a report.</summary>
    public ValidationReport(string format, IEnumerable<ValidationIssue> issues, bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(format);
        Format = format;
        Issues = ModelCollections.Snapshot(issues, nameof(issues));
        Truncated = truncated;
    }

    /// <summary>The detected format: DOCX, XLSX, PPTX, OOXML, ODT, ODS, ODP, ODG, ODF or PDF.</summary>
    public string Format { get; }

    /// <summary>The findings, in the order they were found.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    /// <summary>Whether the validator stopped collecting at its limit (more findings exist).</summary>
    public bool Truncated { get; }

    /// <summary>Whether there is no error (warnings allowed).</summary>
    public bool IsValid => !Issues.Any(issue => issue.Severity == ValidationSeverity.Error);

    /// <summary>The errors only.</summary>
    public IEnumerable<ValidationIssue> Errors => Issues.Where(issue => issue.Severity == ValidationSeverity.Error);
}
