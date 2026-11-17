using MagickNetCore.Internal;

namespace MagickNetCore;

/// <summary>
/// Makes image files smaller — by re-encoding, converting to a more efficient format, stripping metadata
/// and downscaling — using ImageMagick (Magick.NET, Q8 build).
/// </summary>
/// <remarks>
/// <para>The per-format meaning of the options is documented on <see cref="ImageCompressionOptions"/>.</para>
/// <para>Output is never half-written: the result is produced completely, written to a temporary file in the
/// destination directory and moved into place. A failed or cancelled operation leaves an existing destination
/// untouched, so the source and destination may be the same file.</para>
/// <para>ImageMagick is synchronous and CPU-bound; the asynchronous methods run it on the thread pool and abort
/// it from its own progress callback when the token is cancelled.</para>
/// <para>Hostile input is contained: decoding uses only the allow-listed formats of <see cref="ImageFormat"/>,
/// chosen by file signature, and process-wide resource limits make oversized images fail with
/// <see cref="ImageException"/>. Those limits are lowered once, never raised.</para>
/// <para>Instances are immutable and safe to use from several threads at once.</para>
/// </remarks>
public sealed class ImageCompressor
{
    private const int BufferSize = 128 * 1024;
    private const string StreamName = "<stream>";
    private readonly ImageCompressionOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ImageCompressor"/> class.</summary>
    /// <param name="options">The options; <see langword="null"/> selects <see cref="ImageCompressionOptions.Default"/>.</param>
    public ImageCompressor(ImageCompressionOptions? options = null)
    {
        _options = options ?? ImageCompressionOptions.Default;
    }

    /// <summary>Gets the options this compressor was created with.</summary>
    public ImageCompressionOptions Options => _options;

    /// <summary>Gets the lower-case file extensions (with the leading dot) of the supported formats.</summary>
    public static IReadOnlyList<string> SupportedExtensions => ImageFormats.Extensions;

    /// <summary>
    /// Determines whether a file is an image this library can compress. An existing file is judged by its
    /// signature (its extension is irrelevant); a path that does not exist yet is judged by its extension.
    /// </summary>
    /// <param name="path">The file to check.</param>
    public static bool IsSupported(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return ImageFormats.FromExtension(path) is { } byExtension && ImageFormats.CanDecode(byExtension);
        }

