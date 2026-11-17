using System.ComponentModel;
using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>What the user chose in <see cref="CompressOptionsDialog"/>.</summary>
/// <param name="Request">The archive to create.</param>
/// <param name="DeleteSources">Whether the sources go to the Recycle Bin once the archive is verified.</param>
internal sealed record CompressChoices(ArchiveCreateRequest Request, bool DeleteSources);

/// <summary>
/// Collects everything needed to create an archive: where to write it, in which format, how hard to
/// compress and whether to protect it with a password.
/// </summary>
[DesignerCategory("Code")]
internal sealed class CompressOptionsDialog : DialogBase
{
    private static readonly (CompressionPreset Preset, string Caption)[] Levels =
    [
        (CompressionPreset.Store, "Store (no compression)"),
        (CompressionPreset.Fastest, "Fastest"),
        (CompressionPreset.Fast, "Fast"),
        (CompressionPreset.Normal, "Normal"),
        (CompressionPreset.High, "High"),
        (CompressionPreset.Ultra, "Ultra (slowest)"),
    ];

    private readonly bool _singleFile;
    private readonly TextBox _destination;
    private readonly ComboBox _format;
    private readonly ComboBox _level;
    private readonly TextBox _password;
    private readonly TextBox _confirm;
    private readonly CheckBox _showPassword;
    private readonly CheckBox _encryptNames;
    private readonly CheckBox _deleteSources;
    private readonly Label _formatHint;
    private readonly Label _passwordHint;
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private string _currentExtension = string.Empty;

    private CompressOptionsDialog(ICompressionService service, AppSettings settings, IReadOnlyList<string> sources)
        : base("Create archive")
    {
        _singleFile = sources.Count == 1 && File.Exists(sources[0]);

        // Single-stream formats (gzip, bzip2, xz) hold exactly one file, so they are only offered for one.
        FormatChoice[] formats =
        [
            .. service.GetStrategies<IArchiver>()
                .Where(a => a.Capabilities.HasFlag(ArchiverCapabilities.Create))
                .Where(a => _singleFile || a.Capabilities.HasFlag(ArchiverCapabilities.MultipleEntries))
                .Select(a => new FormatChoice(a)),
        ];

        _destination = new TextBox { Width = 380, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
        Button browse = CreateButton("&Browse…");
        browse.Click += (_, _) => BrowseDestination();

        _format = CreateDropDown();
        _format.Items.AddRange(formats);
        _level = CreateDropDown();
        _level.Items.AddRange([.. Levels.Select(level => level.Caption)]);
        _formatHint = CreateHint(string.Empty);

        _password = new TextBox { UseSystemPasswordChar = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
        _confirm = new TextBox { UseSystemPasswordChar = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
        _showPassword = new CheckBox { Text = "&Show password", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _encryptNames = new CheckBox { Text = "Encrypt file &names", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _passwordHint = CreateHint(string.Empty);
        _passwordHint.Margin = new Padding(0, 2, 0, 0);

        _showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = _confirm.UseSystemPasswordChar = !_showPassword.Checked;
            _confirm.Enabled = !_showPassword.Checked;
        };
        _password.TextChanged += (_, _) => UpdatePasswordState();
        _format.SelectedIndexChanged += (_, _) => OnFormatChanged();

        // Archive ------------------------------------------------------------------------------------
        TableLayoutPanel archiveGrid = CreateGrid(columns: 3);
        archiveGrid.Controls.Add(CreateLabel("&Archive:"), 0, 0);
        archiveGrid.Controls.Add(_destination, 1, 0);
        archiveGrid.Controls.Add(browse, 2, 0);
        archiveGrid.Controls.Add(CreateLabel("&Format:"), 0, 1);
        archiveGrid.Controls.Add(_format, 1, 1);
        archiveGrid.Controls.Add(CreateLabel("Compression &level:"), 0, 2);
        archiveGrid.Controls.Add(_level, 1, 2);
        archiveGrid.Controls.Add(_formatHint, 1, 3);
        archiveGrid.SetColumnSpan(_formatHint, 2);

        // Deliberately never remembered: deleting the originals must be chosen each time.
        _deleteSources = new CheckBox { Text = "&Delete files after compression", AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        Label deleteHint = CreateHint(
            "The archive is tested first; only if it is intact are the original files and folders moved to the Recycle Bin.");
        deleteHint.Margin = new Padding(3, 2, 3, 4);
        archiveGrid.Controls.Add(_deleteSources, 1, 4);
        archiveGrid.SetColumnSpan(_deleteSources, 2);
        archiveGrid.Controls.Add(deleteHint, 1, 5);
        archiveGrid.SetColumnSpan(deleteHint, 2);

        // Protection ---------------------------------------------------------------------------------
        TableLayoutPanel passwordGrid = CreateGrid(columns: 2);
        passwordGrid.Controls.Add(CreateLabel("&Password:"), 0, 0);
        passwordGrid.Controls.Add(_password, 1, 0);
        passwordGrid.Controls.Add(CreateLabel("&Confirm:"), 0, 1);
        passwordGrid.Controls.Add(_confirm, 1, 1);
        passwordGrid.Controls.Add(_showPassword, 1, 2);
        passwordGrid.Controls.Add(_encryptNames, 1, 3);
        passwordGrid.Controls.Add(_passwordHint, 1, 4);

        FlowLayoutPanel buttons = CreateButtonRow("Compress", out Button ok);
        ok.DialogResult = DialogResult.None; // Validate before closing.
        ok.Click += (_, _) => Accept();

        TableLayoutPanel layout = CreateGrid(columns: 1);
        layout.Controls.Add(CreateSummary(sources));
        layout.Controls.Add(CreateGroup("Archive", archiveGrid));
        layout.Controls.Add(CreateGroup("Protection (optional)", passwordGrid));
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        // Initial state ------------------------------------------------------------------------------
        _destination.Text = SuggestDestination(sources);
        _destination.TextChanged += (_, _) => ShowEndOfDestination();
        _level.SelectedIndex = Math.Max(0, Array.FindIndex(Levels, level => level.Preset == settings.DefaultPreset));
        int preferred = Array.FindIndex(formats, f => string.Equals(f.Archiver.Id, settings.DefaultFormatId, StringComparison.OrdinalIgnoreCase));
        _format.SelectedIndex = Math.Max(0, preferred);
    }

    /// <summary>Gets the user's choices as a request ready to hand to <see cref="ICompressionService"/>.</summary>
    public ArchiveCreateRequest? Request { get; private set; }

    /// <summary>Shows the dialog and returns the choices, or <see langword="null"/> when cancelled.</summary>
    public static CompressChoices? Prompt(
        IWin32Window? owner,
        ICompressionService service,
        AppSettings settings,
        IReadOnlyList<string> sources)
    {
        using var dialog = new CompressOptionsDialog(service, settings, sources);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Request is null)
        {
            return null;
        }

        // Remember the choices for next time.
        settings.DefaultFormatId = dialog.Request.FormatId ?? settings.DefaultFormatId;
        settings.DefaultPreset = dialog.Request.Options.Preset;
        settings.Save();
        return new CompressChoices(dialog.Request with { SourcePaths = sources }, dialog._deleteSources.Checked);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _errors.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ComboBox CreateDropDown() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 3, 0, 3),
    };

    private static GroupBox CreateGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 6, 12, 10),
            Margin = new Padding(0, 0, 0, 10),
        };
        group.Controls.Add(content);
        return group;
    }

