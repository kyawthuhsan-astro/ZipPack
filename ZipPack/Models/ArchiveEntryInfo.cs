namespace ZipPack;

/// <summary>An engine-independent description of one entry of an archive.</summary>
/// <param name="Path">The path of the entry inside the archive, using <c>/</c> as separator.</param>
/// <param name="IsDirectory"><see langword="true"/> when the entry is a directory.</param>
/// <param name="Size">The uncompressed size in bytes.</param>
/// <param name="CompressedSize">The compressed size in bytes, when the format records it per entry.</param>
/// <param name="LastModified">The last modification time, when recorded.</param>
/// <param name="IsEncrypted"><see langword="true"/> when the entry content is password protected.</param>
public sealed record ArchiveEntryInfo(
    string Path,
    bool IsDirectory,
    long Size,
    long? CompressedSize,
    DateTimeOffset? LastModified,
    bool IsEncrypted);

/// <summary>A snapshot of the progress of a compression or extraction operation.</summary>
/// <param name="BytesProcessed">The number of bytes handled so far.</param>
/// <param name="TotalBytes">The total number of bytes to handle, or 0 when unknown.</param>
/// <param name="CurrentItem">The entry currently being processed, when known.</param>
public readonly record struct CompressionProgress(long BytesProcessed, long TotalBytes, string? CurrentItem = null)
{
    /// <summary>Gets the completed fraction in the range 0..1 (0 when the total is unknown).</summary>
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesProcessed / TotalBytes, 0d, 1d) : 0d;

    /// <summary>Gets the completed percentage in the range 0..100.</summary>
    public int Percent => (int)Math.Round(Fraction * 100d);
}
