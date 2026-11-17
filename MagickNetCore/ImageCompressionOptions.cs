namespace MagickNetCore;

/// <summary>Options controlling how <see cref="ImageCompressor"/> re-encodes images.</summary>
/// <remarks>
/// <para><b>What <see cref="Quality"/> and <see cref="Lossless"/> mean per output format</b></para>
/// <list type="table">
/// <listheader><term>Format</term><description>Behaviour</description></listheader>
/// <item><term><see cref="ImageFormat.Jpeg"/></term><description>
/// <see cref="Quality"/> is the libjpeg quality factor (1–100). Chroma subsampling is 4:4:4 at quality 90 and
/// above (keeps sharp colour edges) and 4:2:0 below (the usual photographic trade-off). Huffman tables are
/// always optimised; <see cref="Progressive"/> selects progressive scans. JPEG has no lossless mode here, so
/// <see cref="Lossless"/> is ignored.</description></item>
/// <item><term><see cref="ImageFormat.Png"/></term><description>
/// Always lossless; <see cref="Quality"/> is not used. The encoder uses zlib level 9 and keeps the smallest of
/// the filter choices the Magick.NET <c>PngOptimizer</c> tries (sub, Paeth, adaptive, MNG-adaptive); an opaque
/// alpha channel is dropped first. Interlacing is never used because it makes PNG files larger.</description></item>
/// <item><term><see cref="ImageFormat.WebP"/></term><description>
/// Lossy: <see cref="Quality"/> is the libwebp quality (1–100). <see cref="Lossless"/>: libwebp lossless mode at
/// maximum effort; <see cref="Quality"/> is not used. The slowest, strongest encoder method (6) is always used.</description></item>
/// <item><term><see cref="ImageFormat.Avif"/></term><description>
/// Lossy: <see cref="Quality"/> is the libheif/AV1 quality (1–100) at encoder speed 6.
/// <see cref="Lossless"/>: quality 100, 4:4:4 chroma and the identity colour matrix (CICP <c>1/13/0/1</c>), so the
/// RGB pixels are stored bit-exactly.</description></item>
/// <item><term><see cref="ImageFormat.JpegXl"/></term><description>
/// Lossy: <see cref="Quality"/> is the libjxl quality (1–100, mapped by libjxl to a Butteraugli distance) at
/// effort 7. <see cref="Lossless"/>: distance 0 (mathematically lossless).</description></item>
/// <item><term><see cref="ImageFormat.Gif"/></term><description>
/// Lossless palette encoding; <see cref="Quality"/> is not used. When frames have to be rebuilt (resizing,
/// conversion) they are re-optimised (frame differencing and transparency optimisation).</description></item>
/// <item><term><see cref="ImageFormat.Tiff"/></term><description>
/// Lossless Deflate (zip) compression with a horizontal predictor; <see cref="Quality"/> is not used.</description></item>
/// <item><term><see cref="ImageFormat.Bmp"/></term><description>Uncompressed; <see cref="Quality"/> is not used.</description></item>
/// <item><term><see cref="ImageFormat.Heic"/></term><description>
/// Written only when the native build has an HEVC encoder (<see cref="ImageCompressor.CanEncode"/>); otherwise
/// requesting it throws <see cref="UnsupportedImageFormatException"/>.</description></item>
/// </list>
/// </remarks>
public sealed record ImageCompressionOptions
{
    /// <summary>
    /// Gets the default options: keep the format, quality 80, strip metadata but keep the colour profile, bake
    /// the EXIF orientation into the pixels, progressive JPEG, never return a larger file.
    /// </summary>
    public static ImageCompressionOptions Default { get; } = new();

    /// <summary>Gets the format to write, or <see langword="null"/> to keep the format of the source.</summary>
    public ImageFormat? OutputFormat { get; init; }

    /// <summary>
    /// Gets the encoder quality in the range 1..100; out-of-range values are clamped. Its meaning depends on the
    /// output format — see the remarks on <see cref="ImageCompressionOptions"/>.
    /// </summary>
    public int Quality { get; init; } = 80;

