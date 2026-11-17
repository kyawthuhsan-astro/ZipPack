namespace MagickNetCore;

/// <summary>The image formats <see cref="ImageCompressor"/> reads and writes.</summary>
/// <remarks>
/// Whether a format can actually be decoded or encoded depends on the delegate libraries compiled into the
/// native ImageMagick build; query <see cref="ImageCompressor.CanDecode"/> and <see cref="ImageCompressor.CanEncode"/>.
/// </remarks>
public enum ImageFormat
{
    /// <summary>JPEG / JFIF (<c>.jpg</c>, <c>.jpeg</c>). Lossy, no transparency, single frame.</summary>
    Jpeg,

    /// <summary>Portable Network Graphics (<c>.png</c>). Lossless, transparency, single frame.</summary>
    Png,

    /// <summary>WebP (<c>.webp</c>). Lossy or lossless, transparency, animation.</summary>
    WebP,

    /// <summary>AV1 Image File Format (<c>.avif</c>). Lossy or lossless, transparency, single frame.</summary>
    Avif,

    /// <summary>JPEG XL (<c>.jxl</c>). Lossy or lossless, transparency, single frame.</summary>
    JpegXl,

    /// <summary>Graphics Interchange Format (<c>.gif</c>). Lossless palette, 1-bit transparency, animation.</summary>
    Gif,

    /// <summary>Tagged Image File Format (<c>.tif</c>, <c>.tiff</c>). Lossless (Deflate), transparency, multiple pages.</summary>
    Tiff,

    /// <summary>Windows bitmap (<c>.bmp</c>). Uncompressed, single frame.</summary>
    Bmp,

    /// <summary>High Efficiency Image File Format (<c>.heic</c>, <c>.heif</c>). Usually read-only; see <see cref="ImageCompressor.CanEncode"/>.</summary>
    Heic,
}
