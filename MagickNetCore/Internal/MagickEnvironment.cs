using ImageMagick;

namespace MagickNetCore.Internal;

/// <summary>
/// One-time, process-wide hardening of ImageMagick against hostile input ("decompression bombs").
/// </summary>
/// <remarks>
/// <para><see cref="ResourceLimits"/> are global to the process, so the limits below only ever <em>lower</em> the
/// current values: a host application that configured stricter limits keeps them.</para>
/// <para>Width and height limits make ImageMagick reject oversized images while reading the header, before any
/// pixels are allocated. Pixel data beyond the memory limit is cached on disk, and the disk limit makes such an
/// image fail instead of filling the drive. The list-length limit stops GIFs with absurd frame counts.</para>
/// <para>Coders that run external programs (Ghostscript, MSL, MVG, SVG delegates, URL fetches, …) are never
/// reached: every decode passes an explicit format from the allow-list in <see cref="ImageFormats"/>, chosen
/// from the file signature, so ImageMagick never picks a coder from file content or a name prefix.</para>
/// </remarks>
internal static class MagickEnvironment
{
    /// <summary>The largest width or height accepted, in pixels.</summary>
    public const ulong MaxDimension = 32_768;

    /// <summary>The largest pixel area kept in memory (larger images use the disk cache).</summary>
    public const ulong MaxAreaInMemory = 256UL * 1024 * 1024;

    /// <summary>The largest pixel cache held in memory, in bytes.</summary>
    public const ulong MaxMemory = 2UL * 1024 * 1024 * 1024;

    /// <summary>The largest pixel cache allowed on disk, in bytes; beyond it decoding fails.</summary>
    public const ulong MaxDisk = 8UL * 1024 * 1024 * 1024;

    /// <summary>The largest number of frames or pages accepted.</summary>
    public const ulong MaxFrames = 10_000;

    private static readonly Lazy<bool> Initialized = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void EnsureInitialized() => _ = Initialized.Value;

    private static bool Initialize()
    {
        ResourceLimits.Width = Math.Min(ResourceLimits.Width, MaxDimension);
        ResourceLimits.Height = Math.Min(ResourceLimits.Height, MaxDimension);
        ResourceLimits.Area = Math.Min(ResourceLimits.Area, MaxAreaInMemory);
        ResourceLimits.Memory = Math.Min(ResourceLimits.Memory, MaxMemory);
        ResourceLimits.Disk = Math.Min(ResourceLimits.Disk, MaxDisk);
        ResourceLimits.ListLength = Math.Min(ResourceLimits.ListLength, MaxFrames);
        return true;
    }
}
