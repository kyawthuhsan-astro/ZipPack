using System.IO.Compression;
using SevenZipCore;
using ZstdCore;
using ZstdCore.Internal;
using CompressionLevel = SevenZipCore.CompressionLevel;

namespace ZipPackTest;

/// <summary>Tests for the multi-core compression paths and the faster extraction sink.</summary>
public sealed class ParallelCompressionTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private readonly TestWorkspace _workspace = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _workspace.Dispose();

    /// <summary>Compressible, varied data: text lines mixed with random bytes.</summary>
    private static byte[] SampleData(int length)
    {
        var random = new Random(7);
        byte[] data = new byte[length];
        for (int i = 0; i < length;)
        {
            if (random.Next(4) == 0)
            {
                int n = Math.Min(length - i, random.Next(16, 256));
                random.NextBytes(data.AsSpan(i, n));
                i += n;
            }
            else
            {
                byte[] line = System.Text.Encoding.ASCII.GetBytes($"line {random.Next(1000)} value={random.Next()} status=ok\n");
                int n = Math.Min(length - i, line.Length);
                line.AsSpan(0, n).CopyTo(data.AsSpan(i));
                i += n;
            }
        }

        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024)]           // exactly one chunk
    [InlineData(64 * 1024 + 1)]
    [InlineData(1_000_000)]           // many chunks, more than threads + 1 in flight
    public async Task ParallelStream_ProducesStandardZstandard_ThatAnyDecoderReads(int length)
    {
        byte[] original = SampleData(length);
        var options = new ZstandardCompressionOptions { Quality = 3, AppendChecksum = true };

        using var compressed = new MemoryStream();
        await using (var parallel = new ParallelZstandardStream(compressed, options, threads: 3, chunkSize: 64 * 1024, leaveOpen: true))
        {
            // Odd write sizes cross the chunk boundaries.
            for (int i = 0; i < original.Length; i += 7_919)
            {
                await parallel.WriteAsync(original.AsMemory(i, Math.Min(7_919, original.Length - i)), Token);
            }
        }

        compressed.Position = 0;
        await using var decoder = new ZstandardStream(compressed, CompressionMode.Decompress);
        using var restored = new MemoryStream();
        await decoder.CopyToAsync(restored, Token);

        Assert.Equal(original, restored.ToArray());

        // Independent chunks lose a little ratio compared with one frame — but only a little.
        using var single = new MemoryStream();
        await using (var zstd = new ZstandardStream(single, options, leaveOpen: true))
        {
            await zstd.WriteAsync(original, Token);
        }

        Assert.True(compressed.Length <= (single.Length * 1.05) + 64, $"parallel {compressed.Length} vs single {single.Length} bytes");
    }

    [Fact]
    public void ParallelStream_SynchronousWrites_AndDispose_Work()
    {
        byte[] original = SampleData(300_000);
        using var compressed = new MemoryStream();
        using (var parallel = new ParallelZstandardStream(compressed, new ZstandardCompressionOptions(), threads: 2, chunkSize: 64 * 1024, leaveOpen: true))
        {
            parallel.Write(original, 0, 100_000);
            parallel.Write(original.AsSpan(100_000));
        }

        compressed.Position = 0;
        using var decoder = new ZstandardStream(compressed, CompressionMode.Decompress);
        using var restored = new MemoryStream();
        decoder.CopyTo(restored);
        Assert.Equal(original, restored.ToArray());
    }

    [Theory]
    [InlineData(3, null, 8 * MiB)]
    [InlineData(9, null, 16 * MiB)]
    [InlineData(19, null, 32 * MiB)]
    [InlineData(22, null, 128 * MiB)]
    [InlineData(3, 20, 4 * MiB)]      // explicit 1 MiB window → 4 MiB, the minimum
    [InlineData(3, 30, 128 * MiB)]    // capped
    public void ChunkSize_IsFourWindows_WithinLimits(int quality, int? windowLog2, long expected)
    {
        Assert.Equal(expected, ParallelZstandardStream.ChunkSizeFor(quality, windowLog2));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(8, null)]
    [InlineData(1, null)]
    [InlineData(8, "pa55word")]
    public async Task Compressor_WithAnyThreadCount_RoundTripsFilesAndTrees(int? threads, string? password)
    {
        // 10 MiB is larger than one 8 MiB chunk, so the parallel path really splits.
        string big = _workspace.PathFor("big.log");
        byte[] original = SampleData(10 * (int)MiB);
        await File.WriteAllBytesAsync(big, original, Token);

        var compressor = new ZstdCompressor(new ZstdCompressionOptions { ThreadCount = threads, Password = password });
        string single = _workspace.PathFor("big.log.zst");
        await compressor.CompressFileAsync(big, single, cancellationToken: Token);
        await new ZstdExtractor(single, password).ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(_workspace.Output, "big.log"), Token));

        string tree = _workspace.PathFor("tree.tar.zst");
        await compressor.CompressDirectoryAsync(_workspace.Source, tree, cancellationToken: Token);
        string treeOut = _workspace.PathFor("tree-out");
        await new ZstdExtractor(tree, password).ExtractArchiveAsync(treeOut, cancellationToken: Token);
        TestWorkspace.AssertTreesEqual(_workspace.Source, treeOut);
    }

    [Fact]
    public async Task Compressor_Cancelled_DeletesThePartialFile()
    {
        string big = _workspace.PathFor("big.bin");
        await File.WriteAllBytesAsync(big, SampleData(24 * (int)MiB), Token);
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<ZstdProgress>(p =>
        {
            if (p.BytesProcessed > 4 * MiB)
            {
                cts.Cancel();
            }
        });

        string archive = _workspace.PathFor("big.bin.zst");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ZstdCompressor(new ZstdCompressionOptions { ThreadCount = 4 }).CompressFileAsync(big, archive, progress, cts.Token));
        Assert.False(File.Exists(archive));
    }

    [Theory]
    [InlineData(CompressionLevel.Normal, 8, null, 4 * MiB)]
    [InlineData(CompressionLevel.Low, 8, null, 1 * MiB)]
    [InlineData(CompressionLevel.High, 8, null, 8 * MiB)]
    [InlineData(CompressionLevel.Ultra, 8, null, 16 * MiB)]
    [InlineData(CompressionLevel.Fast, 8, null, null)]
    [InlineData(CompressionLevel.Normal, 2, null, null)]       // too few threads to gain anything
    [InlineData(CompressionLevel.Normal, 2, 2 * MiB, 2 * MiB)] // an explicit size always wins
    [InlineData(CompressionLevel.Normal, 8, 0L, null)]         // 0 = 7-Zip default
    [InlineData(CompressionLevel.Normal, 8, 100L, 1 * MiB)]    // at least 1 MiB
    public void Lzma2BlockSize_IsChosenPerLevelAndThreads(CompressionLevel level, int threads, long? requested, long? expected)
    {
        var compressor = new SevenZipCompressor(new SevenZipCompressionOptions { CompressionLevel = level, ThreadCount = threads, Lzma2BlockSize = requested });
        Assert.Equal(expected, compressor.ParallelLzma2BlockSize());
    }

    [Fact]
    public void Lzma2BlockSize_StepsAsideForAnExplicitMethod()
    {
        var custom = new SevenZipCompressor(new SevenZipCompressionOptions
        {
            ThreadCount = 8,
            CustomParameters = new Dictionary<string, string> { ["0"] = "PPMd" },
        });
        var ppmd = new SevenZipCompressor(new SevenZipCompressionOptions { ThreadCount = 8, CompressionMethod = CompressionMethod.Ppmd });

        Assert.Null(custom.ParallelLzma2BlockSize());
        Assert.Null(ppmd.ParallelLzma2BlockSize());
    }

    [Fact]
    public async Task SevenZip_WithSmallLzma2Blocks_IsAcceptedBy7Zip_AndRoundTrips()
    {
        string big = Path.Combine(_workspace.Source, "big.log");
        await File.WriteAllBytesAsync(big, SampleData(6 * (int)MiB), Token);

        string archive = _workspace.PathFor("tree.7z");
        await new SevenZipCompressor(new SevenZipCompressionOptions { ThreadCount = 8, Lzma2BlockSize = MiB })
            .CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);

        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);
        TestWorkspace.AssertTreesEqual(_workspace.Source, _workspace.Output);
    }

    [Fact]
    public async Task Extraction_RestoresTimestampsAndAttributes_ThroughTheOpenHandle()
    {
        string readOnly = Path.Combine(_workspace.Source, "read-only.txt");
        await File.WriteAllTextAsync(readOnly, "keep me", Token);
        var stamp = new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(readOnly, stamp);
        File.SetLastWriteTimeUtc(_workspace.TextFile, stamp.AddDays(1));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        string archive = _workspace.PathFor("attrs.7z");
        await new SevenZipCompressor().CompressDirectoryAsync(_workspace.Source, archive, cancellationToken: Token);
        using SevenZipExtractor extractor = SevenZipExtractor.Open(archive);
        await extractor.ExtractArchiveAsync(_workspace.Output, cancellationToken: Token);

        string restored = Path.Combine(_workspace.Output, "read-only.txt");
        Assert.Equal("keep me", await File.ReadAllTextAsync(restored, Token));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(restored));
        Assert.Equal(stamp.AddDays(1), File.GetLastWriteTimeUtc(Path.Combine(_workspace.Output, "hello.txt")));
        Assert.True(File.GetAttributes(restored).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(0, new FileInfo(Path.Combine(_workspace.Output, "sub", "zero-length.dat")).Length);
    }
}
