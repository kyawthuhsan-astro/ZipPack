using ImageMagick;
using Velopack.Sources;
using ZipPack;
using ZipPackNet.Services;
using ZipPackNet.UI;

namespace ZipPackTest;

/// <summary>Tests for the non-visual parts of the image workflow and the Velopack update support.</summary>
public sealed class ZipPackNetImageAndUpdateTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly IMagickNetCore _images = CompressionService.CreateDefault().GetStrategy<IMagickNetCore>("image");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Version_IsTheProjectVersion()
    {
        // Whatever <Version> the project file says (checked against the file in ZipPackNetInstallerTests).
        string version = typeof(UpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single().InformationalVersion;

        var updates = new UpdateService(feed: null);
        Assert.Equal(version, updates.CurrentVersion);
        Assert.Equal("v" + version, updates.DisplayVersion);
        Assert.Equal("v" + version, new UpdateWorkflow(updates).DisplayVersion);
    }

    [Fact]
    public async Task Updates_WithoutFeed_AreUnavailableWithAReason()
    {
        var updates = new UpdateService(feed: "  ");

        Assert.Null(updates.Feed);
        Assert.False(updates.CanCheck);
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Contains("feed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Updates_InADevelopmentBuild_AreUnavailable_EvenWithAFeed()
    {
        // The test host is not a Velopack installation, exactly like running ZipPackNet from Visual Studio.
        var updates = new UpdateService(_workspace.Output);

        Assert.False(updates.CanCheck);
        UpdateCheckResult result = await updates.CheckAsync();
        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Contains("installed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(UpdateCheckStatus.Unavailable, (await new UpdateWorkflow(updates).CheckQuietlyAsync())?.Status);
    }

    [Theory]
    [InlineData("https://github.com/someone/zippack", typeof(GithubSource))]
    [InlineData("https://github.com/someone/zippack/", typeof(GithubSource))]
    [InlineData("https://downloads.example.com/zippack", typeof(SimpleWebSource))]
    [InlineData(@"\\server\share\zippack", typeof(SimpleFileSource))]
    [InlineData(@"D:\Releases\ZipPack", typeof(SimpleFileSource))]
    public void UpdateFeed_PicksTheMatchingVelopackSource(string feed, Type expected)
    {
        Assert.IsType(expected, UpdateService.CreateSource(feed));
    }

    [Fact]
    public void CollectImages_FindsImagesInFilesAndFolders_ByContentAndExtension()
    {
        string album = Directory.CreateDirectory(_workspace.PathFor(Path.Combine("album", "2024"))).FullName;
        string a = TestImages.SaveLoosePhoto(Path.Combine(album, "a.jpg"), MagickFormat.Jpeg, 32, 32);
        string b = TestImages.SaveLoosePhoto(_workspace.PathFor(Path.Combine("album", "b.png")), MagickFormat.Png, 32, 32);
        string disguised = TestImages.SaveLoosePhoto(_workspace.PathFor("scan.dat"), MagickFormat.Png, 32, 32);
        File.WriteAllText(_workspace.PathFor(Path.Combine("album", "readme.txt")), "not an image");

        IReadOnlyList<string> found = ImageWorkflow.CollectImages(
            [_workspace.PathFor("album"), disguised, a, _workspace.TextFile, _workspace.PathFor("missing.png")],
            _images);

        // Folder contents in path order; files listed again later are not repeated.
        Assert.Equal([a, b, disguised], found);
    }

    [Fact]
    public void Plan_WritesNextToOriginals_OneBatchPerFolder_OrEverythingIntoOneFolder()
    {
        string[] images = [@"C:\p\1.jpg", @"C:\p\2.jpg", @"C:\q\3.png"];
        var options = new ImageCompressOptions { TargetFormatId = "webp" };

        IReadOnlyList<ImageBatchCompressRequest> inPlace = ImageWorkflow.Plan(images, new ImageJobChoices(options, ReplaceOriginals: true, OutputFolder: null));
        Assert.Equal([@"C:\p", @"C:\q"], inPlace.Select(batch => batch.DestinationDirectory));
        Assert.Equal([2, 1], inPlace.Select(batch => batch.SourcePaths.Count));
        Assert.All(inPlace, batch => Assert.Same(options, batch.Options));

        ImageBatchCompressRequest single = Assert.Single(ImageWorkflow.Plan(images, new ImageJobChoices(options, false, @"D:\out")));
        Assert.Equal((@"D:\out", 3), (single.DestinationDirectory, single.SourcePaths.Count));

        Assert.Equal(@"C:\p\Compressed", ImageCompressDialog.SuggestFolder(images));
    }

    [Fact]
    public void Summary_ReportsSavingsKeptFilesAndFailures()
    {
        ImageJobSummary summary = ImageJobSummary.From(
        [
            new ImageCompressResult(@"C:\a.jpg", @"C:\a.jpg", 4096, 1024, false),
            new ImageCompressResult(@"C:\b.jpg", @"C:\b.jpg", 4096, 4096, true),
            new ImageCompressResult(@"C:\c.png", @"C:\c.png", 100, 0, false, new UnsupportedFormatException("'c.png' is not an image.")),
        ]);

        Assert.Equal((2, 1, 8192L, 5120L, 3072L, 38), (summary.Compressed, summary.KeptOriginal, summary.OriginalBytes, summary.CompressedBytes, summary.SavedBytes, summary.SavedPercent));
        string text = summary.Describe();
        Assert.Contains("2 of 3 images were compressed", text, StringComparison.Ordinal);
        Assert.Contains("saved 3 KB, 38%", text, StringComparison.Ordinal);
        Assert.Contains("1 image was already as small", text, StringComparison.Ordinal);
        Assert.Contains("c.png — 'c.png' is not an image.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_RememberImageAndUpdateChoices()
    {
        string path = _workspace.PathFor("settings.json");
        new AppSettings
        {
            CheckForUpdatesAtStartup = false,
            ImageFormatId = "avif",
            ImagePreset = CompressionPreset.High,
            ImageMaxSide = 2048,
            ImageStripMetadata = false,
            ImageReplaceOriginals = true,
        }.Save(path);

        AppSettings loaded = AppSettings.Load(path);
        Assert.Equal(
            (false, "avif", CompressionPreset.High, (int?)2048, false, true),
            (loaded.CheckForUpdatesAtStartup, loaded.ImageFormatId, loaded.ImagePreset, loaded.ImageMaxSide, loaded.ImageStripMetadata, loaded.ImageReplaceOriginals));

        AppSettings defaults = new();
        Assert.True(defaults.CheckForUpdatesAtStartup);
        Assert.Null(defaults.ImageFormatId);
    }
}
