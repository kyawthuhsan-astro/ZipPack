using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using ZstdCore.Internal;

namespace ZstdCore;

/// <summary>
/// Lists, verifies and extracts Zstandard files written by <see cref="ZstdCompressor"/> or any other
/// Zstandard tool.
/// </summary>
/// <remarks>
/// The content is inspected after decompression: a tar stream is unpacked entry by entry, anything else is
/// treated as a single file named after the archive (without its <c>.zst</c> extension). Password protected
/// containers are recognised automatically.
/// </remarks>
public sealed class ZstdExtractor
{
    private const int TarBlockSize = 512;
    private const int TarMagicOffset = 257;
    private readonly string? _password;

    /// <summary>Initializes a new instance of the <see cref="ZstdExtractor"/> class.</summary>
    /// <param name="archivePath">The Zstandard file to read.</param>
    /// <param name="password">The password, when the file is protected.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public ZstdExtractor(string archivePath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        ArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(ArchivePath))
        {
            throw new FileNotFoundException($"The archive '{archivePath}' does not exist.", archivePath);
        }

        _password = password;
    }

    /// <summary>Gets the full path of the file being read.</summary>
    public string ArchivePath { get; }

    /// <summary>Lists the entries of the archive. This decompresses the whole file.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ZstdPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="ZstdException">The data is damaged.</exception>
    public async Task<IReadOnlyList<ZstdEntry>> GetEntriesAsync(CancellationToken cancellationToken = default) =>
        await WalkAsync(destinationDirectory: null, overwrite: false, progress: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Extracts every entry into <paramref name="destinationDirectory"/>.</summary>
    /// <param name="destinationDirectory">The directory to extract into. It is created when missing.</param>
    /// <param name="overwrite"><see langword="true"/> to replace existing files; <see langword="false"/> to keep them.</param>
    /// <param name="progress">An optional progress sink (measured in compressed bytes read).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ZstdPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="ZstdException">The data is damaged or an entry points outside the destination.</exception>
    public Task ExtractArchiveAsync(
        string destinationDirectory,
        bool overwrite = true,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);
        return WalkAsync(destinationDirectory, overwrite, progress, cancellationToken);
    }

    /// <summary>
    /// Extracts only the given entries (a folder includes everything below it), keeping their paths below
    /// <paramref name="destinationDirectory"/>. The archive is still decompressed in full, because Zstandard
    /// frames cannot be entered in the middle.
    /// </summary>
    /// <param name="entryPaths">Entry paths as returned by <see cref="GetEntriesAsync"/>, compared case-insensitively.</param>
    /// <param name="destinationDirectory">The directory to extract into. It is created when missing.</param>
    /// <param name="overwrite"><see langword="true"/> to replace existing files; <see langword="false"/> to keep them.</param>
    /// <param name="progress">An optional progress sink (measured in compressed bytes read).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ZstdException">None of the entries exists, or the data is damaged.</exception>
    public async Task ExtractEntriesAsync(
        IEnumerable<string> entryPaths,
        string destinationDirectory,
        bool overwrite = true,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryPaths);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        string[] wanted = [.. entryPaths.Select(path => path.Replace('\\', '/').Trim('/')).Where(path => path.Length > 0)];
        bool Include(string name) => wanted.Any(path =>
            name.Equals(path, StringComparison.OrdinalIgnoreCase) || name.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

        List<ZstdEntry> entries = await WalkAsync(destinationDirectory, overwrite, progress, cancellationToken, Include).ConfigureAwait(false);
        if (!entries.Any(entry => Include(entry.Path)))
        {
            throw new ZstdException($"'{Path.GetFileName(ArchivePath)}' does not contain {string.Join(", ", wanted)}.");
        }
    }

    /// <summary>Verifies the archive by decompressing all of it without writing anything to disk.</summary>
    /// <param name="progress">An optional progress sink (measured in compressed bytes read).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ZstdPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="ZstdException">The data is damaged.</exception>
    public Task TestAsync(IProgress<ZstdProgress>? progress = null, CancellationToken cancellationToken = default) =>
        WalkAsync(destinationDirectory: null, overwrite: false, progress, cancellationToken);

    /// <summary>Decompresses a Zstandard stream (plain or password protected) into another stream.</summary>
    /// <param name="source">The stream to read compressed data from, starting at its current position.</param>
    /// <param name="destination">The stream to write the original data to. It is left open.</param>
    /// <param name="password">The password, when the data is protected.</param>
    /// <param name="progress">An optional progress sink (measured in compressed bytes read).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ZstdPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="ZstdException">The data is damaged.</exception>
    public static async Task DecompressStreamAsync(
        Stream source,
        Stream destination,
        string? password = null,
        IProgress<ZstdProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var tracker = new ProgressTracker(progress, source.CanSeek ? source.Length - source.Position : 0);
        try
        {
            await using var counted = new ProgressReadStream(source, tracker, leaveOpen: true);
            await using Stream decoded = await OpenDecoderAsync(counted, password, cancellationToken).ConfigureAwait(false);
            await decoded.CopyToAsync(destination, ZstdCompressor.BufferSize, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            throw new ZstdException("The Zstandard data is corrupted or not valid.", ex);
        }

        tracker.Complete();
    }

    /// <summary>
    /// Builds the read pipeline <c>source → [decryption] → Zstandard → caller</c>. The source is left open.
    /// </summary>
    private static async ValueTask<Stream> OpenDecoderAsync(Stream source, string? password, CancellationToken cancellationToken)
    {
        byte[] header = new byte[EncryptionFormat.HeaderSize];
        int read = await source.ReadAtLeastAsync(header, EncryptionFormat.MagicSize, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        Stream inner;
        if (ZstdFormat.Identify(header.AsSpan(0, read)) == ZstdFileKind.ProtectedZstd)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new ZstdPasswordException("The data is password protected; a password is required.");
            }

            await source.ReadExactlyAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);

            // Key derivation is deliberately slow, so keep it off the caller's thread.
            inner = await Task.Run(() => new DecryptingStream(source, header, password), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            inner = new PrefixedStream(header.AsMemory(0, read), source, leaveOpen: true);
        }

        var options = new ZstandardDecompressionOptions { MaxWindowLog2 = ZstandardCompressionOptions.MaxWindowLog2 };
        return new ZstandardStream(inner, options, leaveOpen: false);
    }

    private static bool IsDataError(Exception exception) =>
        exception is InvalidDataException or EndOfStreamException;

    private static bool LooksLikeTar(ReadOnlySpan<byte> firstBlock) =>
        firstBlock.Length >= TarMagicOffset + 5 && firstBlock.Slice(TarMagicOffset, 5).SequenceEqual("ustar"u8);

    /// <summary>
    /// Returns the full path of a destination directory with exactly one trailing separator. A drive root
    /// such as <c>D:\</c> already ends with its separator and must not get a second one.
    /// </summary>
    internal static string NormalizeRoot(string destinationDirectory)
    {
        string fullPath = Path.GetFullPath(destinationDirectory);
        return Path.EndsInDirectorySeparator(fullPath) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    /// <summary>Maps an entry path into the destination and rejects paths that would escape it.</summary>
    internal static string ResolveSafePath(string root, string entryPath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, entryPath));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullPath.Length <= root.Length || !fullPath.StartsWith(root, comparison))
        {
            throw new ZstdException($"The entry '{entryPath}' would be extracted outside of the destination directory.");
        }

        return fullPath;
    }

    private async Task<List<ZstdEntry>> WalkAsync(
        string? destinationDirectory,
        bool overwrite,
        IProgress<ZstdProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? include = null)
    {
        string? root = null;
        if (destinationDirectory is not null)
        {
            root = NormalizeRoot(destinationDirectory);
            Directory.CreateDirectory(root);
        }

        var entries = new List<ZstdEntry>();
        try
        {
            await using FileStream file = ZstdCompressor.OpenRead(ArchivePath);
            var tracker = new ProgressTracker(progress, file.Length);
            await using var counted = new ProgressReadStream(file, tracker, leaveOpen: true);
            await using Stream decoded = await OpenDecoderAsync(counted, _password, cancellationToken).ConfigureAwait(false);

            // Peek at the first tar block to find out what the frame contains.
            byte[] firstBlock = new byte[TarBlockSize];
            int peeked = await decoded.ReadAtLeastAsync(firstBlock, firstBlock.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            await using var content = new PrefixedStream(firstBlock.AsMemory(0, peeked), decoded, leaveOpen: true);

            if (LooksLikeTar(firstBlock.AsSpan(0, peeked)))
            {
                await WalkTarAsync(content, root, overwrite, entries, tracker, include, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? singleRoot = include is null || include(GetSingleEntryName()) ? root : null;
                await WalkSingleFileAsync(content, singleRoot, overwrite, entries, tracker, cancellationToken).ConfigureAwait(false);
            }

            tracker.Complete();
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            throw new ZstdException($"'{Path.GetFileName(ArchivePath)}' is corrupted or not a valid Zstandard file.", ex);
        }

        return entries;
    }

    private static async Task WalkTarAsync(
        Stream content,
        string? root,
        bool overwrite,
        List<ZstdEntry> entries,
        ProgressTracker tracker,
        Func<string, bool>? include,
        CancellationToken cancellationToken)
    {
        await using (var reader = new TarReader(content, leaveOpen: true))
        {
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isDirectory = entry.EntryType is TarEntryType.Directory;
                bool isFile = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;
                if (!isDirectory && !isFile)
                {
                    continue; // Links, devices and metadata records are not materialised.
                }

                string name = entry.Name.Replace('\\', '/').TrimEnd('/');
                entries.Add(new ZstdEntry(name, isDirectory, isFile ? entry.Length : 0, entry.ModificationTime));
                tracker.CurrentEntry = name;

                if (root is null || (include is not null && !include(name)))
                {
                    continue; // Listing only, or not selected: the data is skipped by the tar reader.
                }

                string target = ResolveSafePath(root, name);
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                }
                else if (overwrite || !File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await entry.ExtractToFileAsync(target, overwrite: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Read to the very end of the frame so the trailing content checksum is verified.
        await content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
    }

    private async Task WalkSingleFileAsync(
        Stream content,
        string? root,
        bool overwrite,
        List<ZstdEntry> entries,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        string name = GetSingleEntryName();
        tracker.CurrentEntry = name;

        string? target = root is null ? null : ResolveSafePath(root, name);
        if (target is not null && !overwrite && File.Exists(target))
        {
            target = null; // Keep the existing file, but still decode to validate and measure.
        }

        long size;
        if (target is null)
        {
            size = await CopyAndCountAsync(content, Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await using var output = new FileStream(
                    target, FileMode.Create, FileAccess.Write, FileShare.None, ZstdCompressor.BufferSize, FileOptions.Asynchronous);
                size = await CopyAndCountAsync(content, output, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                File.Delete(target);
                throw;
            }
        }

        entries.Add(new ZstdEntry(name, IsDirectory: false, size, File.GetLastWriteTimeUtc(ArchivePath)));
    }

    private static async Task<long> CopyAndCountAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ZstdCompressor.BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string GetSingleEntryName()
    {
        string fileName = Path.GetFileName(ArchivePath);
        string extension = Path.GetExtension(fileName);
        bool isZstdExtension = extension.Equals(".zst", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zstd", StringComparison.OrdinalIgnoreCase);

        string name = isZstdExtension ? Path.GetFileNameWithoutExtension(fileName) : fileName + ".out";
        return name.Length > 0 ? name : "data";
    }
}
