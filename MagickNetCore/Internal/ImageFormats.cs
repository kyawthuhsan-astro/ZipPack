using System.Buffers.Binary;
using ImageMagick;

namespace MagickNetCore.Internal;

/// <summary>
/// The single table of supported formats: extensions, signatures, capabilities and the mapping to
/// <see cref="MagickFormat"/>. Nothing outside this library sees a Magick.NET type.
/// </summary>
internal static class ImageFormats
{
    /// <summary>The number of leading bytes <see cref="Detect"/> needs.</summary>
    public const int SignatureLength = 64;

    private static readonly Dictionary<string, ImageFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = ImageFormat.Jpeg,
        [".jpeg"] = ImageFormat.Jpeg,
        [".jpe"] = ImageFormat.Jpeg,
        [".jfif"] = ImageFormat.Jpeg,
        [".png"] = ImageFormat.Png,
        [".webp"] = ImageFormat.WebP,
        [".avif"] = ImageFormat.Avif,
        [".jxl"] = ImageFormat.JpegXl,
        [".gif"] = ImageFormat.Gif,
        [".tif"] = ImageFormat.Tiff,
        [".tiff"] = ImageFormat.Tiff,
        [".bmp"] = ImageFormat.Bmp,
        [".dib"] = ImageFormat.Bmp,
        [".heic"] = ImageFormat.Heic,
        [".heif"] = ImageFormat.Heic,
    };

    private static readonly Lazy<IReadOnlySet<MagickFormat>> Readable = new(() => Capabilities(info => info.SupportsReading));
    private static readonly Lazy<IReadOnlySet<MagickFormat>> Writable = new(() => Capabilities(info => info.SupportsWriting));

    public static IReadOnlyList<string> Extensions { get; } = [.. ByExtension.Keys];

    /// <summary>Gets the preferred extension for newly written files.</summary>
    public static string PreferredExtension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.WebP => ".webp",
        ImageFormat.Avif => ".avif",
        ImageFormat.JpegXl => ".jxl",
        ImageFormat.Gif => ".gif",
        ImageFormat.Tiff => ".tif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Heic => ".heic",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static ImageFormat? FromExtension(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out ImageFormat format) ? format : null;

    public static MagickFormat ToMagick(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => MagickFormat.Jpeg,
        ImageFormat.Png => MagickFormat.Png,
        ImageFormat.WebP => MagickFormat.WebP,
        ImageFormat.Avif => MagickFormat.Avif,
        ImageFormat.JpegXl => MagickFormat.Jxl,
        ImageFormat.Gif => MagickFormat.Gif,
        ImageFormat.Tiff => MagickFormat.Tiff,
        ImageFormat.Bmp => MagickFormat.Bmp,
        ImageFormat.Heic => MagickFormat.Heic,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Gets a value indicating whether the format can hold an alpha channel.</summary>
    public static bool SupportsAlpha(ImageFormat format) => format is not ImageFormat.Jpeg;

    /// <summary>
    /// Gets a value indicating whether this library writes every frame of an animation or multi-page image in
    /// the format. Formats whose ImageMagick coder writes only the first frame are deliberately excluded.
    /// </summary>
    public static bool SupportsMultipleFrames(ImageFormat format) => format is ImageFormat.Gif or ImageFormat.WebP or ImageFormat.Tiff;

    public static bool CanDecode(ImageFormat format) => Readable.Value.Contains(ToMagick(format));

    public static bool CanEncode(ImageFormat format) => Writable.Value.Contains(ToMagick(format));

    /// <summary>Identifies an image by its leading bytes; returns <see langword="null"/> for anything else.</summary>
    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ImageFormat.Jpeg;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ImageFormat.Png;
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return ImageFormat.Gif;
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8) || header.StartsWith("II+\0"u8) || header.StartsWith("MM\0+"u8))
        {
            return ImageFormat.Tiff;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x0A])
            || header.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A]))
        {
            return ImageFormat.JpegXl;
        }

        if (header.Length >= 14 && header.StartsWith("BM"u8))
        {
            return ImageFormat.Bmp;
        }

        return DetectIsoMedia(header);
    }

    /// <summary>Returns <see langword="true"/> for an animated PNG (an <c>acTL</c> chunk before the first <c>IDAT</c>).</summary>
    public static bool IsAnimatedPng(ReadOnlySpan<byte> data)
    {
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                return true;
            }

            if (type.SequenceEqual("IDAT"u8) || length > int.MaxValue - 12)
            {
                return false;
            }

            offset += 12 + (int)length;
        }

        return false;
    }

    /// <summary>AVIF and HEIC are ISO base media files; the <c>ftyp</c> box names the brand.</summary>
    private static ImageFormat? DetectIsoMedia(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header[4..8].SequenceEqual("ftyp"u8))
        {
            return null;
        }

        int boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        bool heic = false;

        // Major brand at 8, minor version at 12, compatible brands from 16 on.
        for (int offset = 8; offset + 4 <= boxSize; offset += offset == 8 ? 8 : 4)
        {
            ReadOnlySpan<byte> brand = header.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                return ImageFormat.Avif;
            }

            heic |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
                || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
        }

        return heic ? ImageFormat.Heic : null;
    }

    private static HashSet<MagickFormat> Capabilities(Func<IMagickFormatInfo, bool> predicate)
    {
        MagickEnvironment.EnsureInitialized();
        return [.. MagickNET.SupportedFormats.Where(predicate).Select(info => info.Format)];
    }
}
