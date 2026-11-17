namespace ZipPack;

/// <summary>
/// A compression strategy that packs files and directories into an archive file and unpacks them again.
/// </summary>
/// <remarks>
/// All operations report failures through <see cref="CompressionException"/> (or a subclass such as
/// <see cref="InvalidPasswordException"/>) so callers never need to know which engine is behind the interface.
/// </remarks>
public interface IArchiver : ICompressionStrategy
{
    /// <summary>Gets the operations and features this archiver supports.</summary>
    ArchiverCapabilities Capabilities { get; }

    /// <summary>
    /// Determines, by inspecting the file content (and falling back to its extension), whether this archiver
    /// can read <paramref name="archivePath"/>.
    /// </summary>
    /// <param name="archivePath">The path of an existing file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<bool> CanOpenAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>Creates a new archive from the files and directories named in <paramref name="request"/>.</summary>
    /// <param name="request">What to pack, where to write it and how.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation. A partially written archive is deleted.</param>
    /// <exception cref="UnsupportedFormatException">The archiver cannot create archives or the request is not valid for its format.</exception>
    /// <exception cref="CompressionException">The archive could not be created.</exception>
    Task CreateAsync(
        ArchiveCreateRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Extracts every entry of an archive into a directory.</summary>
    /// <param name="request">Which archive to unpack and where to.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="CompressionException">The archive is damaged or could not be extracted.</exception>
    Task ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the entries of an archive without extracting them.</summary>
    /// <param name="archivePath">The archive to inspect.</param>
    /// <param name="password">The password, when the archive is protected.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ArchiveEntryInfo>> ListEntriesAsync(
        string archivePath,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of an archive by decoding all of its content without writing anything to disk.
    /// Completes normally when the archive is intact.
    /// </summary>
    /// <param name="archivePath">The archive to verify.</param>
    /// <param name="password">The password, when the archive is protected.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="CompressionException">The archive is damaged.</exception>
    Task TestAsync(
        string archivePath,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Describes what an <see cref="IArchiver"/> is able to do.</summary>
[Flags]
public enum ArchiverCapabilities
{
    /// <summary>No capability.</summary>
    None = 0,

    /// <summary>New archives can be created.</summary>
    Create = 1 << 0,

    /// <summary>Existing archives can be listed, tested and extracted.</summary>
    Extract = 1 << 1,

    /// <summary>The content can be protected with a password.</summary>
    Password = 1 << 2,

    /// <summary>The entry names can be encrypted in addition to the content.</summary>
    EncryptFileNames = 1 << 3,

    /// <summary>An archive can hold more than one file (as opposed to single-stream formats like gzip).</summary>
    MultipleEntries = 1 << 4,
}