    private static Label CreateSummary(IReadOnlyList<string> sources)
    {
        string text = sources.Count == 1
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sources[0]))
            : $"{sources.Count} items";
        return new Label
        {
            Text = $"Compressing: {text}",
            AutoSize = true,
            AutoEllipsis = true,
            MaximumSize = new Size(520, 0),
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
        };
    }

    /// <summary>Proposes "name.ext" next to the source, named after the source or its parent folder.</summary>
    private static string SuggestDestination(IReadOnlyList<string> sources)
    {
        string first = Path.TrimEndingDirectorySeparator(sources[0]);
        string? directory = Path.GetDirectoryName(first);
        if (directory is null)
        {
            // A drive root was selected.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Archive");
        }

        string name = sources.Count == 1
            ? (File.Exists(first) ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(first))
            : new DirectoryInfo(directory).Name.TrimEnd(Path.DirectorySeparatorChar, ':');

        return Path.Combine(directory, name.Length > 0 ? name : "Archive");
    }

    /// <summary>
    /// Gets the extension a new archive of the given format should get. Zstandard is the one format with two
    /// shapes: a lone file becomes ".zst", anything else is bundled as ".tar.zst".
    /// </summary>
    private string ExtensionFor(IArchiver archiver) =>
        _singleFile && archiver.FileExtensions.Contains(".zst") ? ".zst" : archiver.FileExtensions[0];

    private void OnFormatChanged()
    {
        if (_format.SelectedItem is not FormatChoice choice)
        {
            return;
        }

        // Swap the extension in the destination, keeping whatever name the user typed.
        string path = _destination.Text;
        if (_currentExtension.Length > 0 && path.EndsWith(_currentExtension, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^_currentExtension.Length];
        }

        _currentExtension = ExtensionFor(choice.Archiver);
        _destination.Text = path + _currentExtension;

        ArchiverCapabilities capabilities = choice.Archiver.Capabilities;
        bool supportsPassword = capabilities.HasFlag(ArchiverCapabilities.Password);
        _password.Enabled = _showPassword.Enabled = supportsPassword;
        if (!supportsPassword)
        {
            _password.Clear();
            _confirm.Clear();
        }

        _formatHint.Text = choice.Archiver.Id switch
        {
            "7z" => "Best compression ratio and strong AES-256 encryption.",
            "zip" => "Opens everywhere without extra software.",
            "zstd" => "Very fast to compress and extract with a good ratio.",
            "tar" => "Bundles files without compressing them.",
            _ => "Compresses a single file.",
        };

        UpdatePasswordState();
    }

    private void UpdatePasswordState()
    {
        if (_format.SelectedItem is not FormatChoice choice)
        {
            return;
        }

        ArchiverCapabilities capabilities = choice.Archiver.Capabilities;
        bool supportsPassword = capabilities.HasFlag(ArchiverCapabilities.Password);
        bool hasPassword = supportsPassword && _password.TextLength > 0;
        bool isZstd = choice.Archiver.Id == "zstd";

        _confirm.Enabled = hasPassword && !_showPassword.Checked;

        // Zstandard encrypts the whole stream, names included, so there is nothing to choose there.
        _encryptNames.Enabled = hasPassword && capabilities.HasFlag(ArchiverCapabilities.EncryptFileNames) && !isZstd;
        _encryptNames.Checked = hasPassword && isZstd || (_encryptNames.Enabled && _encryptNames.Checked);

        _passwordHint.Text = !supportsPassword
            ? $"The {choice.Archiver.DisplayName} format cannot be password protected. Choose 7-Zip, ZIP or Zstandard."
            : isZstd
                ? "Zstandard has no encryption of its own. ZipPack wraps the data in AES-256-GCM; " +
                  "a protected file can only be opened with ZipPack, not with other Zstandard tools."
                : "AES-256. There is no way to recover a forgotten password.";
    }

    /// <summary>Keeps the file name — the interesting end of a long path — in view.</summary>
    private void ShowEndOfDestination()
    {
        if (!_destination.Focused)
        {
            _destination.Select(_destination.TextLength, 0);
        }
    }

    private void BrowseDestination()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save archive as",
            OverwritePrompt = false, // Asked once, on OK.
            AddExtension = false,
            Filter = $"{_format.Text}|*{_currentExtension}|All files|*.*",
            FileName = Path.GetFileName(_destination.Text),
        };

        string? directory = Path.GetDirectoryName(_destination.Text);
        if (Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destination.Text = dialog.FileName.EndsWith(_currentExtension, StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName
                : dialog.FileName + _currentExtension;
        }
    }

    private void Accept()
    {
        _errors.Clear();
        if (_format.SelectedItem is not FormatChoice choice)
        {
            return;
        }

        string destination = _destination.Text.Trim();
        if (destination.Length == 0 || destination.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(destination))
        {
            _errors.SetError(_destination, "Enter the full path of the archive to create.");
            _destination.Focus();
            return;
        }

        if (!_showPassword.Checked && _password.Text != _confirm.Text)
        {
            _errors.SetError(_confirm, "The passwords do not match.");
            _confirm.Focus();
            return;
        }

        bool overwrite = false;
        if (File.Exists(destination))
        {
            DialogResult answer = MessageBox.Show(
                this,
                $"“{Path.GetFileName(destination)}” already exists.\n\nDo you want to replace it?",
                "Replace file",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            overwrite = true;
        }

        Request = new ArchiveCreateRequest
        {
            SourcePaths = [],
            DestinationPath = destination,
            FormatId = choice.Archiver.Id,
            Overwrite = overwrite,
            Options = new CompressionOptions
            {
                Preset = Levels[_level.SelectedIndex].Preset,
                Password = _password.TextLength > 0 ? _password.Text : null,
                EncryptFileNames = _encryptNames.Checked,
            },
        };
        DialogResult = DialogResult.OK;
    }

    /// <summary>A format as shown in the drop-down.</summary>
    private sealed record FormatChoice(IArchiver Archiver)
    {
        public override string ToString() => $"{Archiver.DisplayName}  ({string.Join(", ", Archiver.FileExtensions)})";
    }
}
