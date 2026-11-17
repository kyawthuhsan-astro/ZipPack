using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SevenZipCore.Internal;
using SevenZipCore.Interop;

namespace SevenZipCore;

/// <summary>
/// Creates 7z, ZIP, tar, gzip, bzip2 and xz archives with the native 7-Zip engine.
/// </summary>
/// <remarks>
/// <para>The native engine works synchronously; the <c>…Async</c> methods run it on the thread pool so callers
/// such as UI threads are never blocked.</para>
/// <para>Instances are immutable and safe to use from several threads at once.</para>
/// </remarks>
public sealed unsafe class SevenZipCompressor
{
    private readonly SevenZipCompressionOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SevenZipCompressor"/> class.</summary>
    /// <param name="options">The compression options; <see langword="null"/> selects <see cref="SevenZipCompressionOptions.Default"/>.</param>
    /// <exception cref="ArgumentException">The options are not valid for the selected archive format.</exception>
    public SevenZipCompressor(SevenZipCompressionOptions? options = null)
    {
        _options = options ?? SevenZipCompressionOptions.Default;
        ValidateOptions(_options);
    }

    /// <summary>Gets the options this compressor was created with.</summary>
    public SevenZipCompressionOptions Options => _options;

    /// <summary>
    /// Packs files and directories into a new archive. A file is stored under its name; a directory is stored
    /// recursively under its name (like <c>7z a archive file dir</c>).
    /// </summary>
    /// <param name="archivePath">The archive to create. An existing file is replaced.</param>
    /// <param name="paths">The files and directories to pack.</param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written archive is deleted.</param>
    /// <exception cref="FileNotFoundException">A source path does not exist.</exception>
    /// <exception cref="ArgumentException">The sources are not valid for the archive format.</exception>
    /// <exception cref="SevenZipException">7-Zip failed to create the archive.</exception>
    public void CompressFiles(
        string archivePath,
        IEnumerable<string> paths,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        WriteArchiveFile(archivePath, ArchiveItemCollector.Collect(paths), progress, cancellationToken);
    }

    /// <inheritdoc cref="CompressFiles"/>
    public Task CompressFilesAsync(
        string archivePath,
        IEnumerable<string> paths,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressFiles(archivePath, paths, progress, cancellationToken), cancellationToken);

    /// <summary>Packs the content of a directory into a new archive.</summary>
    /// <param name="directory">The directory to pack.</param>
    /// <param name="archivePath">The archive to create. An existing file is replaced.</param>
    /// <param name="includeRootDirectory">
    /// <see langword="true"/> to nest the content under the directory's own name inside the archive.
    /// </param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written archive is deleted.</param>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="SevenZipException">7-Zip failed to create the archive.</exception>
    public void CompressDirectory(
        string directory,
        string archivePath,
        bool includeRootDirectory = false,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        WriteArchiveFile(archivePath, ArchiveItemCollector.CollectDirectory(directory, includeRootDirectory), progress, cancellationToken);
    }

    /// <inheritdoc cref="CompressDirectory"/>
    public Task CompressDirectoryAsync(
        string directory,
        string archivePath,
        bool includeRootDirectory = false,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressDirectory(directory, archivePath, includeRootDirectory, progress, cancellationToken), cancellationToken);

    /// <summary>Creates an archive with a single entry whose content is read from a stream.</summary>
    /// <param name="source">The stream to read the content from, starting at its current position.</param>
    /// <param name="destination">The stream to write the archive to. It must be seekable and is left open.</param>
    /// <param name="entryName">The name of the entry inside the archive.</param>
    /// <param name="progress">An optional progress sink. It is invoked on the thread doing the work.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="SevenZipException">7-Zip failed to create the archive.</exception>
    public void CompressStream(
        Stream source,
        Stream destination,
        string entryName,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        WriteArchive([ArchiveItemCollector.FromStream(source, entryName)], destination, progress, cancellationToken);
    }

    /// <inheritdoc cref="CompressStream"/>
    public Task CompressStreamAsync(
        Stream source,
        Stream destination,
        string entryName,
        IProgress<SevenZipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressStream(source, destination, entryName, progress, cancellationToken), cancellationToken);

