using ZipPack;
using ZipPack.Strategies;

namespace ZipPackTest;

public sealed class CompressionServiceTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly ICompressionService _service = CompressionService.CreateDefault();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("tree.7z", "7z")]
    [InlineData("tree.zip", "zip")]
    [InlineData("tree.tar", "tar")]
    [InlineData("tree.tar.zst", "zstd")]
    [InlineData("tree.tzst", "zstd")]
    [InlineData("TREE.ZIP", "zip")]
    public async Task Archive_IsCreatedListedTestedAndExtracted_ByFileExtension(string fileName, string expectedStrategy)
    {
        string archive = _workspace.PathFor(fileName);

        await _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = archive },
            cancellationToken: Token);

        Assert.Equal(expectedStrategy, (await _service.ResolveArchiverAsync(archive, Token)).Id);

        IReadOnlyList<ArchiveEntryInfo> entries = await _service.ListEntriesAsync(archive, cancellationToken: Token);
        Assert.Contains(entries, e => e is { Path: "source/hello.txt", IsDirectory: false, Size: 15, IsEncrypted: false });
        Assert.Contains(entries, e => e is { Path: "source/sub/deep", IsDirectory: true });

        await _service.TestArchiveAsync(archive, cancellationToken: Token);
        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.Output },
            cancellationToken: Token);

        TestWorkspace.AssertTreesEqual(_workspace.Source, Path.Combine(_workspace.Output, "source"));
    }

    [Theory]
    [InlineData("hello.txt.gz")]
    [InlineData("hello.txt.bz2")]
    [InlineData("hello.txt.xz")]
    [InlineData("hello.txt.zst")]
    public async Task SingleFile_RoundTripsThroughSingleStreamFormats(string fileName)
    {
        string archive = _workspace.PathFor(fileName);

        await _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.TextFile], DestinationPath = archive },
            cancellationToken: Token);
        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.Output },
            cancellationToken: Token);

        Assert.Equal("Hello, ZipPack!", await File.ReadAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), Token));
    }

    [Theory]
    [InlineData("secret.7z", true)]
    [InlineData("secret.7z", false)]
    [InlineData("secret.zip", false)]
    [InlineData("secret.tar.zst", true)]
    public async Task PasswordProtection_WorksTheSameForEveryEngine(string fileName, bool encryptFileNames)
    {
        string archive = _workspace.PathFor(fileName);
        var options = new CompressionOptions { Password = "s3cr3t", EncryptFileNames = encryptFileNames, Preset = CompressionPreset.Fast };

        await _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = archive, Options = options },
            cancellationToken: Token);

        foreach (string? badPassword in new[] { null, "wrong" })
        {
            await Assert.ThrowsAsync<InvalidPasswordException>(() => _service.ExtractArchiveAsync(
                new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.PathFor("bad"), Password = badPassword },
                cancellationToken: Token));
            await Assert.ThrowsAsync<InvalidPasswordException>(() => _service.TestArchiveAsync(archive, badPassword, cancellationToken: Token));
        }

        if (encryptFileNames)
        {
            await Assert.ThrowsAsync<InvalidPasswordException>(() => _service.ListEntriesAsync(archive, cancellationToken: Token));
        }

        IReadOnlyList<ArchiveEntryInfo> entries = await _service.ListEntriesAsync(archive, "s3cr3t", Token);
        Assert.True(entries.Single(e => e.Path == "source/hello.txt").IsEncrypted);

        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.Output, Password = "s3cr3t" },
            cancellationToken: Token);
        TestWorkspace.AssertTreesEqual(_workspace.Source, Path.Combine(_workspace.Output, "source"));
    }

    [Fact]
    public async Task Create_RejectsRequestsTheFormatCannotExpress()
    {
        var withPassword = new CompressionOptions { Password = "s3cr3t" };

        // tar has no encryption, gzip holds one file, .rar is extract-only, .abc is unknown.
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = _workspace.PathFor("a.tar"), Options = withPassword },
            cancellationToken: Token));
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = _workspace.PathFor("a.gz") },
            cancellationToken: Token));
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = _workspace.PathFor("a.rar") },
            cancellationToken: Token));
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = _workspace.PathFor("a.abc") },
            cancellationToken: Token));

        Assert.Empty(Directory.EnumerateFiles(_workspace.Root, "a.*"));
    }

    [Fact]
    public async Task Create_HonoursOverwriteAndExplicitFormat()
    {
        string archive = _workspace.PathFor("backup.dat");
        var request = new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = archive, FormatId = "zip" };

        await _service.CreateArchiveAsync(request, cancellationToken: Token);
        await Assert.ThrowsAsync<IOException>(() => _service.CreateArchiveAsync(request, cancellationToken: Token));
        await _service.CreateArchiveAsync(request with { Overwrite = true }, cancellationToken: Token);

        // The content decides which archiver reads it back, not the ".dat" extension.
        Assert.Equal("zip", (await _service.ResolveArchiverAsync(archive, Token)).Id);
    }

    [Fact]
    public async Task Resolve_RejectsMissingAndUnknownFiles()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _service.ResolveArchiverAsync(_workspace.PathFor("missing.7z"), Token));
        await Assert.ThrowsAsync<UnsupportedFormatException>(async () => await _service.ResolveArchiverAsync(_workspace.TextFile, Token));
    }

    [Fact]
    public async Task Extract_ReportsProgressAndCurrentItem()
    {
        string archive = _workspace.PathFor("tree.7z");
        await _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = archive },
            cancellationToken: Token);

        var reports = new List<CompressionProgress>();
        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.Output },
            new SynchronousProgress<CompressionProgress>(reports.Add),
            Token);

        Assert.Equal(100, reports[^1].Percent);
        Assert.Contains(reports, r => r.CurrentItem is not null && r.CurrentItem.StartsWith("source/", StringComparison.Ordinal));
        Assert.All(reports, r => Assert.InRange(r.Fraction, 0d, 1d));
    }

    [Fact]
    public async Task StreamCompressor_RoundTripsInMemory()
    {
        IStreamCompressor zstd = _service.GetStrategy<IStreamCompressor>("zstd");
        byte[] original = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("stream me ", 10_000)));
        var options = new CompressionOptions { Preset = CompressionPreset.Ultra, Password = "pw" };

        using var compressed = new MemoryStream();
        await zstd.CompressAsync(new MemoryStream(original), compressed, options, cancellationToken: Token);
        compressed.Position = 0;
        using var restored = new MemoryStream();
        await zstd.DecompressAsync(compressed, restored, "pw", cancellationToken: Token);

        Assert.True(compressed.Length < original.Length / 20);
        Assert.Equal(original, restored.ToArray());

        compressed.Position = 0;
        await Assert.ThrowsAsync<InvalidPasswordException>(() => zstd.DecompressAsync(compressed, Stream.Null, "nope", cancellationToken: Token));
    }

    [Fact]
    public void Registry_ExposesStrategiesByKindAndId()
    {
        Assert.Equal(
            ["7z", "zip", "zstd", "tar", "gzip", "bzip2", "xz", "7z-readonly"],
            _service.GetStrategies<IArchiver>().Select(a => a.Id));
        Assert.Same(SevenZipArchiver.Zip, _service.GetStrategy<IArchiver>("ZIP"));
        Assert.False(_service.TryGetStrategy<IStreamCompressor>("zip", out _));
        Assert.Throws<UnsupportedFormatException>(() => _service.GetStrategy<IArchiver>("lha"));

        Assert.Equal("zstd", _service.GetArchiverForNewArchive("backup.tar.zst").Id);
        Assert.Equal("zstd", _service.GetArchiverForNewArchive("notes.txt.zst").Id);
        Assert.Equal("7z", _service.GetArchiverForNewArchive(@"C:\data\backup.7z").Id);
        Assert.True(SevenZipArchiver.SevenZip.Capabilities.HasFlag(ArchiverCapabilities.EncryptFileNames));
        Assert.False(SevenZipArchiver.ReadOnly.Capabilities.HasFlag(ArchiverCapabilities.Create));
    }

    [Fact]
    public async Task ImageStrategy_RoundTripsThroughTheService()
    {
        IMagickNetCore images = _service.GetStrategy<IMagickNetCore>("image");
        Assert.Same(images, Assert.Single(_service.GetStrategies<IMagickNetCore>()));
        Assert.Contains(".webp", images.FileExtensions);
        Assert.Contains("webp", images.TargetFormatIds);

        string source = TestImages.SaveLoosePhoto(_workspace.PathFor("photo.png"), ImageMagick.MagickFormat.Png, 640, 480);
        Assert.True(await images.CanCompressAsync(source, Token));
        Assert.False(await images.CanCompressAsync(_workspace.TextFile, Token));

        string destination = _workspace.PathFor("photo.webp");
        var reports = new List<CompressionProgress>();
        ImageCompressResult result = await images.CompressImageAsync(
            new ImageCompressRequest
            {
                SourcePath = source,
                DestinationPath = destination,
                Options = new ImageCompressOptions { TargetFormatId = "webp", MaxWidth = 320, Preset = CompressionPreset.High },
            },
            new SynchronousProgress<CompressionProgress>(reports.Add),
            Token);

        Assert.True(result.Succeeded);
        Assert.True(result.CompressedSize < result.OriginalSize);
        Assert.Equal(100, reports[^1].Percent);
        Assert.All(reports, r => Assert.InRange(r.Fraction, 0d, 1d));
        using (var webp = new ImageMagick.MagickImage(destination))
        {
            Assert.Equal((ImageMagick.MagickFormat.WebP, 320u, 240u), (webp.Format, webp.Width, webp.Height));
        }

        // Like archives, an existing destination needs an explicit Overwrite.
        var again = new ImageCompressRequest { SourcePath = source, DestinationPath = destination };
        await Assert.ThrowsAsync<IOException>(() => images.CompressImageAsync(again, cancellationToken: Token));
        await images.CompressImageAsync(again with { Overwrite = true }, cancellationToken: Token);

        var batchReports = new List<CompressionProgress>();
        IReadOnlyList<ImageCompressResult> batch = await images.CompressImagesAsync(
            new ImageBatchCompressRequest { SourcePaths = [source, _workspace.TextFile], DestinationDirectory = _workspace.Output },
            new SynchronousProgress<CompressionProgress>(batchReports.Add),
            Token);

        Assert.True(batch[0].Succeeded);
        Assert.IsType<UnsupportedFormatException>(batch[1].Error);
        Assert.Equal([50, 100], batchReports.Select(r => r.Percent));
    }

    [Fact]
    public async Task ImageStrategy_TranslatesEngineExceptions()
    {
        IMagickNetCore images = _service.GetStrategy<IMagickNetCore>("IMAGE");

        await Assert.ThrowsAsync<UnsupportedFormatException>(() => images.CompressImageAsync(
            new ImageCompressRequest { SourcePath = _workspace.TextFile, DestinationPath = _workspace.PathFor("a.png") },
            cancellationToken: Token));

        string gif = TestImages.SaveAnimatedGif(_workspace.PathFor("anim.gif"));
        await Assert.ThrowsAsync<UnsupportedFormatException>(() => images.CompressImageAsync(
            new ImageCompressRequest
            {
                SourcePath = gif,
                DestinationPath = _workspace.PathFor("anim.jpg"),
                Options = new ImageCompressOptions { TargetFormatId = "jpeg" },
            },
            cancellationToken: Token));

        await Assert.ThrowsAsync<UnsupportedFormatException>(() => images.CompressImageAsync(
            new ImageCompressRequest
            {
                SourcePath = gif,
                DestinationPath = _workspace.PathFor("anim.xyz"),
                Options = new ImageCompressOptions { TargetFormatId = "xyz" },
            },
            cancellationToken: Token));

        string full = TestImages.SaveLoosePhoto(_workspace.PathFor("full.png"), ImageMagick.MagickFormat.Png);
        string truncated = _workspace.PathFor("cut.png");
        byte[] bytes = await File.ReadAllBytesAsync(full, Token);
        await File.WriteAllBytesAsync(truncated, bytes.AsMemory(0, bytes.Length / 2), Token);

        CompressionException ex = await Assert.ThrowsAsync<CompressionException>(() => images.CompressImageAsync(
            new ImageCompressRequest { SourcePath = truncated, DestinationPath = _workspace.PathFor("out.png") },
            cancellationToken: Token));
        Assert.Contains("cut.png", ex.Message, StringComparison.Ordinal);

        // The image module is not an archiver, so archive resolution still rejects images.
        await Assert.ThrowsAsync<UnsupportedFormatException>(async () => await _service.ResolveArchiverAsync(full, Token));
    }

    [Fact]
    public void Registry_RejectsDuplicateIds()
    {
        Assert.Throws<ArgumentException>(() => new CompressionService([SevenZipArchiver.Zip, SevenZipArchiver.Zip]));
    }

    /// <summary>
    /// The Open/Closed Principle in action: a brand-new kind of module (image compression) is added with a new
    /// interface and one registration; neither <see cref="CompressionService"/> nor any existing strategy changes.
    /// </summary>
    [Fact]
    public async Task NewKindsOfModules_PlugInWithoutChangingExistingCode()
    {
        var service = new CompressionService([.. CompressionService.DefaultStrategies, new FakeImageCompressor()]);

        IImageCompressor image = Assert.Single(service.GetStrategies<IImageCompressor>());
        Assert.Same(image, service.GetStrategy<IImageCompressor>("fake-jpeg"));
        Assert.Equal(75, await image.CompressImageAsync("in.png", "out.jpg", quality: 75, Token));

        // Existing behaviour is untouched: the image module is not mistaken for an archiver.
        Assert.DoesNotContain(service.GetStrategies<IArchiver>(), a => a.Id == "fake-jpeg");
        Assert.Equal(CompressionService.DefaultStrategies.Count + 1, service.Strategies.Count);
    }

    /// <summary>What a future image module's contract could look like.</summary>
    private interface IImageCompressor : ICompressionStrategy
    {
        Task<int> CompressImageAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken);
    }

    private sealed class FakeImageCompressor : IImageCompressor
    {
        public string Id => "fake-jpeg";

        public string DisplayName => "Fake JPEG";

        public IReadOnlyList<string> FileExtensions { get; } = [".jpg", ".jpeg"];

        public Task<int> CompressImageAsync(string sourcePath, string destinationPath, int quality, CancellationToken cancellationToken) =>
            Task.FromResult(quality);
    }
}
