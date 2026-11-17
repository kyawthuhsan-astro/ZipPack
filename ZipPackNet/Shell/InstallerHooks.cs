using System.Security;
using ZipPack;

namespace ZipPackNet.Shell;

/// <summary>
/// What ZipPack does when Velopack installs, updates or uninstalls it: keep the Explorer menu and the
/// <c>.7z</c> / <c>.zst</c> file associations in step with the installed copy.
/// </summary>
/// <remarks>
/// <para>Velopack runs these "fast callbacks" in a short-lived ZipPackNet process started with its own
/// arguments, before any window exists, and kills it after 15–30 seconds. The work here is a few registry
/// writes under <c>HKCU\Software\Classes</c> — no UI, no network, no administrator rights.</para>
/// <para>A failure (registry locked down by policy) must never break an install, update or uninstall, so it is
/// written to the Velopack log and otherwise ignored; the user can still use Settings or the command line.</para>
/// </remarks>
internal static class InstallerHooks
{
    /// <summary>After the first install: add the menu entries and the file associations.</summary>
    public static void AfterInstall() => Run(nameof(AfterInstall), shell => shell.Install());

    /// <summary>
    /// After an update: rewrite whatever is registered, so new archive extensions are added and commands point
    /// at the new executable. Entries the user removed in Settings stay removed.
    /// </summary>
    public static void AfterUpdate() => Run(nameof(AfterUpdate), shell =>
    {
        if (shell.GetState() != ShellRegistrationState.NotRegistered)
        {
            shell.Register();
        }

        if (shell.AreFileAssociationsRegistered())
        {
            shell.RegisterFileAssociations();
        }
    });

    /// <summary>Before uninstalling: remove everything and give <c>.7z</c> / <c>.zst</c> back to their previous owner.</summary>
    public static void BeforeUninstall() => Run(nameof(BeforeUninstall), shell => shell.Uninstall());

    private static void Run(string hook, Action<ShellIntegration> action)
    {
        try
        {
            action(ShellIntegration.ForCurrentUser(CompressionService.CreateDefault()));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            // Velopack captures standard error in its log (%LocalAppData%\ZipPackNet\velopack.log).
            Console.Error.WriteLine($"ZipPack {hook}: the Explorer integration could not be updated: {ex.Message}");
        }
    }
}
