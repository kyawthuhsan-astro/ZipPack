using System.Formats.Tar;
using System.IO.Compression;
using SevenZipCore;
using CompressionLevel = SevenZipCore.CompressionLevel;

namespace ZipPackTest;

public sealed class SevenZipCoreTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void NativeLibrary_IsDeployedAndLoads()
    {
        Assert.True(SevenZipLibrary.IsAvailable);
        Assert.EndsWith("7z.dll", SevenZipLibrary.LibraryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(OutArchiveFormat.SevenZip)]
    [InlineData(OutArchiveFormat.Zip)]
    [InlineData(OutArchiveFormat.Tar)]
    public async Task Directory_RoundTrips(OutArchiveFormat format)
    {
        string archive = _workspace.PathFor("tree" + format.GetExtension());
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = format });

        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        using SevenZipExtractor extractor = await SevenZipExtractor.OpenAsync(archive, cancellationToken: Token);
        Assert.Equal(format.ToInArchiveFormat(), extractor.Format);
        await extractor.TestAsync(cancellationToken: Token);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        TestWorkspace.AssertTreesEqual(_workspace.Source, _workspace.Output);
    }

    [Theory]
    [InlineData(OutArchiveFormat.GZip)]
    [InlineData(OutArchiveFormat.BZip2)]
    [InlineData(OutArchiveFormat.XZ)]
    public async Task SingleFileFormat_RoundTrips(OutArchiveFormat format)
    {
        string source = Path.Combine(_workspace.Source, "repetitive.txt");
        string archive = _workspace.PathFor("repetitive.txt" + format.GetExtension());
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = format });

        await compressor.CompressFilesAsync(archive, [source], cancellationToken: Token);

        using SevenZipExtractor extractor = await SevenZipExtractor.OpenAsync(archive, cancellationToken: Token);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        Assert.True(new FileInfo(archive).Length < new FileInfo(source).Length / 10);
        Assert.Equal(await File.ReadAllBytesAsync(source, Token), await File.ReadAllBytesAsync(Path.Combine(_workspace.Output, "repetitive.txt"), Token));
    }

    [Fact]
    public void SingleFileFormat_RejectsSeveralFiles()
    {
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.GZip });
        string archive = _workspace.PathFor("tree.gz");

        Assert.Throws<ArgumentException>(() => compressor.CompressDirectory(_workspace.Source, archive, cancellationToken: Token));
        Assert.False(File.Exists(archive));
    }

    [Theory]
    [InlineData(OutArchiveFormat.Tar)]
    [InlineData(OutArchiveFormat.GZip)]
    public void Options_RejectPasswordForFormatsWithoutEncryption(OutArchiveFormat format)
    {
        Assert.Throws<ArgumentException>(
            () => new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = format, Password = "secret" }));
    }

    [Fact]
    public void Options_RejectMethodsTheFormatDoesNotSupport()
    {
        Assert.Throws<ArgumentException>(() => new SevenZipCompressor(
            new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Zip, CompressionMethod = CompressionMethod.Lzma2 }));
    }

    [Theory]
    [InlineData(CompressionMethod.Lzma)]
    [InlineData(CompressionMethod.Lzma2)]
    [InlineData(CompressionMethod.Ppmd)]
    [InlineData(CompressionMethod.BZip2)]
    [InlineData(CompressionMethod.Copy)]
    public async Task SevenZip_SupportsEveryMethod(CompressionMethod method)
    {
        string archive = _workspace.PathFor($"{method}.7z");
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions
        {
            CompressionMethod = method,
            CompressionLevel = CompressionLevel.Fast,
            ThreadCount = 2,
            Solid = true,
        });

        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        string? reported = extractor.Entries.First(e => e.Path == "repetitive.txt").Method;
        Assert.StartsWith(method == CompressionMethod.Ppmd ? "PPMD" : method.ToString(), reported, StringComparison.OrdinalIgnoreCase);
        await extractor.TestAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task CompressionLevel_AndCustomParameters_AreApplied()
    {
        string stored = _workspace.PathFor("stored.7z");
        string ultra = _workspace.PathFor("ultra.7z");
        string source = Path.Combine(_workspace.Source, "repetitive.txt");

        await new SevenZipCompressor(new SevenZipCompressionOptions { CompressionLevel = CompressionLevel.None })
            .CompressFilesAsync(stored, [source], cancellationToken: Token);
        await new SevenZipCompressor(new SevenZipCompressionOptions
            {
                CompressionLevel = CompressionLevel.Ultra,
                CustomParameters = new Dictionary<string, string> { ["d"] = "1m", ["fb"] = "64" },
            })
            .CompressFilesAsync(ultra, [source], cancellationToken: Token);

        Assert.True(new FileInfo(stored).Length >= new FileInfo(source).Length);
        Assert.True(new FileInfo(ultra).Length < new FileInfo(source).Length / 50);
    }

    [Fact]
    public async Task InvalidCustomParameter_IsReported()
    {
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions
        {
            CustomParameters = new Dictionary<string, string> { ["no-such-switch"] = "1" },
        });

        SevenZipException exception = await Assert.ThrowsAsync<SevenZipException>(
            () => compressor.CompressDirectoryAsync(_workspace.Source, _workspace.PathFor("bad.7z"), cancellationToken: Token));
        Assert.Contains("no-such-switch", exception.Message);
    }

    [Fact]
    public async Task EncryptedHeaders_HideTheListingWithoutThePassword()
    {
        string archive = _workspace.PathFor("secret.7z");
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { Password = "s3cr3t", EncryptHeaders = true });
        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        Assert.Throws<SevenZipPasswordException>(() => SevenZipExtractor.Open(archive));
        Assert.Throws<SevenZipPasswordException>(() => SevenZipExtractor.Open(archive, "wrong"));

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive, "s3cr3t");
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);
        TestWorkspace.AssertTreesEqual(_workspace.Source, _workspace.Output);
    }

    [Theory]
    [InlineData(OutArchiveFormat.SevenZip)]
    [InlineData(OutArchiveFormat.Zip)]
    public async Task EncryptedContent_RequiresTheRightPassword(OutArchiveFormat format)
    {
        string archive = _workspace.PathFor("secret" + format.GetExtension());
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = format, Password = "s3cr3t" });
        await compressor.CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        // Without header encryption the listing is readable, the content is not.
        using (SevenZipExtractor noPassword = SevenZipExtractor.Open(archive))
        {
            Assert.True(noPassword.Entries.First(e => e.Path == "hello.txt").IsEncrypted);
            await Assert.ThrowsAsync<SevenZipPasswordException>(() => noPassword.TestAsync(cancellationToken: Token));
        }

        using (SevenZipExtractor wrongPassword = SevenZipExtractor.Open(archive, "wrong"))
        {
            await Assert.ThrowsAsync<SevenZipPasswordException>(
                () => wrongPassword.ExtractArchiveAsync(_workspace.PathFor("wrong"), cancellationToken: Token));

            // Nothing decrypted with the wrong key may be left behind (empty files carry no encrypted data).
            Assert.DoesNotContain(
                Directory.EnumerateFiles(_workspace.PathFor("wrong"), "*", SearchOption.AllDirectories),
                file => new FileInfo(file).Length > 0);
        }

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive, "s3cr3t");
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);
        TestWorkspace.AssertTreesEqual(_workspace.Source, _workspace.Output);
    }

    [Fact]
    public async Task Entries_DescribeTheArchiveContent()
    {
        string archive = _workspace.PathFor("tree.7z");
        await new SevenZipCompressor().CompressFilesAsync(archive, [_workspace.Source, _workspace.TextFile.Replace("hello.txt", "repetitive.txt")], cancellationToken: Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);

        ArchiveEntry hello = Assert.Single(extractor.Entries, e => e.Path == @"source\hello.txt");
        Assert.Equal(15, hello.Size);
        Assert.False(hello.IsDirectory);
        Assert.False(hello.IsEncrypted);
        Assert.Equal(File.GetLastWriteTimeUtc(_workspace.TextFile), hello.LastWriteTime);
        Assert.NotNull(hello.Crc);

        Assert.Contains(extractor.Entries, e => e is { Path: @"source\empty", IsDirectory: true });
        Assert.Contains(extractor.Entries, e => e.Path == "repetitive.txt");
        Assert.Equal(extractor.Entries.Sum(e => e.Size), extractor.TotalSize);
        Assert.Equal(Enumerable.Range(0, extractor.Entries.Count), extractor.Entries.Select(e => e.Index));
    }

    [Fact]
    public async Task SingleEntry_CanBeExtractedToAStream()
    {
        string archive = _workspace.PathFor("tree.zip");
        await new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Zip })
            .CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        ArchiveEntry entry = extractor.Entries.Single(e => e.Path.EndsWith("random.bin", StringComparison.Ordinal));
        using var content = new MemoryStream();
        await extractor.ExtractEntryAsync(entry.Index, content, cancellationToken: Token);

        Assert.Equal(TestWorkspace.RandomBytes(300_000), content.ToArray());
    }

    [Fact]
    public async Task SelectedEntries_CanBeExtracted()
    {
        string archive = _workspace.PathFor("tree.7z");
        await new SevenZipCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        int index = extractor.Entries.Single(e => e.Path == "hello.txt").Index;
        await extractor.ExtractEntriesAsync([index], _workspace.Output, cancellationToken: Token);

        Assert.Equal(["hello.txt"], Directory.EnumerateFileSystemEntries(_workspace.Output).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Streams_CanBeCompressedAndReadBackInMemory()
    {
        byte[] original = TestWorkspace.RandomBytes(50_000, seed: 7);
        using var archive = new MemoryStream();

        await new SevenZipCompressor().CompressStreamAsync(new MemoryStream(original), archive, "payload.bin", cancellationToken: Token);

        Assert.Equal(InArchiveFormat.SevenZip, ArchiveFormatDetector.Detect(archive));
        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive, leaveOpen: true);
        ArchiveEntry entry = Assert.Single(extractor.Entries);
        using var restored = new MemoryStream();
        extractor.ExtractEntry(entry.Index, restored, cancellationToken: Token);

        Assert.Equal("payload.bin", entry.Path);
        Assert.Equal(original, restored.ToArray());
    }

    [Fact]
    public async Task ZipAndTarOutput_AreReadableByOtherImplementations()
    {
        string zip = _workspace.PathFor("interop.zip");
        string tar = _workspace.PathFor("interop.tar");
        await new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Zip })
            .CompressDirectoryAsync(_workspace.Source, zip, cancellationToken: Token);
        await new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Tar })
            .CompressDirectoryAsync(_workspace.Source, tar, cancellationToken: Token);

        string zipOutput = _workspace.PathFor("zip-out");
        string tarOutput = _workspace.PathFor("tar-out");
        await ZipFile.ExtractToDirectoryAsync(zip, zipOutput, Token);
        await TarFile.ExtractToDirectoryAsync(tar, Directory.CreateDirectory(tarOutput).FullName, overwriteFiles: false, Token);

        TestWorkspace.AssertTreesEqual(_workspace.Source, zipOutput);
        TestWorkspace.AssertTreesEqual(_workspace.Source, tarOutput);
    }

    [Fact]
    public async Task ArchivesFromOtherImplementations_AreReadable()
    {
        string zip = _workspace.PathFor("dotnet.zip");
        await ZipFile.CreateFromDirectoryAsync(_workspace.Source, zip, Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(zip);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        Assert.Equal(InArchiveFormat.Zip, extractor.Format);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_workspace.Source, "sub", "random.bin"), Token),
            await File.ReadAllBytesAsync(Path.Combine(_workspace.Output, "sub", "random.bin"), Token));
    }

    [Fact]
    public async Task Format_IsDetectedFromContentNotFromTheExtension()
    {
        string misnamed = _workspace.PathFor("actually-a-zip.7z");
        await new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Zip })
            .CompressDirectoryAsync(_workspace.Source, misnamed, cancellationToken: Token);

        Assert.Equal(InArchiveFormat.Zip, ArchiveFormatDetector.Detect(misnamed));
        using SevenZipExtractor extractor = SevenZipExtractor.Open(misnamed);
        Assert.Equal(InArchiveFormat.Zip, extractor.Format);
    }

    [Fact]
    public void NonArchive_IsRejected()
    {
        Assert.Null(ArchiveFormatDetector.Detect(_workspace.TextFile));
        Assert.Throws<SevenZipArchiveException>(() => SevenZipExtractor.Open(_workspace.TextFile));
        Assert.Throws<SevenZipArchiveException>(() => SevenZipExtractor.Open(_workspace.TextFile, format: InArchiveFormat.SevenZip));
        Assert.Throws<FileNotFoundException>(() => SevenZipExtractor.Open(_workspace.PathFor("missing.7z")));
    }

    [Fact]
    public async Task DamagedArchive_FailsTheTest()
    {
        string archive = _workspace.PathFor("damaged.zip");
        await new SevenZipCompressor(new SevenZipCompressionOptions { ArchiveFormat = OutArchiveFormat.Zip, CompressionLevel = CompressionLevel.None })
            .CompressFilesAsync(archive, [Path.Combine(_workspace.Source, "sub", "random.bin")], cancellationToken: Token);

        byte[] bytes = await File.ReadAllBytesAsync(archive, Token);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        await Assert.ThrowsAsync<SevenZipArchiveException>(() => extractor.TestAsync(cancellationToken: Token));
        await Assert.ThrowsAsync<SevenZipArchiveException>(() => extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token));
        Assert.Empty(Directory.EnumerateFiles(_workspace.Output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Extract_WithoutOverwrite_KeepsExistingFiles()
    {
        string archive = _workspace.PathFor("tree.7z");
        await new SevenZipCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), "mine", Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        await extractor.ExtractArchiveAsync(_workspace.Output, overwrite: false, cancellationToken: Token);

        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_workspace.Output, "hello.txt"), Token));
        Assert.True(File.Exists(Path.Combine(_workspace.Output, "sub", "random.bin")));
    }

    [Fact]
    public async Task Operations_ReportProgress()
    {
        string archive = _workspace.PathFor("tree.7z");
        var compressReports = new List<SevenZipProgress>();
        var extractReports = new List<SevenZipProgress>();

        await new SevenZipCompressor().CompressDirectoryAsync(
            _workspace.Source, archive, progress: new SynchronousProgress<SevenZipProgress>(compressReports.Add), cancellationToken: Token);
        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        await extractor.ExtractArchiveAsync(
            _workspace.Output, progress: new SynchronousProgress<SevenZipProgress>(extractReports.Add), cancellationToken: Token);

        long total = extractor.TotalSize;
        Assert.Equal(new SevenZipProgress(total, total), compressReports[^1]);
        Assert.Equal(new SevenZipProgress(total, total), extractReports[^1]);
    }

    [Fact]
    public async Task Compression_WhenCancelled_DeletesThePartialArchive()
    {
        string archive = _workspace.PathFor("cancelled.7z");
        string big = _workspace.PathFor("big.bin");
        await File.WriteAllBytesAsync(big, TestWorkspace.RandomBytes(32 * 1024 * 1024), Token);

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<SevenZipProgress>(p =>
        {
            if (p.BytesProcessed > 0)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SevenZipCompressor().CompressFilesAsync(archive, [big], progress, cancellation.Token));

        Assert.False(File.Exists(archive));
    }

    [Theory]
    [InlineData(@"D:\", @"backup.bak", @"D:\backup.bak")] // Regression: a drive root as destination.
    [InlineData(@"D:\", @"folder\file.txt", @"D:\folder\file.txt")]
    [InlineData(@"D:\out", @"file.txt", @"D:\out\file.txt")]
    [InlineData(@"D:\out\", @"\rooted\file.txt", @"D:\out\rooted\file.txt")]
    public void ExtractionTarget_IsResolvedInsideTheDestination(string destination, string entryPath, string expected)
    {
        string root = SevenZipCore.Internal.DirectoryExtractSink.NormalizeRoot(destination);
        Assert.Equal(expected, SevenZipCore.Internal.DirectoryExtractSink.ResolveSafePath(root, entryPath));
    }

    [Theory]
    [InlineData(@"D:\out", @"..\evil.txt")]
    [InlineData(@"D:\out", @"sub\..\..\evil.txt")]
    [InlineData(@"D:\out", @"..\output\evil.txt")] // A sibling whose name merely starts with the destination's.
    [InlineData(@"D:\out", @"C:\Windows\evil.txt")]
    [InlineData(@"D:\", @"C:\evil.txt")]
    [InlineData(@"D:\out", @"")]
    public void ExtractionTarget_OutsideTheDestination_IsRejected(string destination, string entryPath)
    {
        string root = SevenZipCore.Internal.DirectoryExtractSink.NormalizeRoot(destination);
        Assert.Throws<SevenZipArchiveException>(() => SevenZipCore.Internal.DirectoryExtractSink.ResolveSafePath(root, entryPath));
    }

    [Fact]
    public async Task SourceFiles_AreNotLockedAfterCompression()
    {
        string archive = _workspace.PathFor("tree.7z");
        await new SevenZipCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        // Every source handle must be closed again: exclusive access succeeds.
        foreach (string file in Directory.EnumerateFiles(_workspace.Source, "*", SearchOption.AllDirectories))
        {
            await using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
