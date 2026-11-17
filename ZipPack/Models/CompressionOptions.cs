namespace ZipPack;

/// <summary>An engine-independent trade-off between speed and compression ratio.</summary>
public enum CompressionPreset
{
    /// <summary>Do not compress (or use the weakest setting the format offers).</summary>
    Store,

    /// <summary>The fastest compression.</summary>
    Fastest,

    /// <summary>Fast compression.</summary>
    Fast,

    /// <summary>A balanced default.</summary>
    Normal,

    /// <summary>Stronger, slower compression.</summary>
    High,

    /// <summary>The strongest and slowest compression.</summary>
    Ultra,
}

/// <summary>Engine-independent options for a compression operation.</summary>
public sealed record CompressionOptions
{
    /// <summary>Gets the default options: <see cref="CompressionPreset.Normal"/>, no password.</summary>
    public static CompressionOptions Default { get; } = new();

    /// <summary>Gets the speed/ratio trade-off.</summary>
    public CompressionPreset Preset { get; init; } = CompressionPreset.Normal;

    /// <summary>Gets the password protecting the output, or <see langword="null"/> for none.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Gets a value indicating whether entry names should be encrypted too.
    /// Only honoured by archivers that report <see cref="ArchiverCapabilities.EncryptFileNames"/>.
    /// </summary>
    public bool EncryptFileNames { get; init; }

    /// <summary>Gets the number of threads to use, or <see langword="null"/> to let the engine decide.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>
    /// Gets engine specific switches that have no engine-independent equivalent
    /// (for 7-Zip these are the <c>-m</c> switches, e.g. <c>"d" = "64m"</c>). Strategies ignore keys they do not know.
    /// </summary>
    public IReadOnlyDictionary<string, string> EngineParameters { get; init; } =
        new Dictionary<string, string>();
}
