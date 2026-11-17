using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore.Interop;

/// <summary>
/// Remembers the first managed exception raised inside a callback from native code. Exceptions cannot cross
/// the native boundary, so callbacks return a failure <c>HRESULT</c> and the caller rethrows the original
/// exception once the native call has returned.
/// </summary>
internal sealed class InteropErrors
{
    private Exception? _first;

    public Exception? First => _first;

    public int Record(Exception exception)
    {
        Interlocked.CompareExchange(ref _first, exception, null);
        return exception is OperationCanceledException ? HResult.Abort : HResult.Fail;
    }
}

/// <summary>Exposes a seekable <see cref="Stream"/> to 7-Zip for reading. The stream is not owned.</summary>
[GeneratedComClass]
internal sealed unsafe partial class InStreamWrapper(Stream stream, InteropErrors errors) : IInStream, ISequentialInStream
{
    public int Read(nint data, uint size, nint processedSize)
    {
        try
        {
            int read = size == 0 ? 0 : stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != 0)
            {
                *(uint*)processedSize = (uint)read;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int Seek(long offset, uint seekOrigin, nint newPosition)
    {
        try
        {
            long position = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != 0)
            {
                *(ulong*)newPosition = (ulong)position;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }
}

/// <summary>Exposes a forward-only <see cref="Stream"/> to 7-Zip for reading. The stream is not owned.</summary>
[GeneratedComClass]
internal sealed unsafe partial class SequentialInStreamWrapper(Stream stream, InteropErrors errors) : ISequentialInStream
{
    public int Read(nint data, uint size, nint processedSize)
    {
        try
        {
            int read = size == 0 ? 0 : stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != 0)
            {
                *(uint*)processedSize = (uint)read;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }
}

/// <summary>
/// Exposes a file to 7-Zip for reading without keeping it open: the handle is acquired on the first read and
/// released as soon as the end of the file is reached (and re-acquired should 7-Zip seek back). This keeps
/// the number of open handles small no matter how many files an archive is built from.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class FileInStream(string path, InteropErrors errors) : IInStream, ISequentialInStream, IDisposable
{
    private const int BufferSize = 128 * 1024;
    private readonly Lock _gate = new();
    private FileStream? _stream;
    private long _position;

    public int Read(nint data, uint size, nint processedSize)
    {
        try
        {
            int read = 0;
            if (size != 0)
            {
                lock (_gate)
                {
                    FileStream stream = EnsureOpen();
                    read = stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
                    _position += read;
                    if (read == 0)
                    {
                        Close();
                    }
                }
            }

            if (processedSize != 0)
            {
                *(uint*)processedSize = (uint)read;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int Seek(long offset, uint seekOrigin, nint newPosition)
    {
        try
        {
            lock (_gate)
            {
                long target = (SeekOrigin)seekOrigin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => EnsureOpen().Length + offset,
                    _ => -1,
                };

                if (target < 0)
                {
                    return HResult.InvalidArgument;
                }

                _position = target;
                if (_stream is not null)
                {
                    _stream.Position = target;
                }
            }

            if (newPosition != 0)
            {
                *(ulong*)newPosition = (ulong)_position;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }

    private FileStream EnsureOpen()
    {
        if (_stream is null)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
            _stream.Position = _position;
        }

        return _stream;
    }

    private void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>Exposes a <see cref="Stream"/> to 7-Zip for forward-only writing. The stream is not owned.</summary>
[GeneratedComClass]
internal sealed unsafe partial class SequentialOutStreamWrapper(Stream stream, InteropErrors errors) : ISequentialOutStream
{
    public int Write(nint data, uint size, nint processedSize)
    {
        try
        {
            stream.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
            if (processedSize != 0)
            {
                *(uint*)processedSize = size;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }
}

/// <summary>Exposes a seekable <see cref="Stream"/> to 7-Zip for writing an archive. The stream is not owned.</summary>
[GeneratedComClass]
internal sealed unsafe partial class OutStreamWrapper(Stream stream, InteropErrors errors) : IOutStream, ISequentialOutStream
{
    public int Write(nint data, uint size, nint processedSize)
    {
        try
        {
            stream.Write(new ReadOnlySpan<byte>((void*)data, checked((int)size)));
            if (processedSize != 0)
            {
                *(uint*)processedSize = size;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int Seek(long offset, uint seekOrigin, nint newPosition)
    {
        try
        {
            long position = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != 0)
            {
                *(ulong*)newPosition = (ulong)position;
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            stream.SetLength(checked((long)newSize));
            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }
}
