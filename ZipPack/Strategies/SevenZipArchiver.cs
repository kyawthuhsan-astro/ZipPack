using SevenZipCore;

namespace ZipPack.Strategies;

/// <summary>
/// An <see cref="IArchiver"/> backed by the 7-Zip engine. One instance exists per writable format
/// (<see cref="SevenZip"/>, <see cref="Zip"/>, <see cref="Tar"/>, <see cref="GZip"/>, <see cref="BZip2"/>,
/// <see cref="Xz"/>) plus <see cref="ReadOnly"/> for the many formats 7-Zip can only extract.
/// </summary>
public sealed class SevenZipArchiver : IArchiver
{
    private const ArchiverCapabilities ReadWrite = ArchiverCapabilities.Create | ArchiverCapabilities.Extract;
    private readonly OutArchiveFormat? _format;

    private SevenZipArchiver(
        string id,
        string displayName,
        OutArchiveFormat? format,
        ArchiverCapabilities capabilities,
        params string[] fileExtensions)
    {
        Id = id;
        DisplayName = displayName;
        Capabilities = capabilities;
        FileExtensions = fileExtensions;
        _format = format;
    }

    /// <summary>Gets the archiver for 7z archives: best compression, AES-256, optional file name encryption.</summary>
    public static SevenZipArchiver SevenZip { get; } = new(
        "7z",
        "7-Zip",
        OutArchiveFormat.SevenZip,
        ReadWrite | ArchiverCapabilities.Password | ArchiverCapabilities.EncryptFileNames | ArchiverCapabilities.MultipleEntries,
        ".7z");

    /// <summary>Gets the archiver for ZIP archives (AES-256 when a password is set).</summary>
    public static SevenZipArchiver Zip { get; } = new(
        "zip",
        "ZIP",
        OutArchiveFormat.Zip,
        ReadWrite | ArchiverCapabilities.Password | ArchiverCapabilities.MultipleEntries,
        ".zip");

    /// <summary>Gets the archiver for uncompressed tar bundles.</summary>
    public static SevenZipArchiver Tar { get; } = new(
        "tar", "tar", OutArchiveFormat.Tar, ReadWrite | ArchiverCapabilities.MultipleEntries, ".tar");

    /// <summary>Gets the archiver for gzip files (a single compressed file).</summary>
    public static SevenZipArchiver GZip { get; } = new("gzip", "gzip", OutArchiveFormat.GZip, ReadWrite, ".gz");

    /// <summary>Gets the archiver for bzip2 files (a single compressed file).</summary>
    public static SevenZipArchiver BZip2 { get; } = new("bzip2", "bzip2", OutArchiveFormat.BZip2, ReadWrite, ".bz2");

    /// <summary>Gets the archiver for xz files (a single compressed file).</summary>
    public static SevenZipArchiver Xz { get; } = new("xz", "xz", OutArchiveFormat.XZ, ReadWrite, ".xz");

    /// <summary>Gets the extract-only archiver for every other format 7-Zip reads (RAR, CAB, ISO, WIM …).</summary>
    public static SevenZipArchiver ReadOnly { get; } = new(
        "7z-readonly",
        "Other archives (extract only)",
        format: null,
        ArchiverCapabilities.Extract | ArchiverCapabilities.Password | ArchiverCapabilities.MultipleEntries,
        ".rar", ".cab", ".iso", ".wim", ".arj", ".lzh", ".cpio", ".rpm", ".deb", ".chm", ".msi", ".vhd", ".vhdx", ".vmdk", ".dmg", ".lzma", ".z");

