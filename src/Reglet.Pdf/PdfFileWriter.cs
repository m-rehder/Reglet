using System.Globalization;

namespace Reglet;

/// <summary>
/// writes a PDF 1.7: header, indirect objects, streams and the classic
/// cross-reference table. Records all byte offsets of the objects.
/// </summary>
internal sealed class PdfFileWriter
{
    private static readonly byte[] BinaryComment = [0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A];

    private readonly Stream _stream;
    private readonly PdfTokenWriter _tokens;
    private readonly PdfObjectRegistry _registry = new();
    private readonly List<long> _offsets = [0];
    private readonly long _start;
    private PdfStandardSecurity? _security;
    private int _encryptObject = -1;

    public PdfFileWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // A non-seekable destination (network, pipe) has no Position; the counting wrapper
        // supplies the written byte count for the xref offsets instead.
        _stream = output.CanSeek ? output : new CountingStream(output);

        // xref offsets count from the PDF file start, not from the stream start: the
        // caller may write into a stream that already contains data.
        _start = _stream.Position;
        _tokens = new PdfTokenWriter(_stream);
        _tokens.WriteRaw("%PDF-1.7\n");
        _stream.Write(BinaryComment, 0, BinaryComment.Length);
    }

    public PdfTokenWriter Tokens => _tokens;

    public PdfObjectRegistry Registry => _registry;

    public long Position => _stream.Position - _start;

    /// <summary>The position of the file start in the stream (for patching the file).</summary>
    public long Start => _start;

    /// <summary>
    /// enables encryption of strings and streams (phase AD). The <c>/Encrypt</c> dictionary
    /// itself is exempt; it must have been reserved beforehand.
    /// </summary>
    public void EnableEncryption(PdfStandardSecurity security, int encryptObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(security);
        _security = security;
        _encryptObject = encryptObjectNumber;
        _tokens.Security = security;
    }

    /// <summary>Reserves an object number.</summary>
    public int ReserveObject() => _registry.Reserve();

    /// <summary>Writes an indirect object and remembers its offset.</summary>
    public void WriteObject(int objectNumber, Action<PdfTokenWriter> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        EnsureSlot(objectNumber);
        _offsets[objectNumber] = Position;

        var previousObject = _tokens.ObjectNumber;
        var previousSuppression = _tokens.SuppressEncryption;
        _tokens.ObjectNumber = objectNumber;
        _tokens.SuppressEncryption = _security is null || objectNumber == _encryptObject;
        try
        {
            _tokens.WriteRaw(objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            body(_tokens);
            _tokens.WriteRaw("\nendobj\n");
        }
        finally
        {
            _tokens.ObjectNumber = previousObject;
            _tokens.SuppressEncryption = previousSuppression;
        }
    }

    /// <summary>Writes a stream object (e.g. content or image data).</summary>
    public void WriteStreamObject(int objectNumber, ReadOnlySpan<byte> data, string? extraDictionary = null)
    {
        EnsureSlot(objectNumber);
        _offsets[objectNumber] = Position;

        if (_security is not null && objectNumber != _encryptObject)
        {
            WriteStreamObjectCore(objectNumber, _security.Encrypt(data.ToArray(), objectNumber), extraDictionary);
        }
        else
        {
            WriteStreamObjectCore(objectNumber, data, extraDictionary);
        }
    }

    private void WriteStreamObjectCore(int objectNumber, ReadOnlySpan<byte> data, string? extraDictionary)
    {
        _tokens.WriteRaw(objectNumber.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
        _tokens.WriteRaw(
            "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
            + (extraDictionary is null ? string.Empty : " " + extraDictionary)
            + " >>\nstream\n");
        _stream.Write(data);
        _tokens.WriteRaw("\nendstream\nendobj\n");
    }

    /// <summary>
    /// writes the xref table and returns its byte offset (for <c>startxref</c>).
    /// </summary>
    public long WriteCrossReferenceTable()
    {
        var offset = Position;

        _tokens.WriteRaw("xref\n");
        _tokens.WriteRaw("0 " + _offsets.Count.ToString(CultureInfo.InvariantCulture) + "\n");
        // A reserved but never written object is free, not "in use at offset 0": readers such as
        // Acrobat otherwise rebuild the xref and then call a signature broken. Free entries form a
        // linked list starting at entry 0.
        var free = Enumerable.Range(1, _offsets.Count - 1).Where(i => _offsets[i] == 0).ToList();
        _tokens.WriteRaw((free.Count > 0 ? free[0] : 0).ToString("D10", CultureInfo.InvariantCulture) + " 65535 f \n");

        for (var i = 1; i < _offsets.Count; i++)
        {
            if (_offsets[i] == 0)
            {
                var next = free.IndexOf(i) + 1;
                _tokens.WriteRaw((next < free.Count ? free[next] : 0).ToString("D10", CultureInfo.InvariantCulture) + " 00001 f \n");
            }
            else
            {
                _tokens.WriteRaw(_offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            }
        }

        return offset;
    }

    /// <summary>Writes trailer, startxref and the EOF marker.</summary>
    public void WriteTrailer(
        int rootObjectNumber,
        int? infoObjectNumber,
        long crossReferenceOffset,
        byte[]? idOriginal = null,
        byte[]? idChanging = null,
        int? encryptObjectNumber = null)
    {
        _tokens.WriteRaw("trailer\n");
        _tokens.WriteDictionary(writer =>
        {
            writer.WriteName("Size");
            writer.WriteInteger(_offsets.Count);
            writer.WriteName("Root");
            writer.WriteReference(rootObjectNumber);
            if (infoObjectNumber is { } info)
            {
                writer.WriteName("Info");
                writer.WriteReference(info);
            }

            if (encryptObjectNumber is { } encrypt)
            {
                writer.WriteName("Encrypt");
                writer.WriteReference(encrypt);
            }

            if (idOriginal is not null && idChanging is not null)
            {
                writer.WriteName("ID");
                writer.WriteArray(array =>
                {
                    array.WriteHexString(idOriginal);
                    array.WriteHexString(idChanging);
                });
            }
        });

        _tokens.WriteRaw("\nstartxref\n");
        _tokens.WriteRaw(crossReferenceOffset.ToString(CultureInfo.InvariantCulture) + "\n");
        _tokens.WriteRaw("%%EOF\n");
    }

    /// <summary>Writes the xref table and trailer and closes the file.</summary>
    public void Complete(
        int rootObjectNumber,
        int? infoObjectNumber = null,
        byte[]? idOriginal = null,
        byte[]? idChanging = null,
        int? encryptObjectNumber = null)
    {
        var offset = WriteCrossReferenceTable();
        WriteTrailer(rootObjectNumber, infoObjectNumber, offset, idOriginal, idChanging, encryptObjectNumber);
    }

    private void EnsureSlot(int objectNumber)
    {
        while (_offsets.Count <= objectNumber)
        {
            _offsets.Add(0);
        }
    }
}
