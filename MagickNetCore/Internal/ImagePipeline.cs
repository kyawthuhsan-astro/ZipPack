using ImageMagick;
using ImageMagick.Formats;

namespace MagickNetCore.Internal;

/// <summary>The result of <see cref="ImagePipeline.Encode"/>.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="SourceFormat">The detected format of the source.</param>
/// <param name="OutputFormat">The format <paramref name="Data"/> is in.</param>
/// <param name="DimensionsChanged"><see langword="true"/> when the output has other dimensions than the source.</param>
internal sealed record EncodedImage(byte[] Data, ImageFormat SourceFormat, ImageFormat OutputFormat, bool DimensionsChanged);

/// <summary>
/// The synchronous, CPU-bound core: decode → orient → colour/metadata policy → resize → flatten → encode.
/// Every Magick.NET call of the library goes through here.
/// </summary>
internal static class ImagePipeline
{
    /// <summary>PNG "quality" values tried (tens: zlib level, units: filter) — the same set as Magick.NET's <c>PngOptimizer</c>.</summary>
    private static readonly uint[] PngCandidates = [91, 94, 95, 97];

    /// <summary>Returns the format of <paramref name="data"/>, or throws when it is not a supported image.</summary>
    public static ImageFormat Identify(ReadOnlySpan<byte> data, ImageFormat? hint, string name)
    {
        ImageFormat format = ImageFormats.Detect(data) ?? hint
            ?? throw new UnsupportedImageFormatException($"'{name}' is not an image of a supported format.");

        if (!ImageFormats.CanDecode(format))
        {
            throw new UnsupportedImageFormatException($"'{name}' is a {format} image, which this ImageMagick build cannot decode.");
        }

        if (format == ImageFormat.Png && ImageFormats.IsAnimatedPng(data))
        {
            throw new UnsupportedImageFormatException($"'{name}' is an animated PNG, which is not supported.");
        }

        return format;
    }

    public static ImageInfo GetInfo(byte[] data, string name)
    {
        MagickEnvironment.EnsureInitialized();
        ImageFormat format = Identify(data, hint: null, name);

        return Guard(name, CancellationToken.None, () =>
        {
            using var frames = new MagickImageCollection();
            frames.Ping(data, new MagickReadSettings { Format = ImageFormats.ToMagick(format) });
            IMagickImage<byte> first = frames[0];
            return new ImageInfo(format, (int)first.Width, (int)first.Height, data.LongLength, frames.Count, first.HasAlpha);
        });
    }

