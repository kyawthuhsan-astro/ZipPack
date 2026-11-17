using System.Security.Cryptography;
using System.Text;

namespace ZipPackNet.Services;

/// <summary>
/// Where entries opened from an archive (double-click in the archive view) are extracted, and the rules
/// around opening them.
/// </summary>
/// <remarks>
/// <para>Each archive gets its own folder below <c>%TEMP%\ZipPack\Opened</c>, named after a hash of its path, so
/// two archives with an entry of the same name never collide and opening the same entry twice reuses the folder.
/// Folders older than a day are removed at start-up; files still open in another program are skipped and retried
/// next time.</para>
/// <para>The "downloaded from the internet" mark of the archive (its <c>Zone.Identifier</c> stream) is copied to the
/// extracted files, so Windows SmartScreen and Office Protected View treat them exactly like the archive.</para>
/// </remarks>
internal static class OpenedEntries
{
    private const string ZoneIdentifier = ":Zone.Identifier";

    /// <summary>Extensions Windows runs as programs or scripts; opening one asks the user first.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".cpl", ".lnk", ".url", ".reg", ".jar", ".application", ".appref-ms", ".msc",
    };

    /// <summary>Gets the root of all opened-entry folders.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "ZipPack", "Opened");

    /// <summary>Returns <see langword="true"/> for files that run code when opened.</summary>
    public static bool IsExecutable(string entryPath) => ExecutableExtensions.Contains(Path.GetExtension(entryPath));

    /// <summary>Gets the folder entries of <paramref name="archivePath"/> are extracted into.</summary>
    public static string FolderFor(string archivePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(archivePath).ToUpperInvariant()));
        string name = Path.GetFileNameWithoutExtension(archivePath);
        string safe = new([.. name.Take(40).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        return Path.Combine(Root, $"{safe}-{Convert.ToHexString(hash, 0, 6)}");
    }

    /// <summary>Gets the path an entry is extracted to below <paramref name="folder"/>.</summary>
    public static string PathOf(string folder, string entryPath) =>
        Path.Combine(folder, entryPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    /// <summary>Copies the archive's "downloaded from the internet" mark to an extracted file or folder.</summary>
    public static void CopyMarkOfTheWeb(string archivePath, string target)
    {
        string? zone;
        try
        {
            zone = File.Exists(archivePath + ZoneIdentifier) ? File.ReadAllText(archivePath + ZoneIdentifier) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return; // Not NTFS, or no mark.
        }

        if (zone is null)
        {
            return;
        }

        IEnumerable<string> files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            : [target];
        foreach (string file in files)
        {
            try
            {
                File.WriteAllText(file + ZoneIdentifier, zone);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Best effort, e.g. a read-only file.
            }
        }
    }

    /// <summary>Removes opened-entry folders older than <paramref name="maxAge"/>, skipping anything still in use.</summary>
    /// <param name="maxAge">How long a folder is kept after it was last used.</param>
    /// <param name="root">The folder to clean; <see langword="null"/> for <see cref="Root"/>.</param>
    public static void CleanUp(TimeSpan maxAge, string? root = null)
    {
        root ??= Root;
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < maxAge)
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in another program; try again next time.
            }
        }
    }
}
