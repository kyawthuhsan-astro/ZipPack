namespace ZipPack;

/// <summary>Engine-independent options for compressing images.</summary>
public sealed record ImageCompressOptions
{
    /// <summary>Gets the default options: <see cref="CompressionPreset.Normal"/>, keep the format, strip metadata.</summary>
    public static ImageCompressOptions Default { get; } = new();

    /// <summary>
    /// Gets the coarse size/quality trade-off: <see cref="CompressionPreset.Store"/> keeps the most detail,
    /// <see cref="CompressionPreset.Ultra"/> produces the smallest files. Ignored when <see cref="Quality"/> is set.
    /// </summary>
    public CompressionPreset Preset { get; init; } = CompressionPreset.Normal;

    /// <summary>Gets an explicit encoder quality (1..100) that overrides <see cref="Preset"/>, or <see langword="null"/>.</summary>
    public int? Quality { get; init; }

    /// <summary>Gets a value indicating whether lossless encoding is requested where the format offers it.</summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// Gets the id of the format to write (one of <see cref="IMagickNetCore.TargetFormatIds"/>), or
    /// <see langword="null"/> to keep the format of each source.
    /// </summary>
    public string? TargetFormatId { get; init; }

    /// <summary>Gets the maximum width in pixels; larger images are scaled down keeping the aspect ratio.</summary>
    public int? MaxWidth { get; init; }

    /// <summary>Gets the maximum height in pixels; larger images are scaled down keeping the aspect ratio.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Gets a value indicating whether EXIF, XMP, IPTC and comments are removed (the colour profile is kept).</summary>
    public bool StripMetadata { get; init; } = true;
}

/// <summary>Describes one image to compress.</summary>
public sealed record ImageCompressRequest
{
    /// <summary>Gets the image to read.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Gets the file to write. It may equal <see cref="SourcePath"/> when <see cref="Overwrite"/> is set.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Gets the compression options.</summary>
    public ImageCompressOptions Options { get; init; } = ImageCompressOptions.Default;

    /// <summary>Gets a value indicating whether an existing destination file (including the source itself) may be replaced.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Describes several images to compress into one directory.</summary>
public sealed record ImageBatchCompressRequest
{
    /// <summary>Gets the images to read.</summary>
    public required IReadOnlyList<string> SourcePaths { get; init; }

    /// <summary>
    /// Gets the directory to write to. Outputs are named after their sources; outputs of one batch never
    /// overwrite each other or another source (a <c>" (2)"</c> suffix is added instead).
    /// </summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>Gets the compression options.</summary>
    public ImageCompressOptions Options { get; init; } = ImageCompressOptions.Default;
}

/// <summary>The outcome of compressing one image.</summary>
/// <param name="SourcePath">The image that was read.</param>
/// <param name="DestinationPath">The file that was (or, on failure, would have been) written.</param>
/// <param name="OriginalSize">The size of the source in bytes.</param>
/// <param name="CompressedSize">The size of the written file in bytes; 0 when <paramref name="Error"/> is set.</param>
/// <param name="KeptOriginal"><see langword="true"/> when re-encoding did not help and the original bytes were written.</param>
/// <param name="Error">The failure of this item in a batch, or <see langword="null"/> on success.</param>
public sealed record ImageCompressResult(
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