    private static void ValidateOptions(SevenZipCompressionOptions options)
    {
        OutArchiveFormat format = options.ArchiveFormat;
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentException($"Unknown archive format '{format}'.", nameof(options));
        }

        if (options.Password is not null && format is not (OutArchiveFormat.SevenZip or OutArchiveFormat.Zip))
        {
            throw new ArgumentException($"The {format} format does not support passwords; use 7z or ZIP.", nameof(options));
        }

        if (options.ThreadCount is <= 0)
        {
            throw new ArgumentException("The thread count must be positive.", nameof(options));
        }

        bool methodIsValid = (options.CompressionMethod, format) switch
        {
            (CompressionMethod.Default, _) => true,
            (CompressionMethod.Lzma2, OutArchiveFormat.SevenZip) => true,
            (CompressionMethod.Deflate or CompressionMethod.Deflate64, OutArchiveFormat.Zip) => true,
            (CompressionMethod.Copy or CompressionMethod.BZip2 or CompressionMethod.Lzma or CompressionMethod.Ppmd,
                OutArchiveFormat.SevenZip or OutArchiveFormat.Zip) => true,
            _ => false,
        };

        if (!methodIsValid)
        {
            throw new ArgumentException(
                $"The {options.CompressionMethod} method cannot be used with the {format} format.", nameof(options));
        }
    }

    private static string GetMethodName(CompressionMethod method) => method switch
    {
        CompressionMethod.Copy => "Copy",
        CompressionMethod.Deflate => "Deflate",
        CompressionMethod.Deflate64 => "Deflate64",
        CompressionMethod.BZip2 => "BZip2",
        CompressionMethod.Lzma => "LZMA",
        CompressionMethod.Lzma2 => "LZMA2",
        CompressionMethod.Ppmd => "PPMd",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private void WriteArchiveFile(
        string archivePath,
        List<ArchiveItem> items,
        IProgress<SevenZipProgress>? progress,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(archivePath);
        if (items.Any(item => string.Equals(item.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The archive cannot be created inside the data it is created from.", nameof(archivePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            using var output = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024);
            WriteArchive(items, output, progress, cancellationToken);
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

    private void WriteArchive(
        IReadOnlyList<ArchiveItem> items,
        Stream destination,
        IProgress<SevenZipProgress>? progress,
        CancellationToken cancellationToken)
    {
        OutArchiveFormat format = _options.ArchiveFormat;
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException("The destination stream must be writable and seekable.", nameof(destination));
        }

        if (format.IsSingleFileFormat() && (items.Count != 1 || items[0].IsDirectory))
        {
            throw new ArgumentException(
                $"The {format} format compresses exactly one file. Bundle several files with tar, 7z or ZIP first.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IOutArchive archive = SevenZipLibrary.CreateArchiveHandler<IOutArchive>((byte)format, format.ToString());
        try
        {
            ApplyProperties(archive);

            var errors = new InteropErrors();
            using var callback = new ArchiveUpdateCallback(items, _options.Password, progress, errors, cancellationToken);
            int result = archive.UpdateItems(new OutStreamWrapper(destination, errors), (uint)items.Count, callback);

            cancellationToken.ThrowIfCancellationRequested();

            if (errors.First is { } error)
            {
                ExceptionDispatchInfo.Throw(error);
            }

            if (result != HResult.Ok)
            {
                throw new SevenZipException($"7-Zip could not create the {format} archive (HRESULT 0x{result:X8}).");
            }

            destination.Flush();
        }
        finally
        {
            SevenZipLibrary.Release(archive);
        }

        if (progress is not null)
        {
            long total = items.Sum(item => item.Size);
            progress.Report(new SevenZipProgress(total, total));
        }
    }

    /// <summary>Builds the 7-Zip method switches (<c>-m…</c>) for the configured options.</summary>
    private Dictionary<string, string> BuildProperties()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        OutArchiveFormat format = _options.ArchiveFormat;

        // The tar handler has no codec to configure.
        if (format != OutArchiveFormat.Tar)
        {
            properties["x"] = ((int)_options.CompressionLevel).ToString();

            if (_options.CompressionMethod != CompressionMethod.Default)
            {
                properties[format == OutArchiveFormat.Zip ? "m" : "0"] = GetMethodName(_options.CompressionMethod);
            }

            if (format == OutArchiveFormat.SevenZip && ParallelLzma2BlockSize() is { } blockSize)
            {
                // Method parameters follow the method name, exactly as in "7z a -m0=LZMA2:c=4m".
                properties["0"] = $"LZMA2:c={blockSize}b";
            }

            if (_options.ThreadCount is { } threads && format is not OutArchiveFormat.GZip)
            {
                properties["mt"] = threads.ToString();
            }

            if (format == OutArchiveFormat.SevenZip)
            {
                if (_options.Solid is { } solid)
                {
                    properties["s"] = solid ? "on" : "off";
                }

                if (_options.Password is not null && _options.EncryptHeaders)
                {
                    properties["he"] = "on";
                }
            }

            if (format == OutArchiveFormat.Zip && _options.Password is not null)
            {
                properties["em"] = _options.ZipEncryptionMethod.ToString().ToUpperInvariant();
            }
        }

        foreach ((string key, string value) in _options.CustomParameters)
        {
            properties[key] = value;
        }

        return properties;
    }

    /// <summary>The LZMA2 block size to request, or <see langword="null"/> to leave 7-Zip's default.</summary>
    internal long? ParallelLzma2BlockSize()
    {
        const long MiB = 1024 * 1024;
        bool lzma2 = _options.CompressionMethod is CompressionMethod.Default or CompressionMethod.Lzma2;
        bool customMethod = _options.CustomParameters.Keys.Any(key => key.Equals("0", StringComparison.OrdinalIgnoreCase));
        if (!lzma2 || customMethod || _options.Lzma2BlockSize == 0)
        {
            return null;
        }

        if (_options.Lzma2BlockSize is { } explicitSize)
        {
            return Math.Max(explicitSize, MiB);
        }

        // Each block keeps two threads busy (match finder + encoder); with fewer than four there is nothing to gain.
        if ((_options.ThreadCount ?? Environment.ProcessorCount) < 4)
        {
            return null;
        }

        return _options.CompressionLevel switch
        {
            CompressionLevel.Low => 1 * MiB,
            CompressionLevel.Normal => 4 * MiB,
            CompressionLevel.High => 8 * MiB,
            CompressionLevel.Ultra => 16 * MiB,
            _ => null, // Store has no codec; Fast uses a tiny dictionary whose default blocks are already small.
        };
    }

    private void ApplyProperties(IOutArchive archive)
    {
        Dictionary<string, string> properties = BuildProperties();
        if (properties.Count == 0)
        {
            return;
        }

        if (archive is not ISetProperties setter)
        {
            throw new SevenZipException($"The {_options.ArchiveFormat} handler does not accept compression options.");
        }

        var names = new nint[properties.Count];
        var values = new PropVariant[properties.Count];
        try
        {
            int i = 0;
            foreach ((string key, string value) in properties)
            {
                names[i] = Marshal.StringToCoTaskMemUni(key);

                // 7-Zip expects numbers as VT_UI4 and everything else as text.
                values[i] = uint.TryParse(value, out uint number) ? PropVariant.FromUInt32(number) : PropVariant.FromString(value);
                i++;
            }

            int result;
            fixed (nint* namePointer = names)
            fixed (PropVariant* valuePointer = values)
            {
                result = setter.SetProperties((nint)namePointer, (nint)valuePointer, (uint)names.Length);
            }

            if (result != HResult.Ok)
            {
                throw new SevenZipException(
                    $"7-Zip rejected the compression options ({string.Join(", ", properties.Select(p => $"{p.Key}={p.Value}"))}) " +
                    $"for the {_options.ArchiveFormat} format (HRESULT 0x{result:X8}).");
            }
        }
        finally
        {
            for (int i = 0; i < names.Length; i++)
            {
                Marshal.FreeCoTaskMem(names[i]);
                values[i].Clear();
            }
        }
    }
}
