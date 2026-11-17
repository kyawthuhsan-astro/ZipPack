using System.Buffers;
using System.IO.Compression;

namespace ZstdCore.Internal;

/// <summary>
/// A write-only stream that compresses with Zstandard on several cores: the input is cut into chunks, every
/// chunk is compressed on the thread pool into its own standard Zstandard frame, and the frames are written in
/// input order.
/// </summary>
/// <remarks>
/// <para>.NET's Zstandard API has no worker threads of its own, so this is how ZstdCore uses more than one core.
/// A sequence of frames is valid Zstandard data: the <c>zstd</c> tool, <see cref="ZstandardStream"/> and every
/// other decoder read it as one stream. Each frame records its content size and (optionally) a checksum.</para>
/// <para>Chunks do not share history, which costs a little ratio; chunks of at least four times the window
/// (the rule <c>zstd -T</c> uses) keep that loss well under one percent.</para>
/// <para>At most <c>threads + 1</c> chunks are in flight, so memory stays bounded however large the input is.
/// Disposing completes the last chunk and waits for every frame to be written.</para>
/// </remarks>
internal sealed class ParallelZstandardStream : Stream
{
    private readonly Stream _destination;
    private readonly bool _leaveOpen;
    private readonly ZstandardCompressionOptions _options;
    private readonly int _chunkSize;
    private readonly int _maxInFlight;
    private readonly Queue<Task<Frame>> _pending = new();

    private byte[] _chunk;
    private int _filled;
    private bool _disposed;

    public ParallelZstandardStream(Stream destination, ZstandardCompressionOptions options, int threads, int chunkSize, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 64 * 1024);

        _destination = destination;
        _options = options;
        _chunkSize = chunkSize;
        _maxInFlight = threads + 1;
        _leaveOpen = leaveOpen;
        _chunk = ArrayPool<byte>.Shared.Rent(chunkSize);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Chooses a chunk size: four times the window (zstd's own multi-threading rule), at least 4 MiB.</summary>
    public static int ChunkSizeFor(int quality, int? windowLog2)
    {
        // The window sizes of the reference levels: 1–5 ≤ 2 MiB, 6–12 4 MiB, 13–17 8 MiB, 18–22 8–128 MiB.
        int window = windowLog2 is { } log ? 1 << Math.Clamp(log, 10, 27)
            : quality <= 5 ? 2 << 20
            : quality <= 12 ? 4 << 20
            : quality <= 19 ? 8 << 20
            : 32 << 20;
        return Math.Clamp(window * 4, 4 << 20, 128 << 20);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            int take = Math.Min(buffer.Length, _chunkSize - _filled);
            buffer[..take].CopyTo(_chunk.AsSpan(_filled));
            _filled += take;
            buffer = buffer[take..];
            if (_filled == _chunkSize)
            {
                DispatchChunk();
                while (_pending.Count >= _maxInFlight)
                {
                    WriteFrame(_pending.Dequeue().GetAwaiter().GetResult());
                }
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(buffer.Length, _chunkSize - _filled);
            buffer[..take].CopyTo(_chunk.AsMemory(_filled));
            _filled += take;
            buffer = buffer[take..];
            if (_filled == _chunkSize)
            {
                DispatchChunk();
                while (_pending.Count >= _maxInFlight)
                {
                    await WriteFrameAsync(await _pending.Dequeue().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Does nothing: frames are written as they complete, and the last one on dispose.</summary>
    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_filled > 0 || _pending.Count == 0)
            {
                // An empty input still becomes one (empty) frame, like ZstandardStream produces.
                DispatchChunk();
            }

            while (_pending.TryDequeue(out Task<Frame>? frame))
            {
                await WriteFrameAsync(await frame.ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            }

            await _destination.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await DrainQuietlyAsync().ConfigureAwait(false);
            if (!_leaveOpen)
            {
                await _destination.DisposeAsync().ConfigureAwait(false);
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private static Frame Compress(byte[] input, int length, ZstandardCompressionOptions options)
    {
        try
        {
            byte[] output = ArrayPool<byte>.Shared.Rent((int)ZstandardEncoder.GetMaxCompressedLength(length));
            using var encoder = new ZstandardEncoder(options);
            encoder.SetSourceLength(length); // Lets decoders size their buffers and verify the frame.
            System.Buffers.OperationStatus status = encoder.Compress(input.AsSpan(0, length), output, out _, out int written, isFinalBlock: true);
            if (status != System.Buffers.OperationStatus.Done)
            {
                ArrayPool<byte>.Shared.Return(output);
                throw new ZstdException($"Zstandard compression failed ({status}).");
            }

            return new Frame(output, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    private void DispatchChunk()
    {
        byte[] input = _chunk;
        int length = _filled;
        ZstandardCompressionOptions options = _options;
        _pending.Enqueue(Task.Run(() => Compress(input, length, options)));
        _chunk = ArrayPool<byte>.Shared.Rent(_chunkSize);
        _filled = 0;
    }

    private void WriteFrame(Frame frame)
    {
        try
        {
            _destination.Write(frame.Buffer, 0, frame.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer);
        }
    }

    private async ValueTask WriteFrameAsync(Frame frame, CancellationToken cancellationToken)
    {
        try
        {
            await _destination.WriteAsync(frame.Buffer.AsMemory(0, frame.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer);
        }
    }

    /// <summary>After a failure: let running chunks finish and give their buffers back.</summary>
    private async ValueTask DrainQuietlyAsync()
    {
        while (_pending.TryDequeue(out Task<Frame>? frame))
        {
            try
            {
                ArrayPool<byte>.Shared.Return((await frame.ConfigureAwait(false)).Buffer);
            }
            catch (Exception ex) when (ex is ZstdException or InvalidOperationException or OutOfMemoryException)
            {
                // The original failure is the one worth reporting.
            }
        }

        if (_chunk.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_chunk);
            _chunk = [];
        }
    }

    private readonly record struct Frame(byte[] Buffer, int Length);
}
