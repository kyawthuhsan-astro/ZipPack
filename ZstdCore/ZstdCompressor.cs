using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using ZstdCore.Internal;

namespace ZstdCore;

/// <summary>
/// Compresses streams, files and directory trees with the Zstandard implementation built into .NET.
/// </summary>
/// <remarks>
/// <para>A single file or stream becomes a plain Zstandard frame (<c>.zst</c>) that any Zstandard tool can read.</para>
/// <para>Several files or a directory are first bundled into a PAX tar stream and then compressed
/// (<c>.tar.zst</c>), the conventional way of archiving with Zstandard.</para>
/// <para>When <see cref="ZstdCompressionOptions.Password"/> is set the output is additionally wrapped in an
/// authenticated AES-256-GCM container; see the remarks on that property.</para>
/// <para>Instances are immutable and safe to use from several threads at once.</para>
/// </remarks>
public sealed class ZstdCompressor
{
    internal const int BufferSize = 128 * 1024;
    private readonly ZstdCompressionOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ZstdCompressor"/> class.</summary>
    /// <param name="options">The compression options; <see langword="null"/> selects <see cref="ZstdCompressionOptions.Default"/>.</param>
    public ZstdCompressor(ZstdCompressionOptions? options = null)
    {
        _options = options ?? ZstdCompressionOptions.Default;
    }

    /// <summary>Gets the options this compressor was created with.</summary>
    public ZstdCompressionOptions Options => _options;

    /// <summary>Compresses everything readable from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="source">The stream to read from, starting at its current position.</param>
    /// <param name="destination">The stream to write the compressed data to. It is left open.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task CompressStreamAsync(
        Stream source,
        Stream destination,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        long? sourceLength = source.CanSeek ? source.Length - source.Position : null;
        var tracker = new ProgressTracker(progress, sourceLength ?? 0);

        Stream pipeline = OpenPipeline(destination, sourceLength);
        try
        {
            await CopyAsync(source, pipeline, tracker, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeQuietlyAsync(pipeline).ConfigureAwait(false);
            throw;
        }

        await pipeline.DisposeAsync().ConfigureAwait(false);
        tracker.Complete();
    }

    /// <summary>Compresses a single file into a Zstandard file (<c>.zst</c>).</summary>
    /// <param name="sourceFile">The file to compress.</param>
    /// <param name="destinationFile">The file to create. An existing file is replaced.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written file is deleted.</param>
    public async Task CompressFileAsync(
        string sourceFile,
        string destinationFile,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);

        await using FileStream source = OpenRead(sourceFile);
        await WriteFileAsync(
            destinationFile,
            output => CompressStreamAsync(source, output, progress, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Packs files and directories into a compressed tar archive (<c>.tar.zst</c>). A file is stored under its
    /// name; a directory is stored recursively under its name.
    /// </summary>
    /// <param name="archivePath">The archive to create. An existing file is replaced.</param>
    /// <param name="paths">The files and directories to pack.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written archive is deleted.</param>
    public Task CompressFilesAsync(
        string archivePath,
        IEnumerable<string> paths,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        List<SourceItem> items = SourceCollector.Collect(paths);
        return WriteFileAsync(archivePath, output => WriteTarAsync(items, output, progress, cancellationToken));
    }

    /// <summary>Packs the content of a directory into a compressed tar archive (<c>.tar.zst</c>).</summary>
    /// <param name="directory">The directory to pack.</param>
    /// <param name="archivePath">The archive to create. An existing file is replaced.</param>
    /// <param name="includeRootDirectory">
    /// <see langword="true"/> to nest the content under the directory's own name inside the archive.
    /// </param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written archive is deleted.</param>
    public Task CompressDirectoryAsync(
        string directory,
        string archivePath,
        bool includeRootDirectory = false,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        List<SourceItem> items = SourceCollector.CollectDirectory(directory, includeRootDirectory);
        return WriteFileAsync(archivePath, output => WriteTarAsync(items, output, progress, cancellationToken));
    }

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static async Task CopyAsync(Stream source, Stream destination, ProgressTracker tracker, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                tracker.Add(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Runs <paramref name="write"/> against a new file and removes the file again when writing fails.</summary>
    private static async Task WriteFileAsync(string path, Func<Stream, Task> write)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            await using var output = new FileStream(
                fullPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await write(output).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort clean-up; the original failure is the one worth reporting.
            }

            throw;
        }
    }

    private static async ValueTask DisposeQuietlyAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ObjectDisposedException)
        {
            // Finishing an aborted frame may fail; the original failure is the one worth reporting.
        }
    }

    /// <summary>
    /// Builds the write pipeline <c>caller → Zstandard → [encryption] → destination</c>.
    /// Disposing the returned stream completes the frame (and container) but leaves the destination open.
    /// </summary>
    private Stream OpenPipeline(Stream destination, long? sourceLength)
    {
        bool encrypt = !string.IsNullOrEmpty(_options.Password);
        Stream target = encrypt ? new EncryptingStream(destination, _options.Password!) : destination;
        ZstandardCompressionOptions native = _options.ToNativeOptions();

        int threads = _options.EffectiveThreadCount;
        int chunkSize = ParallelZstandardStream.ChunkSizeFor(native.Quality, _options.WindowLog2);
        bool worthSplitting = sourceLength is not { } knownLength || knownLength > chunkSize;
        if (threads > 1 && worthSplitting)
        {
            return new ParallelZstandardStream(target, native, threads, chunkSize, leaveOpen: !encrypt);
        }

        var zstd = new ZstandardStream(target, native, leaveOpen: !encrypt);
        if (sourceLength is { } length)
        {
            // Records the content size in the frame header so decoders can size their buffers up front.
            zstd.SetSourceLength(length);
        }

        return zstd;
    }

    private async Task WriteTarAsync(
        List<SourceItem> items,
        Stream output,
        IProgress<ZstdProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tracker = new ProgressTracker(progress, items.Sum(item => item.Size));

        Stream pipeline = OpenPipeline(output, sourceLength: null);
        try
        {
            await using var tar = new TarWriter(pipeline, TarEntryFormat.Pax, leaveOpen: true);
            foreach (SourceItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracker.CurrentEntry = item.EntryPath;

                if (item.IsDirectory)
                {
                    var directoryEntry = new PaxTarEntry(TarEntryType.Directory, item.EntryPath + "/")
                    {
                        ModificationTime = Directory.GetLastWriteTimeUtc(item.FullPath),
                    };
                    await tar.WriteEntryAsync(directoryEntry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using FileStream file = OpenRead(item.FullPath);
                var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, item.EntryPath)
                {
                    ModificationTime = File.GetLastWriteTimeUtc(item.FullPath),
                    DataStream = new ProgressReadStream(file, tracker, leaveOpen: true),
                };
                await tar.WriteEntryAsync(fileEntry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisposeQuietlyAsync(pipeline).ConfigureAwait(false);
            throw;
        }

        await pipeline.DisposeAsync().ConfigureAwait(false);
        tracker.Complete();
    }
}
