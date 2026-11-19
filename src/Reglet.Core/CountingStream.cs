namespace Reglet;

/// <summary>
/// forwards write operations to a non-seekable target stream and counts the bytes.
/// <see cref="Position"/> is the number of bytes written; searching is not supported.
/// This allows PDF and ZIP writers that only advance their offsets to write without a complete
/// write the intermediate buffer directly to a non-seekable target.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private long _written;

    public CountingStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Number of bytes written so far.</summary>
    public long Written => _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        _written += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        _written += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        _written++;
    }
}
