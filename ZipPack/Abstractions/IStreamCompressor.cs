namespace ZipPack;

/// <summary>
/// A compression strategy that transforms one stream of bytes into another, without any notion of files
/// or entries. Useful for in-memory data, network payloads and pipelines.
/// </summary>
public interface IStreamCompressor : ICompressionStrategy
{
    /// <summary>Compresses everything readable from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="source">The stream to read the original data from.</param>
    /// <param name="destination">The stream that receives the compressed data. It is left open.</param>
    /// <param name="options">Compression options; <see langword="null"/> selects the defaults.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task CompressAsync(
        Stream source,
        Stream destination,
        CompressionOptions? options = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Restores the original data from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="source">The stream to read the compressed data from.</param>
    /// <param name="destination">The stream that receives the original data. It is left open.</param>
    /// <param name="password">The password, when the data is protected.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidPasswordException">A password is required or the supplied one is wrong.</exception>
    /// <exception cref="CompressionException">The data is damaged.</exception>
    Task DecompressAsync(
        Stream source,
        Stream destination,
        string? password = null,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
