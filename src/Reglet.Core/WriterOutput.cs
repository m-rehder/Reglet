using System.Diagnostics;

namespace Reglet;

/// <summary>
/// shared output logic of the writers. A seekable target stream is written directly.
/// a non-seekable stream (e.g. an HTTP response body) is first buffered in memory
/// built and then transferred asynchronously: such streams often forbid synchronous
/// write operations (ASP.NET Core: <c>AllowSynchronousIO = false</c>), and an error or
/// Cancellation during the build thus leaves no half output.
/// </summary>
internal static class WriterOutput
{
    /// <summary>
    /// builds the document via <paramref name="build"/> and returns the result. Errors during
    /// build are triggered synchronously; only the transfer of a buffer runs asynchronously.
    /// <paramref name="build"/> returns the page count if the format knows it (otherwise null).
    /// </summary>
    public static Task<WriteResult> Write(
        Stream output,
        Func<Stream, int?> build,
        IReadOnlyList<WriteWarning> warnings,
        WriteOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(options);

        var stopwatch = options.CollectMetrics ? Stopwatch.StartNew() : null;

        if (output.CanSeek)
        {
            var start = output.Position;
            var pages = build(output);
            var bytes = output.Position - start;
            return Task.FromResult(CreateResult(warnings, stopwatch, pages, bytes));
        }

        if (options.StreamNonSeekableOutput)
        {
            // Write directly: no complete intermediate buffer. The offsets of the PDF and
            // ZIP writers are tracked via the counted bytes.
            var counting = new CountingStream(output);
            var pages = build(counting);
            counting.Flush();
            return Task.FromResult(CreateResult(warnings, stopwatch, pages, counting.Written));
        }

        var buffer = new MemoryStream();
        int? bufferPages;
        try
        {
            bufferPages = build(buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }

        var length = buffer.Length;
        buffer.Position = 0;
        return CopyAsync(buffer, output, warnings, stopwatch, bufferPages, length, cancellationToken);
    }

    private static async Task<WriteResult> CopyAsync(
        MemoryStream buffer,
        Stream output,
        IReadOnlyList<WriteWarning> warnings,
        Stopwatch? stopwatch,
        int? pages,
        long bytes,
        CancellationToken cancellationToken)
    {
        await using (buffer.ConfigureAwait(false))
        {
            await buffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        return CreateResult(warnings, stopwatch, pages, bytes);
    }

    private static WriteResult CreateResult(
        IReadOnlyList<WriteWarning> warnings,
        Stopwatch? stopwatch,
        int? pages,
        long bytes)
    {
        if (stopwatch is null)
        {
            return new WriteResult(warnings);
        }

        stopwatch.Stop();
        return new WriteResult(warnings)
        {
            Metrics = new WriteMetrics
            {
                Duration = stopwatch.Elapsed,
                Pages = pages,
                Bytes = bytes,
            },
        };
    }
}
