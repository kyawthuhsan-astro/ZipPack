namespace ZipPackNet.Cli;

/// <summary>What the application was started to do.</summary>
internal enum LaunchAction
{
    /// <summary>Show the main window, optionally with files to queue or an archive to open.</summary>
    Open,

    /// <summary>Show the compression options for the given paths, compress, then exit.</summary>
    Compress,

    /// <summary>Show the extraction options for the given archives, extract, then exit.</summary>
    Extract,

    /// <summary>Add the Explorer context menu entries without showing any UI.</summary>
    RegisterShell,

    /// <summary>Remove the Explorer context menu entries without showing any UI.</summary>
    UnregisterShell,

    /// <summary>Show the command line usage.</summary>
    Help,
}

/// <summary>The parsed command line.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Paths">The absolute paths the action applies to.</param>
/// <param name="Error">A description of what is wrong with the command line, or <see langword="null"/>.</param>
internal sealed record LaunchRequest(LaunchAction Action, IReadOnlyList<string> Paths, string? Error = null);

/// <summary>Turns the process arguments into a <see cref="LaunchRequest"/>.</summary>
internal static class CommandLine
{
    public const string CompressSwitch = "--compress";
    public const string ExtractSwitch = "--extract";
    public const string RegisterSwitch = "--register-shell";
    public const string UnregisterSwitch = "--unregister-shell";

    public const string Usage =
        """
        ZipPackNet [path ...]
            Opens the main window. A single archive is opened; anything else is queued for compression.

        ZipPackNet --compress <path> [path ...]
            Asks for the archive options, compresses the files and folders, then exits.

        ZipPackNet --extract <archive> [archive ...]
            Asks for the destination, extracts the archives, then exits.

        ZipPackNet --register-shell | --unregister-shell
            Adds or removes the Explorer right-click menu entries and the .7z / .zst file
            associations for the current user (the installer does this automatically).
        """;

    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new LaunchRequest(LaunchAction.Open, []);
        }

        string first = args[0];
        LaunchAction? action = first.ToLowerInvariant() switch
        {
            CompressSwitch or "-c" => LaunchAction.Compress,
            ExtractSwitch or "-x" => LaunchAction.Extract,
            RegisterSwitch => LaunchAction.RegisterShell,
            UnregisterSwitch => LaunchAction.UnregisterShell,
            "--help" or "-h" or "-?" or "/?" => LaunchAction.Help,
            _ => null,
        };

        if (action is null && first.StartsWith("--", StringComparison.Ordinal))
        {
            return new LaunchRequest(LaunchAction.Help, [], $"Unknown option '{first}'.");
        }

        IReadOnlyList<string> paths = [.. args.Skip(action is null ? 0 : 1).Where(arg => arg.Length > 0).Select(ToFullPath)];

        return action switch
        {
            null => new LaunchRequest(LaunchAction.Open, paths),
            LaunchAction.Compress or LaunchAction.Extract when paths.Count == 0 =>
                new LaunchRequest(LaunchAction.Help, [], $"'{first}' needs at least one path."),
            _ => new LaunchRequest(action.Value, paths),
        };
    }

    private static string ToFullPath(string path)
    {
        try
        {
            // Explorer passes "C:\" for a drive root as C:" when the quote is escaped by the trailing backslash.
            return Path.GetFullPath(path.Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
