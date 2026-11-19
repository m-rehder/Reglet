using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// appends an incremental update to an existing PDF (OF-E03): new objects, a cross-reference
/// section that lists only them and a trailer whose <c>/Prev</c> points to the previous section.
/// The original bytes are never changed, so a signature over the first revision stays valid.
/// </summary>
internal sealed class PdfIncrementalUpdate
{
    private readonly PdfFile _file;
    private readonly byte[] _existing;
    private readonly List<(int Number, byte[] Body)> _objects = [];
    private readonly HashSet<int> _reserved = [];
    private readonly Dictionary<int, long> _offsets = [];
    private int _next;

    /// <summary>
    /// prepares an update for the loaded file. The catalog must be reachable and the file must
    /// not be encrypted: the appended objects would need the file key.
    /// </summary>
    /// <exception cref="DocumentValidationException">The file has no catalog, no startxref or is encrypted.</exception>
    public PdfIncrementalUpdate(PdfFile file, byte[] existing)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(existing);
        if (file.Trailer.Get("Root") is not PdfReference)
        {
            throw new DocumentValidationException("PDF catalog is missing.");
        }

        if (file.IsEncrypted)
        {
            throw new DocumentValidationException(
                "An incremental update of an encrypted PDF is not supported; the appended objects would not be encrypted.");
        }

        _file = file;
        _existing = existing;
        _next = (int)(file.Trailer.Get("Size") is PdfInteger size ? size.Value : 1);
        PreviousOffset = FindStartXref(existing);
    }

    /// <summary>the offset of the cross-reference section that the update continues.</summary>
    public long PreviousOffset { get; }

    /// <summary>The object numbers that this update writes.</summary>
    public IReadOnlyList<int> Objects => _objects.Select(entry => entry.Number).ToList();

    /// <summary>Reserves a new object number above the previous ones.</summary>
    public int Reserve()
    {
        var number = _next++;
        _reserved.Add(number);
        return number;
    }

    /// <summary>Writes a dictionary or other direct object as a new revision.</summary>
    public void WriteObject(int number, Action<PdfTokenWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var buffer = new MemoryStream();
        var tokens = new PdfTokenWriter(buffer);
        tokens.WriteRaw(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
        body(tokens);
        tokens.WriteRaw("\nendobj\n");
        _objects.Add((number, buffer.ToArray()));
    }

    /// <summary>Writes a prepared object body (<c>&lt;n&gt; 0 obj</c> is added) as a new revision.</summary>
    public void WriteRawObject(int number, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var buffer = new MemoryStream(body.Length + 32);
        var tokens = new PdfTokenWriter(buffer);
        tokens.WriteRaw(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
        buffer.Write(body);
        tokens.WriteRaw("\nendobj\n");
        _objects.Add((number, buffer.ToArray()));
    }

    /// <summary>the absolute offset of an object in the completed file (after <see cref="Complete"/>).</summary>
    public long OffsetOf(int number) => _offsets.TryGetValue(number, out var offset)
        ? offset
        : throw new DocumentValidationException($"The update does not write object {number}.");

    /// <summary>Writes a stream object as a new revision.</summary>
    public void WriteStreamObject(int number, ReadOnlySpan<byte> data, string? extraDictionary = null)
    {
        using var buffer = new MemoryStream();
        var tokens = new PdfTokenWriter(buffer);
        tokens.WriteRaw(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
        tokens.WriteRaw(
            "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
            + (extraDictionary is null ? string.Empty : " " + extraDictionary)
            + " >>\nstream\n");
        buffer.Write(data);
        tokens.WriteRaw("\nendstream\nendobj\n");
        _objects.Add((number, buffer.ToArray()));
    }

    /// <summary>
    /// writes the objects, the cross-reference section and the trailer and returns the complete
    /// file (the original bytes followed by the update).
    /// </summary>
    public byte[] Complete()
    {
        using var output = new MemoryStream(_existing.Length + 4096);
        output.Write(_existing);

        // A revision must start on a fresh line.
        if (_existing.Length > 0 && _existing[^1] != (byte)'\n')
        {
            output.WriteByte((byte)'\n');
        }

        var offsets = new SortedDictionary<int, long>();
        foreach (var (number, body) in _objects)
        {
            offsets[number] = output.Position;
            _offsets[number] = output.Position;
            output.Write(body);
        }

        var crossReference = output.Position;
        var tokens = new PdfTokenWriter(output);
        tokens.WriteRaw("xref\n");
        foreach (var (number, offset) in offsets)
        {
            tokens.WriteRaw(number.ToString(CultureInfo.InvariantCulture) + " 1\n");
            tokens.WriteRaw(
                offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        tokens.WriteRaw("trailer\n");
        tokens.WriteDictionary(writer =>
        {
            writer.WriteName("Size");
            writer.WriteInteger(Math.Max(_next, offsets.Count == 0 ? 1 : offsets.Keys.Max() + 1));
            writer.WriteName("Root");
            PdfPageOperations.WriteValue(writer, _file.Trailer.Get("Root")!);

            if (_file.Trailer.Get("Info") is { } info)
            {
                writer.WriteName("Info");
                PdfPageOperations.WriteValue(writer, info);
            }

            if (_file.Trailer.Get("ID") is { } id)
            {
                writer.WriteName("ID");
                PdfPageOperations.WriteValue(writer, id);
            }

            writer.WriteName("Prev");
            writer.WriteInteger(PreviousOffset);
        });

        tokens.WriteRaw("\nstartxref\n");
        tokens.WriteRaw(crossReference.ToString(CultureInfo.InvariantCulture) + "\n");
        tokens.WriteRaw("%%EOF\n");
        return output.ToArray();
    }

    /// <summary>the last <c>startxref</c> value of the file.</summary>
    private static long FindStartXref(byte[] data)
    {
        var marker = Encoding.ASCII.GetBytes("startxref");
        for (var index = data.Length - marker.Length; index >= 0; index--)
        {
            if (!data.AsSpan(index, marker.Length).SequenceEqual(marker))
            {
                continue;
            }

            var position = index + marker.Length;
            while (position < data.Length && char.IsWhiteSpace((char)data[position]))
            {
                position++;
            }

            var start = position;
            while (position < data.Length && char.IsAsciiDigit((char)data[position]))
            {
                position++;
            }

            if (position > start
                && long.TryParse(data.AsSpan(start, position - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new DocumentValidationException("The PDF has no startxref; an incremental update is not possible.");
    }
}
