namespace Reglet;

/// <summary>
/// validates internal package paths (ZIP entry names). Absolute paths, drive specifiers and
/// parent traversal (<c>..</c>) are rejected so that a package cannot escape the target tree
/// can escape.
/// </summary>
internal static class PackagePath
{
    public static string Validate(string partName)
    {
        ArgumentException.ThrowIfNullOrEmpty(partName);

        if (partName[0] is '/' or '\\' || partName.Contains(':', StringComparison.Ordinal))
        {
            throw new DocumentValidationException(
                $"The package path '{partName}' must not be absolute.", nameof(partName));
        }

        // backslashes are read as separators when unzipping under Windows and would
        // bypass the segment check (e.g. "a\..\..\b").
        if (partName.Contains('\\', StringComparison.Ordinal))
        {
            throw new DocumentValidationException(
                $"The package path '{partName}' must not contain backslashes.", nameof(partName));
        }

        foreach (var segment in partName.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new DocumentValidationException(
                    $"The package path '{partName}' contains invalid segments.", nameof(partName));
            }
        }

        return partName;
    }
}
