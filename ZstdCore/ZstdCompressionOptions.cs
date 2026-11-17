using System.IO.Compression;

namespace ZstdCore;

/// <summary>Options controlling how <see cref="ZstdCompressor"/> compresses data.</summary>
public sealed record ZstdCompressionOptions
{
    /// <summary>Gets the default options: default quality, content checksum enabled, no password.</summary>
    public static ZstdCompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the Zstandard compression level. Valid values range from
    /// <see cref="ZstandardCompressionOptions.MinQuality"/> to <see cref="ZstandardCompressionOptions.MaxQuality"/>;
    /// out-of-range values are clamped.
    /// </summary>
    public int Quality { get; init; } = ZstandardCompressionOptions.DefaultQuality;

    /// <summary>Gets the base-2 logarithm of the match window size, or <see langword="null"/> for the level's default.</summary>
    public int? WindowLog2 { get; init; }

    /// <summary>Gets a value indicating whether a 32-bit content checksum is appended so corruption is detected on decompression.</summary>
    public bool AppendChecksum { get; init; } = true;

    /// <summary>Gets a value indicating whether long distance matching is enabled (helps on large, repetitive inputs).</summary>
    public bool EnableLongDistanceMatching { get; init; }

    /// <summary>
    /// Gets the number of cores used for compression; <see langword="null"/> uses all of them, 1 compresses on the
    /// calling thread as a single frame.
    /// </summary>
    /// <remarks>
    /// With more than one thread the input is split into chunks of four times the window (at least 4 MiB) that
    /// are compressed in parallel into consecutive Zstandard frames — the same approach as <c>zstd -T0</c>. The
    /// result is standard Zstandard that every decoder reads. Measured on 300 MiB of log text with 12 cores:
    /// quality 3 went from 83 to 596 MiB/s and quality 19 from 0.9 to 11 MiB/s, at the same size.
    /// Inputs smaller than one chunk gain nothing.
    /// </remarks>
    public int? ThreadCount { get; init; }

    /// <summary>Gets the number of threads to use, resolved against the machine.</summary>
    internal int EffectiveThreadCount => Math.Max(1, ThreadCount ?? Environment.ProcessorCount);

    /// <summary>
    /// Gets the password protecting the output, or <see langword="null"/> for a plain Zstandard frame.
    /// </summary>
    /// <remarks>
    /// The Zstandard format has no encryption of its own. When a password is set, the compressed frame is
    /// wrapped in an authenticated AES-256-GCM container (key derived with PBKDF2-HMAC-SHA256). Such files
    /// can only be read back by <see cref="ZstdExtractor"/>, not by the <c>zstd</c> command line tool.
    /// </remarks>
    public string? Password { get; init; }

    /// <summary>Creates the equivalent options object of the built-in .NET Zstandard API.</summary>
    internal ZstandardCompressionOptions ToNativeOptions()
    {
        var native = new ZstandardCompressionOptions
        {
            Quality = Math.Clamp(Quality, ZstandardCompressionOptions.MinQuality, ZstandardCompressionOptions.MaxQuality),
            AppendChecksum = AppendChecksum,
            EnableLongDistanceMatching = EnableLongDistanceMatching,
        };

        if (WindowLog2 is { } windowLog2)
        {
            native.WindowLog2 = Math.Clamp(
                windowLog2,
                ZstandardCompressionOptions.MinWindowLog2,
                ZstandardCompressionOptions.MaxWindowLog2);
        }

        return native;
    }
}

/// <summary>A snapshot of the progress of a Zstandard operation.</summary>
/// <param name="BytesProcessed">The number of source bytes consumed so far.</param>
/// <param name="TotalBytes">The total number of source bytes, or 0 when unknown.</param>
/// <param name="CurrentEntry">The entry currently being processed, when known.</param>
public readonly record struct ZstdProgress(long BytesProcessed, long TotalBytes, string? CurrentEntry = null);

/// <summary>Describes one entry of a Zstandard archive.</summary>
/// <param name="Path">The path of the entry, using <c>/</c> as separator.</param>
/// <param name="IsDirectory"><see langword="true"/> when the entry is a directory.</param>
/// <param name="Size">The uncompressed size in bytes.</param>
/// <param name="LastWriteTime">The last modification time, when recorded.</param>
public sealed record ZstdEntry(string Path, bool IsDirectory, long Size, DateTimeOffset? LastWriteTime);
