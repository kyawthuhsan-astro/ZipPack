namespace ZipPack;

/// <summary>
/// The root contract of every compression module (the "strategy" in the Strategy pattern).
/// </summary>
/// <remarks>
/// A strategy only describes <em>what</em> it is. What it can <em>do</em> is expressed by the more specific
/// interface it implements — <see cref="IArchiver"/> and <see cref="IStreamCompressor"/> today, and for
/// example an <c>IImageCompressor</c> or <c>IVideoCompressor</c> tomorrow. New kinds of modules are added by
/// declaring a new interface that derives from this one and registering an implementation with
/// <see cref="ICompressionService"/>; no existing type has to change (Open/Closed Principle).
/// </remarks>
public interface ICompressionStrategy
{
    /// <summary>Gets the stable, case-insensitive identifier of the strategy, e.g. <c>"7z"</c> or <c>"zstd"</c>.</summary>
    string Id { get; }

    /// <summary>Gets a human readable name suitable for display in a UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the lower-case file extensions (including the leading dot) handled by this strategy.
    /// The first entry is the preferred extension for newly created files.
    /// </summary>
    IReadOnlyList<string> FileExtensions { get; }
}
