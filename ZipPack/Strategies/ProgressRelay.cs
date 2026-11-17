namespace ZipPack.Strategies;

/// <summary>
/// Translates the progress type of an engine into <see cref="CompressionProgress"/> and forwards it
/// synchronously, so notifications keep their order.
/// </summary>
internal sealed class ProgressRelay<TEngineProgress>(
    IProgress<CompressionProgress> target,
    Func<TEngineProgress, CompressionProgress> convert) : IProgress<TEngineProgress>
{
    public void Report(TEngineProgress value) => target.Report(convert(value));
}

internal static class ProgressRelay
{
    public static IProgress<SevenZipCore.SevenZipProgress>? ForSevenZip(IProgress<CompressionProgress>? target) =>
        target is null
            ? null
            : new ProgressRelay<SevenZipCore.SevenZipProgress>(
                target, p => new CompressionProgress(p.BytesProcessed, p.TotalBytes, p.CurrentEntry?.Replace('\\', '/')));

    public static IProgress<ZstdCore.ZstdProgress>? ForZstd(IProgress<CompressionProgress>? target) =>
        target is null
            ? null
            : new ProgressRelay<ZstdCore.ZstdProgress>(
                target, p => new CompressionProgress(p.BytesProcessed, p.TotalBytes, p.CurrentEntry));

    /// <summary>Units per image, so that the fraction of the image in progress survives the conversion to whole numbers.</summary>
    private const long ImageUnits = 1000;

    /// <summary>
    /// Images are counted, not measured in bytes: each image is <see cref="ImageUnits"/> units. A report with a
    /// fraction of 1 describes an item already included in <see cref="MagickNetCore.ImageProgress.ItemsProcessed"/>.
    /// </summary>
    public static IProgress<MagickNetCore.ImageProgress>? ForMagick(IProgress<CompressionProgress>? target) =>
        target is null
            ? null
            : new ProgressRelay<MagickNetCore.ImageProgress>(
                target,
                p =>
                {
                    double done = p.CurrentItemFraction >= 1d ? p.ItemsProcessed : p.ItemsProcessed + p.CurrentItemFraction;
                    return new CompressionProgress((long)Math.Round(done * ImageUnits), p.TotalItems * ImageUnits, p.CurrentItem);
                });
}
