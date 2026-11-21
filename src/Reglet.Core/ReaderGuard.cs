using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// fixed limits and error mapping for the readers. Read files are foreign
/// inputs: compression bombs and malformed structures must neither use unbounded memory
/// occupy, nor end with an arbitrary runtime exception.
/// </summary>
internal static class ReaderGuard
{
    /// <summary>Maximum byte count of a decompressed ZIP entry or PDF stream (256 MiB).</summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;

    /// <summary>Maximum byte count of a seekable source file before it is parsed (512 MiB).</summary>
    public const int MaxInputBytes = 512 * 1024 * 1024;

    /// <summary>Maximum byte count of a non-seekable source (256 MiB; the read keeps a second buffer).</summary>
    public const int MaxNonSeekableInputBytes = 256 * 1024 * 1024;

    /// <summary>Maximum number of characters read in a text run (like <see cref="ResourceLimits.MaxTextLength"/>).</summary>
    public static int MaxTextLength => ResourceLimits.Default.MaxTextLength;

    /// <summary>
    /// Whether a hyperlink target from a read file may be adopted: http, https, mailto, a bookmark
    /// or a relative path. Other schemes (javascript:, file:, smb:, …) and network paths are not.
    /// </summary>
    public static bool IsAllowedLinkTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Browsers ignore spaces and control characters in places where a filter would stop.
        var compact = string.Concat(target.Where(character => character > ' '));
        if (compact is ['/' or '\\', '/' or '\\', ..])
        {
            return false;
        }

        var end = compact.AsSpan().IndexOfAny(":/?#\\");
        if (end < 0 || compact[end] != ':')
        {
            return true;
        }

