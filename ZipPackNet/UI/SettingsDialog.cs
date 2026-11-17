using System.ComponentModel;
using System.Security;
using ZipPack;
using ZipPackNet.Services;
using ZipPackNet.Shell;

namespace ZipPackNet.UI;

/// <summary>Application settings: Explorer integration and the defaults for extraction.</summary>
[DesignerCategory("Code")]
internal sealed class SettingsDialog : DialogBase
{
    private readonly ShellIntegration _shell;
    private readonly AppSettings _settings;
    private readonly Label _shellStatus;
    private readonly Button _shellToggle;
    private readonly CheckBox _overwrite;
    private readonly CheckBox _openWhenDone;
    private readonly CheckBox _checkForUpdates;
    private readonly CheckBox _associate;
    private readonly bool _associatedAtStart;

    private SettingsDialog(ICompressionService service, AppSettings settings, string version) : base("Settings")
    {
        _settings = settings;
        _shell = ShellIntegration.ForCurrentUser(service);

        // Explorer integration -----------------------------------------------------------------------
        _shellStatus = new Label { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 8) };
        _shellToggle = CreateButton(string.Empty);
        _shellToggle.Margin = new Padding(0, 0, 0, 8);
        _shellToggle.Click += (_, _) => ToggleShellIntegration();

        Label shellHint = CreateHint(
            "Adds “Compress with ZipPack” to files and folders and “Extract with ZipPack” to archives " +
            $"({string.Join(" ", _shell.ArchiveExtensions.Take(8))} …) for your user account only. " +
            "No administrator rights are needed. On Windows 11 the entries are under “Show more options”.",
            maxWidth: 440);
        shellHint.Margin = Padding.Empty;

        _associate = new CheckBox
        {
            Text = $"Open {string.Join(" and ", ShellIntegration.AssociatedExtensions)} files with ZipPack (&double-click)",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        try
        {
            _associate.Checked = _associatedAtStart = _shell.AreFileAssociationsRegistered();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _associate.Enabled = false;
        }

        TableLayoutPanel shellGrid = CreateGrid(columns: 1);
        shellGrid.Controls.Add(_shellStatus);
        shellGrid.Controls.Add(_shellToggle);
        shellGrid.Controls.Add(shellHint);
        shellGrid.Controls.Add(_associate);

        // Extraction ---------------------------------------------------------------------------------
        _overwrite = new CheckBox { Text = "&Replace existing files by default", AutoSize = true, Checked = settings.OverwriteOnExtract };
        _openWhenDone = new CheckBox { Text = "&Open the destination folder when done", AutoSize = true, Checked = settings.OpenFolderAfterExtract };

        TableLayoutPanel extractGrid = CreateGrid(columns: 1);
        extractGrid.Controls.Add(_overwrite);
        extractGrid.Controls.Add(_openWhenDone);

        // Updates ------------------------------------------------------------------------------------
        _checkForUpdates = new CheckBox { Text = "Check for &updates when ZipPack starts", AutoSize = true, Checked = settings.CheckForUpdatesAtStartup };
        Label updatesHint = CreateHint(
            $"ZipPack {version}. A new version is never installed without asking; click the version in the status bar to check now.",
            maxWidth: 440);
        updatesHint.Margin = new Padding(3, 2, 3, 0);

        TableLayoutPanel updatesGrid = CreateGrid(columns: 1);
        updatesGrid.Controls.Add(_checkForUpdates);
        updatesGrid.Controls.Add(updatesHint);

        FlowLayoutPanel buttons = CreateButtonRow("OK", out _);

        TableLayoutPanel layout = CreateGrid(columns: 1);
        layout.Controls.Add(CreateGroup("Windows Explorer right-click menu", shellGrid));
        layout.Controls.Add(CreateGroup("Extraction", extractGrid));
        layout.Controls.Add(CreateGroup("Updates", updatesGrid));
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        RefreshShellState();
    }

    /// <summary>Shows the dialog and saves the settings when it is confirmed.</summary>
    public static void ShowSettings(IWin32Window? owner, ICompressionService service, AppSettings settings, string version)
    {
        using var dialog = new SettingsDialog(service, settings, version);
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            settings.OverwriteOnExtract = dialog._overwrite.Checked;
            settings.OpenFolderAfterExtract = dialog._openWhenDone.Checked;
            settings.CheckForUpdatesAtStartup = dialog._checkForUpdates.Checked;
            dialog.ApplyFileAssociations();
            settings.Save();
        }
    }

    private static GroupBox CreateGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        group.Controls.Add(content);
        return group;
    }

    private void RefreshShellState()
    {
        ShellRegistrationState state;
        try
        {
            state = _shell.GetState();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _shellStatus.Text = "The registry could not be read: " + ex.Message;
            _shellToggle.Enabled = false;
            return;
        }

        (_shellStatus.Text, _shellStatus.ForeColor, _shellToggle.Text) = state switch
        {
            ShellRegistrationState.Registered =>
                ("✔  The menu entries are installed.", Color.SeaGreen, "&Remove from Explorer menu"),
            ShellRegistrationState.RegisteredElsewhere =>
                ("⚠  The menu entries point to another copy of ZipPack (it was moved or updated).", Color.DarkOrange, "&Repair Explorer menu"),
            _ =>
                ("The menu entries are not installed.", SystemColors.ControlText, "&Add to Explorer menu"),
        };
    }

    private void ApplyFileAssociations()
    {
        if (!_associate.Enabled || _associate.Checked == _associatedAtStart)
        {
            return;
        }

        try
        {
            if (_associate.Checked)
            {
                _shell.RegisterFileAssociations();
            }
            else
            {
                _shell.UnregisterFileAssociations();
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(
                Owner,
                "The file associations could not be changed. The registry may be locked by a policy of your organisation.\n\n" + ex.Message,
                "File associations",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ToggleShellIntegration()
    {
        try
        {
            if (_shell.GetState() == ShellRegistrationState.Registered)
            {
                _shell.Unregister();
            }
            else
            {
                _shell.Register();
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            MessageBox.Show(
                this,
                "The Explorer menu could not be changed. The registry may be locked by a policy of your organisation.\n\n" + ex.Message,
                "Explorer integration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        RefreshShellState();
    }
}
