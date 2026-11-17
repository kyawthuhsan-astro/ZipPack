using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZstdCore.Internal;

/// <summary>
/// A read-only stream that opens the container described by <see cref="EncryptionFormat"/>.
/// The header must already have been consumed from the inner stream, which is left open on dispose.
/// </summary>
internal sealed class DecryptingStream : Stream
{
    private readonly Stream _inner;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _plain = new byte[EncryptionFormat.ChunkSize];
    private readonly byte[] _frame = new byte[EncryptionFormat.ChunkSize + EncryptionFormat.TagSize];
    private readonly byte[] _lengthBytes = new byte[sizeof(uint)];
    private int _plainOffset;
    private int _plainCount;
    private uint _counter;
    private bool _finalSeen;

    public DecryptingStream(Stream inner, ReadOnlySpan<byte> header, string password)
    {
        _inner = inner;
        _header = header.ToArray();
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

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_plainCount == 0)
        {
            if (_finalSeen)
            {
                return 0;
            }

            try
            {
                _inner.ReadExactly(_lengthBytes);
                int length = ParseLength(out bool isFinal);
                _inner.ReadExactly(_frame, 0, length + EncryptionFormat.TagSize);
                OpenChunk(length, isFinal);
            }
            catch (EndOfStreamException ex)
            {
                throw new ZstdException("The protected data is truncated.", ex);
            }
        }

        return Drain(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_plainCount == 0)
        {
            if (_finalSeen)
            {
                return 0;
            }

            try
            {
                await _inner.ReadExactlyAsync(_lengthBytes, cancellationToken).ConfigureAwait(false);
                int length = ParseLength(out bool isFinal);
                await _inner.ReadExactlyAsync(_frame.AsMemory(0, length + EncryptionFormat.TagSize), cancellationToken)
                    .ConfigureAwait(false);
                OpenChunk(length, isFinal);
            }
            catch (EndOfStreamException ex)
            {
                throw new ZstdException("The protected data is truncated.", ex);
            }
        }

        return Drain(buffer.Span);
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
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }

        base.Dispose(disposing);
    }

    private int ParseLength(out bool isFinal)
    {
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(_lengthBytes);
        isFinal = (raw & EncryptionFormat.FinalChunkFlag) != 0;
        uint length = raw & ~EncryptionFormat.FinalChunkFlag;
        if (length > EncryptionFormat.ChunkSize)
        {
            throw _counter == 0
                ? new ZstdPasswordException("The password is wrong or the protected data is corrupted.")
                : new ZstdException("The protected data is corrupted.");
        }

        return (int)length;
    }

    private void OpenChunk(int length, bool isFinal)
    {
        Span<byte> nonce = stackalloc byte[EncryptionFormat.NonceSize];
        EncryptionFormat.BuildNonce(_header, _counter, isFinal, nonce);

        try
        {
            _aes.Decrypt(
                nonce,
                _frame.AsSpan(0, length),
                _frame.AsSpan(length, EncryptionFormat.TagSize),
                _plain.AsSpan(0, length),
                _header);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // The very first chunk failing is, in practice, always a wrong password.
            throw _counter == 0
                ? new ZstdPasswordException("The password is wrong or the protected data is corrupted.", ex)
                : new ZstdException("The protected data is corrupted.", ex);
        }

        _counter = checked(_counter + 1);
        _plainOffset = 0;
        _plainCount = length;
        _finalSeen = isFinal;
    }

    private int Drain(Span<byte> buffer)
    {
        int take = Math.Min(buffer.Length, _plainCount);
        _plain.AsSpan(_plainOffset, take).CopyTo(buffer);
        _plainOffset += take;
        _plainCount -= take;
        return take;
    }
}
