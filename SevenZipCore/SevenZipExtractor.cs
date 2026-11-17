using System.Runtime.ExceptionServices;
using SevenZipCore.Internal;
using SevenZipCore.Interop;

namespace SevenZipCore;

/// <summary>
/// Opens an archive of any format 7-Zip can read and lists, tests or extracts its content.
/// </summary>
/// <remarks>
/// <para>The format is detected from the content of the archive (and its file extension as a fallback).</para>
/// <para>The native 7-Zip engine works synchronously; the <c>…Async</c> methods run it on the thread pool so
/// callers such as UI threads are never blocked. Operations on one instance are serialised.</para>
/// </remarks>
public sealed unsafe class SevenZipExtractor : IDisposable
{
    private const ulong MaxSignatureSearchRange = 1 << 22;
    private readonly Lock _gate = new();
    private readonly IInArchive _archive;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ArchiveOpenCallback _openCallback;
    private readonly string? _password;
    private bool _disposed;

    private SevenZipExtractor(
        IInArchive archive,
        Stream stream,
        bool leaveOpen,
        ArchiveOpenCallback openCallback,
        InArchiveFormat format,
        string? archivePath,
        string? password)
    {
        _archive = archive;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _openCallback = openCallback;
        _password = password;
        Format = format;
        ArchivePath = archivePath;
        Entries = ReadEntries();
        IsSolid = ReadArchiveFlag(PropId.Solid);
        TotalSize = Entries.Sum(entry => entry.Size);
    }

    /// <summary>Gets the detected format of the archive.</summary>
    public InArchiveFormat Format { get; }

    /// <summary>Gets the full path of the archive, or <see langword="null"/> when it was opened from a stream.</summary>
    public string? ArchivePath { get; }

    /// <summary>Gets the items stored in the archive.</summary>
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Gets a value indicating whether the archive is solid (items share compression blocks).</summary>
    public bool IsSolid { get; }

    /// <summary>Gets the sum of the uncompressed sizes of all items.</summary>
    public long TotalSize { get; }