    /// <summary>Re-encodes <paramref name="source"/> according to <paramref name="options"/>.</summary>
    /// <param name="source">The complete source file.</param>
    /// <param name="hint">The format to assume when the signature is not recognised.</param>
    /// <param name="name">The name used in error messages.</param>
    /// <param name="options">What to do.</param>
    /// <param name="report">Receives the completed fraction (0..1), monotonically increasing.</param>
    /// <param name="cancellationToken">Cancels the operation, also while ImageMagick is busy.</param>
    public static EncodedImage Encode(
        byte[] source,
        ImageFormat? hint,
        string name,
        ImageCompressionOptions options,
        Action<double>? report,
        CancellationToken cancellationToken)
    {
        MagickEnvironment.EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();

        ImageFormat sourceFormat = Identify(source, hint, name);
        ImageFormat target = options.OutputFormat ?? sourceFormat;
        if (!ImageFormats.CanEncode(target))
        {
            throw new UnsupportedImageFormatException($"This ImageMagick build cannot encode {target} images ('{name}').");
        }

        var progress = new ProgressScope(report, cancellationToken);
        return Guard(name, cancellationToken, () =>
        {
            using MagickImageCollection frames = Read(source, sourceFormat, progress, name);
            cancellationToken.ThrowIfCancellationRequested();
            if (frames.Count > 1 && !ImageFormats.SupportsMultipleFrames(target))
            {
                throw new UnsupportedImageFormatException(
                    $"'{name}' has {frames.Count} frames, but {target} can hold only one. Choose GIF, WebP or TIFF to keep them.");
            }

            uint originalWidth = frames[0].Width;
            uint originalHeight = frames[0].Height;

            progress.Stage(0.15, 0.45);
            Transform(frames, sourceFormat, target, options);
            cancellationToken.ThrowIfCancellationRequested();

            progress.Stage(0.45, 0.95);
            byte[] data = Write(frames, target, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress.Complete();

            bool resized = frames[0].Width != originalWidth || frames[0].Height != originalHeight;
            return new EncodedImage(data, sourceFormat, target, resized);
        });
    }

    private static MagickImageCollection Read(byte[] source, ImageFormat format, ProgressScope progress, string name)
    {
        var settings = new MagickReadSettings { Format = ImageFormats.ToMagick(format) };
        var frames = new MagickImageCollection();
        MagickWarningException? damage = null;
        void OnWarning(object? sender, WarningEventArgs e) =>
            damage ??= e.Exception is MagickCorruptImageWarningException ? e.Exception : null;

        try
        {
            progress.Stage(0, 0.15);
            if (ImageFormats.SupportsMultipleFrames(format))
            {
                // Animations and multi-page files: the collection keeps every frame with its delay and disposal.
                frames.Warning += OnWarning;
                frames.Read(source, settings);
                frames.Warning -= OnWarning;
            }
            else
            {
                // A single image reports progress (and therefore honours cancellation) while it decodes.
                var image = new MagickImage();
                frames.Add(image);
                image.Warning += OnWarning;
                progress.Attach(image);
                image.Read(source, settings);
                image.Warning -= OnWarning;
            }

            // A truncated or damaged file often decodes "successfully" with a warning and grey or missing rows.
            // Re-encoding would make that damage permanent, so it is treated as an error.
            if (damage is not null)
            {
                throw new ImageException($"'{name}' is truncated or damaged: {damage.Message}", damage);
            }

            foreach (IMagickImage<byte> frame in frames)
            {
                progress.Attach(frame);
            }

            return frames;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    private static void Transform(MagickImageCollection frames, ImageFormat source, ImageFormat target, ImageCompressionOptions options)
    {
        bool animated = frames.Count > 1;
        (uint Width, uint Height)? size = TargetSize(frames, options);

        // Frame-optimised animations store partial frames; they have to be expanded to full frames before they
        // are scaled or handed to another encoder, and re-optimised afterwards.
        bool rebuild = animated && (size is not null || source != target);
        if (rebuild)
        {
            frames.Coalesce();
        }

        foreach (IMagickImage<byte> frame in frames)
        {
            // Orientation first: stripping removes the EXIF tag that says how the photo is rotated.
            if (options.AutoOrient)
            {
                frame.AutoOrient();
            }

            ApplyColorAndMetadataPolicy(frame, target, options);
        }

        // Recompute after orientation, which may have swapped width and height.
        size = TargetSize(frames, options);
        if (size is { } newSize)
        {
            foreach (IMagickImage<byte> frame in frames)
            {
                frame.Resize(new MagickGeometry(newSize.Width, newSize.Height) { IgnoreAspectRatio = true }, FilterType.Lanczos);
                if (animated)
                {
                    frame.ResetPage();
                }
            }
        }

        if (!ImageFormats.SupportsAlpha(target))
        {
            foreach (IMagickImage<byte> frame in frames.Where(frame => frame.HasAlpha))
            {
                // Without this, transparent pixels keep whatever colour they happen to store (often black).
                frame.BackgroundColor = MagickColors.White;
                frame.Alpha(AlphaAction.Remove);
                frame.Alpha(AlphaAction.Off);
            }
        }

        if (rebuild && target == ImageFormat.Gif)
        {
            frames.Optimize();
            frames.OptimizeTransparency();
        }
    }

    private static void ApplyColorAndMetadataPolicy(IMagickImage<byte> frame, ImageFormat target, ImageCompressionOptions options)
    {
        IColorProfile? profile = frame.GetColorProfile();

        // Only JPEG and TIFF store CMYK; everything else needs RGB pixels.
        if (frame.ColorSpace == ColorSpace.CMYK && target is not (ImageFormat.Jpeg or ImageFormat.Tiff))
        {
            if (profile is not null)
            {
                frame.TransformColorSpace(ColorProfiles.SRGB);
            }
            else
            {
                frame.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
            }

            profile = frame.GetColorProfile();
        }

        if (!options.StripMetadata)
        {
            return;
        }

        if (profile is not null && !options.KeepColorProfile)
        {
            // Bake the profile into the pixels so the colours look the same without it.
            frame.TransformColorSpace(ColorProfiles.SRGB);
            profile = null;
        }

        frame.Strip();
        if (profile is not null)
        {
            frame.SetProfile(profile);
        }
    }

    /// <summary>Returns the downscaled size, or <see langword="null"/> when no scaling is needed. Never upscales.</summary>
    private static (uint Width, uint Height)? TargetSize(MagickImageCollection frames, ImageCompressionOptions options)
    {
        if (options.MaxWidth is null && options.MaxHeight is null)
        {
            return null;
        }

        uint width = frames[0].Width;
        uint height = frames[0].Height;
        double scale = Math.Min(
            options.MaxWidth is { } maxWidth ? (double)Math.Max(maxWidth, 1) / width : 1d,
            options.MaxHeight is { } maxHeight ? (double)Math.Max(maxHeight, 1) / height : 1d);

        if (scale >= 1d)
        {
            return null;
        }

        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private static byte[] Write(MagickImageCollection frames, ImageFormat target, ImageCompressionOptions options, CancellationToken cancellationToken)
    {
        MagickFormat format = ImageFormats.ToMagick(target);
        foreach (IMagickImage<byte> frame in frames)
        {
            frame.Format = format;
            ConfigureEncoder(frame, target, options);
        }

        if (target == ImageFormat.Png)
        {
            return WriteSmallestPng(frames[0], cancellationToken);
        }

        using var output = new MemoryStream();
        if (frames.Count == 1)
        {
            frames[0].Write(output);
        }
        else
        {
            frames.Write(output);
        }

        return output.ToArray();
    }

    /// <summary>Applies the per-format tuning documented on <see cref="ImageCompressionOptions"/>.</summary>
    private static void ConfigureEncoder(IMagickImage<byte> frame, ImageFormat target, ImageCompressionOptions options)
    {
        uint quality = (uint)options.ClampedQuality;
        switch (target)
        {
            case ImageFormat.Jpeg:
                frame.Quality = quality;
                frame.Settings.Interlace = options.Progressive ? Interlace.Jpeg : Interlace.NoInterlace;
                frame.Settings.SetDefines(new JpegWriteDefines
                {
                    OptimizeCoding = true,
                    SamplingFactor = quality >= 90 ? JpegSamplingFactor.Ratio444 : JpegSamplingFactor.Ratio420,
                });
                break;

            case ImageFormat.Png:
                frame.Settings.Interlace = Interlace.NoInterlace;
                if (options.StripMetadata)
                {
                    frame.Settings.SetDefines(new PngWriteDefines { ExcludeChunks = PngChunkFlags.date });
                }

                break;

            case ImageFormat.WebP:
                frame.Quality = options.Lossless ? 100 : quality;
                frame.Settings.SetDefines(new WebPWriteDefines { Method = 6, Lossless = options.Lossless });
                break;

            case ImageFormat.Avif:
                frame.Quality = options.Lossless ? 100 : quality;
                frame.Settings.SetDefines(new HeicWriteDefines
                {
                    Speed = 6,

                    // Lossless needs RGB stored as-is: 4:4:4 and the identity matrix (CICP matrix 0), full range,
                    // BT.709 primaries with the sRGB transfer curve; YCbCr at quality 100 is close, not exact.
                    Chroma = options.Lossless ? HeicChroma.Chroma444 : null,
                    Cicp = options.Lossless ? "1/13/0/1" : null,
                });
                break;

            case ImageFormat.JpegXl:
                if (options.Lossless)
                {
                    frame.Settings.SetDefines(new JxlWriteDefines { Effort = 7, Distance = 0 });
                }
                else
                {
                    frame.Quality = quality;
                    frame.Settings.SetDefines(new JxlWriteDefines { Effort = 7 });
                }

                break;

            case ImageFormat.Tiff:
                frame.Settings.Compression = CompressionMethod.Zip;
                frame.Settings.SetDefines(new TiffWriteDefines { Predictor = 2 });
                break;

            case ImageFormat.Heic:
                frame.Quality = quality;
                break;

            case ImageFormat.Gif:
            case ImageFormat.Bmp:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    /// <summary>PNG is lossless, so the only freedom is the zlib filter; keep the smallest result.</summary>
    private static byte[] WriteSmallestPng(IMagickImage<byte> image, CancellationToken cancellationToken)
    {
        if (image.HasAlpha && image.IsOpaque)
        {
            image.HasAlpha = false;
        }

        byte[]? best = null;
        foreach (uint candidate in PngCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.Quality = candidate;
            using var output = new MemoryStream();
            image.Write(output);
            if (best is null || output.Length < best.Length)
            {
                best = output.ToArray();
            }
        }

        return best!;
    }

    /// <summary>Translates Magick.NET exceptions; a cancelled token always wins.</summary>
    private static T Guard<T>(string name, CancellationToken cancellationToken, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (MagickException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (MagickMissingDelegateErrorException ex)
        {
            throw new UnsupportedImageFormatException($"'{name}' cannot be processed: {ex.Message}", ex);
        }
        catch (MagickResourceLimitErrorException ex)
        {
            throw new ImageException($"'{name}' exceeds the image size limits: {ex.Message}", ex);
        }
        catch (MagickException ex)
        {
            throw new ImageException($"'{name}' could not be processed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Turns ImageMagick's per-operation progress (0..100 for every step) into one increasing fraction, and
    /// aborts the running native operation when the token is cancelled.
    /// </summary>
    private sealed class ProgressScope(Action<double>? report, CancellationToken cancellationToken)
    {
        private readonly HashSet<IMagickImage<byte>> _attached = new(ReferenceEqualityComparer.Instance);
        private double _start;
        private double _end;
        private double _last;

        public void Attach(IMagickImage<byte> image)
        {
            if (_attached.Add(image))
            {
                image.Progress += OnProgress;
            }
        }

        public void Stage(double start, double end)
        {
            Publish(start);
            _start = start;
            _end = end;
        }

        public void Complete() => Publish(1d);

        private void OnProgress(object? sender, ProgressEventArgs e)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Makes the native call return early; Guard turns the resulting state into OperationCanceledException.
                e.Cancel = true;
                return;
            }

            Publish(_start + ((_end - _start) * Math.Clamp(e.Progress.ToDouble() / 100d, 0d, 1d)));
        }

        private void Publish(double fraction)
        {
            if (report is not null && fraction > _last)
            {
                _last = fraction;
                report(fraction);
            }
        }
    }
}
