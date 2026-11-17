using ZstdCore;

namespace ZipPack.Strategies;

/// <summary>
/// The Zstandard module, backed by the Zstandard implementation built into .NET. It acts both as an
/// <see cref="IArchiver"/> (<c>.zst</c> for a single file, <c>.tar.zst</c> for several files or directories)
/// and as an <see cref="IStreamCompressor"/> for raw streams.
/// </summary>
/// <remarks>
/// Password protected output uses the AES-256-GCM container of <see cref="ZstdCore"/> and can only be read
/// back by this module. <see cref="CompressionOptions.ThreadCount"/> sets how many cores compress in parallel
/// (all by default); <see cref="CompressionOptions.EngineParameters"/> is not used.
/// </remarks>
public sealed class ZstdStrategy : IArchiver, IStreamCompressor
{
    private static readonly string[] TarExtensions = [".tar.zst", ".tzst"];

    /// <inheritdoc />
    public string Id => "zstd";

    /// <inheritdoc />
    public string DisplayName => "Zstandard";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; } = [".tar.zst", ".tzst", ".zst"];

    /// <inheritdoc />
    public ArchiverCapabilities Capabilities =>
        ArchiverCapabilities.Create
        | ArchiverCapabilities.Extract
        | ArchiverCapabilities.Password
        | ArchiverCapabilities.EncryptFileNames // The whole tar stream, names included, is inside the encrypted container.
        | ArchiverCapabilities.MultipleEntries;

    /// <inheritdoc />
    public async ValueTask<bool> CanOpenAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        try
        {
            return await ZstdFormat.IdentifyFileAsync(archivePath, cancellationToken).ConfigureAwait(false) != ZstdFileKind.NotZstd;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
            var compressor = new ZstdCompressor(ToEngineOptions(request.Options));
            IProgress<ZstdProgress>? relay = ProgressRelay.ForZstd(progress);

            // A lone file becomes a plain .zst frame that every Zstandard tool understands;
            // anything else (or an explicit .tar.zst name) is bundled with tar first.
            bool namedAsTar = TarExtensions.Any(extension => request.DestinationPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            bool singleFile = request.SourcePaths.Count == 1 && File.Exists(request.SourcePaths[0]);

            if (singleFile && !namedAsTar)
            {
                await compressor.CompressFileAsync(request.SourcePaths[0], request.DestinationPath, relay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await compressor.CompressFilesAsync(request.DestinationPath, request.SourcePaths, relay, cancellationToken).ConfigureAwait(false);
            }

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
            var extractor = new ZstdExtractor(request.ArchivePath, StrategyHelpers.NormalizePassword(request.Password));
            if (request.EntryPaths is { Count: > 0 } entryPaths)
            {
                await extractor
                    .ExtractEntriesAsync(entryPaths, request.DestinationDirectory, request.Overwrite, ProgressRelay.ForZstd(progress), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await extractor
                    .ExtractArchiveAsync(request.DestinationDirectory, request.Overwrite, ProgressRelay.ForZstd(progress), cancellationToken)
                    .ConfigureAwait(false);
            }

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
            var extractor = new ZstdExtractor(archivePath, StrategyHelpers.NormalizePassword(password));
            bool encrypted = await ZstdFormat.IdentifyFileAsync(archivePath, cancellationToken).ConfigureAwait(false) == ZstdFileKind.ProtectedZstd;
            IReadOnlyList<ZstdEntry> entries = await extractor.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
            return [.. entries.Select(e => new ArchiveEntryInfo(e.Path, e.IsDirectory, e.Size, CompressedSize: null, e.LastWriteTime, encrypted))];
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
            var extractor = new ZstdExtractor(archivePath, StrategyHelpers.NormalizePassword(password));
            await extractor.TestAsync(ProgressRelay.ForZstd(progress), cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <inheritdoc />
    public Task CompressAsync(
        Stream source,
        Stream destination,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return GuardAsync(async () =>
        {
            var compressor = new ZstdCompressor(ToEngineOptions(options ?? CompressionOptions.Default));
            await compressor.CompressStreamAsync(source, destination, ProgressRelay.ForZstd(progress), cancellationToken).ConfigureAwait(false);
            return true;
        });
    }

    /// <inheritdoc />
    public Task DecompressAsync(
        Stream source,
        Stream destination,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        GuardAsync(async () =>
        {
            await ZstdExtractor
                .DecompressStreamAsync(source, destination, StrategyHelpers.NormalizePassword(password), ProgressRelay.ForZstd(progress), cancellationToken)
                .ConfigureAwait(false);
            return true;
        });

    private static ZstdCompressionOptions ToEngineOptions(CompressionOptions options) => new()
    {
        Quality = options.Preset switch
        {
            CompressionPreset.Store => -3,
            CompressionPreset.Fastest => 1,
            CompressionPreset.Fast => 2,
            CompressionPreset.Normal => 3,
            CompressionPreset.High => 12,
            CompressionPreset.Ultra => 19,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Preset, null),
        },
        Password = StrategyHelpers.NormalizePassword(options.Password),
        ThreadCount = options.ThreadCount,
    };

    /// <summary>Translates the exceptions of the Zstandard engine into the engine-independent ones of ZipPack.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ZstdPasswordException ex)
        {
            throw new InvalidPasswordException(ex.Message, ex);
        }
        catch (ZstdException ex)
        {
            throw new CompressionException(ex.Message, ex);
        }
    }
}
