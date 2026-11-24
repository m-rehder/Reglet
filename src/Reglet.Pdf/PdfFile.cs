using System.Text;

namespace Reglet;

/// <summary>
/// loads a PDF (subset): classic xref tables and xref streams (PDF 1.5+), trailer,
/// indirect objects, object streams (<c>/ObjStm</c>) and stream decoding
/// (FlateDecode, ASCIIHexDecode). /Prev chains and hybrid references are followed.
/// </summary>
internal sealed class PdfFile
{
    private readonly byte[] _data;
    private readonly Dictionary<int, int> _offsets;
    private readonly Dictionary<int, (int Container, int Index)> _compressed;
    private readonly Dictionary<int, PdfValue> _cache = [];
    private readonly Dictionary<int, ObjectStreamIndex> _objectStreams = [];
    private long _objectStreamBytes;
    private Dictionary<int, int>? _scannedOffsets;
    private PdfDecryptor? _decryptor;
    private PdfDictionary? _encryptDictionary;
    private string? _password;
    private int _encryptObjectNumber = -1;

    private PdfFile(
        byte[] data,
        Dictionary<int, int> offsets,
        Dictionary<int, (int Container, int Index)> compressed,
        PdfDictionary trailer)
    {
        _data = data;
        _offsets = offsets;
        _compressed = compressed;
        Trailer = trailer;
    }

    public PdfDictionary Trailer { get; }

    /// <summary>The bytes all object streams may decode to; they stay in memory while the file is open.</summary>
    internal long ObjectStreamBudget { get; set; } = ReaderGuard.MaxInputBytes;

    /// <summary>Whether the document is encrypted (trailer contains /Encrypt).</summary>
    public bool IsEncrypted => Trailer.Get("Encrypt") is not null;

    /// <summary>Loads a PDF; <paramref name="password"/> opens an encrypted file (phase AD).</summary>
    public static PdfFile Load(byte[] data, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var offsets = new Dictionary<int, int>();
        var compressed = new Dictionary<int, (int, int)>();
        PdfDictionary? trailer;
        try
        {
            trailer = LoadCrossReferences(data, new XrefEntries(offsets, compressed));
        }
        catch (Exception exception) when (exception is DocumentValidationException or UnsupportedFeatureException)
        {
            // Damaged or missing cross-reference data is common in real files (truncated
            // downloads, wrong offsets after hand edits). Like other readers, rebuild the object
            // directory by scanning for "N G obj" headers.
            (offsets, trailer) = Reconstruct(data);
            compressed.Clear();
        }

        var file = new PdfFile(data, offsets, compressed, trailer);
        if (trailer.Get("Encrypt") is { } encryptValue)
        {
            if (encryptValue is PdfReference reference)
            {
                file._encryptObjectNumber = reference.ObjectNumber;
            }

            file._encryptDictionary = file.Dictionary(encryptValue)
                ?? throw new DocumentValidationException("The PDF /Encrypt dictionary is missing.");
            file._password = password;
        }

        return file;
    }

    /// <summary>
    /// The cross-reference data exactly as the file states it, without the repair <see cref="Load"/> falls back to
    /// (for validation).
    /// </summary>
    /// <exception cref="DocumentValidationException">The cross-reference data cannot be read.</exception>
    internal static (IReadOnlyDictionary<int, int> Offsets, IReadOnlyDictionary<int, (int Container, int Index)> Compressed, PdfDictionary Trailer) ReadCrossReferencesStrict(byte[] data)
    {
        var offsets = new Dictionary<int, int>();
        var compressed = new Dictionary<int, (int, int)>();
        var trailer = LoadCrossReferences(data, new XrefEntries(offsets, compressed));
        return (offsets, compressed.ToDictionary(entry => entry.Key, entry => (entry.Value.Item1, entry.Value.Item2)), trailer);
    }

    private static PdfDictionary LoadCrossReferences(byte[] data, XrefEntries entries)
    {
        PdfDictionary? trailer = null;
        var visited = new HashSet<int>();
        var offset = FindStartXref(data);

        while (offset >= 0 && offset < data.Length && visited.Add(offset))
        {
            var (sectionTrailer, previous) = ParseXrefSection(data, offset, entries);
            trailer = trailer is null ? sectionTrailer : MergeTrailers(trailer, sectionTrailer);
            offset = previous;
        }

        return trailer ?? throw new DocumentValidationException("PDF trailer is missing.");
    }

