using System.Diagnostics.CodeAnalysis;

namespace ZipPack;

/// <summary>
/// The single entry point UI layers talk to. It owns the registered <see cref="ICompressionStrategy"/>
/// instances, picks the right one for a file, and forwards the work to it.
/// </summary>
public interface ICompressionService
{
    /// <summary>Gets every registered strategy, in registration order.</summary>
    IReadOnlyList<ICompressionStrategy> Strategies { get; }

    /// <summary>Gets the registered strategies of a given kind, e.g. all <see cref="IArchiver"/>s.</summary>
    /// <typeparam name="TStrategy">The strategy interface to filter by.</typeparam>
    IEnumerable<TStrategy> GetStrategies<TStrategy>() where TStrategy : class, ICompressionStrategy;

    /// <summary>Gets the strategy of a given kind with the specified <see cref="ICompressionStrategy.Id"/>.</summary>
    /// <exception cref="UnsupportedFormatException">No such strategy is registered.</exception>
    TStrategy GetStrategy<TStrategy>(string id) where TStrategy : class, ICompressionStrategy;

    /// <summary>Tries to get the strategy of a given kind with the specified <see cref="ICompressionStrategy.Id"/>.</summary>
    bool TryGetStrategy<TStrategy>(string id, [NotNullWhen(true)] out TStrategy? strategy)
        where TStrategy : class, ICompressionStrategy;

    /// <summary>Selects the archiver that creates archives for the extension of <paramref name="destinationPath"/>.</summary>
    /// <exception cref="UnsupportedFormatException">No registered archiver can create that kind of file.</exception>
    IArchiver GetArchiverForNewArchive(string destinationPath);

    /// <summary>Selects the archiver able to read an existing archive, based on its content and extension.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="UnsupportedFormatException">No registered archiver recognises the file.</exception>
    ValueTask<IArchiver> ResolveArchiverAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an archive. The format is taken from <see cref="ArchiveCreateRequest.FormatId"/> when set,
    /// otherwise from the extension of <see cref="ArchiveCreateRequest.DestinationPath"/>.
    /// </summary>
    Task CreateArchiveAsync(
        ArchiveCreateRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Extracts an archive of any registered format into a directory.</summary>
    Task ExtractArchiveAsync(
        ArchiveExtractRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the entries of an archive of any registered format.</summary>
    Task<IReadOnlyList<ArchiveEntryInfo>> ListEntriesAsync(
        string archivePath,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies the integrity of an archive of any registered format.</summary>
    Task TestArchiveAsync(
        string archivePath,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
