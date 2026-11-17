namespace SevenZipCore;

/// <summary>Describes one item stored in an archive.</summary>
public sealed record ArchiveEntry
{
    /// <summary>Gets the zero-based index of the item in the archive.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the path of the item inside the archive, using the platform directory separator.</summary>
    public required string Path { get; init; }

    /// <summary>Gets a value indicating whether the item is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Gets the uncompressed size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets the compressed size in bytes, or <see langword="null"/> when the format does not record it per item
    /// (for example inside solid 7z blocks).</summary>
    public long? PackedSize { get; init; }

    /// <summary>Gets the file attributes.</summary>
    public FileAttributes Attributes { get; init; }

    /// <summary>Gets the last modification time (UTC), when recorded.</summary>
    public DateTime? LastWriteTime { get; init; }

    /// <summary>Gets the creation time (UTC), when recorded.</summary>
    public DateTime? CreationTime { get; init; }

    /// <summary>Gets the last access time (UTC), when recorded.</summary>
    public DateTime? LastAccessTime { get; init; }

    /// <summary>Gets a value indicating whether the item content is encrypted.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Gets the CRC-32 of the uncompressed content, when recorded.</summary>
    public uint? Crc { get; init; }

    /// <summary>Gets the compression method as reported by 7-Zip, e.g. <c>"LZMA2:24 7zAES"</c>.</summary>
    public string? Method { get; init; }

    /// <summary>Gets the item comment, when present.</summary>
    public string? Comment { get; init; }
}

/// <summary>A snapshot of the progress of a 7-Zip operation.</summary>
/// <param name="BytesProcessed">The number of uncompressed bytes handled so far.</param>
/// <param name="TotalBytes">The total number of uncompressed bytes, or 0 when unknown.</param>
/// <param name="CurrentEntry">The entry most recently started, when known.</param>
public readonly record struct SevenZipProgress(long BytesProcessed, long TotalBytes, string? CurrentEntry = null);
