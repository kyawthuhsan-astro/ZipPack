namespace ZipPack;

/// <summary>Describes an archive to be created.</summary>
public sealed record ArchiveCreateRequest
{
    /// <summary>
    /// Gets the files and directories to pack. A file is stored under its name; a directory is stored
    /// recursively under its name (the same convention as <c>7z a archive file dir</c>).
    /// </summary>
    public required IReadOnlyList<string> SourcePaths { get; init; }

    /// <summary>Gets the path of the archive file to create.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Gets the compression options.</summary>
    public CompressionOptions Options { get; init; } = CompressionOptions.Default;

    /// <summary>
    /// Gets the <see cref="ICompressionStrategy.Id"/> of the archiver to use. When <see langword="null"/>,
    /// <see cref="ICompressionService"/> chooses by the extension of <see cref="DestinationPath"/>.
    /// </summary>
    public string? FormatId { get; init; }

    /// <summary>Gets a value indicating whether an existing destination file may be replaced.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Describes an archive to be extracted.</summary>
public sealed record ArchiveExtractRequest
{
    /// <summary>Gets the path of the archive to unpack.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Gets the directory to extract into. It is created when missing.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>Gets the password, when the archive is protected.</summary>
    public string? Password { get; init; }

    /// <summary>Gets a value indicating whether existing files are replaced (<see langword="true"/>) or kept.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Gets the entries to extract, as paths returned by <see cref="ICompressionService.ListEntriesAsync"/>
    /// (<c>/</c> separators, compared case-insensitively). A folder includes everything below it. Entries keep their
    /// path below <see cref="DestinationDirectory"/>. <see langword="null"/> or empty extracts everything.
    /// </summary>
    /// <remarks>An archiver throws <see cref="CompressionException"/> when none of the paths exists.</remarks>
    public IReadOnlyList<string>? EntryPaths { get; init; }

    /// <summary>Returns <see langword="true"/> when <paramref name="entryPath"/> is selected by <see cref="EntryPaths"/>.</summary>
    /// <param name="entryPath">An entry path; <c>\</c> separators are accepted too.</param>
    public bool Includes(string entryPath)
    {
        ArgumentNullException.ThrowIfNull(entryPath);
        if (EntryPaths is not { Count: > 0 } selected)
        {
            return true;
        }

        string path = entryPath.Replace('\\', '/').Trim('/');
        foreach (string candidate in selected)
        {
            string wanted = candidate.Replace('\\', '/').Trim('/');
            if (path.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