    /// <summary>
    /// Rebuilds the object directory from the object headers in the file (the last
    /// definition of an object number wins, as with incremental updates) and finds the
    /// trailer: the last <c>trailer</c> dictionary with a <c>/Root</c>, else the last xref
    /// stream dictionary with a <c>/Root</c>, else a synthesized one pointing to the catalog.
    /// </summary>
    private static (Dictionary<int, int> Offsets, PdfDictionary Trailer) Reconstruct(byte[] data)
    {
        var offsets = ScanObjectHeaders(data);
        PdfDictionary? trailer = null;

        var marker = "trailer"u8.ToArray();
        for (var index = LastIndexOf(data, marker); index >= 0 && trailer is null; index = LastIndexOf(data, marker, index - 1))
        {
            try
            {
                if (new PdfObjectParser(new PdfLexer(data, index + marker.Length)).ParseValue() is PdfDictionary candidate
                    && candidate.Get("Root") is PdfReference)
                {
                    trailer = candidate;
                }
            }
            catch (DocumentValidationException)
            {
                // try the previous trailer
            }
        }

        int? catalog = null;
        foreach (var (number, offset) in offsets.OrderByDescending(pair => pair.Value))
        {
            if (trailer is not null)
            {
                break;
            }

            try
            {
                var value = ParseIndirectObjectAt(data, offset, number, decryptor: null);
                if (value is PdfStreamValue { Dictionary: var dictionary }
                    && NameOf(dictionary.Get("Type")) == "XRef"
                    && dictionary.Get("Root") is PdfReference)
                {
                    trailer = dictionary;
                }
                else if (catalog is null && value is PdfDictionary candidate && NameOf(candidate.Get("Type")) == "Catalog")
                {
                    catalog = number;
                }
            }
            catch (DocumentValidationException)
            {
                // skip damaged objects
            }
        }

        if (trailer is null && catalog is { } root)
        {
            trailer = new PdfDictionary(new Dictionary<string, PdfValue>(StringComparer.Ordinal)
            {
                ["Root"] = new PdfReference(root),
            });
        }

        return trailer is null
            ? throw new DocumentValidationException("PDF contains neither a readable cross-reference table nor a catalog.")
            : (offsets, trailer);
    }

    /// <summary>Finds all <c>N G obj</c> headers at the start of a line; the last one per number wins.</summary>
    private static Dictionary<int, int> ScanObjectHeaders(byte[] data)
    {
        var offsets = new Dictionary<int, int>();
        for (var index = 0; index + 5 < data.Length; index++)
        {
            if (!IsDigit(data[index]) || (index > 0 && data[index - 1] is not (10 or 13 or 32 or 9 or 12 or 0)))
            {
                continue;
            }

            var position = index;
            long number = 0;
            while (position < data.Length && IsDigit(data[position]) && number <= int.MaxValue)
            {
                number = (number * 10) + (data[position++] - (byte)'0');
            }

            if (!SkipSpaces(data, ref position) || !IsDigit(data[position]))
            {
                continue;
            }

            while (position < data.Length && IsDigit(data[position]))
            {
                position++;
            }

            if (SkipSpaces(data, ref position)
                && position + 3 <= data.Length
                && data.AsSpan(position, 3).SequenceEqual("obj"u8)
                && number is > 0 and <= int.MaxValue)
            {
                offsets[(int)number] = index;
                index = position + 2;
            }
        }

        return offsets;

        static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

        static bool SkipSpaces(byte[] data, ref int position)
        {
            var start = position;
            while (position < data.Length && data[position] is 32 or 9 or 10 or 13 or 12 or 0)
            {
                position++;
            }

            return position > start && position < data.Length;
        }
    }

    /// <summary>
    /// The decryptor, opened lazily on the first object access. That way a caller that
    /// merely inspects the trailer (for example the page operations) does not need a
    /// password; accessing content of an encrypted file without one raises a clear error.
    /// </summary>
    private PdfDecryptor? Decryptor
    {
        get
        {
            if (_decryptor is null && _encryptDictionary is not null)
            {
                _decryptor = PdfDecryptor.Open(_encryptDictionary, Trailer, _password);
            }

            return _decryptor;
        }
    }

    /// <summary>Resolves references recursively (at most 32 levels).</summary>
    public PdfValue Resolve(PdfValue? value)
    {
        var guard = 0;
        while (value is PdfReference reference && guard++ < 32)
        {
            value = GetObject(reference.ObjectNumber);
        }

        return value ?? PdfValue.Null;
    }

