using System.ComponentModel;
using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>
/// Collects the options for compressing images: output format, quality, size limit, metadata and where the
/// results go.
/// </summary>
[DesignerCategory("Code")]
internal sealed class ImageCompressDialog : DialogBase
{
    private static readonly (CompressionPreset Preset, string Caption)[] Levels =
    [
        (CompressionPreset.Store, "Best quality (95)"),
        (CompressionPreset.Fastest, "Very high quality (90)"),
        (CompressionPreset.Fast, "High quality (85)"),
        (CompressionPreset.Normal, "Balanced (80) — recommended"),
        (CompressionPreset.High, "Small files (70)"),
        (CompressionPreset.Ultra, "Smallest files (60)"),
    ];

    /// <summary>
    /// The formats offered, in order. HEIC is left out: the bundled ImageMagick build can read it but has no
    /// HEVC encoder, so HEIC files are converted to another format instead.
    /// </summary>
    private static readonly (string? Id, string Caption, string Hint)[] Formats =
    [
        (null, "Keep original format", "Each image is re-encoded in its own format. Lossless formats (PNG, GIF, TIFF, BMP) stay pixel-identical."),
        ("jpeg", "JPEG (.jpg)", "Photos. Opens everywhere. Transparent areas become white."),
        ("webp", "WebP (.webp)", "Photos and graphics, usually 25–35% smaller than JPEG. Keeps transparency and animation."),
        ("avif", "AVIF (.avif)", "The smallest photos at the same quality. Slower to encode; supported by current browsers and Windows 11."),
        ("jxl", "JPEG XL (.jxl)", "Very efficient, but not every program opens it yet."),
        ("png", "PNG (.png)", "Lossless graphics and screenshots. Quality has no effect."),
        ("gif", "GIF (.gif)", "Simple animations, 256 colours."),
        ("tiff", "TIFF (.tif)", "Lossless, for print and archiving."),
        ("bmp", "BMP (.bmp)", "Uncompressed; only useful for old programs."),
    ];

    private static readonly HashSet<string> LosslessCapable = new(StringComparer.OrdinalIgnoreCase) { "webp", "avif", "jxl" };

