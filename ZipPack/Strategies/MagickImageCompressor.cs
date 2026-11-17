using MagickNetCore;

namespace ZipPack.Strategies;

/// <summary>
/// The image module, backed by <see cref="MagickNetCore"/> (Magick.NET / ImageMagick). It is an
/// <see cref="IMagickNetCore"/>, not an <see cref="IArchiver"/>, so archive resolution never picks it.
/// </summary>
/// <remarks>
/// The colour profile is always kept, the EXIF orientation is applied to the pixels, JPEG output is
/// progressive and a file is never made larger without a format or size change — the defaults of
/// <see cref="ImageCompressionOptions"/>.
/// </remarks>
public sealed class MagickImageCompressor : IMagickNetCore
{
    private static readonly Dictionary<string, ImageFormat> FormatsById = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = ImageFormat.Jpeg,
        ["jpg"] = ImageFormat.Jpeg,
        ["png"] = ImageFormat.Png,
        ["webp"] = ImageFormat.WebP,
        ["avif"] = ImageFormat.Avif,
        ["jxl"] = ImageFormat.JpegXl,
        ["gif"] = ImageFormat.Gif,
        ["tiff"] = ImageFormat.Tiff,
        ["tif"] = ImageFormat.Tiff,
        ["bmp"] = ImageFormat.Bmp,
        ["heic"] = ImageFormat.Heic,
    };

    /// <inheritdoc />
    public string Id => "image";

    /// <inheritdoc />
    public string DisplayName => "Images (ImageMagick)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ImageCompressor.SupportedExtensions;

    /// <inheritdoc />
    public IReadOnlyList<string> TargetFormatIds { get; } = ["jpeg", "png", "webp", "avif", "jxl", "gif", "tiff", "bmp", "heic"];

    /// <inheritdoc />
    public ValueTask<bool> CanCompressAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ImageCompressor.IsSupported(path));
    }

    /// <inheritdoc />
    public Task<ImageCompressResult> CompressImageAsync(
        ImageCompressRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.SourcePath);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationPath);

        return GuardAsync(async () =>
        {
            if (!request.Overwrite && File.Exists(request.DestinationPath))
            {
                throw new IOException($"The file '{request.DestinationPath}' already exists.");
            }

            var compressor = new ImageCompressor(ToEngineOptions(request.Options));
            ImageCompressionResult result = await compressor
                .CompressFileAsync(request.SourcePath, request.DestinationPath, ProgressRelay.ForMagick(progress), cancellationToken)
                .ConfigureAwait(false);
            return ToResult(result);
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ImageCompressResult>> CompressImagesAsync(
        ImageBatchCompressRequest request,
        IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationDirectory);

        return GuardAsync<IReadOnlyList<ImageCompressResult>>(async () =>
        {
            var compressor = new ImageCompressor(ToEngineOptions(request.Options));
            IReadOnlyList<ImageCompressionResult> results = await compressor
                .CompressFilesAsync(request.SourcePaths, request.DestinationDirectory, ProgressRelay.ForMagick(progress), cancellationToken)
                .ConfigureAwait(false);
            return [.. results.Select(ToResult)];
        });
    }

    private static ImageCompressionOptions ToEngineOptions(ImageCompressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ImageFormat? target = null;
        if (options.TargetFormatId is { Length: > 0 } id)
        {
            target = FormatsById.TryGetValue(id, out ImageFormat format)
                ? format
                : throw new UnsupportedFormatException($"'{id}' is not a known image format. Supported: {string.Join(", ", FormatsById.Keys)}.");
        }

        return new ImageCompressionOptions
        {
            OutputFormat = target,

            // For images a "stronger" preset means a smaller file, i.e. a lower encoder quality.
            Quality = options.Quality ?? options.Preset switch
            {
                CompressionPreset.Store => 95,
                CompressionPreset.Fastest => 90,
                CompressionPreset.Fast => 85,
                CompressionPreset.Normal => 80,
                CompressionPreset.High => 70,
                CompressionPreset.Ultra => 60,
                _ => throw new ArgumentOutOfRangeException(nameof(options), options.Preset, null),
            },
            Lossless = options.Lossless,
            MaxWidth = options.MaxWidth,
            MaxHeight = options.MaxHeight,
            StripMetadata = options.StripMetadata,
        };
    }

    private static ImageCompressResult ToResult(ImageCompressionResult result) => new(
        result.SourcePath,
        result.DestinationPath,
        result.OriginalSize,
        result.CompressedSize,
        result.KeptOriginal,
        result.Error is null ? null : Translate(result.Error));

    /// <summary>Maps an exception of the image engine to the engine-independent ones of ZipPack; others are returned as they are.</summary>
    private static Exception Translate(Exception exception) => exception switch
    {
        UnsupportedImageFormatException ex => new UnsupportedFormatException(ex.Message, ex),
        ImageException ex => new CompressionException(ex.Message, ex),
        _ => exception,
    };

    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ImageException ex)
        {
            throw Translate(ex);
        }
    }
}
