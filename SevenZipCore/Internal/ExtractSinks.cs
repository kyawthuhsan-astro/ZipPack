using SevenZipCore.Interop;

namespace SevenZipCore.Internal;

/// <summary>Writes extracted entries into a directory tree, restoring timestamps and basic attributes.</summary>
internal sealed class DirectoryExtractSink : IExtractSink
{
    private const int BufferSize = 128 * 1024;
    private const long MaxPreallocation = 4L * 1024 * 1024 * 1024;
    private const FileAttributes RestorableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    private readonly string _root;
    private readonly bool _overwrite;
    private readonly Dictionary<Stream, string> _targets = [];

    public DirectoryExtractSink(string destinationDirectory, bool overwrite)
    {
        _root = NormalizeRoot(destinationDirectory);
        _overwrite = overwrite;
        Directory.CreateDirectory(_root);
    }

    public Stream? OpenEntry(ArchiveEntry entry)
    {
        string target = ResolveSafePath(_root, entry.Path);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            return null;
        }

        if (File.Exists(target))
        {
            if (!_overwrite)
            {
                return null;
            }

            // A read-only file from a previous extraction must not block replacing it.
            File.SetAttributes(target, FileAttributes.Normal);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var stream = new FileStream(target, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,

            // Reserving the final size up front saves the file system from growing the file block by block
            // and keeps large files in one piece on disk. It reserves space only (the length still grows as data
            // arrives), and is capped so a header that lies about the size cannot claim the whole disk.
            PreallocationSize = Math.Clamp(entry.Size, 0, MaxPreallocation),
        });
        _targets[stream] = target;
        return stream;
    }

    public void CloseEntry(ArchiveEntry entry, Stream stream, bool succeeded)
    {
        if (!_targets.Remove(stream, out string? target))
        {
            stream.Dispose();
            return;
        }

        if (!succeeded)
        {
            stream.Dispose();
            File.Delete(target);
            return;
        }

        // Restore the metadata through the open handle: no second open per file, which matters for archives
        // with thousands of small files. Flush first so closing does not touch the timestamp again.
        var file = (FileStream)stream;
        file.Flush();
        if (entry.LastWriteTime is { } lastWriteTime)
        {
            File.SetLastWriteTimeUtc(file.SafeFileHandle, lastWriteTime);
        }

        FileAttributes attributes = entry.Attributes & RestorableAttributes;
        if (attributes != 0)
        {
            File.SetAttributes(file.SafeFileHandle, attributes);
        }

        file.Dispose();
    }

    /// <summary>
    /// Returns the full path of a destination directory with exactly one trailing separator, so that
    /// "starts with" comparisons cannot be fooled by sibling names ("C:\out" vs "C:\output").
    /// </summary>
    /// <remarks>A drive root such as <c>D:\</c> already ends with its separator and must not get a second one.</remarks>
    internal static string NormalizeRoot(string destinationDirectory)
    {
        string fullPath = Path.GetFullPath(destinationDirectory);
        return Path.EndsInDirectorySeparator(fullPath) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    /// <summary>Maps an entry path into the destination and rejects paths that would escape it ("zip slip").</summary>
    /// <param name="root">The destination as returned by <see cref="NormalizeRoot"/>.</param>
    /// <param name="entryPath">The path of the entry inside the archive.</param>
    internal static string ResolveSafePath(string root, string entryPath)
    {
        string relative = entryPath.TrimStart('\\', '/');
        string fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (relative.Length == 0 || fullPath.Length <= root.Length || !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new SevenZipArchiveException(
                $"The entry '{entryPath}' would be extracted outside of the destination directory.");
        }

        return fullPath;
    }
}

/// <summary>Writes the content of a single entry into a caller supplied stream, which is left open.</summary>
internal sealed class StreamExtractSink(int entryIndex, Stream destination) : IExtractSink
{
    public Stream? OpenEntry(ArchiveEntry entry) => entry.Index == entryIndex ? destination : null;

    public void CloseEntry(ArchiveEntry entry, Stream stream, bool succeeded)
    {
    }
}
