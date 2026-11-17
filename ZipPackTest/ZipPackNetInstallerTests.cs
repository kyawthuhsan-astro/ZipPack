using System.Reflection;
using Microsoft.Win32;
using ZipPackNet.Shell;

namespace ZipPackTest;

/// <summary>Tests for the file associations the installer hooks write, and the version metadata.</summary>
public sealed class ZipPackNetInstallerTests : IDisposable
{
    private const string Exe = @"C:\Users\me\AppData\Local\ZipPackNet\current\ZipPackNet.exe";

    // A private key stands in for HKCU\Software\Classes, so the test never touches the real associations.
    private readonly string _sandbox = $@"Software\ZipPackNet.Tests\{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);

    private RegistryKey OpenRoot() => Registry.CurrentUser.CreateSubKey(_sandbox);

    private ShellIntegration CreateShell(string executable = Exe) => new(OpenRoot, executable, [".7z", ".zst", ".zip"], notifyShell: false);

    [Fact]
    public void FileAssociations_OpenArchivesWithZipPack_AndRestoreThePreviousOwner()
    {
        using (RegistryKey root = OpenRoot())
        using (RegistryKey sevenZip = root.CreateSubKey(".7z"))
        {
            // 7-Zip owned .7z before, and another program is listed under "Open with".
            sevenZip.SetValue(null, "7-Zip.7z");
            using RegistryKey openWith = sevenZip.CreateSubKey("OpenWithProgids");
            openWith.SetValue("OtherApp.7z", Array.Empty<byte>(), RegistryValueKind.None);
        }

        ShellIntegration shell = CreateShell();
        Assert.False(shell.AreFileAssociationsRegistered());
        Assert.Equal([".7z", ".zst"], ShellIntegration.AssociatedExtensions);

        shell.RegisterFileAssociations();
        shell.RegisterFileAssociations(); // Re-running (an update) must not lose the remembered owner.

        Assert.True(shell.AreFileAssociationsRegistered());
        using (RegistryKey root = OpenRoot())
        {
            foreach (string extension in new[] { ".7z", ".zst" })
            {
                string progId = "ZipPackNet.Archive" + extension;
                Assert.Equal(progId, ShellIntegration.ProgIdFor(extension));
                Assert.Equal(progId, root.OpenSubKey(extension)!.GetValue(null));
                Assert.Contains(progId, root.OpenSubKey($@"{extension}\OpenWithProgids")!.GetValueNames());
                Assert.Equal($"\"{Exe}\",0", root.OpenSubKey($@"{progId}\DefaultIcon")!.GetValue(null));
                Assert.Equal("open", root.OpenSubKey($@"{progId}\shell")!.GetValue(null));
                Assert.Equal($"\"{Exe}\" \"%1\"", root.OpenSubKey($@"{progId}\shell\open\command")!.GetValue(null));
            }

            Assert.Equal("7-Zip.7z", root.OpenSubKey(".7z")!.GetValue(ShellIntegration.PreviousProgIdValue));
            Assert.Null(root.OpenSubKey(".zst")!.GetValue(ShellIntegration.PreviousProgIdValue));
        }

        // A copy at another path (e.g. moved by hand) sees the associations as not its own.
        Assert.False(CreateShell(@"D:\Other\ZipPackNet.exe").AreFileAssociationsRegistered());

        shell.UnregisterFileAssociations();

        Assert.False(shell.AreFileAssociationsRegistered());
        using (RegistryKey root = OpenRoot())
        {
            using RegistryKey sevenZip = root.OpenSubKey(".7z")!;
            Assert.Equal("7-Zip.7z", sevenZip.GetValue(null));
            Assert.Null(sevenZip.GetValue(ShellIntegration.PreviousProgIdValue));
            Assert.Equal(["OtherApp.7z"], root.OpenSubKey(@".7z\OpenWithProgids")!.GetValueNames());

            // .zst had no owner, so nothing of ours is left behind.
            Assert.Null(root.OpenSubKey(".zst"));
            Assert.Null(root.OpenSubKey("ZipPackNet.Archive.7z"));
            Assert.Null(root.OpenSubKey("ZipPackNet.Archive.zst"));
        }
    }

    [Fact]
    public void FileAssociations_Uninstall_LeavesAnExtensionAnotherAppTookOver()
    {
        ShellIntegration shell = CreateShell();
        shell.RegisterFileAssociations();

        using (RegistryKey root = OpenRoot())
        using (RegistryKey zst = root.OpenSubKey(".zst", writable: true)!)
        {
            zst.SetValue(null, "NewerApp.zst");
        }

        shell.UnregisterFileAssociations();

        using RegistryKey after = OpenRoot();
        Assert.Equal("NewerApp.zst", after.OpenSubKey(".zst")!.GetValue(null));
    }

    [Fact]
    public void InstallAndUninstall_CoverTheMenuAndTheAssociations()
    {
        ShellIntegration shell = CreateShell();

        shell.Install();
        Assert.Equal(ShellRegistrationState.Registered, shell.GetState());
        Assert.True(shell.AreFileAssociationsRegistered());
        using (RegistryKey root = OpenRoot())
        {
            Assert.Equal(
                $"\"{Exe}\" --extract \"%1\"",
                root.OpenSubKey(@"SystemFileAssociations\.7z\shell\ZipPackNet.Extract\command")!.GetValue(null));
        }

        shell.Uninstall();
        Assert.Equal(ShellRegistrationState.NotRegistered, shell.GetState());
        Assert.False(shell.AreFileAssociationsRegistered());
        using RegistryKey empty = OpenRoot();
        Assert.DoesNotContain(empty.GetSubKeyNames(), name => name is not ("*" or "Directory" or "SystemFileAssociations"));
    }

    [Fact]
    public void ApplicationVersion_ComesFromTheProjectFile()
    {
        Assembly app = typeof(ShellIntegration).Assembly;

        Assert.Equal(new Version(1, 0, 0, 0), app.GetName().Version);
        Assert.Equal("1.0.0.0", app.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);

        // What Application.ProductVersion and the status bar show: no "+commit" suffix.
        Assert.Equal("1.0.0", app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
}
