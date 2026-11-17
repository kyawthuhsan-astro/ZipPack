namespace ZstdCore.Internal;

/// <summary>One file or directory that goes into an archive.</summary>
/// <param name="EntryPath">The path inside the archive, using <c>/</c> as separator.</param>
/// <param name="FullPath">The path on disk.</param>
/// <param name="IsDirectory"><see langword="true"/> for a directory entry.</param>
/// <param name="Size">The file size in bytes (0 for directories).</param>
internal readonly record struct SourceItem(string EntryPath, string FullPath, bool IsDirectory, long Size);

/// <summary>Turns file system paths into the flat list of entries of an archive.</summary>
internal static class SourceCollector
{
    /// <summary>
    /// Collects the given files (stored under their name) and directories (stored recursively under their name).
    /// </summary>
    /// <exception cref="FileNotFoundException">A path does not exist.</exception>
    /// <exception cref="ArgumentException">No path was given or two sources map to the same entry name.</exception>
    public static List<SourceItem> Collect(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = new List<SourceItem>();

        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                string name = new DirectoryInfo(fullPath).Name;
                items.Add(new SourceItem(name, fullPath, IsDirectory: true, Size: 0));
                AddDirectoryContent(items, fullPath, name + "/");
            }
            else if (File.Exists(fullPath))
            {
                items.Add(new SourceItem(Path.GetFileName(fullPath), fullPath, IsDirectory: false, new FileInfo(fullPath).Length));
            }
            else
            {
                throw new FileNotFoundException($"The source path '{path}' does not exist.", path);
            }
        }

        return Validate(items);
    }

    /// <summary>Collects the content of a directory, optionally nested under the directory's own name.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public static List<SourceItem> CollectDirectory(string directory, bool includeRootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The directory '{directory}' does not exist.");
        }

        if (includeRootDirectory)
        {
            return Collect([fullPath]);
        }

        var items = new List<SourceItem>();
        AddDirectoryContent(items, fullPath, prefix: string.Empty);
        return Validate(items);
    }

    private static void AddDirectoryContent(List<SourceItem> items, string directory, string prefix)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            string entryPath = prefix + Path.GetRelativePath(directory, entry).Replace('\\', '/');
            bool isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);
            items.Add(new SourceItem(entryPath, entry, isDirectory, isDirectory ? 0 : new FileInfo(entry).Length));
        }
    }

    private static List<SourceItem> Validate(List<SourceItem> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("There is nothing to compress.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceItem item in items)
        {
            if (!names.Add(item.EntryPath))
            {
                throw new ArgumentException($"More than one source maps to the archive entry '{item.EntryPath}'.");
            }
        }

        return items;
    }
}