    private readonly ComboBox _format;
    private readonly ComboBox _quality;
    private readonly CheckBox _lossless;
    private readonly CheckBox _resize;
    private readonly NumericUpDown _maxSide;
    private readonly CheckBox _strip;
    private readonly RadioButton _nextToOriginals;
    private readonly RadioButton _toFolder;
    private readonly TextBox _folder;
    private readonly Button _browse;
    private readonly Label _formatHint;
    private readonly Label _destinationHint;
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };

    private ImageCompressDialog(IMagickNetCore images, AppSettings settings, IReadOnlyList<string> files)
        : base("Compress images")
    {
        (string? Id, string Caption, string Hint)[] formats =
            [.. Formats.Where(f => f.Id is null || images.TargetFormatIds.Contains(f.Id, StringComparer.OrdinalIgnoreCase))];

        _format = CreateDropDown(260);
        _format.Items.AddRange([.. formats.Select(f => f.Caption)]);
        _format.Tag = formats;
        _formatHint = CreateHint(string.Empty, maxWidth: 420);

        _quality = CreateDropDown(260);
        _quality.Items.AddRange([.. Levels.Select(level => level.Caption)]);
        _lossless = new CheckBox { Text = "&Lossless (larger files, no quality loss)", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _lossless.CheckedChanged += (_, _) => _quality.Enabled = !_lossless.Checked;

        _resize = new CheckBox { Text = "&Shrink so the longest side is at most", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 6, 3) };
        _maxSide = new NumericUpDown { Minimum = 16, Maximum = 32_768, Increment = 100, Value = 1920, Width = 80, Margin = new Padding(0, 3, 4, 3) };
        var pixels = new Label { Text = "pixels", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 3) };
        _resize.CheckedChanged += (_, _) => _maxSide.Enabled = _resize.Checked;

        _strip = new CheckBox
        {
            Text = "Remove &metadata (camera details, GPS location, comments)",
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 3),
        };

        _nextToOriginals = new RadioButton { Text = "Save &next to the originals", AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        _toFolder = new RadioButton { Text = "Save to &folder:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 6, 3) };
        _folder = new TextBox { Width = 300, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 3, 0, 3) };
        _browse = CreateButton("&Browse…");
        _browse.Click += (_, _) => BrowseFolder();
        _destinationHint = CreateHint(string.Empty, maxWidth: 420);
        _nextToOriginals.CheckedChanged += (_, _) => UpdateDestinationState();
        _format.SelectedIndexChanged += (_, _) => OnFormatChanged();

        // Output -------------------------------------------------------------------------------------
        TableLayoutPanel outputGrid = CreateGrid(columns: 2);
        outputGrid.Controls.Add(CreateLabel("F&ormat:"), 0, 0);
        outputGrid.Controls.Add(_format, 1, 0);
        outputGrid.Controls.Add(_formatHint, 1, 1);
        outputGrid.Controls.Add(CreateLabel("&Quality:"), 0, 2);
        outputGrid.Controls.Add(_quality, 1, 2);
        outputGrid.Controls.Add(_lossless, 1, 3);

        var resizeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        resizeRow.Controls.AddRange([_resize, _maxSide, pixels]);
        TableLayoutPanel adjustGrid = CreateGrid(columns: 1);
        adjustGrid.Controls.Add(resizeRow);
        adjustGrid.Controls.Add(_strip);

        // Destination --------------------------------------------------------------------------------
        TableLayoutPanel destinationGrid = CreateGrid(columns: 3);
        destinationGrid.Controls.Add(_nextToOriginals, 0, 0);
        destinationGrid.SetColumnSpan(_nextToOriginals, 3);
        destinationGrid.Controls.Add(_toFolder, 0, 1);
        destinationGrid.Controls.Add(_folder, 1, 1);
        destinationGrid.Controls.Add(_browse, 2, 1);
        destinationGrid.Controls.Add(_destinationHint, 0, 2);
        destinationGrid.SetColumnSpan(_destinationHint, 3);

        FlowLayoutPanel buttons = CreateButtonRow("Compress", out Button ok);
        ok.DialogResult = DialogResult.None; // Validate before closing.
        ok.Click += (_, _) => Accept();

        var summary = new Label
        {
            Text = files.Count == 1 ? $"Compressing: {Path.GetFileName(files[0])}" : $"Compressing {files.Count} images",
            AutoSize = true,
            AutoEllipsis = true,
            MaximumSize = new Size(520, 0),
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 10),
        };

        TableLayoutPanel layout = CreateGrid(columns: 1);
        layout.Controls.Add(summary);
        layout.Controls.Add(CreateGroup("Output", outputGrid));
        layout.Controls.Add(CreateGroup("Adjustments", adjustGrid));
        layout.Controls.Add(CreateGroup("Save", destinationGrid));
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        // Initial state ------------------------------------------------------------------------------
        _format.SelectedIndex = Math.Max(0, Array.FindIndex(formats, f => string.Equals(f.Id, settings.ImageFormatId, StringComparison.OrdinalIgnoreCase)));
        _quality.SelectedIndex = Math.Max(0, Array.FindIndex(Levels, level => level.Preset == settings.ImagePreset));
        _resize.Checked = settings.ImageMaxSide is not null;
        _maxSide.Value = Math.Clamp(settings.ImageMaxSide ?? 1920, (int)_maxSide.Minimum, (int)_maxSide.Maximum);
        _maxSide.Enabled = _resize.Checked;
        _strip.Checked = settings.ImageStripMetadata;
        _folder.Text = SuggestFolder(files);
        (settings.ImageReplaceOriginals ? _nextToOriginals : _toFolder).Checked = true;
        UpdateDestinationState();
    }

    /// <summary>Gets the user's choices.</summary>
    public ImageJobChoices? Choices { get; private set; }

    /// <summary>Shows the dialog and returns the choices, or <see langword="null"/> when cancelled.</summary>
    public static ImageJobChoices? Prompt(IWin32Window? owner, IMagickNetCore images, AppSettings settings, IReadOnlyList<string> files)
    {
        using var dialog = new ImageCompressDialog(images, settings, files);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Choices is not { } choices)
        {
            return null;
        }

        // Remember the choices for next time.
        settings.ImageFormatId = choices.Options.TargetFormatId;
        settings.ImagePreset = choices.Options.Preset;
        settings.ImageMaxSide = choices.Options.MaxWidth;
        settings.ImageStripMetadata = choices.Options.StripMetadata;
        settings.ImageReplaceOriginals = choices.ReplaceOriginals;
        settings.Save();
        return choices;
    }

    /// <summary>Proposes a "Compressed" folder next to the first image.</summary>
    internal static string SuggestFolder(IReadOnlyList<string> files)
    {
        string? directory = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
        return Path.Combine(directory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Compressed");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _errors.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ComboBox CreateDropDown(int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width,
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

    private (string? Id, string Caption, string Hint) SelectedFormat =>
        (((string? Id, string Caption, string Hint)[])_format.Tag!)[Math.Max(0, _format.SelectedIndex)];

    private void OnFormatChanged()
    {
        (string? id, _, string hint) = SelectedFormat;
        _formatHint.Text = hint;

        bool lossless = id is not null && LosslessCapable.Contains(id);
        _lossless.Enabled = lossless;
        if (!lossless)
        {
            _lossless.Checked = false;
        }

        // PNG, GIF, TIFF and BMP have no quality setting.
        _quality.Enabled = !_lossless.Checked && id is not ("png" or "gif" or "tiff" or "bmp");
        UpdateDestinationState();
    }

    private void UpdateDestinationState()
    {
        bool toFolder = _toFolder.Checked;
        _folder.Enabled = _browse.Enabled = toFolder;

        _destinationHint.Text = toFolder
            ? "The originals are not changed. Images with the same name get a “ (2)” suffix."
            : SelectedFormat.Id is null
                ? "⚠  The originals are replaced by the compressed images. An image that cannot be made smaller is left untouched."
                : "The converted images are saved beside the originals, which are kept.";
        _destinationHint.ForeColor = !toFolder && SelectedFormat.Id is null ? Color.DarkOrange : SystemColors.GrayText;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Save the compressed images to", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        string? existing = Directory.Exists(_folder.Text) ? _folder.Text : Path.GetDirectoryName(_folder.Text);
        if (Directory.Exists(existing))
        {
            dialog.InitialDirectory = existing;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dialog.SelectedPath;
        }
    }

    private void Accept()
    {
        _errors.Clear();
        string? folder = null;
        if (_toFolder.Checked)
        {
            folder = _folder.Text.Trim();
            if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder))
            {
                _errors.SetError(_folder, "Enter the full path of the folder to save to.");
                _folder.Focus();
                return;
            }
        }

        int? maxSide = _resize.Checked ? (int)_maxSide.Value : null;
        Choices = new ImageJobChoices(
            new ImageCompressOptions
            {
                TargetFormatId = SelectedFormat.Id,
                Preset = Levels[Math.Max(0, _quality.SelectedIndex)].Preset,
                Lossless = _lossless.Checked,
                MaxWidth = maxSide,
                MaxHeight = maxSide,
                StripMetadata = _strip.Checked,
            },
            ReplaceOriginals: _nextToOriginals.Checked,
            OutputFolder: folder);
        DialogResult = DialogResult.OK;
    }
}
