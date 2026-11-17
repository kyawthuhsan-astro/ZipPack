using Microsoft.VisualBasic.FileIO;

namespace ZipPackNet.Services;

/// <summary>What <see cref="SourceCleanup.Plan"/> decided.</summary>
/// <param name="Delete">The sources that can be removed.</param>
/// <param name="Keep">Sources that must stay because the archive itself lies inside them.</param>
internal sealed record CleanupPlan(IReadOnlyList<string> Delete, IReadOnlyList<string> Keep);

/// <summary>The outcome of <see cref="SourceCleanup.Run"/>.</summary>
/// <param name="Deleted">The sources moved to the Recycle Bin.</param>
/// <param name="Failed">The sources that could not be removed, with the reason.</param>
internal sealed record CleanupResult(IReadOnlyList<string> Deleted, IReadOnlyList<(string Path, string Reason)> Failed);

/// <summary>
/// "Delete files after compression": removes the sources of an archive that has been created and verified.
/// </summary>
/// <remarks>
/// Files are moved to the Recycle Bin rather than deleted permanently, so a mistake can be undone. The caller
/// verifies the archive first; this class only decides what may go and removes it.
/// </remarks>
internal static class SourceCleanup
{
    /// <summary>
    /// Excludes any source that contains the archive (deleting that folder would delete the archive too), and
    /// the archive itself should it be listed as a source.
    /// </summary>
    public static CleanupPlan Plan(IReadOnlyList<string> sources, string archivePath)
    {
        string archive = Path.GetFullPath(archivePath);
        var delete = new List<string>();
        var keep = new List<string>();

        foreach (string source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string root = Path.TrimEndingDirectorySeparator(source);
            bool isArchive = string.Equals(root, archive, StringComparison.OrdinalIgnoreCase);
            bool containsArchive = archive.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            (isArchive || containsArchive ? keep : delete).Add(source);
        }

        return new CleanupPlan(delete, keep);
    }

    /// <summary>Removes every source in <paramref name="paths"/>, continuing past failures.</summary>
    /// <param name="paths">Files and folders to remove.</param>
    /// <param name="remove">How to remove one path; <see langword="null"/> moves it to the Recycle Bin.</param>
    public static CleanupResult Run(IReadOnlyList<string> paths, Action<string>? remove = null)
    {
        remove ??= MoveToRecycleBin;
        var deleted = new List<string>();
        var failed = new List<(string, string)>();

        foreach (string path in paths)
        {
            try
            {
                remove(path);
                deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException)
            {
                failed.Add((path, ex.Message));
            }
        }

        return new CleanupResult(deleted, failed);
    }

    private static void MoveToRecycleBin(string path)
    {
        // OnlyErrorDialogs: no "are you sure" prompt per file (the user already chose this), but Windows still
        // reports what it cannot recycle.
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }
}
