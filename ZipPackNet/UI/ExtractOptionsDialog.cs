using System.ComponentModel;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>The user's choices for an extraction.</summary>
/// <param name="Destination">The folder to extract into.</param>
/// <param name="Overwrite"><see langword="true"/> to replace existing files.</param>
/// <param name="OpenWhenDone"><see langword="true"/> to show the folder in Explorer afterwards.</param>
internal sealed record ExtractChoices(string Destination, bool Overwrite, bool OpenWhenDone);

/// <summary>Asks where to extract to. Passwords are asked for later, and only if an archive needs one.</summary>
[DesignerCategory("Code")]
internal sealed class ExtractOptionsDialog : DialogBase
{
    private readonly TextBox _destination;
    private readonly CheckBox _overwrite;
    private readonly CheckBox _openWhenDone;
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };

    private ExtractOptionsDialog(AppSettings settings, IReadOnlyList<string> archives) : base("Extract")
    {
        bool several = archives.Count > 1;

        var summary = new Label
        {
            Text = several ? $"Extracting {archives.Count} archives" : $"Extracting: {Path.GetFileName(archives[0])}",
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 10),
        };

        _destination = new TextBox
        {
            Width = 380,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 3, 0, 3),
            Text = SuggestDestination(archives),
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.FileSystemDirectories,
        };

        Button browse = CreateButton("&Browse…");
        browse.Click += (_, _) => Browse();

        _overwrite = new CheckBox { Text = "&Replace existing files", AutoSize = true, Checked = settings.OverwriteOnExtract, Margin = new Padding(0, 3, 0, 3) };
        _openWhenDone = new CheckBox { Text = "&Open the folder when done", AutoSize = true, Checked = settings.OpenFolderAfterExtract, Margin = new Padding(0, 3, 0, 3) };

        TableLayoutPanel grid = CreateGrid(columns: 3);
        grid.Controls.Add(CreateLabel("Extract &to:"), 0, 0);
        grid.Controls.Add(_destination, 1, 0);
        grid.Controls.Add(browse, 2, 0);
        if (several)
        {
            Label hint = CreateHint("Each archive is extracted into its own sub-folder.");
            grid.Controls.Add(hint, 1, 1);
            grid.SetColumnSpan(hint, 2);
        }

        grid.Controls.Add(_overwrite, 1, 2);
        grid.Controls.Add(_openWhenDone, 1, 3);

        FlowLayoutPanel buttons = CreateButtonRow("Extract", out Button ok);
        ok.DialogResult = DialogResult.None;
        ok.Click += (_, _) => Accept();

        TableLayoutPanel layout = CreateGrid(columns: 1);
        layout.Controls.Add(summary);
        layout.Controls.Add(grid);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    /// <summary>Shows the dialog and returns the choices, or <see langword="null"/> when cancelled.</summary>
    public static ExtractChoices? Prompt(IWin32Window? owner, AppSettings settings, IReadOnlyList<string> archives)
    {
        using var dialog = new ExtractOptionsDialog(settings, archives);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        settings.OverwriteOnExtract = dialog._overwrite.Checked;
        settings.OpenFolderAfterExtract = dialog._openWhenDone.Checked;
        settings.Save();
        return new ExtractChoices(dialog._destination.Text.Trim(), dialog._overwrite.Checked, dialog._openWhenDone.Checked);
    }

    /// <summary>Gets the archive name without its (possibly compound, like ".tar.zst") extension.</summary>
    public static string ArchiveBaseName(string archivePath)
    {
        string name = Path.GetFileNameWithoutExtension(archivePath);
        return name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? name[..^4] : name;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _errors.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>One archive: a new folder named after it. Several: the folder they are in.</summary>
    private static string SuggestDestination(IReadOnlyList<string> archives)
    {
        string directory = Path.GetDirectoryName(archives[0]) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return archives.Count == 1 ? Path.Combine(directory, ArchiveBaseName(archives[0])) : directory;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder to extract to",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        // Start in the closest folder that already exists.
        string? start = _destination.Text;
        while (!string.IsNullOrEmpty(start) && !Directory.Exists(start))
        {
            start = Path.GetDirectoryName(start);
        }

        if (!string.IsNullOrEmpty(start))
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destination.Text = dialog.SelectedPath;
        }
    }

    private void Accept()
    {
        _errors.Clear();
        string destination = _destination.Text.Trim();
        if (destination.Length == 0 || destination.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(destination))
        {
            _errors.SetError(_destination, "Enter the full path of the destination folder.");
            _destination.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
    }
}
