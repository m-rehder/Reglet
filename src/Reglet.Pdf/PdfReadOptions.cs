namespace Reglet;

/// <summary>Options for reading PDF files (phase AD).</summary>
public sealed record PdfReadOptions
{
    /// <summary>Default options (empty password).</summary>
    public static PdfReadOptions Default { get; } = new();

    /// <summary>
    /// User or owner password of an encrypted PDF. If it is missing, the empty password is
    /// tried; an encrypted PDF with a different password then raises
    /// <see cref="DocumentValidationException"/>.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Recovers headings, lists, tables, repeated headers/footers and indented paragraphs from the text positions
    /// (ADR-0033). Default false: the reader returns paragraphs of joined lines only.
    /// </summary>
    public bool RecoverStructure { get; init; }

    /// <summary>
    /// With <see cref="RecoverStructure"/>: turns display formulas (fractions, roots, scripts, sums, integrals) into
    /// <see cref="Equation"/> blocks (ADR-0034). A guess from positions and rules; default false.
    /// </summary>
    public bool RecoverFormulas { get; init; }
}
