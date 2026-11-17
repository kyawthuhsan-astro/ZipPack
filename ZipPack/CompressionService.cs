using System.Diagnostics.CodeAnalysis;
using ZipPack.Strategies;

namespace ZipPack;

/// <summary>
/// The default <see cref="ICompressionService"/>: a registry of strategies plus the logic to pick one.
/// </summary>
/// <remarks>
/// <para>Use <see cref="CreateDefault"/> for the built-in modules, or pass your own set to the constructor.
/// A new module (say, an image compressor) is plugged in without touching this class:</para>
/// <code>
/// var service = new CompressionService([.. CompressionService.DefaultStrategies, new MyImageCompressor()]);
/// var images = service.GetStrategies&lt;IImageCompressor&gt;();
/// </code>
/// <para>The service is immutable after construction and therefore thread-safe; it can be registered as a
/// singleton in a dependency injection container.</para>
/// </remarks>
public sealed class CompressionService : ICompressionService
{
    private readonly ICompressionStrategy[] _strategies;

    /// <summary>Initializes a new instance of the <see cref="CompressionService"/> class.</summary>
    /// <param name="strategies">The strategies to make available.</param>
    /// <exception cref="ArgumentException">Two strategies share the same <see cref="ICompressionStrategy.Id"/>.</exception>
    public CompressionService(IEnumerable<ICompressionStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _strategies = [.. strategies];

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ICompressionStrategy strategy in _strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy, nameof(strategies));
            if (!ids.Add(strategy.Id))
            {
                throw new ArgumentException($"More than one strategy is registered with the id '{strategy.Id}'.", nameof(strategies));
            }
        }
    }

    /// <summary>
    /// Gets the built-in strategies: 7z, ZIP, Zstandard, tar, gzip, bzip2, xz and extract-only 7-Zip formats,
    /// plus the image compressor (<see cref="IMagickNetCore"/>).
    /// </summary>
    public static IReadOnlyList<ICompressionStrategy> DefaultStrategies { get; } =
    [
        SevenZipArchiver.SevenZip,
        SevenZipArchiver.Zip,
        new ZstdStrategy(),
        SevenZipArchiver.Tar,
        SevenZipArchiver.GZip,
        SevenZipArchiver.BZip2,
        SevenZipArchiver.Xz,
        SevenZipArchiver.ReadOnly,
        new MagickImageCompressor(),
    ];

    /// <inheritdoc />
    public IReadOnlyList<ICompressionStrategy> Strategies => _strategies;

    /// <summary>Creates a service with the <see cref="DefaultStrategies"/>.</summary>
    public static CompressionService CreateDefault() => new(DefaultStrategies);

    /// <inheritdoc />
    public IEnumerable<TStrategy> GetStrategies<TStrategy>() where TStrategy : class, ICompressionStrategy =>
        _strategies.OfType<TStrategy>();

    /// <inheritdoc />
    public bool TryGetStrategy<TStrategy>(string id, [NotNullWhen(true)] out TStrategy? strategy)
        where TStrategy : class, ICompressionStrategy
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        strategy = GetStrategies<TStrategy>().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        return strategy is not null;
    }

    /// <inheritdoc />
    public TStrategy GetStrategy<TStrategy>(string id) where TStrategy : class, ICompressionStrategy =>
        TryGetStrategy(id, out TStrategy? strategy)
            ? strategy
            : throw new UnsupportedFormatException($"No {typeof(TStrategy).Name} with the id '{id}' is registered.");

    /// <inheritdoc />
    public IArchiver GetArchiverForNewArchive(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        // The longest matching extension wins, so "data.tar.zst" prefers ".tar.zst" over ".zst".
        return GetStrategies<IArchiver>()
                .Where(archiver => archiver.Capabilities.HasFlag(ArchiverCapabilities.Create))
                .Select(archiver => (Archiver: archiver, Match: MatchLength(archiver, destinationPath)))
                .Where(candidate => candidate.Match > 0)
                .OrderByDescending(candidate => candidate.Match)
                .Select(candidate => candidate.Archiver)
                .FirstOrDefault()
            ?? throw new UnsupportedFormatException(
                $"No registered archiver can create '{Path.GetFileName(destinationPath)}'. Supported extensions: " +
                string.Join(", ", GetStrategies<IArchiver>()
                    .Where(archiver => archiver.Capabilities.HasFlag(ArchiverCapabilities.Create))
                    .SelectMany(archiver => archiver.FileExtensions)) + ".");
    }

    /// <inheritdoc />
    public async ValueTask<IArchiver> ResolveArchiverAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"The archive '{archivePath}' does not exist.", archivePath);
        }

        // Ask the archivers whose extension matches first; the content has the final say.
        IEnumerable<IArchiver> candidates = GetStrategies<IArchiver>()
            .Where(archiver => archiver.Capabilities.HasFlag(ArchiverCapabilities.Extract))
            .OrderByDescending(archiver => MatchLength(archiver, archivePath));

        foreach (IArchiver archiver in candidates)
        {
            if (await archiver.CanOpenAsync(archivePath, cancellationToken).ConfigureAwait(false))
            {
                return archiver;
            }
        }

        throw new UnsupportedFormatException($"'{Path.GetFileName(archivePath)}' is not an archive of a supported format.");
    }

    /// <inheritdoc />
    public Task CreateArchiveAsync(
        ArchiveCreateRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IArchiver archiver = request.FormatId is { Length: > 0 } formatId
            ? GetStrategy<IArchiver>(formatId)
            : GetArchiverForNewArchive(request.DestinationPath);

        return archiver.CreateAsync(request, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExtractArchiveAsync(
        ArchiveExtractRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IArchiver archiver = await ResolveArchiverAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
        await archiver.ExtractAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchiveEntryInfo>> ListEntriesAsync(
        string archivePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        IArchiver archiver = await ResolveArchiverAsync(archivePath, cancellationToken).ConfigureAwait(false);
        return await archiver.ListEntriesAsync(archivePath, password, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task TestArchiveAsync(
        string archivePath,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IArchiver archiver = await ResolveArchiverAsync(archivePath, cancellationToken).ConfigureAwait(false);
        await archiver.TestAsync(archivePath, password, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the length of the longest extension of <paramref name="strategy"/> that <paramref name="path"/> ends with.</summary>
    private static int MatchLength(ICompressionStrategy strategy, string path) =>
        strategy.FileExtensions
            .Where(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(extension => extension.Length)
            .DefaultIfEmpty(0)
            .Max();
}