    /// <summary>Gets all 7-Zip backed archivers.</summary>
    public static IReadOnlyList<SevenZipArchiver> All { get; } = [SevenZip, Zip, Tar, GZip, BZip2, Xz, ReadOnly];

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; }

    /// <inheritdoc />
    public ArchiverCapabilities Capabilities { get; }

    /// <inheritdoc />
    public ValueTask<bool> CanOpenAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(ArchiveFormatDetector.Detect(archivePath) is { } detected && Accepts(detected));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(
        ArchiveCreateRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return GuardAsync(async () =>
        {
            StrategyHelpers.ValidateCreateRequest(this, request);
            CompressionOptions options = request.Options;
            var compressor = new SevenZipCompressor(new SevenZipCompressionOptions
            {
                ArchiveFormat = _format!.Value,
                CompressionLevel = ToLevel(options.Preset),
                Password = StrategyHelpers.NormalizePassword(options.Password),
                EncryptHeaders = options.EncryptFileNames,
                ThreadCount = options.ThreadCount,
                CustomParameters = options.EngineParameters,
            });

            await compressor
                .CompressFilesAsync(request.DestinationPath, request.SourcePaths, ProgressRelay.ForSevenZip(progress), cancellationToken)
                .ConfigureAwait(false);
            return true;
        });
    }

    /// <inheritdoc />
    public Task ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return GuardAsync(async () =>
        {
            StrategyHelpers.ValidateExtractRequest(request);
            using SevenZipExtractor extractor = await OpenAsync(request.ArchivePath, request.Password, cancellationToken).ConfigureAwait(false);
            if (request.EntryPaths is not { Count: > 0 })
            {
                await extractor
                    .ExtractArchiveAsync(request.DestinationDirectory, request.Overwrite, ProgressRelay.ForSevenZip(progress), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            int[] selected = [.. extractor.Entries.Where(entry => request.Includes(entry.Path)).Select(entry => entry.Index)];
            if (selected.Length == 0)
            {
                throw new CompressionException(
                    $"'{Path.GetFileName(request.ArchivePath)}' does not contain {string.Join(", ", request.EntryPaths)}.");
            }

            await extractor
                .ExtractEntriesAsync(selected, request.DestinationDirectory, request.Overwrite, ProgressRelay.ForSevenZip(progress), cancellationToken)
                .ConfigureAwait(false);
            return true;
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveEntryInfo>> ListEntriesAsync(
        string archivePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        return GuardAsync<IReadOnlyList<ArchiveEntryInfo>>(async () =>
        {
            using SevenZipExtractor extractor = await OpenAsync(archivePath, password, cancellationToken).ConfigureAwait(false);
            return [.. extractor.Entries.Select(ToEntryInfo)];
        });
    }

    /// <inheritdoc />
    public Task TestAsync(
        string archivePath,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        return GuardAsync(async () =>
        {
            using SevenZipExtractor extractor = await OpenAsync(archivePath, password, cancellationToken).ConfigureAwait(false);
            await extractor.TestAsync(ProgressRelay.ForSevenZip(progress), cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    private static Task<SevenZipExtractor> OpenAsync(string archivePath, string? password, CancellationToken cancellationToken) =>
        SevenZipExtractor.OpenAsync(archivePath, StrategyHelpers.NormalizePassword(password), format: null, cancellationToken);

    private static ArchiveEntryInfo ToEntryInfo(ArchiveEntry entry) => new(
        entry.Path.Replace('\\', '/'),
        entry.IsDirectory,
        entry.Size,
        entry.PackedSize,
        entry.LastWriteTime is { } lastWriteTime ? new DateTimeOffset(lastWriteTime) : null,
        entry.IsEncrypted);

    private static CompressionLevel ToLevel(CompressionPreset preset) => preset switch
    {
        CompressionPreset.Store => CompressionLevel.None,
        CompressionPreset.Fastest => CompressionLevel.Fast,
        CompressionPreset.Fast => CompressionLevel.Low,
        CompressionPreset.Normal => CompressionLevel.Normal,
        CompressionPreset.High => CompressionLevel.High,
        CompressionPreset.Ultra => CompressionLevel.Ultra,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };

    /// <summary>Translates the exceptions of the 7-Zip engine into the engine-independent ones of ZipPack.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SevenZipPasswordException ex)
        {
            throw new InvalidPasswordException(ex.Message, ex);
        }
        catch (SevenZipException ex)
        {
            throw new CompressionException(ex.Message, ex);
        }
        catch (ArgumentException ex) when (ex is not ArgumentNullException && ex.ParamName != "request")
        {
            // The engine rejects combinations a format cannot express (e.g. several files in a gzip file).
            throw new UnsupportedFormatException(ex.Message, ex);
        }
    }

    private bool Accepts(InArchiveFormat detected)
    {
        if (_format is { } own)
        {
            return detected == own.ToInArchiveFormat();
        }

        // The read-only archiver takes everything the dedicated archivers (and the Zstandard module) do not.
        return detected != InArchiveFormat.Zstd && !Enum.IsDefined((OutArchiveFormat)(byte)detected);
    }
}
