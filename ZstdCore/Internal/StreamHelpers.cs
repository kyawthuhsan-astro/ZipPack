namespace ZstdCore.Internal;

/// <summary>Accumulates processed bytes and forwards throttled notifications to an <see cref="IProgress{T}"/>.</summary>
internal sealed class ProgressTracker(IProgress<ZstdProgress>? progress, long totalBytes)
{
    private const long ReportInterval = 256 * 1024;
    private long _processed;
    private long _lastReported;

    /// <summary>Gets or sets the entry reported with the next notifications.</summary>
    public string? CurrentEntry { get; set; }

    public void Add(int bytes)
    {
        if (progress is null || bytes <= 0)
        {
            return;
        }

        _processed += bytes;
        if (_processed - _lastReported >= ReportInterval)
        {
            Report();
        }
    }

    /// <summary>Reports the final state of a successfully completed operation.</summary>
    public void Complete()
    {
        if (progress is null)
        {
            return;
        }

        _processed = Math.Max(_processed, totalBytes);
        CurrentEntry = null;
        Report();
    }

    private void Report()
    {
        _lastReported = _processed;
        progress!.Report(new ZstdProgress(_processed, Math.Max(totalBytes, _processed), CurrentEntry));
    }
}

/// <summary>A read-only pass-through stream that counts the bytes read from the inner stream.</summary>
internal sealed class ProgressReadStream(Stream inner, ProgressTracker tracker, bool leaveOpen = false) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        tracker.Add(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        tracker.Add(read);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A read-only stream that first replays bytes that were already consumed from a stream ("peeked")
/// and then continues with the rest of that stream.
/// </summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest, bool leaveOpen = false) : Stream
{
    private ReadOnlyMemory<byte> _prefix = prefix;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) =>
        TryReadPrefix(buffer, out int read) ? read : rest.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        TryReadPrefix(buffer.Span, out int read) ? ValueTask.FromResult(read) : rest.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            rest.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await rest.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private bool TryReadPrefix(Span<byte> buffer, out int read)
    {
        if (_prefix.IsEmpty || buffer.IsEmpty)
        {
            read = 0;
            return buffer.IsEmpty;
        }

        read = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..read].CopyTo(buffer);
        _prefix = _prefix[read..];
        return true;
    }
}