        try
        {
            Span<byte> header = stackalloc byte[ImageFormats.SignatureLength];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return ImageFormats.Detect(header[..read]) is { } format && ImageFormats.CanDecode(format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Gets a value indicating whether the native ImageMagick build can read <paramref name="format"/>.</summary>
    public static bool CanDecode(ImageFormat format) => ImageFormats.CanDecode(format);

    /// <summary>Gets a value indicating whether the native ImageMagick build can write <paramref name="format"/>.</summary>
    public static bool CanEncode(ImageFormat format) => ImageFormats.CanEncode(format);

    /// <summary>Reads the format, dimensions, frame count and alpha flag of an image without decoding its pixels.</summary>
    /// <param name="path">The image file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="UnsupportedImageFormatException">The file is not an image of a supported format.</exception>
    /// <exception cref="ImageException">The image is damaged.</exception>
    public static async Task<ImageInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        string name = Path.GetFileName(path);
        return await Task.Run(() => ImagePipeline.GetInfo(data, name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compresses one image file.</summary>
    /// <param name="sourceFile">The image to read.</param>
    /// <param name="destinationFile">
    /// The file to write; it may be <paramref name="sourceFile"/> itself. An existing file is replaced. The
    /// output format is <see cref="ImageCompressionOptions.OutputFormat"/> (or the source format) regardless of
    /// this file's extension.
    /// </param>
    /// <param name="progress">An optional progress sink (one item; the fraction grows while it is processed).</param>
    /// <param name="cancellationToken">A token to cancel the operation. Nothing is written when it is cancelled.</param>
    /// <returns>The sizes before and after.</returns>
    /// <exception cref="UnsupportedImageFormatException">The file is not a supported image, or the output format cannot hold it.</exception>
    /// <exception cref="ImageException">The image is damaged, exceeds the resource limits or cannot be encoded.</exception>
    public async Task<ImageCompressionResult> CompressFileAsync(
        string sourceFile,
        string destinationFile,
        IProgress<ImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);

        string source = Path.GetFullPath(sourceFile);
        string name = Path.GetFileName(source);
        IProgress<double>? fraction = progress is null
            ? null
            : new FractionProgress(f => progress.Report(new ImageProgress(0, 1, name, f)));

        ImageCompressionResult result = await CompressCoreAsync(source, Path.GetFullPath(destinationFile), fraction, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new ImageProgress(1, 1, name, 1d));
        return result;
    }

    /// <summary>Compresses an image read from a stream into another stream.</summary>
    /// <param name="source">The stream to read the image from, starting at its current position.</param>
    /// <param name="destination">
    /// The stream that receives the result. It is left open and written only once the image is fully encoded.
    /// </param>
    /// <param name="sourceFormatHint">The format to assume when the data has no recognisable signature.</param>
    /// <param name="progress">An optional progress sink.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="UnsupportedImageFormatException">The data is not a supported image, or the output format cannot hold it.</exception>
    /// <exception cref="ImageException">The image is damaged, exceeds the resource limits or cannot be encoded.</exception>
    public async Task CompressStreamAsync(
        Stream source,
        Stream destination,
        ImageFormat? sourceFormatHint = null,
        IProgress<ImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        byte[] original;
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, BufferSize, cancellationToken).ConfigureAwait(false);
            original = buffer.ToArray();
        }

        EncodedImage encoded = await Task.Run(
            () => ImagePipeline.Encode(
                original,
                sourceFormatHint,
                StreamName,
                _options,
                progress is null ? null : f => progress.Report(new ImageProgress(0, 1, null, f)),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        byte[] output = ChooseOutput(original, encoded, out _);
        await destination.WriteAsync(output, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ImageProgress(1, 1, null, 1d));
    }

    /// <summary>Compresses several image files into a directory.</summary>
    /// <param name="sourceFiles">The images to compress.</param>
    /// <param name="destinationDirectory">
    /// The directory to write to (created when missing). Each output is named after its source, with the
    /// extension of <see cref="ImageCompressionOptions.OutputFormat"/> when one is set. Outputs of this batch never
    /// overwrite each other or another source of this batch — such names get a <c>" (2)"</c>, <c>" (3)"</c> …
    /// suffix. Unrelated existing files are replaced, and a source may be replaced by its own output.
    /// </param>
    /// <param name="progress">An optional progress sink; it receives one report per finished item.</param>
    /// <param name="cancellationToken">A token to cancel the operation. Items not finished yet are not written.</param>
    /// <returns>One result per source, in the order of <paramref name="sourceFiles"/>.</returns>
    /// <remarks>
    /// Up to <see cref="ImageCompressionOptions.MaxDegreeOfParallelism"/> images are processed at once.
    /// A file that fails does not stop the batch; its result carries the exception in
    /// <see cref="ImageCompressionResult.Error"/>.
    /// </remarks>
    public async Task<IReadOnlyList<ImageCompressionResult>> CompressFilesAsync(
        IEnumerable<string> sourceFiles,
        string destinationDirectory,
        IProgress<ImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        string[] sources = [.. sourceFiles.Select(path =>
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(sourceFiles));
            return Path.GetFullPath(path);
        })];
        string directory = Path.GetFullPath(destinationDirectory);
        string[] destinations = PlanDestinations(sources, directory);

        var results = new ImageCompressionResult[sources.Length];
        int completed = 0;
        var gate = new Lock();

        // ImageMagick already parallelises the pixel work of one image (OpenMP, ResourceLimits.Thread), so
        // running more images than cores — or many more than a few — mostly adds memory pressure.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism ?? Math.Clamp(Environment.ProcessorCount, 1, 4)),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForAsync(0, sources.Length, parallelOptions, async (index, token) =>
        {
            ImageCompressionResult result;
            try
            {
                result = await CompressCoreAsync(sources[index], destinations[index], null, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ImageException or IOException or UnauthorizedAccessException)
            {
                // Per-item errors are returned, not thrown: in a batch over user files a few bad ones are an
                // expected outcome, and an aggregate exception would throw away the results of the good ones.
                result = new ImageCompressionResult(sources[index], destinations[index], LengthOrZero(sources[index]), 0, false, ex);
            }

            results[index] = result;
            lock (gate)
            {
                completed++;
                progress?.Report(new ImageProgress(completed, sources.Length, Path.GetFileName(sources[index]), 1d));
            }
        }).ConfigureAwait(false);

        return results;
    }

    /// <summary>Gives every source a destination that no other item of the batch uses or reads from.</summary>
    internal string[] PlanDestinations(string[] sources, string directory)
    {
        var sourceSet = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new string[sources.Length];

        for (int i = 0; i < sources.Length; i++)
        {
            string baseName = Path.GetFileNameWithoutExtension(sources[i]);
            string extension = _options.OutputFormat is { } format
                ? ImageFormats.PreferredExtension(format)
                : Path.GetExtension(sources[i]);

            string candidate = Path.Combine(directory, baseName + extension);
            for (int n = 2; planned.Contains(candidate) || (sourceSet.Contains(candidate) && !SamePath(candidate, sources[i])); n++)
            {
                candidate = Path.Combine(directory, $"{baseName} ({n}){extension}");
            }

            planned.Add(candidate);
            destinations[i] = candidate;
        }

        return destinations;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static long LengthOrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static async Task WriteAtomicallyAsync(string destination, byte[] data, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort clean-up; the original failure is the one worth reporting.
            }

            throw;
        }
    }

    /// <summary>Applies <see cref="ImageCompressionOptions.KeepOriginalIfLarger"/>.</summary>
    private byte[] ChooseOutput(byte[] original, EncodedImage encoded, out bool keptOriginal)
    {
        keptOriginal = _options.KeepOriginalIfLarger
            && encoded.Data.Length >= original.Length
            && encoded.OutputFormat == encoded.SourceFormat
            && !encoded.DimensionsChanged;
        return keptOriginal ? original : encoded.Data;
    }

    private async Task<ImageCompressionResult> CompressCoreAsync(
        string source,
        string destination,
        IProgress<double>? fraction,
        CancellationToken cancellationToken)
    {
        byte[] original = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        string name = Path.GetFileName(source);

        EncodedImage encoded = await Task.Run(
            () => ImagePipeline.Encode(original, hint: null, name, _options, fraction is null ? null : fraction.Report, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        byte[] output = ChooseOutput(original, encoded, out bool keptOriginal);

        // Rewriting a file with its own bytes would only touch its timestamps.
        if (!(keptOriginal && SamePath(source, destination)))
        {
            await WriteAtomicallyAsync(destination, output, cancellationToken).ConfigureAwait(false);
        }

        return new ImageCompressionResult(source, destination, original.LongLength, output.LongLength, keptOriginal);
    }

    /// <summary>Forwards synchronously, so reports keep their order and arrive before the operation completes.</summary>
    private sealed class FractionProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
