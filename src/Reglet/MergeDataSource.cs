using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// data sources of a mail merge (phase AJ, AJ03): CSV, XML, JSON and Excel (xlsx). The rows
/// are returned as dictionaries of name and value; the first row of a CSV or a spreadsheet
/// holds the names.
/// </summary>
public static class MergeDataSource
{
    /// <summary>
    /// reads the rows of a CSV file (comma separated, RFC 4180: a quoted field may contain the
    /// separator and line breaks). The first line holds the names of the fields.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidDataException">A line has more fields than the header.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromCsv(TextReader reader, char separator = ',')
    {
        ArgumentNullException.ThrowIfNull(reader);

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        string[]? header = null;
        foreach (var fields in ReadCsvRows(reader, separator))
        {
            if (header is null)
            {
                header = fields;
                continue;
            }

            if (fields.Length == 1 && fields[0].Length == 0)
            {
                continue; // an empty line
            }

            if (fields.Length > header.Length)
            {
                throw new InvalidDataException(
                    $"The CSV row has {fields.Length} fields, the header has {header.Length}.");
            }

            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Length; index++)
            {
                var name = header[index];
                if (name.Length == 0)
                {
                    continue;
                }

                row[name] = index < fields.Length ? fields[index] : null;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>reads the rows of a CSV file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromCsvFile(string path, char separator = ',')
    {
        ArgumentNullException.ThrowIfNull(path);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return FromCsv(reader, separator);
    }

    /// <summary>
    /// reads the rows of an XML file. Every element with the given name is a row; its
    /// attributes and its child elements are the fields. Without a name, the child elements of
    /// the root element are the rows.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidDataException">The XML is not well formed.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromXml(TextReader reader, string? rowElement = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        XDocument document;
        try
        {
            using var xml = XmlReader.Create(reader, ReaderGuard.SafeXml);
            document = XDocument.Load(ReaderGuard.LimitXmlDepth(xml), LoadOptions.None);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new InvalidDataException("The XML data source is not well formed.", exception);
        }

        var root = document.Root ?? throw new InvalidDataException("The XML data source is empty.");
        var rows = rowElement is null
            ? root.Elements().ToList()
            : root.Descendants(rowElement).ToList();

        var result = new List<IReadOnlyDictionary<string, string?>>();
        foreach (var element in rows)
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attribute in element.Attributes())
            {
                row[attribute.Name.LocalName] = attribute.Value;
            }

            foreach (var child in element.Elements())
            {
                row[child.Name.LocalName] = child.Value;
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>reads the rows of an XML file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromXmlFile(string path, string? rowElement = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return FromXml(reader, rowElement);
    }

    /// <summary>
    /// reads the rows of a JSON file: an array of objects. The name of a property is the name
    /// of the field; a null value is null, a number and a boolean are written as text.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidDataException">The JSON is not well formed or is no array.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromJson(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return MergeJsonReader.ReadRows(reader.ReadToEnd());
    }

    /// <summary>reads the rows of a JSON file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromJsonFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return FromJson(reader);
    }

    /// <summary>
    /// reads the rows of an Excel workbook (xlsx). The first sheet is read; its first line
    /// holds the names of the fields. Only the values are read, no formulas, no dates.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">The file is no xlsx package.</exception>
    /// <exception cref="DocumentValidationException">A package entry exceeds the size limit when decompressed.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var sharedStrings = ReadSharedStrings(archive);
        var workbook = ReadExcelPart(archive, "xl/workbook.xml");
        var firstSheet = workbook.Descendants().FirstOrDefault(node => node.Name.LocalName == "sheets")?
            .Elements().FirstOrDefault(node => node.Name.LocalName == "sheet")
            ?? throw new InvalidDataException("The Excel file has no sheet.");
        var document = ReadExcelWorksheet(archive, firstSheet);

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        List<string>? header = null;
        foreach (var row in document.Descendants().Where(element => element.Name.LocalName == "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
            {
                var reference = cell.Attribute("r")?.Value;
                var column = ColumnIndex(reference);
                if (header is not null && column >= header.Count)
                {
                    continue; // no field: padding up to it would cost up to 16,384 cells per row
                }

                while (cells.Count < column)
                {
                    cells.Add(string.Empty);
                }

                cells.Add(CellValue(cell, sharedStrings));
            }

            if (header is null)
            {
                header = cells;
                continue;
            }

            if (cells.Count == 1 && cells[0].Length == 0)
            {
                continue; // an empty line
            }

            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Count; index++)
            {
                var name = header[index];
                if (name.Length == 0)
                {
                    continue;
                }

                result[name] = index < cells.Count ? cells[index] : null;
            }

            rows.Add(result);
        }

