namespace ZipPackTest;

/// <summary>
/// A throw-away directory with a small, deterministic sample tree for round-trip tests.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ZipPackTest", Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "source");
        Output = Path.Combine(Root, "output");
        Directory.CreateDirectory(Output);
        CreateSampleTree();
    }

    /// <summary>Gets the workspace directory.</summary>
    public string Root { get; }

    /// <summary>Gets the directory holding the sample tree.</summary>
    public string Source { get; }

    /// <summary>Gets an empty directory to extract into.</summary>
    public string Output { get; }

    /// <summary>Gets a small text file inside the sample tree.</summary>
    public string TextFile => Path.Combine(Source, "hello.txt");

    /// <summary>Gets a path (not yet existing) inside the workspace.</summary>
    public string PathFor(string name) => Path.Combine(Root, name);

    /// <summary>Returns deterministic pseudo-random bytes that do not compress.</summary>
    public static byte[] RandomBytes(int length, int seed = 42)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Asserts that two directory trees contain the same directories and identical files.</summary>
    public static void AssertTreesEqual(string expectedRoot, string actualRoot)
    {
        Assert.Equal(ListRelative(expectedRoot), ListRelative(actualRoot));
        foreach (string file in Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(expectedRoot, file);
            Assert.True(
                File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actualRoot, relative))),
                $"The content of '{relative}' differs.");
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a temp directory behind must not fail a test.
        }
    }

    private static string[] ListRelative(string root) =>
    [
        .. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? Path.DirectorySeparatorChar : string.Empty))
            .Order(StringComparer.Ordinal),
    ];

    private void CreateSampleTree()
    {
        Directory.CreateDirectory(Path.Combine(Source, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(Source, "empty"));

        File.WriteAllText(TextFile, "Hello, ZipPack!");
        File.WriteAllText(Path.Combine(Source, "repetitive.txt"), string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog.\n", 20_000)));
        File.WriteAllBytes(Path.Combine(Source, "sub", "random.bin"), RandomBytes(300_000));
        File.WriteAllText(Path.Combine(Source, "sub", "deep", "မင်္ဂလာပါ ünïcödé.txt"), "Unicode file names survive the round trip.");
        File.WriteAllBytes(Path.Combine(Source, "sub", "zero-length.dat"), []);
    }
}
