using SevenZipCore.Interop;

namespace SevenZipCore.Internal;

/// <summary>Turns file system paths into the flat list of items of an archive.</summary>
internal static class ArchiveItemCollector
{
    /// <summary>
    /// Collects the given files (stored under their name) and directories (stored recursively under their name).
    /// </summary>
    /// <exception cref="FileNotFoundException">A path does not exist.</exception>
    /// <exception cref="ArgumentException">No path was given or two sources map to the same entry name.</exception>
    public static List<ArchiveItem> Collect(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = new List<ArchiveItem>();

        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                items.Add(FromFileSystem(directory, directory.Name));
                AddDirectoryContent(items, directory, directory.Name + '\\');
            }
            else if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                items.Add(FromFileSystem(file, file.Name));
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
    public static List<ArchiveItem> CollectDirectory(string directory, bool includeRootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"The directory '{directory}' does not exist.");
        }

        if (includeRootDirectory)
        {
            return Collect([root.FullName]);
        }

        var items = new List<ArchiveItem>();
        AddDirectoryContent(items, root, prefix: string.Empty);
        return Validate(items);
    }

    /// <summary>Creates the item for content that comes from a stream rather than from the file system.</summary>
    public static ArchiveItem FromStream(Stream source, string entryName)
    {
        DateTime now = DateTime.UtcNow;
        long size = source.CanSeek ? source.Length - source.Position : 0;
        return new ArchiveItem(
            entryName.Replace('/', '\\'), IsDirectory: false, size, FileAttributes.Archive, now, now, now, SourceStream: source);
    }

    private static void AddDirectoryContent(List<ArchiveItem> items, DirectoryInfo directory, string prefix)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory.FullName, entry.FullName).Replace('/', '\\');
            items.Add(FromFileSystem(entry, prefix + relative));
        }
    }

    private static ArchiveItem FromFileSystem(FileSystemInfo info, string entryPath)
    {
        bool isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
        return new ArchiveItem(
            entryPath,
            isDirectory,
            isDirectory ? 0 : ((FileInfo)info).Length,
            info.Attributes,
            info.CreationTimeUtc,
            info.LastAccessTimeUtc,
            info.LastWriteTimeUtc,
            SourcePath: isDirectory ? null : info.FullName);
    }

    private static List<ArchiveItem> Validate(List<ArchiveItem> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("There is nothing to compress.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveItem item in items)
        {
            if (!names.Add(item.EntryPath))
            {
                throw new ArgumentException($"More than one source maps to the archive entry '{item.EntryPath}'.");
            }
        }

        return items;
    }
}