        return compact[..end].ToLowerInvariant() is "http" or "https" or "mailto";
    }

    /// <summary>
    /// Whether a link target runs code or carries a document when it is followed (<c>javascript:</c>, <c>vbscript:</c>,
    /// <c>data:</c>); spaces and control characters are ignored as a browser does.
    /// </summary>
    public static bool IsScriptLinkTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var compact = string.Concat(target.Where(character => character > ' '));
        var end = compact.AsSpan().IndexOfAny(":/?#\\");
        return end > 0 && compact[end] == ':' && compact[..end].ToLowerInvariant() is "javascript" or "vbscript" or "data";
    }

    /// <summary>Maximum element depth of a foreign XML part; real documents stay far below it.</summary>
    public const int MaxXmlDepth = 1024;

    /// <summary>XML reader settings for foreign parts: no DTD, no external entities (XXE).</summary>
    public static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>
    /// A reader that fails with an <see cref="XmlException"/> beyond <see cref="MaxXmlDepth"/>.
    /// Loading deep XML costs time quadratic in the depth, so a few kilobytes could block a thread.
    /// </summary>
    public static XmlReader LimitXmlDepth(XmlReader reader) => new DepthLimitedXmlReader(reader);

    /// <summary>
    /// Loads a foreign XML part without DTD processing and within the depth limit. Every reader loads foreign XML
    /// through these three methods: each extra copy of the settings is a place to forget the protection.
    /// </summary>
    public static XDocument LoadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, SafeXml);
        return XDocument.Load(LimitXmlDepth(reader));
    }

    /// <summary>Loads foreign XML bytes; see <see cref="LoadXml(Stream)"/>.</summary>
    public static XDocument LoadXml(byte[] data) => LoadXml(new MemoryStream(data, writable: false));

    /// <summary>Parses a foreign XML text; see <see cref="LoadXml(Stream)"/>.</summary>
    public static XDocument ParseXml(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, SafeXml);
        return XDocument.Load(LimitXmlDepth(reader));
    }

    /// <summary>Opens a ZIP entry such that at most <see cref="MaxDecodedBytes"/> are read.</summary>
    public static Stream OpenBounded(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new BoundedReadStream(entry.Open(), MaxDecodedBytes, entry.FullName);
    }

    /// <summary>Reads a source stream completely, bounded by the matching input limit.</summary>
    public static byte[] ReadAllBounded(Stream input, string what)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReadAllBounded(input, what, input.CanSeek ? MaxInputBytes : MaxNonSeekableInputBytes);
    }

    /// <summary>Reads a source stream completely, bounded by <paramref name="maxBytes"/>.</summary>
    internal static byte[] ReadAllBounded(Stream input, string what, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (input.CanSeek)
        {
            try
            {
                var remaining = input.Length - input.Position;
                if (remaining < 0)
                {
                    throw new DocumentValidationException($"{what}: the stream length is invalid.");
                }

                if (remaining > maxBytes)
                {
                    throw new DocumentValidationException(
                        $"{what}: the file exceeds the limit of {maxBytes} bytes.");
                }

                if (remaining <= Array.MaxLength)
                {
                    var data = new byte[(int)remaining];
                    input.ReadExactly(data);
                    return data;
                }
            }
            catch (NotSupportedException)
            {
                // Fall back to the growable path for streams that claim CanSeek but have no Length.
            }
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new DocumentValidationException(
                    $"{what}: the file exceeds the limit of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Throws if a decoded amount of data exceeds the limit.</summary>
    /// <summary>
    /// Reads a package entry (e.g. an embedded image) completely, bounded by
    /// <paramref name="limit"/> decompressed bytes. The limit is checked while
    /// inflating, so a compression bomb never occupies more than the limit.
    /// </summary>
    public static byte[] ReadBoundedBytes(ZipArchiveEntry entry, long limit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var stream = new BoundedReadStream(entry.Open(), Math.Min(limit, MaxDecodedBytes), entry.FullName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Bytes the media parts of one document (images, previews, charts, embedded objects) may decompress to in
    /// total (256 MiB, as much as one decoded part may hold). Every reference counts: each becomes its own copy, so
    /// one part referenced ten thousand times would otherwise take ten thousand times its size; the document model
    /// keeps further copies, so a read can occupy several times the budget.
    /// </summary>
    public const long MaxMediaBytesPerDocument = MaxDecodedBytes;

    /// <summary>The media budget of one read; see <see cref="MaxMediaBytesPerDocument"/>.</summary>
    public sealed class MediaBudget
    {
        private long _used;

        /// <summary>
        /// Reads a media part after charging its declared size (the ZIP reader never returns more): once the budget
        /// is spent, nothing more is decompressed.
        /// </summary>
        /// <exception cref="DocumentValidationException">The part or the document exceeds its limit.</exception>
        public byte[] Read(ZipArchiveEntry entry, long limit)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _used += entry.Length;
            if (_used > MaxMediaBytesPerDocument)
            {
                throw new DocumentValidationException(
                    $"The media parts of the document exceed the limit of {MaxMediaBytesPerDocument} bytes.");
            }

            return ReadBoundedBytes(entry, limit);
        }
    }

    /// <summary>The data points all charts of one read may hold (the limit of <see cref="ResourceLimits.MaxTableCells"/>).</summary>
    public const int MaxChartPointsPerDocument = 1_000_000;

    /// <summary>
    /// The chart budget of one read: a declared point count, a high point index or repeated cells cost no bytes,
    /// so the points are counted instead.
    /// </summary>
    public sealed class ChartBudget
    {
        private long _used;

        /// <exception cref="DocumentValidationException">The charts of the document exceed <see cref="MaxChartPointsPerDocument"/>.</exception>
        public void Take(long points)
        {
            _used += points;
            if (_used > MaxChartPointsPerDocument)
            {
                throw new DocumentValidationException(
                    $"The charts of the document exceed the limit of {MaxChartPointsPerDocument} data points.");
            }
        }
    }

    /// <summary>
    /// Reads every entry of a package whose signatures are checked (they may cover any part), bounded by the input
    /// limit and in total by <see cref="MaxDecodedBytes"/>. Two entries with the same name fail like a damaged package.
    /// </summary>
    public static Dictionary<string, byte[]> ReadPackageParts(byte[] package, string what)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Length > MaxInputBytes)
        {
            throw new DocumentValidationException($"{what}: the file exceeds the input size limit.");
        }

        using var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Sum(entry => entry.Length) > MaxDecodedBytes)
        {
            throw new DocumentValidationException($"{what}: the package exceeds the decoded size limit.");
        }

        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long remaining = MaxDecodedBytes;
        foreach (var entry in zip.Entries)
        {
            var data = ReadBoundedBytes(entry, remaining);
            remaining -= data.Length;
            parts.Add(entry.FullName, data);
        }

        return parts;
    }

    /// <summary>
    /// Guards recursive descent over foreign structures (nested tables, lists, PDF arrays):
    /// instead of a <see cref="StackOverflowException"/>, which would terminate the whole
    /// process, a deeply nested input fails with a catchable exception.
    /// </summary>
    public static void EnsureStack() => RuntimeHelpers.EnsureSufficientExecutionStack();

    public static void CheckDecodedLength(long length, string what)
    {
        if (length > MaxDecodedBytes)
        {
            throw new DocumentValidationException(
                $"{what} exceeds the limit of {MaxDecodedBytes} bytes when decompressed.");
        }
    }

    /// <summary>
    /// performs a read operation and maps exceptions that can only arise from malformed input
    /// can arise to <see cref="DocumentValidationException"/>. Its own errors and
    /// Breaks remain unchanged.
    /// </summary>
    public static T Run<T>(string format, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception exception) when (IsMalformedInput(exception))
        {
            throw new DocumentValidationException($"{format}: the file is corrupted or invalid.", exception);
        }
    }

    private static bool IsMalformedInput(Exception exception)
        => exception is not IRegletError
            && exception is IndexOutOfRangeException
                or InvalidCastException
                or ArgumentException
                or OverflowException
                or FormatException
                or KeyNotFoundException
                or XmlException
                or InvalidDataException
                or EndOfStreamException
                or CryptographicException
                or InsufficientExecutionStackException;

    private sealed class BoundedReadStream(Stream inner, long limit, string name) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _read += read;
            if (_read > limit)
            {
                throw new DocumentValidationException(
                    $"The package entry '{name}' exceeds the limit of {limit} bytes when decompressed.");
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DepthLimitedXmlReader(XmlReader inner) : XmlReader
    {
        public override int AttributeCount => inner.AttributeCount;
        public override string BaseURI => inner.BaseURI;
        public override int Depth => inner.Depth;
        public override bool EOF => inner.EOF;
        public override bool IsEmptyElement => inner.IsEmptyElement;
        public override string LocalName => inner.LocalName;
        public override string NamespaceURI => inner.NamespaceURI;
        public override XmlNameTable NameTable => inner.NameTable;
        public override XmlNodeType NodeType => inner.NodeType;
        public override string Prefix => inner.Prefix;
        public override ReadState ReadState => inner.ReadState;
        public override string Value => inner.Value;
        public override string GetAttribute(int i) => inner.GetAttribute(i);
        public override string? GetAttribute(string name) => inner.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => inner.GetAttribute(name, namespaceURI);
        public override string? LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => inner.MoveToAttribute(name, ns);
        public override bool MoveToElement() => inner.MoveToElement();
        public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
        public override bool ReadAttributeValue() => inner.ReadAttributeValue();
        public override void ResolveEntity() => inner.ResolveEntity();

        public override bool Read()
        {
            var read = inner.Read();
            if (read && inner.Depth > MaxXmlDepth)
            {
                throw new XmlException($"The XML is nested deeper than {MaxXmlDepth} levels.");
            }

            return read;
        }
    }
}
