using ZstdCore;

namespace ZipPackTest;

public sealed class ZstdCoreTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("correct horse battery staple")]
    public async Task Stream_RoundTrips(string? password)
    {
        byte[] original = [.. TestWorkspace.RandomBytes(150_000), .. new byte[400_000]];
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Password = password });

        using var compressed = new MemoryStream();
        await compressor.CompressStreamAsync(new MemoryStream(original), compressed, cancellationToken: Token);

        compressed.Position = 0;
        using var restored = new MemoryStream();
        await ZstdExtractor.DecompressStreamAsync(compressed, restored, password, cancellationToken: Token);

        Assert.True(compressed.Length < original.Length);
        Assert.Equal(original, restored.ToArray());
    }

    [Fact]
    public async Task Stream_EmptyInput_RoundTrips()
    {
        using var compressed = new MemoryStream();
        await new ZstdCompressor().CompressStreamAsync(new MemoryStream(), compressed, cancellationToken: Token);

        compressed.Position = 0;
        using var restored = new MemoryStream();
        await ZstdExtractor.DecompressStreamAsync(compressed, restored, cancellationToken: Token);

        Assert.Equal(0, restored.Length);
    }

    [Fact]
    public async Task PlainOutput_IsAStandardZstandardFrame()
    {
        string archive = _workspace.PathFor("hello.txt.zst");
        await new ZstdCompressor().CompressFileAsync(_workspace.TextFile, archive, cancellationToken: Token);

        Assert.Equal(ZstdFileKind.Zstd, await ZstdFormat.IdentifyFileAsync(archive, Token));

        // Decode with the raw .NET API to prove no proprietary framing is involved.
        await using var file = File.OpenRead(archive);
        await using var zstd = new System.IO.Compression.ZstandardStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(zstd);
        Assert.Equal("Hello, ZipPack!", await reader.ReadToEndAsync(Token));
    }

    [Fact]
    public async Task ProtectedOutput_IsRecognisedAndDoesNotLeakContent()
    {
        string archive = _workspace.PathFor("repetitive.zst");
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Password = "secret" });
        await compressor.CompressFileAsync(Path.Combine(_workspace.Source, "repetitive.txt"), archive, cancellationToken: Token);

        Assert.Equal(ZstdFileKind.ProtectedZstd, await ZstdFormat.IdentifyFileAsync(archive, Token));
        Assert.DoesNotContain("quick brown fox", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(archive, Token)));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData(null)]
    public async Task ProtectedData_WithWrongOrMissingPassword_Throws(string? password)
    {
        string archive = _workspace.PathFor("tree.tar.zst");
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Password = "secret" });
        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        var extractor = new ZstdExtractor(archive, password);

        await Assert.ThrowsAsync<ZstdPasswordException>(() => extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Output));
    }

    [Fact]
    public async Task ProtectedData_WhenTampered_Throws()
    {
        string archive = _workspace.PathFor("tree.tar.zst");
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Password = "secret" });
        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        byte[] bytes = await File.ReadAllBytesAsync(archive, Token);
        bytes[^100] ^= 0x01;
        await File.WriteAllBytesAsync(archive, bytes, Token);

        var extractor = new ZstdExtractor(archive, "secret");
        await Assert.ThrowsAnyAsync<ZstdException>(() => extractor.TestAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task ProtectedData_WhenTruncated_Throws()
    {
        string archive = _workspace.PathFor("tree.tar.zst");
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Password = "secret" });
        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        byte[] bytes = await File.ReadAllBytesAsync(archive, Token);
        await File.WriteAllBytesAsync(archive, bytes.AsMemory(0, bytes.Length / 2), Token);

        var extractor = new ZstdExtractor(archive, "secret");
        await Assert.ThrowsAnyAsync<ZstdException>(() => extractor.TestAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task PlainData_WhenCorrupted_Throws()
    {
        string archive = _workspace.PathFor("random.bin.zst");
        await new ZstdCompressor().CompressFileAsync(Path.Combine(_workspace.Source, "sub", "random.bin"), archive, cancellationToken: Token);

        byte[] bytes = await File.ReadAllBytesAsync(archive, Token);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Token);

        await Assert.ThrowsAnyAsync<ZstdException>(() => new ZstdExtractor(archive).TestAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task NonZstandardFile_Throws()
    {
        await Assert.ThrowsAnyAsync<ZstdException>(() => new ZstdExtractor(_workspace.TextFile).TestAsync(cancellationToken: Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pässwörd")]
    public async Task Directory_RoundTrips(string? password)
    {
        string archive = _workspace.PathFor("tree.tar.zst");
        var compressor = new ZstdCompressor(new ZstdCompressionOptions { Quality = 9, Password = password });

        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);
        var extractor = new ZstdExtractor(archive, password);
        await extractor.TestAsync(cancellationToken: Token);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        TestWorkspace.AssertTreesEqual(_workspace.Source, _workspace.Output);
    }

    [Fact]
    public async Task Files_AreStoredUnderTheirNames_AndDirectoriesRecursively()
    {
        string archive = _workspace.PathFor("mixed.tar.zst");
        await new ZstdCompressor().CompressFilesAsync(
            archive, [_workspace.TextFile, Path.Combine(_workspace.Source, "sub")], cancellationToken: Token);

        IReadOnlyList<ZstdEntry> entries = await new ZstdExtractor(archive).GetEntriesAsync(Token);

        Assert.Contains(entries, e => e is { Path: "hello.txt", IsDirectory: false, Size: 15 });
        Assert.Contains(entries, e => e is { Path: "sub", IsDirectory: true });
        Assert.Contains(entries, e => e is { Path: "sub/random.bin", Size: 300_000 });
        Assert.Contains(entries, e => e.Path.StartsWith("sub/deep/", StringComparison.Ordinal) && e.Path.Contains("ünïcödé"));
    }

    [Fact]
    public async Task SingleFile_ExtractsUnderTheArchiveNameWithoutExtension()
    {
        string archive = _workspace.PathFor("renamed.txt.zst");
        await new ZstdCompressor().CompressFileAsync(_workspace.TextFile, archive, cancellationToken: Token);

        var extractor = new ZstdExtractor(archive);
        ZstdEntry entry = Assert.Single(await extractor.GetEntriesAsync(Token));
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        Assert.Equal(new ZstdEntry("renamed.txt", false, 15, entry.LastWriteTime), entry);
        Assert.Equal("Hello, ZipPack!", await File.ReadAllTextAsync(Path.Combine(_workspace.Output, "renamed.txt"), Token));
    }

    [Fact]
    public void ExtractionTarget_AcceptsADriveRootAndRejectsEscapes()
    {
        // Regression: extracting straight into "D:\" was rejected as "outside of the destination".
        Assert.Equal(@"D:\backup.bak", ZstdExtractor.ResolveSafePath(ZstdExtractor.NormalizeRoot(@"D:\"), "backup.bak"));
        Assert.Equal(@"D:\out\a\b.txt", ZstdExtractor.ResolveSafePath(ZstdExtractor.NormalizeRoot(@"D:\out"), "a/b.txt"));

        Assert.Throws<ZstdException>(() => ZstdExtractor.ResolveSafePath(ZstdExtractor.NormalizeRoot(@"D:\out"), "../evil.txt"));
        Assert.Throws<ZstdException>(() => ZstdExtractor.ResolveSafePath(ZstdExtractor.NormalizeRoot(@"D:\out"), "../output/evil.txt"));
        Assert.Throws<ZstdException>(() => ZstdExtractor.ResolveSafePath(ZstdExtractor.NormalizeRoot(@"D:\"), @"C:\evil.txt"));
    }

    [Fact]
    public async Task Extract_WithoutOverwrite_KeepsExistingFiles()
    {
        string archive = _workspace.PathFor("tree.tar.zst");
        await new ZstdCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), "mine", Token);

        await new ZstdExtractor(archive).ExtractArchiveAsync(_workspace.Output, overwrite: false, cancellationToken: Token);

        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), Token));
        Assert.True(File.Exists(Path.Combine(_workspace.Output, "sub", "random.bin")));
    }

    [Fact]
    public async Task Compression_ReportsProgressUpToTheTotal()
    {
        var reports = new List<ZstdProgress>();
        var progress = new SynchronousProgress<ZstdProgress>(reports.Add);

        await new ZstdCompressor().CompressDirectoryAsync(_workspace.Source, _workspace.PathFor("tree.tar.zst"), progress: progress, cancellationToken: Token);

        Assert.NotEmpty(reports);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.BytesProcessed <= pair.Second.BytesProcessed));
        Assert.Equal(reports[^1].TotalBytes, reports[^1].BytesProcessed);
    }

    [Fact]
    public async Task Compression_WhenCancelled_DeletesThePartialArchive()
    {
        string archive = _workspace.PathFor("cancelled.tar.zst");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ZstdCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(archive));
    }
}

/// <summary>An <see cref="IProgress{T}"/> that invokes its handler inline, keeping tests deterministic.</summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
