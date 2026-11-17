using ZipPack;
using ZipPackNet.Services;

namespace ZipPackTest;

/// <summary>Tests for extracting selected entries (used by double-click in the archive view) and their temp folders.</summary>
public sealed class OpenEntryTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly ICompressionService _service = CompressionService.CreateDefault();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("source/hello.txt", "source/hello.txt", true)]
    [InlineData("SOURCE\\Hello.TXT", "source/hello.txt", true)]       // case and separators do not matter
    [InlineData("source/sub", "source/sub/deep/x.txt", true)]          // a folder includes everything below it
    [InlineData("source/sub", "source/subway.txt", false)]             // … but not a sibling with the same prefix
    [InlineData("source/hello.txt", "source/hello.txt.bak", false)]
    public void Includes_MatchesEntriesAndFolderContent(string selected, string entry, bool expected)
    {
        var request = new ArchiveExtractRequest { ArchivePath = "a.7z", DestinationDirectory = "out", EntryPaths = [selected] };
        Assert.Equal(expected, request.Includes(entry));
        Assert.True(new ArchiveExtractRequest { ArchivePath = "a.7z", DestinationDirectory = "out" }.Includes(entry));
    }

    [Theory]
    [InlineData("tree.7z", null)]
    [InlineData("tree.zip", null)]
    [InlineData("tree.tar.zst", null)]
    [InlineData("secret.7z", "pw")]
    [InlineData("secret.tar.zst", "pw")]
    public async Task Extract_WithEntryPaths_WritesOnlyTheSelection(string fileName, string? password)
    {
        string archive = _workspace.PathFor(fileName);
        await _service.CreateArchiveAsync(
            new ArchiveCreateRequest { SourcePaths = [_workspace.Source], DestinationPath = archive, Options = new CompressionOptions { Password = password } },
            cancellationToken: Token);

        // One file.
        string one = _workspace.PathFor("one");
        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = one, Password = password, EntryPaths = ["source/hello.txt"] },
            cancellationToken: Token);
        Assert.Equal(["source/hello.txt"], ListRelative(one));
        Assert.Equal("Hello, ZipPack!", await File.ReadAllTextAsync(Path.Combine(one, "source", "hello.txt"), Token));

        // A folder with its content.
        string folder = _workspace.PathFor("folder");
        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = folder, Password = password, EntryPaths = ["source/sub"] },
            cancellationToken: Token);
        Assert.Equal(
            ["source/sub/deep/မင်္ဂလာပါ ünïcödé.txt", "source/sub/random.bin", "source/sub/zero-length.dat"],
            ListRelative(folder));

        // Something that is not there.
        await Assert.ThrowsAsync<CompressionException>(() => _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.PathFor("none"), Password = password, EntryPaths = ["source/missing.txt"] },
            cancellationToken: Token));
    }

    [Fact]
    public async Task Extract_WithEntryPaths_WorksForASingleFileZst()
    {
        string archive = _workspace.PathFor("hello.txt.zst");
        await _service.CreateArchiveAsync(new ArchiveCreateRequest { SourcePaths = [_workspace.TextFile], DestinationPath = archive }, cancellationToken: Token);

        await _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.Output, EntryPaths = ["hello.txt"] },
            cancellationToken: Token);
        Assert.Equal("Hello, ZipPack!", await File.ReadAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), Token));

        await Assert.ThrowsAsync<CompressionException>(() => _service.ExtractArchiveAsync(
            new ArchiveExtractRequest { ArchivePath = archive, DestinationDirectory = _workspace.PathFor("x"), EntryPaths = ["other.txt"] },
            cancellationToken: Token));
    }

    [Fact]
    public void OpenedEntries_FoldersArePerArchive_AndPathsStayInside()
    {
        string a = OpenedEntries.FolderFor(@"C:\data\photos.7z");
        Assert.Equal(a, OpenedEntries.FolderFor(@"c:\DATA\PHOTOS.7z"), ignoreCase: true); // same archive, same folder
        Assert.NotEqual(a, OpenedEntries.FolderFor(@"D:\other\photos.7z"));          // same name, other archive
        Assert.StartsWith(OpenedEntries.Root, a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(a, "docs", "read me.txt"), OpenedEntries.PathOf(a, "docs/read me.txt"));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("tools/run.PS1", true)]
    [InlineData("link.lnk", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("report.pdf", false)]
    public void OpenedEntries_FlagsProgramsAndScripts(string entry, bool executable)
    {
        Assert.Equal(executable, OpenedEntries.IsExecutable(entry));
    }

    [Fact]
    public void OpenedEntries_CopyTheDownloadMark_ToExtractedFiles()
    {
        string archive = _workspace.PathFor("downloaded.7z");
        File.WriteAllText(archive, "x");
        const string zone = "[ZoneTransfer]\r\nZoneId=3\r\n";
        File.WriteAllText(archive + ":Zone.Identifier", zone);

        string file = _workspace.PathFor("extracted.txt");
        File.WriteAllText(file, "y");
        OpenedEntries.CopyMarkOfTheWeb(archive, file);
        Assert.Equal(zone, File.ReadAllText(file + ":Zone.Identifier"));

        // An archive without the mark leaves files alone.
        string plain = _workspace.PathFor("plain.7z");
        File.WriteAllText(plain, "x");
        string other = _workspace.PathFor("other.txt");
        File.WriteAllText(other, "z");
        OpenedEntries.CopyMarkOfTheWeb(plain, other);
        Assert.False(File.Exists(other + ":Zone.Identifier"));
    }

    [Fact]
    public void OpenedEntries_CleanUp_RemovesOnlyOldFolders()
    {
        string root = _workspace.PathFor("opened");
        string old = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "a.txt"), "a");
        File.SetAttributes(Path.Combine(old, "a.txt"), FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        string fresh = Directory.CreateDirectory(Path.Combine(root, "fresh")).FullName;

        OpenedEntries.CleanUp(TimeSpan.FromDays(1), root);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        OpenedEntries.CleanUp(TimeSpan.FromDays(1), _workspace.PathFor("does-not-exist")); // no error
    }

    private static string[] ListRelative(string root) =>
    [
        .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal),
    ];
}
