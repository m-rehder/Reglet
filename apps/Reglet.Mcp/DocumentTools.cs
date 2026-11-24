using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Reglet.Mcp;

/// <summary>The tools of the Reglet MCP server (ADR-0045). Every path is checked by <see cref="PathPolicy"/>.</summary>
[McpServerToolType]
public sealed class DocumentTools(PathPolicy paths)
{
    private const int DefaultMaxCharacters = 20_000;
    private const int MaxCharactersLimit = 1_000_000;
    private const int MaxColumns = 200;
    private const int MaxMergeInputs = 200;
    private const long MaxInputBytes = 512L * 1024 * 1024;

    /// <summary>The <c>create_document</c> tool; see its description.</summary>
    [McpServerTool(Name = "create_document", OpenWorld = false), Description("Creates a DOCX, ODT, PDF or HTML file (by the extension of output_path) from Markdown: headings, paragraphs with **bold**, *italic* and `code`, bullet and numbered lists, pipe tables with a header row, and --- for a page break.")]
    public async Task<string> CreateDocumentAsync(
        [Description("The file to write, ending in .docx, .odt, .pdf or .html.")] string output_path,
        [Description("The content as Markdown.")] string markdown,
        [Description("The document title (metadata).")] string? title = null,
        [Description("Replace the file if it exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var path = paths.Writable(output_path, overwrite);
        var document = MarkdownDocument.Parse(markdown ?? string.Empty, title);
        var format = Guard(() => DocumentWriter.FormatFromPath(path));
        var result = await WriteFile(path, stream => DocumentWriter.WriteAsync(document, stream, format, cancellationToken: cancellationToken));
        return Written(path, result);
    }

    /// <summary>The <c>convert_document</c> tool; see its description.</summary>
    [McpServerTool(Name = "convert_document", OpenWorld = false), Description("Converts a document between DOCX, ODT, PDF and HTML; the formats come from the file extensions. Reading keeps the content (text, formatting, tables, lists, pictures), not every layout detail.")]
    public async Task<string> ConvertDocumentAsync(
        [Description("The file to read (.docx, .odt, .pdf or .html).")] string input_path,
        [Description("The file to write (.docx, .odt, .pdf or .html).")] string output_path,
        [Description("Replace the output file if it exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var input = paths.Existing(input_path);
        var output = paths.Writable(output_path, overwrite);
        var read = Guard(() => DocumentReader.Read(input, cancellationToken));
        var format = Guard(() => DocumentWriter.FormatFromPath(output));
        var result = await WriteFile(output, stream => DocumentWriter.WriteAsync(read.Document, stream, format, cancellationToken: cancellationToken));
        var notes = read.Warnings.Select(warning => "read: " + warning.Message).Concat(result.Warnings.Select(warning => "write: " + warning.Message)).ToList();
        return $"Wrote {output}." + Notes(notes);
    }

    /// <summary>The <c>read_document</c> tool; see its description.</summary>
    [McpServerTool(Name = "read_document", ReadOnly = true, OpenWorld = false), Description("Reads a DOCX, ODT, PDF or HTML file and returns its content as Markdown-like text: headings, paragraphs, lists and tables.")]
    public string ReadDocument(
        [Description("The file to read (.docx, .odt, .pdf or .html).")] string path,
        [Description("The most characters to return (default 20000).")] int max_characters = DefaultMaxCharacters,
        CancellationToken cancellationToken = default)
    {
        var input = paths.Existing(path);
        var read = Guard(() => DocumentReader.Read(input, cancellationToken));
        return DocumentText.Of(read.Document, Math.Clamp(max_characters, 100, MaxCharactersLimit));
    }

    /// <summary>The <c>fill_template</c> tool; see its description.</summary>
    [McpServerTool(Name = "fill_template", OpenWorld = false), Description("Fills the {{Name}} placeholders of a DOCX or ODT template with text and writes the result in the same format; styles and layout of the template stay.")]
    public async Task<string> FillTemplateAsync(
        [Description("The template (.docx or .odt) with placeholders such as {{CustomerName}}.")] string template_path,
        [Description("The file to write; same format as the template.")] string output_path,
        [Description("Placeholder names (without braces) and their text; a name must not be blank or contain braces.")] Dictionary<string, string> values,
        [Description("Replace the output file if it exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var template = paths.Existing(template_path);
        var output = paths.Writable(output_path, overwrite);
        var format = Guard(() => DocumentWriter.FormatFromPath(template));
        if (format is not (DocumentFormat.Docx or DocumentFormat.Odt) || DocumentWriter.FormatFromPath(output) != format)
        {
            throw new McpException("A template is a .docx or .odt file, and the output has the same format.");
        }

        if (new FileInfo(template).Length > MaxInputBytes)
        {
            throw new McpException($"The template is larger than {MaxInputBytes / (1024 * 1024)} MB.");
        }

        var bound = Guard(() => DocumentTemplate.FromBytes(File.ReadAllBytes(template)).Bind((values ?? []).ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal)));
        WriteOptions options = format == DocumentFormat.Docx ? new DocxWriteOptions { Template = bound } : new OdtWriteOptions { Template = bound };
        var empty = new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>()));
        var result = await WriteFile(output, stream => DocumentWriter.WriteAsync(empty, stream, format, options, cancellationToken));
        return Written(output, result);
    }

    /// <summary>The <c>validate_file</c> tool; see its description.</summary>
    [McpServerTool(Name = "validate_file", ReadOnly = true, OpenWorld = false), Description("Checks a DOCX, XLSX, ODT/ODS or PDF file for what Word, Excel, LibreOffice or a PDF reader would reject: package structure, the ECMA-376 and ODF schemas, PDF cross-references, page tree, content and fonts. Returns each finding with its part and line.")]
    public string ValidateFile(
        [Description("The file to check.")] string path,
        CancellationToken cancellationToken = default)
    {
        var input = paths.Existing(path);
        using var stream = File.OpenRead(input);
        var report = Guard(() => new DocumentValidator().Validate(stream, cancellationToken));
        var text = new StringBuilder($"{report.Format}: {(report.IsValid ? "valid" : "not valid")}, {report.Errors.Count()} error(s), {report.Issues.Count - report.Errors.Count()} warning(s).");
        foreach (var issue in report.Issues)
        {
            text.Append('\n').Append(issue);
        }

        if (report.Truncated)
        {
            text.Append("\n(more findings were not collected)");
        }

        return text.ToString();
    }

    /// <summary>The <c>read_workbook</c> tool; see its description.</summary>
    [McpServerTool(Name = "read_workbook", ReadOnly = true, OpenWorld = false), Description("Reads an Excel workbook (.xlsx) and returns each sheet as tab-separated rows of the values as Excel shows them; formulas are shown as =FORMULA when the file has no calculated value.")]
    public string ReadWorkbook(
        [Description("The workbook to read (.xlsx).")] string path,
        [Description("The most rows to return per sheet (default 200).")] int max_rows = 200,
        CancellationToken cancellationToken = default)
    {
        var input = paths.Existing(path);
        using var stream = File.OpenRead(input);
        var workbook = Guard(() => new XlsxWorkbookReader().Read(stream, cancellationToken));
        var limit = Math.Clamp(max_rows, 1, 100_000);
        var text = new StringBuilder();
        foreach (var sheet in workbook.Worksheets)
        {
            text.Append("## ").AppendLine(sheet.Name);
            var rows = sheet.Cells.GroupBy(cell => cell.Row).OrderBy(group => group.Key).ToList();
            var cut = false;
            foreach (var row in rows.Take(limit))
            {
                var cells = row.ToDictionary(cell => cell.Column);
                var last = cells.Keys.Max();
                if (last > MaxColumns)
                {
                    cut = true;
                    last = MaxColumns;
                }

                var values = Enumerable.Range(1, last).Select(column => cells.TryGetValue(column, out var cell) ? Shown(cell) : string.Empty);
                text.Append(row.Key.ToString(CultureInfo.InvariantCulture)).Append('\t').AppendLine(string.Join('\t', values));
            }

            if (rows.Count > limit)
            {
                text.AppendLine($"[… {rows.Count - limit} more rows]");
            }

            if (cut)
            {
                text.AppendLine($"[columns after {MaxColumns} are not shown]");
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();

        static string Shown(WorksheetCell cell)
            => (cell.Formula is { } formula && cell.Value is null ? "=" + formula : cell.GetFormattedText(CultureInfo.InvariantCulture))
                .Replace('\t', ' ').Replace('\n', ' ');
    }

    /// <summary>The <c>create_workbook</c> tool; see its description.</summary>
    [McpServerTool(Name = "create_workbook", OpenWorld = false), Description("Creates an Excel workbook (.xlsx). Each sheet is a name and rows of cells; a cell that parses as a number (invariant culture, e.g. 12.5) becomes a number, one starting with = becomes a formula (calculated when Excel opens the file), anything else text. The first row can be a bold header.")]
    public async Task<string> CreateWorkbookAsync(
        [Description("The file to write (.xlsx).")] string output_path,
        [Description("The sheets, in order.")] List<SheetInput> sheets,
        [Description("Make the first row of each sheet a bold, shaded header.")] bool header = true,
        [Description("Replace the file if it exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var path = paths.Writable(output_path, overwrite);
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("A workbook is written as .xlsx.");
        }

        if (sheets is not { Count: > 0 })
        {
            throw new McpException("A workbook needs at least one sheet.");
        }

        var workbook = new Workbook();
        foreach (var input in sheets)
        {
            var sheet = Guard(() => workbook.AddWorksheet(input.Name ?? string.Empty));
            var rows = input.Rows ?? [];
            for (var row = 0; row < rows.Count; row++)
            {
                var cells = rows[row] ?? [];
                for (var column = 0; column < cells.Count; column++)
                {
                    var cell = sheet.Cell(row + 1, column + 1);
                    var value = cells[column] ?? string.Empty;
                    if (value.StartsWith('=') && value.Length > 1)
                    {
                        cell.Formula = value;
                    }
                    else if (IsNumber(value, out var number))
                    {
                        cell.Value = number;
                    }
                    else if (value.Length > 0)
                    {
                        Guard(() => cell.Value = value);
                    }
                }
            }

            var columns = rows.Count == 0 ? 0 : rows.Max(cells => cells?.Count ?? 0);
            for (var column = 1; column <= columns; column++)
            {
                var widest = rows.Select(cells => cells is not null && cells.Count >= column ? (cells[column - 1] ?? string.Empty).Length : 0).Max();
                sheet.ColumnWidths[column] = Math.Clamp(widest + 2, 8, 60);
            }

            if (header && rows.Count > 0 && columns > 0)
            {
                sheet.SetStyle(new CellRange((1, 1), (1, columns)).ToString(), style => style with
                {
                    Font = style.Font with { Bold = true },
                    Fill = Color.FromHex("D9D9D9"),
                });
                sheet.PageSetup = sheet.PageSetup with { RepeatRows = (1, 1), FitToPagesWide = 1, FitToPagesTall = 0 };
            }
        }

        var result = await WriteFile(path, stream => new XlsxWorkbookWriter().WriteAsync(workbook, stream, cancellationToken: cancellationToken));
        return Written(path, result);
    }

    /// <summary>The <c>merge_pdfs</c> tool; see its description.</summary>
    [McpServerTool(Name = "merge_pdfs", OpenWorld = false), Description("Merges the pages of several PDF files into one, in the given order.")]
    public string MergePdfs(
        [Description("The PDF files to merge, in order.")] List<string> input_paths,
        [Description("The PDF to write.")] string output_path,
        [Description("Replace the output file if it exists.")] bool overwrite = false)
    {
        if (input_paths is not { Count: > 0 and <= MaxMergeInputs })
        {
            throw new McpException($"Merge 1 to {MaxMergeInputs} PDFs.");
        }

        var inputs = input_paths.Select(paths.Existing).ToList();
        var output = paths.Writable(output_path, overwrite);
        var streams = inputs.Select(File.OpenRead).ToList();
        try
        {
            WriteFile(output, destination =>
            {
                PdfPageOperations.Merge(streams, destination);
                return Task.FromResult(WriteResult.Empty);
            }).GetAwaiter().GetResult();
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }

        return $"Wrote {output}.";
    }

    /// <summary>The <c>extract_pdf_pages</c> tool; see its description.</summary>
    [McpServerTool(Name = "extract_pdf_pages", OpenWorld = false), Description("Writes chosen pages of a PDF into a new PDF, e.g. \"1-3,5\" (1-based, in the given order).")]
    public string ExtractPdfPages(
        [Description("The PDF to read.")] string input_path,
        [Description("The pages, such as \"2\", \"1-3\" or \"1-3,7,5\".")] string pages,
        [Description("The PDF to write.")] string output_path,
        [Description("Replace the output file if it exists.")] bool overwrite = false)
    {
        var input = paths.Existing(input_path);
        var output = paths.Writable(output_path, overwrite);
        var list = PageList(pages);
        using var source = File.OpenRead(input);
        WriteFile(output, destination =>
        {
            PdfPageOperations.Extract(source, list, destination);
            return Task.FromResult(WriteResult.Empty);
        }).GetAwaiter().GetResult();
        return $"Wrote {output} ({list.Count} page(s)).";
    }

    /// <summary>Parses "1-3,5" into 1-based page numbers (at most 10,000).</summary>
    internal static List<int> PageList(string? pages)
    {
        var list = new List<int>();
        foreach (var part in (pages ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is 1 or 2
                && int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(range[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                && from >= 1 && to >= from && list.Count + (to - from + 1) <= 10_000)
            {
                list.AddRange(Enumerable.Range(from, to - from + 1));
            }
            else
            {
                throw new McpException($"'{part}' is not a page or page range (1-based, such as 2 or 1-3; at most 10,000 pages).");
            }
        }

        return list.Count > 0 ? list : throw new McpException("No pages were given.");
    }

    /// <summary>
    /// Writes a file through a temporary file in the same folder and moves it into place only when the write succeeded,
    /// so a failed write never leaves a half file or destroys the file it was to replace.
    /// </summary>
    private static async Task<WriteResult> WriteFile(string path, Func<Stream, Task<WriteResult>> write)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            WriteResult result;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                result = await Guard(() => write(stream));
            }

            File.Move(temporary, path, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>A plain number in the invariant culture; leading zeros and a leading + keep a value text (codes, phone numbers).</summary>
    internal static bool IsNumber(string value, out double number)
    {
        number = 0;
        var trimmed = value.Trim();
        var digits = trimmed.TrimStart('-');
        if (trimmed.Length == 0 || trimmed.StartsWith('+') || (digits.Length > 1 && digits[0] == '0' && digits[1] != '.'))
        {
            return false;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    }

    private static string Written(string path, WriteResult result)
        => $"Wrote {path}." + Notes(result.Warnings.Select(warning => warning.Message).ToList());

    private static string Notes(IReadOnlyList<string> notes)
        => notes.Count == 0 ? string.Empty : "\nNotes:\n" + string.Join('\n', notes.Take(50).Select(note => "- " + note)) + (notes.Count > 50 ? $"\n- … {notes.Count - 50} more" : string.Empty);

    /// <summary>Turns Reglet's errors about the input into tool errors the agent can read; other faults stay faults.</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is DocumentValidationException or UnsupportedFeatureException or ArgumentException or ImageException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(exception.Message, exception);
        }
    }

    private static void Guard(Action action) => Guard(() =>
    {
        action();
        return 0;
    });

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is DocumentValidationException or UnsupportedFeatureException or ArgumentException or ImageException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}

/// <summary>One sheet of <c>create_workbook</c>.</summary>
public sealed class SheetInput
{
    /// <summary>The sheet name: 1–31 characters, none of [ ] : * ? / \.</summary>
    [Description("The sheet name: 1-31 characters, none of [ ] : * ? / \\.")]
    public string? Name { get; set; }

    /// <summary>The rows, each a list of cell values as text.</summary>
    [Description("The rows, each a list of cell values as text.")]
    public List<List<string?>>? Rows { get; set; }
}
