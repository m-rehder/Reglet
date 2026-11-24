using System.Text;

namespace Reglet;

/// <summary>
/// PDF files: the header, the end-of-file marker, the cross-references read as the file states them (no repair), every
/// object offset landing on its object header, and a trailer whose /Root is the catalog and whose /Size covers every
/// object. Readers repair such faults silently or with a prompt; Acrobat asks to save the repaired file.
/// </summary>
internal static class PdfValidator
{
    private const string File = "[file]";

    public static bool LooksLikePdf(byte[] data) => IndexOf(data, "%PDF-"u8, 0, Math.Min(data.Length, 1024)) >= 0;

    public static string Validate(byte[] data, IssueList issues, CancellationToken cancellationToken)
    {
        issues.Format = "PDF";
        var header = IndexOf(data, "%PDF-"u8, 0, Math.Min(data.Length, 1024));
        if (header > 0)
        {
            issues.Warning(File, null, $"The header %PDF- starts at byte {header}, not at the start of the file.");
        }

        if (IndexOf(data, "%%EOF"u8, Math.Max(0, data.Length - 1024), data.Length) < 0)
        {
            issues.Error(File, null, "There is no %%EOF marker in the last 1024 bytes; the file may be truncated.");
        }

        IReadOnlyDictionary<int, int> offsets;
        IReadOnlyDictionary<int, (int Container, int Index)> compressed;
        PdfDictionary trailer;
        try
        {
            (offsets, compressed, trailer) = PdfFile.ReadCrossReferencesStrict(data);
        }
        catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
        {
            issues.Error(File, null, "The cross-reference data cannot be read (readers have to rebuild it): " + exception.Message);
            return "PDF";
        }

        foreach (var (number, offset) in offsets.OrderBy(entry => entry.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number == 0)
            {
                continue;
            }

            if (!IsObjectHeader(data, offset, number))
            {
                issues.Error($"object {number}", null, $"The cross-reference offset {offset} does not point to the header '{number} … obj'.");
            }
        }

        foreach (var (number, (container, _)) in compressed.OrderBy(entry => entry.Key))
        {
            if (!offsets.ContainsKey(container))
            {
                issues.Error($"object {number}", null, $"The object stream {container} that holds it has no cross-reference entry.");
            }
        }

        var size = trailer.Get("Size") is PdfInteger integer ? integer.Value : (long?)null;
        var highest = offsets.Keys.Concat(compressed.Keys).DefaultIfEmpty(0).Max();
        if (size is null)
        {
            issues.Error("trailer", null, "The trailer has no /Size.");
        }
        else if (size <= highest)
        {
            issues.Error("trailer", null, $"The trailer's /Size {size} does not cover object {highest}.");
        }

        if (trailer.Get("Root") is null)
        {
            issues.Error("trailer", null, "The trailer has no /Root (document catalog).");
            return "PDF";
        }

        if (trailer.Get("Encrypt") is not null)
        {
            // The catalog of an encrypted file needs the password; its structure was checked above.
            return "PDF";
        }

        try
        {
            var file = PdfFile.Load(data);
            if (file.Name(file.Dictionary(trailer.Get("Root"))?.Get("Type")) != "Catalog")
            {
                issues.Error("trailer", null, "/Root is not a dictionary of /Type /Catalog.");
            }
        }
        catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
        {
            issues.Error("trailer", null, "The document catalog cannot be read: " + exception.Message);
        }

        return "PDF";
    }

    /// <summary>Whether <c>number generation obj</c> starts at the offset (white space before it allowed).</summary>
    private static bool IsObjectHeader(byte[] data, int offset, int number)
    {
        var index = offset;
        while (index < data.Length && IsWhite(data[index]))
        {
            index++;
        }

        var expected = Encoding.ASCII.GetBytes(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (index + expected.Length > data.Length || !data.AsSpan(index, expected.Length).SequenceEqual(expected))
        {
            return false;
        }

        index += expected.Length;
        if (index >= data.Length || !IsWhite(data[index]))
        {
            return false;
        }

        while (index < data.Length && IsWhite(data[index])) index++;
        var digits = index;
        while (index < data.Length && data[index] is >= (byte)'0' and <= (byte)'9') index++;
        if (index == digits)
        {
            return false;
        }

        while (index < data.Length && IsWhite(data[index])) index++;
        return index + 3 <= data.Length && data.AsSpan(index, 3).SequenceEqual("obj"u8);
    }

    private static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern, int start, int end)
    {
        var index = data.AsSpan(start, Math.Max(0, end - start)).IndexOf(pattern);
        return index < 0 ? -1 : start + index;
    }
}
