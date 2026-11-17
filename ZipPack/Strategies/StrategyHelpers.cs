namespace ZipPack.Strategies;

/// <summary>Checks shared by the built-in archivers.</summary>
internal static class StrategyHelpers
{
    /// <summary>Treats an empty password the same as no password.</summary>
    public static string? NormalizePassword(string? password) => string.IsNullOrEmpty(password) ? null : password;

    /// <summary>Validates a create request against the capabilities of the archiver that is about to run it.</summary>
    /// <exception cref="UnsupportedFormatException">The archiver cannot fulfil the request.</exception>
    /// <exception cref="IOException">The destination exists and may not be overwritten.</exception>
    public static void ValidateCreateRequest(IArchiver archiver, ArchiveCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);

        if (!archiver.Capabilities.HasFlag(ArchiverCapabilities.Create))
        {
            throw new UnsupportedFormatException($"{archiver.DisplayName} archives can only be extracted, not created.");
        }

        if (request.SourcePaths.Count == 0)
        {
            throw new ArgumentException("At least one source path is required.", nameof(request));
        }

        if (NormalizePassword(request.Options.Password) is not null && !archiver.Capabilities.HasFlag(ArchiverCapabilities.Password))
        {
            throw new UnsupportedFormatException($"The {archiver.DisplayName} format does not support password protection.");
        }

        if (!request.Overwrite && File.Exists(request.DestinationPath))
        {
            throw new IOException($"The file '{request.DestinationPath}' already exists.");
        }
    }

    /// <summary>Validates an extract request.</summary>
    public static void ValidateExtractRequest(ArchiveExtractRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.ArchivePath);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationDirectory);
    }
}
