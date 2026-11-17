using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZstdCore.Internal;

/// <summary>
/// A write-only stream that seals everything written to it into the container described by
/// <see cref="EncryptionFormat"/>. The inner stream is left open on dispose.
/// </summary>
internal sealed class EncryptingStream : Stream
{
    private readonly Stream _inner;
    private readonly AesGcm _aes;
    private readonly byte[] _header = new byte[EncryptionFormat.HeaderSize];
    private readonly byte[] _plain = new byte[EncryptionFormat.ChunkSize];
    private readonly byte[] _frame = new byte[sizeof(uint) + EncryptionFormat.ChunkSize + EncryptionFormat.TagSize];
    private int _plainCount;
    private uint _counter;
    private bool _headerWritten;
    private bool _completed;

    public EncryptingStream(Stream inner, string password)
    {
        _inner = inner;
        EncryptionFormat.CreateHeader(_header);
        byte[] key = EncryptionFormat.DeriveKey(_header, password);
        try
        {
            _aes = new AesGcm(key, EncryptionFormat.TagSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_completed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            buffer = buffer[Append(buffer)..];
            if (_plainCount == _plain.Length)
            {
                _inner.Write(SealChunk(isFinal: false).Span);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            buffer = buffer[Append(buffer.Span)..];
            if (_plainCount == _plain.Length)
            {
                await _inner.WriteAsync(SealChunk(isFinal: false), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Partial chunks are only emitted on completion; flushing just forwards what is already sealed.
    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_completed)
        {
            _completed = true;
            try
            {
                _inner.Write(SealChunk(isFinal: true).Span);
                _inner.Flush();
            }
            finally
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_plain);
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _completed = true;
            try
            {
                await _inner.WriteAsync(SealChunk(isFinal: true)).ConfigureAwait(false);
                await _inner.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_plain);
            }
        }

        GC.SuppressFinalize(this);
    }

    private int Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        int take = Math.Min(data.Length, _plain.Length - _plainCount);
        data[..take].CopyTo(_plain.AsSpan(_plainCount));
        _plainCount += take;
        return take;
    }

    /// <summary>Seals the buffered plaintext and returns the bytes to append to the inner stream.</summary>
    private ReadOnlyMemory<byte> SealChunk(bool isFinal)
    {
        Span<byte> nonce = stackalloc byte[EncryptionFormat.NonceSize];
        EncryptionFormat.BuildNonce(_header, _counter, isFinal, nonce);
        _counter = checked(_counter + 1);

        int length = _plainCount;
        Span<byte> frame = _frame;
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length | (isFinal ? EncryptionFormat.FinalChunkFlag : 0));
        _aes.Encrypt(
            nonce,
            _plain.AsSpan(0, length),
            frame.Slice(sizeof(uint), length),
            frame.Slice(sizeof(uint) + length, EncryptionFormat.TagSize),
            _header);
        _plainCount = 0;

        ReadOnlyMemory<byte> sealedChunk = _frame.AsMemory(0, sizeof(uint) + length + EncryptionFormat.TagSize);
        if (_headerWritten)
        {
            return sealedChunk;
        }

        // The header travels with the first chunk so that header and data hit the stream in one write.
        _headerWritten = true;
        byte[] withHeader = new byte[_header.Length + sealedChunk.Length];
        _header.CopyTo(withHeader, 0);
        sealedChunk.CopyTo(withHeader.AsMemory(_header.Length));
        return withHeader;
    }
}
