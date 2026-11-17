using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SevenZipCore.Interop;

/// <summary>One item to be written into an archive.</summary>
/// <param name="EntryPath">The path inside the archive, using <c>\</c> as separator (7-Zip converts as the format requires).</param>
/// <param name="IsDirectory"><see langword="true"/> for a directory item.</param>
/// <param name="Size">The content size in bytes (0 for directories or when unknown).</param>
/// <param name="Attributes">The file attributes to record.</param>
/// <param name="CreationTime">The creation time to record.</param>
/// <param name="LastAccessTime">The last access time to record.</param>
/// <param name="LastWriteTime">The modification time to record.</param>
/// <param name="SourcePath">The file to read the content from, or <see langword="null"/>.</param>
/// <param name="SourceStream">The stream to read the content from when there is no <paramref name="SourcePath"/>.</param>
internal sealed record ArchiveItem(
    string EntryPath,
    bool IsDirectory,
    long Size,
    FileAttributes Attributes,
    DateTime CreationTime,
    DateTime LastAccessTime,
    DateTime LastWriteTime,
    string? SourcePath = null,
    Stream? SourceStream = null);

/// <summary>
/// Describes the items of a new archive to 7-Zip and hands out their content streams on demand.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ArchiveUpdateCallback(
    IReadOnlyList<ArchiveItem> items,
    string? password,
    IProgress<SevenZipProgress>? progress,
    InteropErrors errors,
    CancellationToken cancellationToken)
    : IArchiveUpdateCallback, ICryptoGetTextPassword2, IDisposable
{
    private readonly List<FileInStream> _files = [];
    private readonly Lock _gate = new();
    private string? _currentEntry;
    private long _total;

    public int SetTotal(ulong total)
    {
        _total = (long)total;
        return HResult.Ok;
    }

    public int SetCompleted(nint completeValue)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return HResult.Abort;
        }

        if (progress is not null && completeValue != 0)
        {
            try
            {
                progress.Report(new SevenZipProgress((long)*(ulong*)completeValue, _total, _currentEntry));
            }
            catch (Exception ex)
            {
                return errors.Record(ex);
            }
        }

        return HResult.Ok;
    }

    public int GetUpdateItemInfo(uint index, nint newData, nint newProperties, nint indexInArchive)
    {
        // Every item is new: it has new data, new properties and no counterpart in an existing archive.
        if (newData != 0)
        {
            *(int*)newData = 1;
        }

        if (newProperties != 0)
        {
            *(int*)newProperties = 1;
        }

        if (indexInArchive != 0)
        {
            *(uint*)indexInArchive = uint.MaxValue;
        }

        return HResult.Ok;
    }

    public int GetProperty(uint index, uint propId, ref PropVariant value)
    {
        try
        {
            ArchiveItem item = items[(int)index];

            // Ownership of string values passes to 7-Zip, which clears the variant.
            value = propId switch
            {
                PropId.Path => PropVariant.FromString(item.EntryPath),
                PropId.IsDirectory => PropVariant.FromBool(item.IsDirectory),
                PropId.Size => PropVariant.FromUInt64((ulong)item.Size),
                PropId.Attributes => PropVariant.FromUInt32((uint)item.Attributes),
                PropId.CreationTime => PropVariant.FromFileTime(item.CreationTime),
                PropId.LastAccessTime => PropVariant.FromFileTime(item.LastAccessTime),
                PropId.LastWriteTime => PropVariant.FromFileTime(item.LastWriteTime),
                PropId.IsAnti => PropVariant.FromBool(false),
                _ => default,
            };
            return HResult.Ok;
        }
        catch (Exception ex)
        {
            value = default;
            return errors.Record(ex);
        }
    }

    public int GetStream(uint index, out ISequentialInStream? inStream)
    {
        inStream = null;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return HResult.Abort;
            }

            ArchiveItem item = items[(int)index];
            _currentEntry = item.EntryPath;
            if (item.IsDirectory)
            {
                return HResult.Ok;
            }

            if (item.SourcePath is not null)
            {
                var file = new FileInStream(item.SourcePath, errors);
                lock (_gate)
                {
                    _files.Add(file);
                }

                inStream = file;
            }
            else if (item.SourceStream is { CanSeek: true } seekable)
            {
                inStream = new InStreamWrapper(seekable, errors);
            }
            else if (item.SourceStream is { } sequential)
            {
                inStream = new SequentialInStreamWrapper(sequential, errors);
            }

            return HResult.Ok;
        }
        catch (Exception ex)
        {
            return errors.Record(ex);
        }
    }

    public int SetOperationResult(int operationResult) => HResult.Ok;

    public int CryptoGetTextPassword2(out int passwordIsDefined, out nint password1)
    {
        // 7-Zip frees the BSTR.
        passwordIsDefined = password is null ? 0 : 1;
        password1 = Marshal.StringToBSTR(password ?? string.Empty);
        return HResult.Ok;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (FileInStream file in _files)
            {
                file.Dispose();
            }

            _files.Clear();
        }
    }
}