    public PdfValue GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        var decryptor = number == _encryptObjectNumber ? null : Decryptor;
        PdfValue value;
        if (_offsets.TryGetValue(number, out var offset))
        {
            value = HeaderMatches(offset, number)
                ? ParseIndirectObjectAt(_data, offset, number, decryptor)
                : ScannedOffset(number) is { } scanned
                    ? ParseIndirectObjectAt(_data, scanned, number, decryptor)
                    : throw new DocumentValidationException($"PDF object {number} has no valid object header.");
        }
        else if (_compressed.ContainsKey(number))
        {
            value = ParseCompressedObject(number);
        }
        else
        {
            // ISO 32000-1, 7.3.10: a reference to an undefined object is treated as null.
            value = PdfValue.Null;
        }

        _cache[number] = value;
        return value;
    }

    /// <summary>Whether the xref offset really points to the header of the object (wrong offsets are common).</summary>
    private bool HeaderMatches(int offset, int number)
    {
        if (offset < 0 || offset >= _data.Length)
        {
            return false;
        }

        var lexer = new PdfLexer(_data, offset);
        var first = lexer.Next();
        var second = lexer.Next();
        var keyword = lexer.Next();
        return first.Kind == PdfTokenKind.Integer && first.Integer == number
            && second.Kind == PdfTokenKind.Integer
            && keyword.Kind == PdfTokenKind.Keyword && keyword.Text == "obj";
    }

    private int? ScannedOffset(int number)
    {
        _scannedOffsets ??= ScanObjectHeaders(_data);
        return _scannedOffsets.TryGetValue(number, out var offset) ? offset : null;
    }

    public PdfStreamValue? GetStream(PdfValue? value)
        => Resolve(value) as PdfStreamValue;

    public PdfDictionary? Dictionary(PdfValue? value) => Resolve(value) as PdfDictionary;

    public PdfArray? Array(PdfValue? value) => Resolve(value) as PdfArray;

    public string? Name(PdfValue? value) => Resolve(value) is PdfName name ? name.Name : null;

    public long? Integer(PdfValue? value)
        => Resolve(value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when Math.Abs(real.Value % 1) < 1e-9 => (long)real.Value,
            _ => null,
        };

    public double? Number(PdfValue? value)
        => Resolve(value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => null,
        };

    /// <summary>Decodes a stream according to its filters (FlateDecode, ASCIIHexDecode).</summary>
    public byte[] DecodeStream(PdfStreamValue stream) => Decode(stream);

    private PdfValue ParseCompressedObject(int number)
    {
        var (container, objectIndex) = _compressed[number];

        // object streams must not themselves be compressed (ISO 32000-1, 7.5.7). This
        // also prevents endless recursion over self-containing streams.
        if (!_offsets.ContainsKey(container))
        {
            throw new DocumentValidationException($"PDF object {number} does not reference a valid object stream.");
        }

        if (GetObject(container) is not PdfStreamValue stream
            || NameOf(stream.Dictionary.Get("Type")) != "ObjStm")
        {
            throw new DocumentValidationException($"PDF object {number} does not reference an object stream.");
        }

        if (!_objectStreams.TryGetValue(container, out var index))
        {
            var decoded = Decode(stream);
            _objectStreamBytes += decoded.Length;
            if (_objectStreamBytes > ObjectStreamBudget)
            {
                throw new DocumentValidationException($"The PDF object streams exceed the limit of {ObjectStreamBudget} bytes when decompressed.");
            }

            index = ObjectStreamIndex.Build(decoded, stream.Dictionary, number);
            _objectStreams[container] = index;
        }

        return index.Parse(objectIndex, number);
    }

    /// <summary>
    /// A decoded object stream with its parsed header. Decoded once per container instead of
    /// once per contained object (a stream commonly holds hundreds of objects).
    /// </summary>
    private sealed class ObjectStreamIndex(byte[] data, long first, long[] offsets)
    {
        public static ObjectStreamIndex Build(byte[] data, PdfDictionary dictionary, int number)
        {
            var count = IntegerDirect(dictionary.Get("N")) ?? 0;
            var first = IntegerDirect(dictionary.Get("First")) ?? 0;
            if (count < 0 || first < 0 || first >= data.Length)
            {
                throw new DocumentValidationException($"The PDF object stream for object {number} is invalid.");
            }

            // Each header entry is a pair "object number offset"; the count is bounded by the data.
            var header = new PdfObjectParser(new PdfLexer(data));
            var offsets = new List<long>();
            for (var item = 0L; item < count && item <= data.Length; item++)
            {
                if (header.ParseValue() is not PdfInteger || header.ParseValue() is not PdfInteger offset)
                {
                    throw new DocumentValidationException($"The header of the PDF object stream for object {number} is invalid.");
                }

                offsets.Add(offset.Value);
            }

            return new ObjectStreamIndex(data, first, offsets.ToArray());
        }

        public PdfValue Parse(int index, int number)
        {
            if (index < 0 || index >= offsets.Length)
            {
                throw new DocumentValidationException($"PDF object {number} is outside the object stream.");
            }

            var position = first + offsets[index];
            if (offsets[index] < 0 || position >= data.Length)
            {
                throw new DocumentValidationException($"PDF object {number} is outside the object stream.");
            }

            return new PdfObjectParser(new PdfLexer(data, (int)position)).ParseValue();
        }
    }

    private static PdfValue ParseIndirectObjectAt(byte[] data, long offset, int number, PdfDecryptor? decryptor)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new DocumentValidationException($"PDF object {number} is outside the file.");
        }

        var parser = new PdfObjectParser(new PdfLexer(data, (int)offset));
        if (parser.Peek(0).Kind != PdfTokenKind.Integer
            || parser.Peek(1).Kind != PdfTokenKind.Integer
            || parser.Peek(2).Kind != PdfTokenKind.Keyword
            || parser.Peek(2).Text != "obj")
        {
            throw new DocumentValidationException($"PDF object {number} has no valid object header.");
        }

        var generation = (int)parser.Peek(1).Integer;
        parser.Advance();
        parser.Advance();
        parser.Advance();

        var value = parser.ParseValue();
        if (value is PdfDictionary dictionary
            && parser.Peek(0).Kind == PdfTokenKind.Keyword
            && parser.Peek(0).Text == "stream")
        {
            var streamToken = parser.Advance();
            var length = IntegerDirect(dictionary.Get("Length")) is { } size and >= 0 and <= int.MaxValue ? (int)size : -1;
            var start = SkipEndOfLine(data, streamToken.End);
            var raw = length >= 0 && (long)start + length <= data.Length
                ? data[start..(start + length)]
                : ReadUntilEndstream(data, start);
            value = new PdfStreamValue(dictionary, raw);
        }

        return decryptor is null || number <= 0
            ? value
            : DecryptValue(decryptor, value, number, generation);
    }

    /// <summary>Decrypts every string and stream of an indirect object (phase AD).</summary>
    private static PdfValue DecryptValue(PdfDecryptor decryptor, PdfValue value, int objectNumber, int generation)
    {
        ReaderGuard.EnsureStack();
        return value switch
        {
            PdfString text => new PdfString(decryptor.DecryptString(text.Bytes, objectNumber, generation)),
            PdfArray array => new PdfArray(array.Items
                .Select(item => DecryptValue(decryptor, item, objectNumber, generation))
                .ToList()),
            PdfDictionary dictionary => new PdfDictionary(dictionary.Entries.ToDictionary(
                pair => pair.Key,
                pair => DecryptValue(decryptor, pair.Value, objectNumber, generation),
                StringComparer.Ordinal)),
            PdfStreamValue stream => DecryptStream(decryptor, stream, objectNumber, generation),
            _ => value,
        };
    }

    private static PdfStreamValue DecryptStream(
        PdfDecryptor decryptor,
        PdfStreamValue stream,
        int objectNumber,
        int generation)
    {
        var dictionary = (PdfDictionary)DecryptValue(decryptor, stream.Dictionary, objectNumber, generation);
        return new PdfStreamValue(dictionary, decryptor.DecryptStream(dictionary, stream.Raw, objectNumber, generation));
    }

    private static (PdfDictionary Trailer, int Previous) ParseXrefSection(byte[] data, int offset, XrefEntries entries)
    {
        var parser = new PdfObjectParser(new PdfLexer(data, offset));
        if (parser.Peek(0).Kind == PdfTokenKind.Keyword && parser.Peek(0).Text == "xref")
        {
            parser.Advance();
            ParseClassicXref(parser, entries);

            parser.Advance(); // 'trailer'
            if (parser.ParseValue() is not PdfDictionary trailer)
            {
                throw new DocumentValidationException("PDF trailer is missing.");
            }

            // Hybrid reference: classic table plus XRef stream for compressed objects.
            if (IntegerDirect(trailer.Get("XRefStm")) is { } hybrid
                && ParseIndirectObjectAt(data, hybrid, -1, null) is PdfStreamValue hybridStream)
            {
                ParseXrefStreamEntries(hybridStream, entries);
            }

            return (trailer, PreviousOffset(trailer));
        }

        if (ParseIndirectObjectAt(data, offset, -1, null) is not PdfStreamValue stream
            || NameOf(stream.Dictionary.Get("Type")) != "XRef")
        {
            throw new UnsupportedFeatureException("The PDF xref directory is neither a table nor a stream.");
        }

        ParseXrefStreamEntries(stream, entries);
        return (stream.Dictionary, PreviousOffset(stream.Dictionary));
    }

    private static int PreviousOffset(PdfDictionary trailer)
        => IntegerDirect(trailer.Get("Prev")) is { } previous and >= 0 and <= int.MaxValue ? (int)previous : -1;

    private static void ParseClassicXref(PdfObjectParser parser, XrefEntries entries)
    {
        while (!(parser.Peek(0).Kind == PdfTokenKind.Keyword && parser.Peek(0).Text == "trailer"))
        {
            if (parser.ParseValue() is not PdfInteger start || parser.ParseValue() is not PdfInteger count)
            {
                throw new DocumentValidationException("xref section is invalid.");
            }

            for (var index = 0; index < count.Value; index++)
            {
                var offsetToken = parser.Advance();
                parser.Advance(); // Generation
                var typeToken = parser.Advance();
                if (offsetToken.Kind != PdfTokenKind.Integer || typeToken.Kind != PdfTokenKind.Keyword)
                {
                    throw new DocumentValidationException("xref entry is invalid.");
                }

                if (typeToken.Text == "n")
                {
                    entries.AddOffset(start.Value + index, offsetToken.Integer);
                }
            }
        }
    }

    private static void ParseXrefStreamEntries(PdfStreamValue stream, XrefEntries entries)
    {
        var dictionary = stream.Dictionary;
        if (dictionary.Get("W") is not PdfArray widths || widths.Items.Count < 2)
        {
            throw new DocumentValidationException("PDF xref stream without a /W field.");
        }

        var w0 = IntegerDirect(widths.Items[0]) ?? 0;
        var w1 = IntegerDirect(widths.Items[1]) ?? 0;
        var w2 = widths.Items.Count > 2 ? IntegerDirect(widths.Items[2]) ?? 0 : 0;
        if (w0 is < 0 or > 8 || w1 is < 0 or > 8 || w2 is < 0 or > 8 || w0 + w1 + w2 == 0)
        {
            throw new DocumentValidationException("PDF xref stream has an invalid entry width.");
        }

        var (width0, width1, width2) = ((int)w0, (int)w1, (int)w2);
        var entrySize = width0 + width1 + width2;
        var size = IntegerDirect(dictionary.Get("Size")) ?? 0;
        var index = dictionary.Get("Index") is PdfArray indexArray
            ? indexArray.Items.Select(item => IntegerDirect(item) ?? 0).ToArray()
            : [0, size];

        var data = Decode(stream);
        var position = 0;
        for (var pair = 0; pair + 1 < index.Length; pair += 2)
        {
            var start = index[pair];
            var count = index[pair + 1];
            for (var item = 0; item < count && position + entrySize <= data.Length; item++, position += entrySize)
            {
                var type = width0 == 0 ? 1 : ReadBigEndian(data, position, width0);
                var field2 = ReadBigEndian(data, position + width0, width1);
                var field3 = ReadBigEndian(data, position + width0 + width1, width2);
                var number = start + item;

                if (type == 1)
                {
                    entries.AddOffset(number, field2);
                }
                else if (type == 2)
                {
                    entries.AddCompressed(number, field2, field3);
                }
            }
        }
    }

    private static long ReadBigEndian(byte[] data, int offset, int width)
    {
        long value = 0;
        for (var index = 0; index < width; index++)
        {
            value = (value << 8) | data[offset + index];
        }

        return value;
    }

    private static PdfDictionary MergeTrailers(PdfDictionary newest, PdfDictionary older)
    {
        var entries = new Dictionary<string, PdfValue>(newest.Entries, StringComparer.Ordinal);
        foreach (var (key, value) in older.Entries)
        {
            entries.TryAdd(key, value);
        }

        return new PdfDictionary(entries);
    }

    private static byte[] Decode(PdfStreamValue stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return PdfStreamFilters.Decode(stream.Raw, BuildFilters(stream.Dictionary));
    }

    private static IReadOnlyList<PdfFilter> BuildFilters(PdfDictionary dictionary)
    {
        var names = FilterNames(dictionary);
        var parameters = DecodeParameters(dictionary);
        var filters = new List<PdfFilter>(names.Count);
        for (var index = 0; index < names.Count; index++)
        {
            filters.Add(new PdfFilter(names[index], index < parameters.Count ? parameters[index] : null));
        }

        return filters;
    }

    private static List<string> FilterNames(PdfDictionary dictionary) => dictionary.Get("Filter") switch
    {
        PdfName name => [name.Name],
        PdfArray array => array.Items.OfType<PdfName>().Select(item => item.Name).ToList(),
        _ => [],
    };

    private static List<PdfDictionary?> DecodeParameters(PdfDictionary dictionary) => dictionary.Get("DecodeParms") switch
    {
        PdfDictionary single => [single],
        PdfArray array => array.Items.Select(item => item as PdfDictionary).ToList(),
        _ => [],
    };

    private static int SkipEndOfLine(byte[] data, int position)
    {
        if (position < data.Length && data[position] == 13
            && position + 1 < data.Length && data[position + 1] == 10)
        {
            return position + 2;
        }

        if (position < data.Length && data[position] is 13 or 10)
        {
            return position + 1;
        }

        return position;
    }

    private static byte[] ReadUntilEndstream(byte[] data, int start)
    {
        var marker = Encoding.ASCII.GetBytes("endstream");
        var end = IndexOf(data, marker, start);
        if (end < 0)
        {
            end = data.Length;
        }

        // The line ending before "endstream" (LF, CR or CRLF) does not belong to the data.
        var length = end - start;
        if (length > 0 && data[start + length - 1] == 10)
        {
            length--;
        }

        if (length > 0 && data[start + length - 1] == 13)
        {
            length--;
        }

        return data[start..(start + Math.Max(0, length))];
    }

    private static int FindStartXref(byte[] data)
    {
        var marker = Encoding.ASCII.GetBytes("startxref");
        var index = LastIndexOf(data, marker);
        if (index < 0)
        {
            throw new DocumentValidationException("PDF contains no 'startxref'.");
        }

        var parser = new PdfObjectParser(new PdfLexer(data, index + marker.Length));
        return parser.ParseValue() is PdfInteger integer
            ? (int)integer.Value
            : throw new DocumentValidationException("'startxref' does not point to a valid position.");
    }

    private static string? NameOf(PdfValue? value) => value is PdfName name ? name.Name : null;

    private static long? IntegerDirect(PdfValue? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real when Math.Abs(real.Value % 1) < 1e-9 => (long)real.Value,
        _ => null,
    };

    private static int LastIndexOf(byte[] data, byte[] pattern) => LastIndexOf(data, pattern, data.Length - pattern.Length);

    private static int LastIndexOf(byte[] data, byte[] pattern, int start)
    {
        for (var index = Math.Min(start, data.Length - pattern.Length); index >= 0; index--)
        {
            if (data.AsSpan(index, pattern.Length).SequenceEqual(pattern))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        for (var index = start; index <= data.Length - pattern.Length; index++)
        {
            if (data.AsSpan(index, pattern.Length).SequenceEqual(pattern))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// collects xref entries. The sections are read from newest to oldest
    /// (/Prev chain); the first-seen entry of an object number wins, so that
    /// incremental updates override older versions.
    /// </summary>
    private sealed class XrefEntries(Dictionary<int, int> offsets, Dictionary<int, (int, int)> compressed)
    {
        public void AddOffset(long number, long offset)
        {
            if (IsValidNumber(number) && offset is >= 0 and <= int.MaxValue && !compressed.ContainsKey((int)number))
            {
                offsets.TryAdd((int)number, (int)offset);
            }
        }

        public void AddCompressed(long number, long container, long index)
        {
            if (IsValidNumber(number) && IsValidNumber(container) && index is >= 0 and <= int.MaxValue
                && !offsets.ContainsKey((int)number))
            {
                compressed.TryAdd((int)number, ((int)container, (int)index));
            }
        }

        private static bool IsValidNumber(long number) => number is > 0 and <= int.MaxValue;
    }
}
