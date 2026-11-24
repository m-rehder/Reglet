using System.IO.Compression;

namespace Reglet;

/// <summary>
/// Checks a document file for what Word, Excel, LibreOffice or a PDF reader would reject or offer to repair (ADR-0044).
/// The format is recognised from the content: DOCX, XLSX and other Office Open XML packages (package structure and the
/// ECMA-376 Transitional schemas, with markup compatibility as Office 2019 understands it), ODF packages (mimetype,
/// manifest, well-formed XML) and PDF (header, cross-references, trailer and catalog).
/// </summary>
public sealed class DocumentValidator
{
    /// <summary>The most findings one report collects.</summary>
    public const int MaxIssues = 1000;

    /// <summary>Validates the file in the stream; the stream stays open.</summary>
    /// <exception cref="DocumentValidationException">The input exceeds the reader limits.</exception>
    public ValidationReport Validate(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var data = ReaderGuard.ReadAllBounded(input, "The document");
        var issues = new IssueList();
        string format;
        try
        {
            format = data switch
            {
                [0xD0, 0xCF, 0x11, 0xE0, ..] => Encrypted(issues),
                [(byte)'P', (byte)'K', ..] => ValidatePackage(data, issues, cancellationToken),
                _ when PdfValidator.LooksLikePdf(data) => PdfValidator.Validate(data, issues, cancellationToken),
                _ => Unknown(issues),
            };
        }
        catch (IssueList.FullException)
        {
            format = issues.Format ?? "unknown";
        }

        return new ValidationReport(issues.Format ?? format, issues.Items, issues.Truncated);
    }

    private static string ValidatePackage(byte[] data, IssueList issues, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException exception)
        {
            issues.Error("[package]", null, "The ZIP container cannot be read: " + exception.Message);
            return "ZIP";
        }

        using (zip)
        {
            if (zip.GetEntry("[Content_Types].xml") is not null)
            {
                return OoxmlValidator.Validate(zip, issues, cancellationToken);
            }

            if (zip.GetEntry("mimetype") is not null || zip.GetEntry("META-INF/manifest.xml") is not null)
            {
                return OdfValidator.Validate(data, zip, issues, cancellationToken);
            }

            issues.Error("[package]", null, "The ZIP container is neither an Office Open XML package ([Content_Types].xml) nor an ODF package (mimetype).");
            return "ZIP";
        }
    }

    private static string Encrypted(IssueList issues)
    {
        issues.Warning("[package]", null, "The file is an encrypted Office document (compound file); its content cannot be validated without decrypting it.");
        return "OOXML";
    }

    private static string Unknown(IssueList issues)
    {
        issues.Error("[package]", null, "The file is neither a ZIP package nor a PDF.");
        return "unknown";
    }
}

/// <summary>Collects findings up to <see cref="DocumentValidator.MaxIssues"/>, then stops the validation.</summary>
internal sealed class IssueList
{
    private readonly List<ValidationIssue> _items = [];

    public IReadOnlyList<ValidationIssue> Items => _items;

    public bool Truncated { get; private set; }

    /// <summary>The format, once known (kept when the list fills up before the validator returns).</summary>
    public string? Format { get; set; }

    public void Error(string part, int? line, string message) => Add(new(ValidationSeverity.Error, part, line, message));

    public void Warning(string part, int? line, string message) => Add(new(ValidationSeverity.Warning, part, line, message));

    private void Add(ValidationIssue issue)
    {
        if (_items.Count >= DocumentValidator.MaxIssues)
        {
            Truncated = true;
            throw new FullException();
        }

        _items.Add(issue);
    }

    internal sealed class FullException : Exception
    {
    }
}
