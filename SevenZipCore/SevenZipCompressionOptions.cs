namespace SevenZipCore;

/// <summary>Options controlling how <see cref="SevenZipCompressor"/> creates an archive.</summary>
public sealed record SevenZipCompressionOptions
{
    /// <summary>Gets the default options: a 7z archive with normal LZMA2 compression and no password.</summary>
    public static SevenZipCompressionOptions Default { get; } = new();

    /// <summary>Gets the format of the archive to create.</summary>
    public OutArchiveFormat ArchiveFormat { get; init; } = OutArchiveFormat.SevenZip;

    /// <summary>Gets the compression effort (the <c>-mx</c> switch).</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Normal;

    /// <summary>Gets the compression method; <see cref="CompressionMethod.Default"/> lets the format choose.</summary>
    public CompressionMethod CompressionMethod { get; init; } = CompressionMethod.Default;

    /// <summary>Gets the password protecting the content, or <see langword="null"/> for none. Only 7z and ZIP support passwords.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets a value indicating whether the archive headers (file names, sizes …) are encrypted too, so the
    /// archive cannot even be listed without the password (the <c>-mhe</c> switch). 7z only.
    /// </summary>
    public bool EncryptHeaders { get; init; }

    /// <summary>Gets the encryption used for password protected ZIP archives.</summary>
    public ZipEncryptionMethod ZipEncryptionMethod { get; init; } = ZipEncryptionMethod.Aes256;

    /// <summary>
    /// Gets a value controlling solid compression for 7z archives (the <c>-ms</c> switch);
    /// <see langword="null"/> keeps the 7-Zip default.
    /// </summary>
    public bool? Solid { get; init; }

    /// <summary>Gets the number of threads to use (the <c>-mmt</c> switch); <see langword="null"/> uses all cores.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>
    /// Gets the LZMA2 block size in bytes for 7z archives (the <c>-m0=LZMA2:c=…</c> switch), which decides how many
    /// cores LZMA2 can use: every block is compressed independently by its own pair of threads.
    /// <see langword="null"/> chooses automatically; 0 keeps the 7-Zip default.
    /// </summary>
    /// <remarks>
    /// <para>7-Zip's default block is four times the dictionary (64 MiB at <see cref="CompressionLevel.Normal"/>),
    /// so a file smaller than that keeps only two of the cores busy. The automatic choice is a quarter of the
    /// dictionary (1 MiB at <see cref="CompressionLevel.Low"/>, 4 MiB at Normal, 8 MiB at High, 16 MiB at Ultra) when at
    /// least four threads are available. Measured on 16 MiB of log text with 12 cores, 2 MiB blocks compressed 9×
    /// faster than the default for a 2.8% larger archive; larger blocks lose less.</para>
    /// <para>Ignored for other formats and methods, and when <see cref="CustomParameters"/> sets <c>"0"</c>.</para>
    /// </remarks>
    public long? Lzma2BlockSize { get; init; }

    /// <summary>
    /// Gets additional method switches passed to 7-Zip verbatim, keyed without the leading <c>-m</c>
    /// (for example <c>"d" = "64m"</c> for a 64 MiB dictionary or <c>"fb" = "273"</c>). They override the
    /// switches derived from the other options.
    /// </summary>
    public IReadOnlyDictionary<string, string> CustomParameters { get; init; } = new Dictionary<string, string>();
}