        return rows;
    }

    /// <summary>reads the rows of an Excel workbook (xlsx).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcelFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var stream = File.OpenRead(path);
        return FromExcel(stream);
    }

    /// <summary>Reads a single rectangular named range from an Excel workbook. Its first row contains field names.</summary>
    /// <exception cref="InvalidDataException">The name is missing, ambiguous, or does not denote a local rectangular cell range.</exception>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcel(Stream stream, string namedRange)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(namedRange);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var workbook = ReadExcelPart(archive, "xl/workbook.xml");
        var definitions = workbook.Descendants().Where(node => node.Name.LocalName == "definedName"
            && string.Equals((string?)node.Attribute("name"), namedRange, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (definitions.Length != 1) throw new InvalidDataException($"Excel named range '{namedRange}' is missing or ambiguous.");
        var reference = definitions[0].Value;
        var separator = reference.LastIndexOf('!');
        if (separator <= 0) throw new InvalidDataException("The Excel name must refer to one rectangular range on a sheet.");
        var sheetName = reference[..separator];
        if (sheetName.StartsWith('\'') && sheetName.EndsWith('\'') && sheetName.Length >= 2)
            sheetName = sheetName[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (sheetName.Contains('[') || sheetName.Contains(']')) throw new InvalidDataException("External Excel ranges are not supported.");
        var bounds = reference[(separator + 1)..].Split(':');
        if (bounds.Length is < 1 or > 2) throw new InvalidDataException("The Excel name must refer to one rectangular range.");
        var (firstColumn, firstRow) = ParseExcelCell(bounds[0]);
        var (lastColumn, lastRow) = ParseExcelCell(bounds[^1]);
        if (lastColumn < firstColumn || lastRow < firstRow) throw new InvalidDataException("The Excel range is reversed.");

        var sheets = workbook.Descendants().Where(node => node.Name.LocalName == "sheet"
            && string.Equals((string?)node.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sheets.Length != 1) throw new InvalidDataException("The Excel worksheet is missing.");
        var worksheet = ReadExcelWorksheet(archive, sheets[0]);
        var sharedStrings = ReadSharedStrings(archive);
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        string[]? header = null;
        foreach (var row in worksheet.Descendants().Where(node => node.Name.LocalName == "row"))
        {
            if (!int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var rowNumber)
                || rowNumber < firstRow || rowNumber > lastRow) continue;
            var cells = new string?[lastColumn - firstColumn + 1];
            foreach (var cell in row.Elements().Where(node => node.Name.LocalName == "c"))
            {
                var (column, cellRow) = ParseExcelCell((string?)cell.Attribute("r") ?? "");
                if (cellRow == rowNumber && column >= firstColumn && column <= lastColumn)
                    cells[column - firstColumn] = CellValue(cell, sharedStrings);
            }
            if (rowNumber == firstRow)
            {
                if (header is not null) throw new InvalidDataException("The Excel range has a duplicate header row.");
                header = cells.Select(value => value ?? "").ToArray();
                // Columns after the last field name are never read: rows then cost the fields, not the range width.
                header = header[..Math.Max(1, Array.FindLastIndex(header, name => name.Length > 0) + 1)];
                lastColumn = firstColumn + header.Length - 1;
                continue;
            }
            if (header is null) throw new InvalidDataException("The Excel range's header row is missing or out of order.");
            if (cells.All(string.IsNullOrEmpty)) continue;
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < header.Length; index++)
                if (header[index].Length > 0) result[header[index]] = cells[index];
            rows.Add(result);
        }
        if (header is null) throw new InvalidDataException("The Excel range's header row is missing.");
        return rows;
    }

    private static XDocument ReadExcelWorksheet(ZipArchive archive, XElement sheet)
    {
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var relationshipId = (string?)sheet.Attribute(relationships + "id");
        if (relationshipId is null) throw new InvalidDataException("The Excel worksheet is missing.");
        var rels = ReadExcelPart(archive, "xl/_rels/workbook.xml.rels");
        var matches = rels.Descendants().Where(node => node.Name.LocalName == "Relationship" && (string?)node.Attribute("Id") == relationshipId).ToArray();
        if (matches.Length != 1 || (string?)matches[0].Attribute("TargetMode") == "External"
            || !((string?)matches[0].Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal))
            throw new InvalidDataException("The Excel worksheet relationship is invalid.");
        var target = (string?)matches[0].Attribute("Target");
        if (target is null || !Uri.TryCreate(new Uri("http://xlsx.local/xl/workbook.xml"), target, out var uri)
            || uri.Host != "xlsx.local" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("The Excel worksheet target is invalid.");
        return ReadExcelPart(archive, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }

    /// <summary>Reads a named rectangular range from an Excel workbook file.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcelFile(string path, string namedRange)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return FromExcel(stream, namedRange);
    }

    private static XDocument ReadExcelPart(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Excel part '{name}' is missing.");
        using var stream = ReaderGuard.OpenBounded(entry);
        return LoadXml(stream);
    }

    private static (int Column, int Row) ParseExcelCell(string reference)
    {
        var split = 0;
        if (split < reference.Length && reference[split] == '$') split++;
        var columnStart = split;
        while (split < reference.Length && char.IsAsciiLetter(reference[split])) split++;
        var columnLength = split - columnStart;
        if (split < reference.Length && reference[split] == '$') split++;
        if (columnLength is < 1 or > 3 || !int.TryParse(reference.AsSpan(split), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || row is < 1 or > 1048576)
            throw new InvalidDataException("The Excel range has an invalid cell reference.");
        return (ColumnIndex(reference.Substring(columnStart, columnLength)), row);
    }

    /// <summary>the shared strings of the workbook (the values of the text cells).</summary>
    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = ReaderGuard.OpenBounded(entry);
        var document = LoadXml(stream);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(element => string.Concat(element.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value)))
            .ToList();
    }

    /// <summary>the text of a cell.</summary>
    private static string CellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants().Where(child => child.Name.LocalName == "t").Select(child => child.Value));
        }

        var value = cell.Elements().FirstOrDefault(child => child.Name.LocalName == "v")?.Value;
        if (value is null)
        {
            return string.Empty;
        }

        if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        if (type == "b")
        {
            return value == "1" ? "true" : "false";
        }

        return value;
    }

    /// <summary>Last column of an xlsx sheet (XFD); a larger reference would allocate unbounded empty cells.</summary>
    private const int MaxExcelColumns = 16384;

    private static XDocument LoadXml(Stream stream)
    {
        try
        {
            return ReaderGuard.LoadXml(stream);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The Excel file contains invalid XML.", exception);
        }
    }

    /// <summary>the column index of a cell reference (A = 0).</summary>
    private static int ColumnIndex(string? reference)
    {
        if (reference is null)
        {
            return 0;
        }

        var index = 0;
        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
            if (index > MaxExcelColumns)
            {
                throw new InvalidDataException("The Excel cell reference is outside the sheet.");
            }
        }

        return Math.Max(0, index - 1);
    }

    /// <summary>reads the fields of the CSV lines.</summary>
    private static IEnumerable<string[]> ReadCsvRows(TextReader reader, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var pending = reader.Read();

        while (pending >= 0)
        {
            var value = (char)pending;
            if (quoted)
            {
                if (value == '"')
                {
                    var next = reader.Read();
                    if (next == '"')
                    {
                        field.Append('"');
                        pending = reader.Read();
                        continue;
                    }

                    quoted = false;
                    pending = next;
                    continue;
                }

                field.Append(value);
                pending = reader.Read();
                continue;
            }

            if (value == '"')
            {
                quoted = true;
                pending = reader.Read();
                continue;
            }

            if (value == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
                pending = reader.Read();
                continue;
            }

            if (value == '\r' || value == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();

                if (value == '\r')
                {
                    var next = reader.Read();
                    pending = next == '\n' ? reader.Read() : next;
                }
                else
                {
                    pending = reader.Read();
                }

                continue;
            }

            field.Append(value);
            pending = reader.Read();
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}
