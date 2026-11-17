namespace ZipPack;

/// <summary>
/// A compression strategy for image files: re-encoding, format conversion, metadata stripping and downscaling.
/// Unlike an <see cref="IArchiver"/> it produces images, not archives, so archive resolution never selects it.
/// </summary>
public interface IMagickNetCore : ICompressionStrategy
{
    /// <summary>Gets the format ids accepted by <see cref="ImageCompressOptions.TargetFormatId"/>, e.g. <c>"webp"</c>.</summary>
    IReadOnlyList<string> TargetFormatIds { get; }

    /// <summary>Determines whether a file is an image this strategy can compress (by content when it exists, else by extension).</summary>
    /// <param name="path">The file to check.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<bool> CanCompressAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Compresses one image.</summary>
    /// <param name="request">What to compress and how.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation. Nothing is written when it is cancelled.</param>
    /// <exception cref="UnsupportedFormatException">The file is not a supported image, or the target format cannot hold it.</exception>
    /// <exception cref="CompressionException">The image is damaged, too large or cannot be encoded.</exception>
    /// <exception cref="IOException">The destination exists and <see cref="ImageCompressRequest.Overwrite"/> is not set.</exception>
    Task<ImageCompressResult> CompressImageAsync(
        ImageCompressRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compresses several images into a directory. A file that fails does not stop the batch; its result carries
    /// the error in <see cref="ImageCompressResult.Error"/>.
    /// </summary>
    /// <param name="request">What to compress and how.</param>
    /// <param name="progress">An optional progress sink; <see cref="CompressionProgress.CurrentItem"/> is the finished file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One result per source, in the order of <see cref="ImageBatchCompressRequest.SourcePaths"/>.</returns>
    Task<IReadOnlyList<ImageCompressResult>> CompressImagesAsync(
        ImageBatchCompressRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