    /// <summary>
    /// Gets a value indicating whether a lossless encoding is requested for formats that have both modes
    /// (WebP, AVIF, JPEG XL). PNG, GIF, TIFF and BMP are always lossless; JPEG is always lossy.
    /// </summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// Gets the maximum width in pixels, or <see langword="null"/> for no limit. Larger images are scaled down
    /// with the aspect ratio kept (Lanczos filter); images are never scaled up.
    /// </summary>
    public int? MaxWidth { get; init; }

    /// <summary>Gets the maximum height in pixels, or <see langword="null"/> for no limit. See <see cref="MaxWidth"/>.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Gets a value indicating whether EXIF, XMP, IPTC, comments and other metadata are removed.</summary>
    public bool StripMetadata { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the ICC colour profile is kept when <see cref="StripMetadata"/> removes
    /// everything else. When <see langword="false"/>, pixels are first converted to sRGB so that colours do not
    /// shift once the profile is gone.
    /// </summary>
    public bool KeepColorProfile { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the EXIF orientation is applied to the pixels (and the tag reset). This
    /// always happens before metadata is stripped, so photos keep their visual orientation.
    /// </summary>
    public bool AutoOrient { get; init; } = true;

    /// <summary>Gets a value indicating whether JPEG output uses progressive scans. Other formats are not interlaced.</summary>
    public bool Progressive { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the original bytes are written instead of the re-encoded image when the
    /// result is not smaller and neither the format nor the dimensions changed. Such results report
    /// <see cref="ImageCompressionResult.KeptOriginal"/>.
    /// </summary>
    public bool KeepOriginalIfLarger { get; init; } = true;

    /// <summary>
    /// Gets the number of images <see cref="ImageCompressor.CompressFilesAsync"/> processes at once, or
    /// <see langword="null"/> for the default (the processor count, capped at 4, because ImageMagick already
    /// spreads each image over several threads).
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init; }

    internal int ClampedQuality => Math.Clamp(Quality, 1, 100);
}

/// <summary>Basic facts about an image file.</summary>
/// <param name="Format">The detected format.</param>
/// <param name="Width">The width of the (first) frame in pixels.</param>
/// <param name="Height">The height of the (first) frame in pixels.</param>
/// <param name="FileSize">The size of the file in bytes.</param>
/// <param name="FrameCount">The number of frames or pages.</param>
/// <param name="HasAlpha"><see langword="true"/> when the image has an alpha channel.</param>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height, long FileSize, int FrameCount, bool HasAlpha);

/// <summary>The outcome of compressing one image.</summary>
/// <param name="SourcePath">The file that was read.</param>
/// <param name="DestinationPath">The file that was (or, on failure, would have been) written.</param>
/// <param name="OriginalSize">The size of the source in bytes.</param>
/// <param name="CompressedSize">The size of the written file in bytes; 0 when <paramref name="Error"/> is set.</param>
/// <param name="KeptOriginal">
/// <see langword="true"/> when re-encoding did not help and the original bytes were written instead
/// (see <see cref="ImageCompressionOptions.KeepOriginalIfLarger"/>).
/// </param>
/// <param name="Error">
/// The failure of this item in a batch (<see cref="ImageCompressor.CompressFilesAsync"/>), or
/// <see langword="null"/> on success. Single-file operations throw instead.
/// </param>
public sealed record ImageCompressionResult(
    string SourcePath,
    string DestinationPath,
    long OriginalSize,
    long CompressedSize,
    bool KeptOriginal,
    Exception? Error = null)
{
    /// <summary>Gets <see cref="CompressedSize"/> divided by <see cref="OriginalSize"/> (smaller is better; 0 when unknown).</summary>
    public double Ratio => OriginalSize > 0 && Error is null ? (double)CompressedSize / OriginalSize : 0d;

    /// <summary>Gets a value indicating whether the item was compressed without error.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>A snapshot of the progress of an image operation.</summary>
/// <param name="ItemsProcessed">The number of images finished so far.</param>
/// <param name="TotalItems">The number of images in the operation.</param>
/// <param name="CurrentItem">The file name of the image the snapshot is about, when known.</param>
/// <param name="CurrentItemFraction">The completed fraction (0..1) of <paramref name="CurrentItem"/>.</param>
public readonly record struct ImageProgress(int ItemsProcessed, int TotalItems, string? CurrentItem, double CurrentItemFraction);