    /// <summary>Opens an archive file.</summary>
    /// <param name="archivePath">The path of the archive.</param>
    /// <param name="password">The password, when the archive is encrypted.</param>
    /// <param name="format">The format to assume; <see langword="null"/> to detect it.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="SevenZipPasswordException">The archive headers are encrypted and the password is missing or wrong.</exception>
    /// <exception cref="SevenZipArchiveException">The file is not a readable archive.</exception>
    /// <exception cref="SevenZipLibraryException">The native 7-Zip library is unavailable.</exception>
    public static SevenZipExtractor Open(string archivePath, string? password = null, InArchiveFormat? format = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        string fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The archive '{archivePath}' does not exist.", archivePath);
        }

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.RandomAccess);
        try
        {
            return OpenCore(stream, leaveOpen: false, fullPath, password, format);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens an archive from a readable, seekable stream.</summary>
    /// <param name="archiveStream">The stream holding the archive from its very beginning; its current position is ignored.</param>
    /// <param name="password">The password, when the archive is encrypted.</param>
    /// <param name="format">The format to assume; <see langword="null"/> to detect it from the signature.</param>
    /// <param name="leaveOpen"><see langword="true"/> to leave the stream open when the extractor is disposed.</param>
    /// <exception cref="SevenZipPasswordException">The archive headers are encrypted and the password is missing or wrong.</exception>
    /// <exception cref="SevenZipArchiveException">The stream is not a readable archive.</exception>
    public static SevenZipExtractor Open(
        Stream archiveStream,
        string? password = null,
        InArchiveFormat? format = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanRead || !archiveStream.CanSeek)
        {
            throw new ArgumentException("The archive stream must be readable and seekable.", nameof(archiveStream));
        }

        return OpenCore(archiveStream, leaveOpen, archivePath: null, password, format);
    }

    /// <summary>Opens an archive file without blocking the calling thread.</summary>
    /// <inheritdoc cref="Open(string, string?, InArchiveFormat?)"/>
    public static Task<SevenZipExtractor> OpenAsync(
        string archivePath,
        string? password = null,
        InArchiveFormat? format = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(archivePath, password, format), cancellationToken);

    /// <summary>Extracts every item into a directory.</summary>
    /// <param name="destinationDirectory">The directory to extract into. It is created when missing.</param>
    /// <param name="overwrite"><see langword="true"/> to replace existing files; <see langword="false"/> to keep them.</param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="SevenZipPasswordException">The password is missing or wrong.</exception>
    /// <exception cref="SevenZipArchiveException">An item is damaged or has an unsafe path.</exception>
    public void ExtractArchive(
        string destinationDirectory,
        bool overwrite = true,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);
        Run(indices: null, testMode: false, new DirectoryExtractSink(destinationDirectory, overwrite), progress, cancellationToken);
    }

    /// <inheritdoc cref="ExtractArchive"/>
    public Task ExtractArchiveAsync(
        string destinationDirectory,
        bool overwrite = true,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractArchive(destinationDirectory, overwrite, progress, cancellationToken), cancellationToken);

    /// <summary>Extracts the selected items into a directory.</summary>
    /// <param name="entryIndices">The <see cref="ArchiveEntry.Index"/> values of the items to extract.</param>
    /// <param name="destinationDirectory">The directory to extract into. It is created when missing.</param>
    /// <param name="overwrite"><see langword="true"/> to replace existing files; <see langword="false"/> to keep them.</param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public void ExtractEntries(
        IEnumerable<int> entryIndices,
        string destinationDirectory,
        bool overwrite = true,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIndices);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        // 7-Zip requires the indices in ascending order.
        uint[] indices = [.. entryIndices.Distinct().Order().Select(ValidateIndex)];
        Run(indices, testMode: false, new DirectoryExtractSink(destinationDirectory, overwrite), progress, cancellationToken);
    }

    /// <inheritdoc cref="ExtractEntries"/>
    public Task ExtractEntriesAsync(
        IEnumerable<int> entryIndices,
        string destinationDirectory,
        bool overwrite = true,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractEntries(entryIndices, destinationDirectory, overwrite, progress, cancellationToken), cancellationToken);

    /// <summary>Extracts the content of one item into a stream.</summary>
    /// <param name="entryIndex">The <see cref="ArchiveEntry.Index"/> of the item.</param>
    /// <param name="destination">The stream to write to. It is left open.</param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public void ExtractEntry(
        int entryIndex,
        Stream destination,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        uint index = ValidateIndex(entryIndex);
        Run([index], testMode: false, new StreamExtractSink(entryIndex, destination), progress, cancellationToken);
    }

    /// <inheritdoc cref="ExtractEntry"/>
    public Task ExtractEntryAsync(
        int entryIndex,
        Stream destination,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractEntry(entryIndex, destination, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Verifies the integrity of the archive by decoding every item without writing anything.
    /// Returns normally when the archive is intact.
    /// </summary>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="SevenZipPasswordException">The password is missing or wrong.</exception>
    /// <exception cref="SevenZipArchiveException">An item is damaged.</exception>
    public void Test(IProgress<SevenZipProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Run(indices: null, testMode: true, sink: null, progress, cancellationToken);

    /// <inheritdoc cref="Test"/>
    public Task TestAsync(IProgress<SevenZipProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Test(progress, cancellationToken), cancellationToken);

    /// <summary>Closes the archive and releases the native handler and the underlying file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _archive.Close();
            SevenZipLibrary.Release(_archive);
            _openCallback.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private static SevenZipExtractor OpenCore(Stream stream, bool leaveOpen, string? archivePath, string? password, InArchiveFormat? format)
    {
        IReadOnlyList<InArchiveFormat> candidates = format is { } forced
            ? [forced]
            : ArchiveFormatDetector.GetCandidates(stream, archivePath);

        string displayName = archivePath is null ? "The stream" : $"'{Path.GetFileName(archivePath)}'";
        if (candidates.Count == 0)
        {
            throw new SevenZipArchiveException($"{displayName} is not a recognised archive.");
        }

        foreach (InArchiveFormat candidate in candidates)
        {
            stream.Position = 0;
            var errors = new InteropErrors();
            var callback = new ArchiveOpenCallback(password, archivePath, errors);
            IInArchive archive = SevenZipLibrary.CreateArchiveHandler<IInArchive>((byte)candidate, candidate.ToString());

            ulong searchRange = MaxSignatureSearchRange;
            int result = archive.Open(new InStreamWrapper(stream, errors), ref searchRange, callback);
            if (result == HResult.Ok)
            {
                try
                {
                    return new SevenZipExtractor(archive, stream, leaveOpen, callback, candidate, archivePath, password);
                }
                catch
                {
                    archive.Close();
                    SevenZipLibrary.Release(archive);
                    callback.Dispose();
                    throw;
                }
            }

            archive.Close();
            SevenZipLibrary.Release(archive);
            callback.Dispose();

            if (callback.PasswordRequested)
            {
                throw new SevenZipPasswordException(
                    password is null
                        ? $"{displayName} is encrypted; a password is required."
                        : $"The password for {displayName} is wrong.");
            }

            if (errors.First is { } error)
            {
                ExceptionDispatchInfo.Throw(error);
            }
        }

        throw new SevenZipArchiveException($"{displayName} is damaged or not a supported archive.");
    }

    private void Run(uint[]? indices, bool testMode, IExtractSink? sink, IProgress<SevenZipProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var errors = new InteropErrors();
            var callback = new ArchiveExtractCallback(Entries, sink, _password, progress, errors, cancellationToken);

            int result;
            try
            {
                fixed (uint* indexPointer = indices)
                {
                    uint count = indices is null ? uint.MaxValue : (uint)indices.Length;
                    result = _archive.Extract((nint)indexPointer, count, testMode ? 1 : 0, callback);
                }
            }
            finally
            {
                callback.CloseCurrent(succeeded: false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (callback.PasswordMissing)
            {
                throw new SevenZipPasswordException("The archive is encrypted; a password is required.");
            }

            if (errors.First is { } error)
            {
                ExceptionDispatchInfo.Throw(error);
            }

            if (callback.FirstEntryError is { } entryError)
            {
                throw callback.FailedEntries == 1
                    ? entryError
                    : entryError is SevenZipPasswordException
                        ? new SevenZipPasswordException($"{entryError.Message} ({callback.FailedEntries} entries failed.)", entryError)
                        : new SevenZipArchiveException($"{entryError.Message} ({callback.FailedEntries} entries failed.)", entryError);
            }

            if (result != HResult.Ok)
            {
                throw new SevenZipArchiveException($"7-Zip could not read the archive (HRESULT 0x{result:X8}).");
            }

            if (progress is not null && TotalSize > 0 && indices is null)
            {
                progress.Report(new SevenZipProgress(TotalSize, TotalSize));
            }
        }
    }

    private uint ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Entries.Count);
        return (uint)index;
    }

    private List<ArchiveEntry> ReadEntries()
    {
        if (_archive.GetNumberOfItems(out uint count) != HResult.Ok)
        {
            throw new SevenZipArchiveException("The archive item table could not be read.");
        }

        var entries = new List<ArchiveEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            string path = ReadItem(i, PropId.Path, value => value.AsString()) ?? string.Empty;
            if (path.Length == 0)
            {
                // Single-stream formats (gzip, bzip2, xz …) often do not store a name.
                path = ArchivePath is null ? "data" : Path.GetFileNameWithoutExtension(ArchivePath);
            }

            entries.Add(new ArchiveEntry
            {
                Index = (int)i,
                Path = path,
                IsDirectory = ReadItem(i, PropId.IsDirectory, value => value.AsBool()),
                Size = (long)(ReadItem(i, PropId.Size, value => value.AsUInt64()) ?? 0),
                PackedSize = (long?)ReadItem(i, PropId.PackedSize, value => value.AsUInt64()),
                Attributes = (FileAttributes)(uint)(ReadItem(i, PropId.Attributes, value => value.AsUInt64()) ?? 0),
                LastWriteTime = ReadItem(i, PropId.LastWriteTime, value => value.AsDateTime()),
                CreationTime = ReadItem(i, PropId.CreationTime, value => value.AsDateTime()),
                LastAccessTime = ReadItem(i, PropId.LastAccessTime, value => value.AsDateTime()),
                IsEncrypted = ReadItem(i, PropId.Encrypted, value => value.AsBool()),
                Crc = (uint?)ReadItem(i, PropId.Crc, value => value.AsUInt64()),
                Method = ReadItem(i, PropId.Method, value => value.AsString()),
                Comment = ReadItem(i, PropId.Comment, value => value.AsString()),
            });
        }

        return entries;
    }

    private T? ReadItem<T>(uint index, uint propId, Func<PropVariant, T?> read)
    {
        PropVariant value = default;
        try
        {
            return _archive.GetProperty(index, propId, ref value) == HResult.Ok ? read(value) : default;
        }
        finally
        {
            value.Clear();
        }
    }

    private bool ReadArchiveFlag(uint propId)
    {
        PropVariant value = default;
        try
        {
            return _archive.GetArchiveProperty(propId, ref value) == HResult.Ok && value.AsBool();
        }
        finally
        {
            value.Clear();
        }
    }
}
