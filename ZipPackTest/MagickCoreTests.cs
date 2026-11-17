using ImageMagick;
using MagickNetCore;
using Orientation = ImageMagick.Orientation;

namespace ZipPackTest;

public sealed class MagickCoreTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("photo.jpg", ImageFormat.Jpeg)]
    [InlineData("photo.png", ImageFormat.Png)]
    [InlineData("photo.webp", ImageFormat.WebP)]
    public async Task Reencode_ProducesSmallerDecodableFile_WithSameDimensions(string fileName, ImageFormat format)
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor(fileName), ToMagick(format));
        string destination = _workspace.PathFor("out-" + fileName);

        ImageCompressionResult result = await new ImageCompressor().CompressFileAsync(source, destination, cancellationToken: Token);

        Assert.False(result.KeptOriginal);
        Assert.True(result.CompressedSize < result.OriginalSize, $"{result.CompressedSize} is not smaller than {result.OriginalSize}.");
        Assert.Equal(new FileInfo(destination).Length, result.CompressedSize);
        Assert.True(result.Ratio is > 0 and < 1);

        ImageInfo info = await ImageCompressor.GetInfoAsync(destination, Token);
        Assert.Equal((format, 320, 240, 1), (info.Format, info.Width, info.Height, info.FrameCount));
        using var decoded = new MagickImage(destination);
        Assert.Equal((320u, 240u), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task Convert_PngToWebP_WritesWebP()
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.png"), MagickFormat.Png);
        string destination = _workspace.PathFor("photo.webp");

        var compressor = new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.WebP });
        ImageCompressionResult result = await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

        Assert.Equal(ImageFormat.WebP, (await ImageCompressor.GetInfoAsync(destination, Token)).Format);
        Assert.True(result.CompressedSize < result.OriginalSize / 4);
    }

    [Fact]
    public async Task Convert_TransparentPngToJpeg_FlattensOntoWhite()
    {
        string source = _workspace.PathFor("logo.png");
        using (var logo = new MagickImage(MagickColors.Transparent, 100, 100))
        using (var square = new MagickImage(MagickColors.Red, 40, 40))
        {
            logo.Composite(square, 30, 30, CompositeOperator.Over);
            TestImages.Save(logo, source, MagickFormat.Png);
        }

        string destination = _workspace.PathFor("logo.jpg");
        var compressor = new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.Jpeg, Quality = 90 });
        await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

        using var jpeg = new MagickImage(destination);
        Assert.Equal(MagickFormat.Jpeg, jpeg.Format);
        Assert.False(jpeg.HasAlpha);
        TestImages.AssertColorNear(MagickColors.White, TestImages.ColorAt(jpeg, 5, 5));
        TestImages.AssertColorNear(MagickColors.Red, TestImages.ColorAt(jpeg, 50, 50), tolerance: 40);
    }

    [Theory]
    [InlineData(100, null, 100, 50)]
    [InlineData(null, 50, 100, 50)]
    [InlineData(150, 25, 50, 25)]
    [InlineData(1000, 1000, 400, 200)]
    public async Task MaxDimensions_ShrinkKeepingAspectRatio_AndNeverUpscale(int? maxWidth, int? maxHeight, int expectedWidth, int expectedHeight)
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("wide.jpg"), MagickFormat.Jpeg, 400, 200);
        string destination = _workspace.PathFor("small.jpg");

        var compressor = new ImageCompressor(new ImageCompressionOptions { MaxWidth = maxWidth, MaxHeight = maxHeight });
        await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

        ImageInfo info = await ImageCompressor.GetInfoAsync(destination, Token);
        Assert.Equal((expectedWidth, expectedHeight), (info.Width, info.Height));
    }

    [Fact]
    public async Task StripMetadata_RemovesExif_AndAutoOrientKeepsTheVisualOrientation()
    {
        // Stored 200×100 with a red marker top-left; EXIF orientation 6 says "rotate 90° clockwise to display".
        string source = _workspace.PathFor("camera.jpg");
        using (MagickImage photo = TestImages.Photo(200, 100))
        using (var marker = new MagickImage(MagickColors.Red, 30, 30))
        {
            photo.Composite(marker, 0, 0);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "ZipPack Test Camera");
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            photo.SetProfile(exif);
            photo.Orientation = Orientation.RightTop;
            TestImages.Save(photo, source, MagickFormat.Jpeg, 95);
        }

        string stripped = _workspace.PathFor("stripped.jpg");
        await new ImageCompressor().CompressFileAsync(source, stripped, cancellationToken: Token);

        using (var image = new MagickImage(stripped))
        {
            Assert.Null(image.GetExifProfile());
            Assert.Equal((100u, 200u), (image.Width, image.Height));
            Assert.True(image.Orientation is Orientation.Undefined or Orientation.TopLeft);

            // After a 90° clockwise turn the stored top-left corner is displayed top-right.
            TestImages.AssertColorNear(MagickColors.Red, TestImages.ColorAt(image, 95, 5), tolerance: 40);
        }

        string kept = _workspace.PathFor("kept.jpg");
        await new ImageCompressor(new ImageCompressionOptions { StripMetadata = false }).CompressFileAsync(source, kept, cancellationToken: Token);

        using (var image = new MagickImage(kept))
        {
            IExifProfile exif = Assert.IsAssignableFrom<IExifProfile>(image.GetExifProfile());
            Assert.Equal("ZipPack Test Camera", exif.GetValue(ExifTag.Make)?.Value);
            Assert.Equal((100u, 200u), (image.Width, image.Height));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StripMetadata_KeepsOrRemovesTheColorProfileAsAsked(bool keepColorProfile)
    {
        string source = _workspace.PathFor("adobe.jpg");
        using (MagickImage photo = TestImages.Photo())
        {
            photo.SetProfile(ColorProfiles.AdobeRGB1998);
            TestImages.Save(photo, source, MagickFormat.Jpeg, 95);
        }

        string destination = _workspace.PathFor("out.jpg");
        var compressor = new ImageCompressor(new ImageCompressionOptions { KeepColorProfile = keepColorProfile });
        await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

        using var image = new MagickImage(destination);
        Assert.Equal(keepColorProfile, image.GetColorProfile() is not null);
    }

    [Fact]
    public async Task KeepOriginalIfLarger_ReturnsAnAlreadyOptimalFileByteIdentical()
    {
        // Quality 30 is far below the default 80, so re-encoding could only grow the file.
        string source = _workspace.PathFor("tight.jpg");
        using (MagickImage photo = TestImages.Photo())
        {
            TestImages.Save(photo, source, MagickFormat.Jpeg, 30);
        }

        string destination = _workspace.PathFor("out.jpg");
        ImageCompressionResult result = await new ImageCompressor().CompressFileAsync(source, destination, cancellationToken: Token);

        Assert.True(result.KeptOriginal);
        Assert.Equal(result.OriginalSize, result.CompressedSize);
        Assert.Equal(await File.ReadAllBytesAsync(source, Token), await File.ReadAllBytesAsync(destination, Token));

        // Opting out writes the re-encoded (larger) image.
        ImageCompressionResult forced = await new ImageCompressor(new ImageCompressionOptions { KeepOriginalIfLarger = false })
            .CompressFileAsync(source, destination, cancellationToken: Token);
        Assert.False(forced.KeptOriginal);
        Assert.True(forced.CompressedSize > forced.OriginalSize);
    }

    [Fact]
    public async Task SourceEqualsDestination_OverwritesSafely()
    {
        string path = TestImages.SaveLoosePhoto(_workspace.PathFor("inplace.png"), MagickFormat.Png);
        long before = new FileInfo(path).Length;

        ImageCompressionResult result = await new ImageCompressor().CompressFileAsync(path, path, cancellationToken: Token);

        Assert.Equal(before, result.OriginalSize);
        Assert.Equal(new FileInfo(path).Length, result.CompressedSize);
        Assert.True(result.CompressedSize < before);
        using var image = new MagickImage(path);
        Assert.Equal((320u, 240u), (image.Width, image.Height));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedWrite_LeavesTheOriginalIntact_AndNoTemporaryFile()
    {
        string path = TestImages.SaveLoosePhoto(_workspace.PathFor("readonly.png"), MagickFormat.Png);
        byte[] original = await File.ReadAllBytesAsync(path, Token);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ImageCompressor().CompressFileAsync(path, path, cancellationToken: Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task AnimatedGif_KeepsEveryFrameAndItsTiming_AlsoWhenResized()
    {
        string source = TestImages.SaveAnimatedGif(_workspace.PathFor("anim.gif"));

        foreach (int? maxWidth in new int?[] { null, 60 })
        {
            string destination = _workspace.PathFor($"anim-{maxWidth}.gif");
            var compressor = new ImageCompressor(new ImageCompressionOptions { MaxWidth = maxWidth, KeepOriginalIfLarger = false });
            await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

            using var frames = new MagickImageCollection(destination);
            Assert.Equal(3, frames.Count);
            Assert.All(frames, frame => Assert.Equal(10u, frame.AnimationDelay));
            Assert.Equal(maxWidth is null ? 120u : 60u, frames[0].Page.Width is > 0 and var w ? w : frames[0].Width);
        }

        ImageInfo info = await ImageCompressor.GetInfoAsync(source, Token);
        Assert.Equal((ImageFormat.Gif, 3), (info.Format, info.FrameCount));
    }

    [Fact]
    public async Task AnimatedGif_ToWebP_KeepsTheAnimation_ButToJpegIsRejected()
    {
        string source = TestImages.SaveAnimatedGif(_workspace.PathFor("anim.gif"));

        string webp = _workspace.PathFor("anim.webp");
        await new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.WebP }).CompressFileAsync(source, webp, cancellationToken: Token);
        Assert.Equal(3, (await ImageCompressor.GetInfoAsync(webp, Token)).FrameCount);

        string jpeg = _workspace.PathFor("anim.jpg");
        var ex = await Assert.ThrowsAsync<UnsupportedImageFormatException>(() =>
            new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.Jpeg }).CompressFileAsync(source, jpeg, cancellationToken: Token));
        Assert.Contains("anim.gif", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(jpeg));
    }

    [Fact]
    public async Task NonImage_ThrowsUnsupportedFormat_WithTheFileName()
    {
        string fake = _workspace.PathFor("notes.png");
        File.Copy(_workspace.TextFile, fake);

        var ex = await Assert.ThrowsAsync<UnsupportedImageFormatException>(() =>
            new ImageCompressor().CompressFileAsync(fake, _workspace.PathFor("out.png"), cancellationToken: Token));
        Assert.Contains("notes.png", ex.Message, StringComparison.Ordinal);
        Assert.False(ImageCompressor.IsSupported(fake));
        Assert.False(File.Exists(_workspace.PathFor("out.png")));
    }

    [Theory]
    [InlineData("cut.png", MagickFormat.Png)]
    [InlineData("cut.jpg", MagickFormat.Jpeg)]
    public async Task TruncatedImage_ThrowsImageException_WithTheFileName(string fileName, MagickFormat format)
    {
        string full = TestImages.SaveLoosePhoto(_workspace.PathFor("full-" + fileName), format);
        byte[] bytes = await File.ReadAllBytesAsync(full, Token);
        string truncated = _workspace.PathFor(fileName);
        await File.WriteAllBytesAsync(truncated, bytes.AsMemory(0, bytes.Length / 2), Token);

        var ex = await Assert.ThrowsAnyAsync<ImageException>(() =>
            new ImageCompressor().CompressFileAsync(truncated, _workspace.PathFor("out-" + fileName), cancellationToken: Token));
        Assert.IsNotType<UnsupportedImageFormatException>(ex);
        Assert.Contains(fileName, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_workspace.PathFor("out-" + fileName)));
    }

    [Fact]
    public async Task DecompressionBomb_FailsWithImageException()
    {
        string bomb = _workspace.PathFor("bomb.png");
        await File.WriteAllBytesAsync(bomb, TestImages.PngClaimingSize(100_000, 100_000), Token);

        var ex = await Assert.ThrowsAnyAsync<ImageException>(() =>
            new ImageCompressor().CompressFileAsync(bomb, _workspace.PathFor("out.png"), cancellationToken: Token));
        Assert.Contains("bomb.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_WhileImageMagickIsBusy_ThrowsAndLeavesNoOutput()
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("big.png"), MagickFormat.Png, 2400, 1800);
        string destination = _workspace.PathFor("big-out.png");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);

        // Cancel as soon as ImageMagick reports its first progress; the next callback aborts the native call.
        var reports = new List<ImageProgress>();
        var progress = new SynchronousProgress<ImageProgress>(p =>
        {
            reports.Add(p);
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageCompressor(new ImageCompressionOptions { MaxWidth = 1200 }).CompressFileAsync(source, destination, progress, cts.Token));

        Assert.NotEmpty(reports);
        Assert.DoesNotContain(reports, r => r.ItemsProcessed == 1);
        Assert.False(File.Exists(destination));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task Progress_GrowsToCompletion_ForASingleFile()
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.jpg"), MagickFormat.Jpeg, 800, 600);
        var reports = new List<ImageProgress>();

        await new ImageCompressor(new ImageCompressionOptions { MaxWidth = 400 })
            .CompressFileAsync(source, _workspace.PathFor("out.jpg"), new SynchronousProgress<ImageProgress>(reports.Add), Token);

        Assert.Equal(new ImageProgress(1, 1, "photo.jpg", 1d), reports[^1]);
        double[] fractions = [.. reports.Select(r => r.CurrentItemFraction)];
        Assert.Equal(fractions.Order(), fractions);
        Assert.All(fractions, f => Assert.InRange(f, 0d, 1d));
    }

    [Fact]
    public async Task Batch_ContinuesPastBadFiles_HandlesNameCollisions_AndReportsEveryItem()
    {
        string dirA = Directory.CreateDirectory(_workspace.PathFor("a")).FullName;
        string dirB = Directory.CreateDirectory(_workspace.PathFor("b")).FullName;
        string[] sources =
        [
            TestImages.SaveLoosePhoto(Path.Combine(dirA, "photo.png"), MagickFormat.Png),
            TestImages.SaveLoosePhoto(Path.Combine(dirB, "photo.png"), MagickFormat.Png),
            TestImages.SaveLoosePhoto(Path.Combine(dirA, "photo.jpg"), MagickFormat.Jpeg),
            TestImages.SaveLoosePhoto(Path.Combine(dirA, "other.bmp"), MagickFormat.Bmp),
        ];
        string bad = Path.Combine(dirB, "broken.png");
        File.Copy(_workspace.TextFile, bad);

        var reports = new List<ImageProgress>();
        var compressor = new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.WebP });
        IReadOnlyList<ImageCompressionResult> results = await compressor.CompressFilesAsync(
            [sources[0], bad, sources[1], sources[2], sources[3]],
            _workspace.Output,
            new SynchronousProgress<ImageProgress>(reports.Add),
            Token);

        Assert.Equal(5, results.Count);
        Assert.Equal(5, reports.Count);
        Assert.Equal([1, 2, 3, 4, 5], reports.Select(r => r.ItemsProcessed));
        Assert.All(reports, r => Assert.Equal(5, r.TotalItems));

        Assert.IsType<UnsupportedImageFormatException>(results[1].Error);
        Assert.False(results[1].Succeeded);

        ImageCompressionResult[] good = [results[0], results[2], results[3], results[4]];
        Assert.All(good, r => Assert.True(r.Succeeded, r.Error?.Message));
        Assert.Equal(4, good.Select(r => r.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ["other.webp", "photo (2).webp", "photo (3).webp", "photo.webp"],
            Directory.EnumerateFiles(_workspace.Output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (ImageCompressionResult result in good)
        {
            Assert.Equal(ImageFormat.WebP, (await ImageCompressor.GetInfoAsync(result.DestinationPath, Token)).Format);
        }
    }

    [Fact]
    public async Task Batch_InPlace_NeverOverwritesAnotherSource()
    {
        // photo.png → photo.jpg would clobber the other source, photo.jpg.
        string png = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.png"), MagickFormat.Png);
        string jpg = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.jpg"), MagickFormat.Jpeg);
        byte[] jpgBefore = await File.ReadAllBytesAsync(jpg, Token);

        var compressor = new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.Jpeg, MaxDegreeOfParallelism = 1 });
        IReadOnlyList<ImageCompressionResult> results = await compressor.CompressFilesAsync([png, jpg], _workspace.Root, cancellationToken: Token);

        Assert.Equal(_workspace.PathFor("photo (2).jpg"), results[0].DestinationPath);
        Assert.Equal(jpg, results[1].DestinationPath);
        Assert.True(new FileInfo(jpg).Length < jpgBefore.Length);
    }

    [Fact]
    public async Task Batch_Cancelled_ThrowsOperationCanceled()
    {
        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.jpg"), MagickFormat.Jpeg);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageCompressor().CompressFilesAsync([source], _workspace.Output, cancellationToken: cts.Token));
        Assert.Empty(Directory.EnumerateFiles(_workspace.Output));
    }

    [Fact]
    public async Task Stream_CompressesInMemory_AndUsesTheHintOnlyWithoutSignature()
    {
        using MagickImage photo = TestImages.Photo();
        byte[] png = photo.ToByteArray(MagickFormat.Png);

        using var output = new MemoryStream();
        await new ImageCompressor(new ImageCompressionOptions { OutputFormat = ImageFormat.Jpeg })
            .CompressStreamAsync(new MemoryStream(png), output, cancellationToken: Token);

        Assert.True(output.Length > 0 && output.Length < png.Length);
        Assert.Equal([0xFF, 0xD8, 0xFF], output.ToArray()[..3]);

        await Assert.ThrowsAsync<UnsupportedImageFormatException>(() =>
            new ImageCompressor().CompressStreamAsync(new MemoryStream("hello"u8.ToArray()), Stream.Null, cancellationToken: Token));
    }

    [Fact]
    public void Detection_UsesSignatureForExistingFiles_AndExtensionOtherwise()
    {
        string disguised = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.dat"), MagickFormat.Png);

        Assert.True(ImageCompressor.IsSupported(disguised));
        Assert.True(ImageCompressor.IsSupported(_workspace.PathFor("not-yet-there.webp")));
        Assert.False(ImageCompressor.IsSupported(_workspace.PathFor("not-yet-there.txt")));
        Assert.False(ImageCompressor.IsSupported(_workspace.TextFile));
        Assert.Contains(".jpg", ImageCompressor.SupportedExtensions);
        Assert.All([ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP, ImageFormat.Gif, ImageFormat.Tiff, ImageFormat.Bmp], f =>
        {
            Assert.True(ImageCompressor.CanDecode(f), $"{f} cannot be decoded.");
            Assert.True(ImageCompressor.CanEncode(f), $"{f} cannot be encoded.");
        });
    }

    [Theory]
    [InlineData(ImageFormat.Avif, false)]
    [InlineData(ImageFormat.Avif, true)]
    [InlineData(ImageFormat.JpegXl, false)]
    [InlineData(ImageFormat.JpegXl, true)]
    [InlineData(ImageFormat.Tiff, false)]
    [InlineData(ImageFormat.WebP, true)]
    public async Task ModernFormats_RoundTrip(ImageFormat format, bool lossless)
    {
        Assert.SkipUnless(ImageCompressor.CanEncode(format), $"{format} encoding is not available in this build.");

        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.png"), MagickFormat.Png, 96, 64);
        string destination = _workspace.PathFor("photo.out");
        var compressor = new ImageCompressor(new ImageCompressionOptions { OutputFormat = format, Lossless = lossless });
        await compressor.CompressFileAsync(source, destination, cancellationToken: Token);

        ImageInfo info = await ImageCompressor.GetInfoAsync(destination, Token);
        Assert.Equal((format, 96, 64), (info.Format, info.Width, info.Height));

        if (lossless)
        {
            using var expected = new MagickImage(source);
            using var actual = new MagickImage(destination);
            Assert.Equal(0d, expected.Compare(actual, ErrorMetric.Absolute));
        }
    }

    private static MagickFormat ToMagick(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => MagickFormat.Jpeg,
        ImageFormat.Png => MagickFormat.Png,
        ImageFormat.WebP => MagickFormat.WebP,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_workspace.Root, "*.tmp", SearchOption.AllDirectories));
}
