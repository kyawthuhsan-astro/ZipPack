using ImageMagick;

namespace ZipPackTest;

/// <summary>Generates test images in code, so no binary assets are committed.</summary>
internal static class TestImages
{
    /// <summary>A photo-like image: a colour gradient with mild noise (compresses like a real photo).</summary>
    public static MagickImage Photo(uint width = 320, uint height = 240)
    {
        var image = new MagickImage("gradient:#2050c0-#f0a030", width, height);
        image.AddNoise(NoiseType.Gaussian, 0.6);
        image.ColorSpace = ColorSpace.sRGB;
        return image;
    }

    /// <summary>Writes <paramref name="image"/> to <paramref name="path"/> in <paramref name="format"/>.</summary>
    public static string Save(MagickImage image, string path, MagickFormat format, uint? quality = null)
    {
        image.Format = format;
        if (quality is { } q)
        {
            image.Quality = q;
        }

        image.Write(path);
        return path;
    }

    /// <summary>Saves a photo at high quality (or, for PNG, without zlib compression) so re-encoding pays off.</summary>
    public static string SaveLoosePhoto(string path, MagickFormat format, uint width = 320, uint height = 240)
    {
        using MagickImage photo = Photo(width, height);
        return Save(photo, path, format, format == MagickFormat.Png ? 0u : 98u);
    }

    /// <summary>A three-frame animated GIF, 10/100 s per frame.</summary>
    public static string SaveAnimatedGif(string path, uint width = 120, uint height = 80)
    {
        using var frames = new MagickImageCollection();
        foreach (MagickColor color in new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue })
        {
            var frame = new MagickImage(color, width, height) { AnimationDelay = 10, Format = MagickFormat.Gif };
            using var square = new MagickImage(MagickColors.White, width / 4, height / 4);
            frame.Composite(square, (int)width / 3, (int)height / 3);
            frames.Add(frame);
        }

        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    /// <summary>Returns the colour at a pixel.</summary>
    public static IMagickColor<byte> ColorAt(IMagickImage<byte> image, int x, int y)
    {
        using IPixelCollection<byte> pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    /// <summary>Asserts that a colour is close to the expected one (lossy formats shift values slightly).</summary>
    public static void AssertColorNear(IMagickColor<byte> expected, IMagickColor<byte> actual, int tolerance = 24)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"Expected a colour near {expected}, got {actual}.");
    }

    /// <summary>
    /// Builds a valid, tiny PNG whose header claims <paramref name="width"/> × <paramref name="height"/> pixels
    /// (a "decompression bomb").
    /// </summary>
    public static byte[] PngClaimingSize(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Black, 1, 1);
        byte[] png = image.ToByteArray(MagickFormat.Png);

        // Signature (8) + IHDR length (4) + "IHDR" (4) → width, height; the CRC covers type and data (17 bytes).
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
