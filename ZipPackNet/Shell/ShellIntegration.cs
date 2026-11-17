using System.Runtime.InteropServices;
using Microsoft.Win32;
using ZipPack;
using ZipPackNet.Cli;

namespace ZipPackNet.Shell;

/// <summary>Whether the Explorer context menu entries are present.</summary>
internal enum ShellRegistrationState
{
    /// <summary>No entry exists.</summary>
    NotRegistered,

    /// <summary>The entries exist and start this executable.</summary>
    Registered,

    /// <summary>Entries exist but point to another copy of the application (it was moved or updated).</summary>
    RegisteredElsewhere,
}

/// <summary>
/// Adds and removes the "Compress with ZipPack" / "Extract with ZipPack" entries of the Windows Explorer
/// context menu.
/// </summary>
/// <remarks>
/// <para>Everything is written below <c>HKEY_CURRENT_USER\Software\Classes</c>, so no administrator rights
/// are needed and only the current user is affected:</para>
/// <code>
/// *\shell\ZipPackNet.Compress                               every file
/// Directory\shell\ZipPackNet.Compress                       every folder
/// SystemFileAssociations\.7z\shell\ZipPackNet.Extract       every supported archive extension
/// </code>
/// <para><c>SystemFileAssociations</c> is used for the archive types because it applies to an extension no
/// matter which application currently owns it, and leaves that ownership untouched.</para>
/// <para>These are classic ("static verb") entries. On Windows 11 they appear under
/// <em>Show more options</em>; the compact Windows 11 menu only accepts packaged <c>IExplorerCommand</c>
/// handlers.</para>
/// <para><b>File associations</b> (double-click opens the archive in ZipPack) are separate and cover
/// <see cref="AssociatedExtensions"/>:</para>
/// <code>
/// ZipPackNet.Archive.7z                    ProgID: description, DefaultIcon, shell\open\command = "exe" "%1"
/// .7z                  (default)           = ZipPackNet.Archive.7z  (the previous owner is kept in ZipPackNet.PreviousProgId)
/// .7z\OpenWithProgids  ZipPackNet.Archive.7z  so ZipPack is listed under "Open with"
/// </code>
/// <para>A choice the user made with "Always use this app" (Windows' hash-protected <c>UserChoice</c>) still wins;
/// applications cannot and should not override it.</para>
/// <para>Every command is built from the path of the running executable (<see cref="Environment.ProcessPath"/>).
/// Velopack keeps that path (<c>%LocalAppData%\ZipPackNet\current\ZipPackNet.exe</c>) across updates, and the
/// update hook rewrites the entries anyway.</para>
/// </remarks>
internal sealed partial class ShellIntegration
{
    public const string CompressVerb = "ZipPackNet.Compress";
    public const string ExtractVerb = "ZipPackNet.Extract";
    public const string ProgIdPrefix = "ZipPackNet.Archive";

    /// <summary>The name of the value that remembers which ProgID owned an extension before ZipPack.</summary>
    internal const string PreviousProgIdValue = "ZipPackNet.PreviousProgId";

    private const string FileAssociations = "SystemFileAssociations";

    /// <summary>The extensions double-click opens in ZipPack, with their description in Explorer.</summary>
    private static readonly (string Extension, string Description)[] Associations =
    [
        (".7z", "7-Zip archive"),
        (".zst", "Zstandard archive"),
    ];
    private static readonly string[] CompressTargets = ["*", "Directory"];

    private readonly Func<RegistryKey> _openClassesRoot;
    private readonly string _executablePath;
    private readonly string[] _archiveExtensions;
    private readonly bool _notifyShell;

    /// <summary>Initializes a new instance of the <see cref="ShellIntegration"/> class.</summary>
    /// <param name="openClassesRoot">Opens the writable registry key that plays the role of <c>Software\Classes</c>.</param>
    /// <param name="executablePath">The executable the menu entries start.</param>
    /// <param name="archiveExtensions">The extensions (with leading dot) that get the extract entry.</param>
    /// <param name="notifyShell"><see langword="true"/> to tell Explorer to refresh its associations after a change.</param>
    public ShellIntegration(
        Func<RegistryKey> openClassesRoot,
        string executablePath,
        IEnumerable<string> archiveExtensions,
        bool notifyShell = true)
    {
        _openClassesRoot = openClassesRoot;
        _executablePath = executablePath;
        _notifyShell = notifyShell;

        // Explorer only knows the last extension of "name.tar.zst", so compound extensions are left out.
        _archiveExtensions =
        [
            .. archiveExtensions
                .Where(extension => extension.Length > 1 && extension.LastIndexOf('.') == 0)
                .Select(extension => extension.ToLowerInvariant())
                .Distinct(),
        ];
    }

    /// <summary>Gets the extensions that get the extract entry.</summary>
    public IReadOnlyList<string> ArchiveExtensions => _archiveExtensions;

    /// <summary>Gets the extensions that <see cref="RegisterFileAssociations"/> opens with ZipPack.</summary>
    public static IReadOnlyList<string> AssociatedExtensions { get; } = [.. Associations.Select(a => a.Extension)];

    /// <summary>Gets the ProgID for an associated extension, e.g. <c>ZipPackNet.Archive.7z</c>.</summary>
    public static string ProgIdFor(string extension) => ProgIdPrefix + extension.ToLowerInvariant();

    /// <summary>Creates the integration for the running executable, the current user and every extractable format.</summary>
    public static ShellIntegration ForCurrentUser(ICompressionService service)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The path of the running executable is unknown.");

        IEnumerable<string> extensions = service.GetStrategies<IArchiver>()
            .Where(archiver => archiver.Capabilities.HasFlag(ArchiverCapabilities.Extract))
            .SelectMany(archiver => archiver.FileExtensions);

        return new ShellIntegration(() => Registry.CurrentUser.CreateSubKey(@"Software\Classes"), executable, extensions);
    }

    /// <summary>Determines whether the menu entries exist and whether they start this executable.</summary>
    public ShellRegistrationState GetState()
    {
        using RegistryKey root = _openClassesRoot();
        using RegistryKey? command = root.OpenSubKey($@"*\shell\{CompressVerb}\command");
        if (command?.GetValue(null) is not string registered)
        {
            return ShellRegistrationState.NotRegistered;
        }

        return string.Equals(registered, BuildCommand(CommandLine.CompressSwitch), StringComparison.OrdinalIgnoreCase)
            ? ShellRegistrationState.Registered
            : ShellRegistrationState.RegisteredElsewhere;
    }

    /// <summary>Adds (or repairs) the menu entries.</summary>
    /// <exception cref="UnauthorizedAccessException">The registry is locked down by policy.</exception>
    public void Register()
    {
        using RegistryKey root = _openClassesRoot();

        // Start clean so extensions that are no longer supported do not keep a stale entry.
        RemoveEntries(root);

        foreach (string target in CompressTargets)
        {
            WriteVerb(root, $@"{target}\shell\{CompressVerb}", "Compress with ZipPack", CommandLine.CompressSwitch);
        }

        foreach (string extension in _archiveExtensions)
        {
            WriteVerb(root, $@"{FileAssociations}\{extension}\shell\{ExtractVerb}", "Extract with ZipPack", CommandLine.ExtractSwitch);
        }

        NotifyShell();
    }

    /// <summary>Removes every menu entry this application ever added.</summary>
    /// <exception cref="UnauthorizedAccessException">The registry is locked down by policy.</exception>
    public void Unregister()
    {
        using RegistryKey root = _openClassesRoot();
        RemoveEntries(root);
        NotifyShell();
    }

    /// <summary>Adds the context menu entries and the file associations (what the installer does).</summary>
    public void Install()
    {
        Register();
        RegisterFileAssociations();
    }

    /// <summary>Removes the context menu entries and the file associations (what the uninstaller does).</summary>
    public void Uninstall()
    {
        Unregister();
        UnregisterFileAssociations();
    }

    /// <summary>
    /// Determines whether double-clicking every <see cref="AssociatedExtensions"/> file opens this executable.
    /// </summary>
    public bool AreFileAssociationsRegistered()
    {
        using RegistryKey root = _openClassesRoot();
        foreach ((string extension, _) in Associations)
        {
            string progId = ProgIdFor(extension);
            using RegistryKey? owner = root.OpenSubKey(extension);
            using RegistryKey? command = root.OpenSubKey($@"{progId}\shell\open\command");
            if (owner?.GetValue(null) as string != progId
                || !string.Equals(command?.GetValue(null) as string, BuildOpenCommand(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Makes ZipPack open <see cref="AssociatedExtensions"/> on double-click, with its icon, and lists it under
    /// "Open with". The application that owned an extension before is remembered and restored by
    /// <see cref="UnregisterFileAssociations"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The registry is locked down by policy.</exception>
    public void RegisterFileAssociations()
    {
        using RegistryKey root = _openClassesRoot();
        foreach ((string extension, string description) in Associations)
        {
            string progId = ProgIdFor(extension);
            using (RegistryKey type = root.CreateSubKey(progId))
            {
                type.SetValue(null, description);
                type.SetValue("FriendlyTypeName", description);
                using (RegistryKey icon = type.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(null, $"\"{_executablePath}\",0");
                }

                using RegistryKey shell = type.CreateSubKey("shell");
                shell.SetValue(null, "open");
                using RegistryKey open = shell.CreateSubKey("open");
                open.SetValue(null, "&Open with ZipPack");
                open.SetValue("Icon", $"\"{_executablePath}\",0");
                using RegistryKey command = open.CreateSubKey("command");
                command.SetValue(null, BuildOpenCommand());
            }

            using RegistryKey owner = root.CreateSubKey(extension);
            if (owner.GetValue(null) is string previous && previous.Length > 0
                && !previous.Equals(progId, StringComparison.OrdinalIgnoreCase)
                && owner.GetValue(PreviousProgIdValue) is null)
            {
                owner.SetValue(PreviousProgIdValue, previous);
            }

            owner.SetValue(null, progId);
            using RegistryKey openWith = owner.CreateSubKey("OpenWithProgids");
            openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    /// <summary>
    /// Removes the file associations: the ProgIDs, the "Open with" entries and — only where ZipPack still owns
    /// the extension — the ownership itself, which goes back to the previous application.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The registry is locked down by policy.</exception>
    public void UnregisterFileAssociations()
    {
        using RegistryKey root = _openClassesRoot();
        foreach ((string extension, _) in Associations)
        {
            string progId = ProgIdFor(extension);
            root.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);

            using (RegistryKey? owner = root.OpenSubKey(extension, writable: true))
            {
                if (owner is null)
                {
                    continue;
                }

                string? previous = owner.GetValue(PreviousProgIdValue) as string;
                if (string.Equals(owner.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
                {
                    if (previous is { Length: > 0 })
                    {
                        owner.SetValue(null, previous);
                    }
                    else
                    {
                        owner.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }
                }

                owner.DeleteValue(PreviousProgIdValue, throwOnMissingValue: false);
                using (RegistryKey? openWith = owner.OpenSubKey("OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(progId, throwOnMissingValue: false);
                }

                DeleteIfEmpty(owner, "OpenWithProgids");
            }

            DeleteIfEmpty(root, extension);
        }

        NotifyShell();
    }

    private static void RemoveEntries(RegistryKey root)
    {
        foreach (string target in CompressTargets)
        {
            root.DeleteSubKeyTree($@"{target}\shell\{CompressVerb}", throwOnMissingSubKey: false);
        }

        using RegistryKey? associations = root.OpenSubKey(FileAssociations, writable: true);
        if (associations is null)
        {
            return;
        }

        // Scan instead of using the current extension list: an older version may have registered others.
        foreach (string extension in associations.GetSubKeyNames())
        {
            using (RegistryKey? shell = associations.OpenSubKey($@"{extension}\shell", writable: true))
            {
                if (shell is null || !shell.GetSubKeyNames().Contains(ExtractVerb, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                shell.DeleteSubKeyTree(ExtractVerb, throwOnMissingSubKey: false);
            }

            // Remove the containers we created, but never anything another application put there.
            DeleteIfEmpty(associations, $@"{extension}\shell");
            DeleteIfEmpty(associations, extension);
        }
    }

    private static void DeleteIfEmpty(RegistryKey parent, string subKey)
    {
        using (RegistryKey? key = parent.OpenSubKey(subKey))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }

        parent.DeleteSubKey(subKey, throwOnMissingSubKey: false);
    }

    private void WriteVerb(RegistryKey root, string path, string caption, string commandSwitch)
    {
        using RegistryKey verb = root.CreateSubKey(path);
        verb.SetValue(null, caption);
        verb.SetValue("Icon", $"\"{_executablePath}\",0");

        // Without this Explorer refuses the verb for selections of more than 15 items.
        verb.SetValue("MultiSelectModel", "Player");

        using RegistryKey command = verb.CreateSubKey("command");
        command.SetValue(null, BuildCommand(commandSwitch));
    }

    private string BuildCommand(string commandSwitch) => $"\"{_executablePath}\" {commandSwitch} \"%1\"";

    /// <summary>Double-click: a bare path, which <see cref="CommandLine"/> routes to the main window.</summary>
    private string BuildOpenCommand() => $"\"{_executablePath}\" \"%1\"";

    private void NotifyShell()
    {
        if (_notifyShell)
        {
            const int AssociationChanged = 0x08000000; // SHCNE_ASSOCCHANGED
            SHChangeNotify(AssociationChanged, 0, 0, 0);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
