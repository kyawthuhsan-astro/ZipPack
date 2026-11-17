using System.ComponentModel;
using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>
/// The main window. It shows one of two things: the queue of files and folders about to be compressed, or
/// the content of an open archive.
/// </summary>
[DesignerCategory("Code")]
internal sealed class MainForm : Form
{
    private const string ArchiveFilter = "Archives|*.7z;*.zip;*.zst;*.tzst;*.tar;*.gz;*.bz2;*.xz;*.rar;*.cab;*.iso;*.wim|All files|*.*";

    private readonly ICompressionService _service;
    private readonly AppSettings _settings;
    private readonly ArchiveWorkflows _workflows;
    private readonly ImageWorkflow _imageWorkflow;
    private readonly UpdateWorkflow _updates;
    private readonly IReadOnlyList<string> _startupPaths;

    private readonly ToolStrip _toolbar = new();
    private readonly FileListView _list = new() { Dock = DockStyle.Fill };
    private readonly Label _emptyHint = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly LinkLabel _closeArchive = new();
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _summary = new();
    private readonly ToolStripProgressBar _busyBar = new();
    private readonly ToolStripStatusLabel _version = new();
    private readonly List<(ToolStripButton Button, string Glyph)> _toolButtons = [];
    private ToolStripButton _compressButton = null!;
    private ToolStripButton _imagesButton = null!;
    private Font? _titleFont;

    // The result of the background update check at start-up, reused when the version link is clicked.
    private UpdateCheckResult? _update;
    private bool _checkingForUpdates;

    // Archive mode: the open archive and the password that unlocked it (reused for extract and test).
    private string? _archivePath;
    private string? _archivePassword;
    private CancellationTokenSource? _loading;
    private bool _busy;

    public MainForm(
        ICompressionService service,
        AppSettings settings,
        ArchiveWorkflows workflows,
        ImageWorkflow imageWorkflow,
        UpdateWorkflow updates,
        IReadOnlyList<string> startupPaths)
    {
        _service = service;
        _settings = settings;
        _workflows = workflows;
        _imageWorkflow = imageWorkflow;
        _updates = updates;
        _startupPaths = startupPaths;

        Text = "ZipPack";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        KeyPreview = true;

        // Drag and drop from Explorer: the form is the drop target for the whole window.
        AllowDrop = true;

        BuildToolbar();
        Control header = BuildHeader();
        BuildList();
        BuildStatusBar();

        // Docking is resolved back to front: the status bar and tool bar claim their edges first,
        // the header sits below the tool bar and the list fills what is left.
        Controls.Add(_list);
        Controls.Add(header);
        Controls.Add(_toolbar);
        Controls.Add(_statusBar);

        UpdateView();
    }

    private bool IsArchiveOpen => _archivePath is not null;

    // ------------------------------------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------------------------------------

    private void BuildToolbar()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Renderer = new FlatToolStripRenderer();
        _toolbar.BackColor = SystemColors.Window;
        _toolbar.Padding = new Padding(8, 4, 8, 4);
        _toolbar.ImageScalingSize = new Size(LogicalToDeviceUnits(24), LogicalToDeviceUnits(24));

        AddToolButton("Add Files", Glyphs.AddFiles, "Add files to compress (Ctrl+N)", (_, _) => AddFilesFromDialog());
        AddToolButton("Add Folder", Glyphs.AddFolder, "Add a folder to compress", (_, _) => AddFolderFromDialog());
        _compressButton = AddToolButton("Compress", Glyphs.Compress, "Create an archive from the listed items (Ctrl+Enter)", (_, _) => CompressQueue());
        _imagesButton = AddToolButton("Images", Glyphs.Images, "Make the listed images smaller: re-encode, convert, resize (Ctrl+I)", (_, _) => CompressImages());
        _toolbar.Items.Add(new ToolStripSeparator { Margin = new Padding(6, 0, 6, 0) });
        AddToolButton("Open", Glyphs.Open, "Open an archive to look inside (Ctrl+O)", async (_, _) => await OpenArchiveFromDialogAsync());
        AddToolButton("Extract", Glyphs.Extract, "Extract an archive (Ctrl+E)", (_, _) => ExtractArchive());
        AddToolButton("Test", Glyphs.Test, "Check an archive for errors (Ctrl+T)", (_, _) => TestArchive());

        ToolStripButton settings = AddToolButton("Settings", Glyphs.Settings, "Explorer integration, updates and preferences", (_, _) =>
            SettingsDialog.ShowSettings(this, _service, _settings, _updates.DisplayVersion));
        settings.Alignment = ToolStripItemAlignment.Right;

        RenderToolbarImages();
    }

    private ToolStripButton AddToolButton(string text, string glyph, string toolTip, EventHandler onClick)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageAboveText,
            ToolTipText = toolTip,
            Padding = new Padding(8, 4, 8, 2),
            Margin = new Padding(1, 0, 1, 2),
        };
        button.Click += onClick;
        _toolbar.Items.Add(button);
        _toolButtons.Add((button, glyph));
        return button;
    }

    /// <summary>Draws the tool bar icons for the current DPI.</summary>
    private void RenderToolbarImages()
    {
        int size = LogicalToDeviceUnits(24);
        _toolbar.ImageScalingSize = new Size(size, size);
        foreach ((ToolStripButton button, string glyph) in _toolButtons)
        {
            Image? previous = button.Image;
            button.Image = Glyphs.Render(glyph, size, AppTheme.Accent);
            previous?.Dispose();
        }
    }

    private Control BuildHeader()
    {
        _titleFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _title.AutoSize = true;
        _title.Font = _titleFont;
        _title.UseMnemonic = false;
        _title.Margin = new Padding(0, 0, 0, 2);

        _subtitle.AutoSize = true;
        _subtitle.ForeColor = SystemColors.GrayText;
        _subtitle.UseMnemonic = false;
        _subtitle.Margin = Padding.Empty;

        _closeArchive.Text = "Close archive";
        _closeArchive.AutoSize = true;
        _closeArchive.Anchor = AnchorStyles.Right;
        _closeArchive.LinkBehavior = LinkBehavior.HoverUnderline;
        _closeArchive.LinkClicked += (_, _) => ShowQueue([]);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14, 10, 14, 10),
            BackColor = SystemColors.Window,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0);
        header.Controls.Add(_subtitle, 0, 1);
        header.Controls.Add(_closeArchive, 1, 0);
        header.SetRowSpan(_closeArchive, 2);
        return header;
    }

    private void BuildList()
    {
        _list.ItemsChanged += (_, _) => UpdateView();

        // Double-click or Enter: open the entry of an archive, or the queued file itself.
        _list.ItemActivate += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateSummary();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => UpdateSummary();

        // The hint is a child of the list, so it scrolls nothing and drops fall through to the form.
        _emptyHint.AutoSize = true;
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = SystemColors.GrayText;
        _emptyHint.BackColor = SystemColors.Window;
        _emptyHint.Text = "Drop files and folders here to compress them\r\nor drop a single archive to look inside.";
        _list.Controls.Add(_emptyHint);
        _list.Resize += (_, _) => CenterEmptyHint();

        var remove = new ToolStripMenuItem("&Remove from list", null, (_, _) => _list.RemoveSelectedRows()) { ShortcutKeyDisplayString = "Del" };
        var clear = new ToolStripMenuItem("&Clear list", null, (_, _) => ShowQueue([]));
        var selectAll = new ToolStripMenuItem("Select &all", null, (_, _) => _list.SelectAllRows()) { ShortcutKeyDisplayString = "Ctrl+A" };
        var reveal = new ToolStripMenuItem("Show in &Explorer", null, (_, _) => RevealSelected());
        var extract = new ToolStripMenuItem("E&xtract archive…", null, (_, _) => ExtractArchive());
        var images = new ToolStripMenuItem("Compress &images…", null, (_, _) => CompressImages(selectedOnly: true));
        var open = new ToolStripMenuItem("&Open", null, (_, _) => OpenSelected()) { ShortcutKeyDisplayString = "Enter" };
        open.Font = new Font(open.Font, FontStyle.Bold); // The default action, as in Explorer.

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([open, new ToolStripSeparator(), remove, clear, new ToolStripSeparator(), selectAll, reveal, images, extract]);
        menu.Opening += (_, _) =>
        {
            bool hasSelection = _list.SelectedIndices.Count > 0;
            open.Enabled = _list.SelectedIndices.Count == 1 && !_busy;
            images.Visible = !IsArchiveOpen;
            images.Enabled = hasSelection && !_busy;
            remove.Visible = clear.Visible = !IsArchiveOpen;
            remove.Enabled = hasSelection && !_busy;
            clear.Enabled = _list.Rows.Count > 0 && !_busy;
            selectAll.Enabled = _list.Rows.Count > 0;
            reveal.Visible = !IsArchiveOpen;
            reveal.Enabled = _list.SelectedIndices.Count == 1;
            extract.Visible = IsArchiveOpen;
        };
        _list.ContextMenuStrip = menu;
    }

    private void BuildStatusBar()
    {
        _statusBar.SizingGrip = true;
        _statusBar.BackColor = SystemColors.Control;

        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _busyBar.Style = ProgressBarStyle.Marquee;
        _busyBar.MarqueeAnimationSpeed = 25;
        _busyBar.Visible = false;
        _busyBar.AutoSize = false;
        _busyBar.Width = LogicalToDeviceUnits(120);

        // The version doubles as the "check for updates" link.
        _version.IsLink = true;
        _version.LinkBehavior = LinkBehavior.HoverUnderline;
        _version.Margin = new Padding(12, 3, 4, 2);
        _version.Click += async (_, _) => await CheckForUpdatesAsync();
        ShowVersion();

        _statusBar.Items.AddRange([_status, _busyBar, _summary, _version]);
    }

    // ------------------------------------------------------------------------------------------------
    // Window events
    // ------------------------------------------------------------------------------------------------

    protected override void OnLoad(EventArgs e)
    {
        // Every size in code is written for 96 DPI. Declaring that baseline here, once all controls exist,
        // makes WinForms scale the whole tree to the DPI of the monitor (the mode must be set first:
        // changing it resets the dimensions).
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        PerformAutoScale();
        CenterToScreen();
        base.OnLoad(e);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterEmptyHint();

        // Files opened from archives in earlier sessions; whatever is still in use is skipped.
        _ = Task.Run(() => OpenedEntries.CleanUp(TimeSpan.FromDays(1)));

        // Quietly, in the background: the user is told only when there is something new.
        Task<UpdateCheckResult?>? updateCheck = _settings.CheckForUpdatesAtStartup && _updates.CanCheck
            ? _updates.CheckQuietlyAsync()
            : null;

        if (_startupPaths.Count > 0)
        {
            await HandleIncomingPathsAsync(_startupPaths);
        }

        if (updateCheck is not null && await updateCheck is { } result && !IsDisposed)
        {
            _update = result;
            ShowVersion();
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        bool acceptable = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        base.OnDragEnter(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            // Return to Explorer immediately; it is blocked until the drop handler returns.
            BeginInvoke(async () =>
            {
                Activate();
                await HandleIncomingPathsAsync(paths);
            });
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_busy)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        switch (keyData)
        {
            case Keys.Delete when !IsArchiveOpen && _list.Focused:
                _list.RemoveSelectedRows();
                return true;
            case Keys.Control | Keys.A when _list.Focused:
                _list.SelectAllRows();
                return true;
            case Keys.Control | Keys.N:
                AddFilesFromDialog();
                return true;
            case Keys.Control | Keys.O:
                _ = OpenArchiveFromDialogAsync();
                return true;
            case Keys.Control | Keys.E:
                ExtractArchive();
                return true;
            case Keys.Control | Keys.T:
                TestArchive();
                return true;
            case Keys.Control | Keys.Enter when _compressButton.Enabled:
                CompressQueue();
                return true;
            case Keys.Control | Keys.I when _imagesButton.Enabled:
                CompressImages();
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RenderToolbarImages(); // Keep the icons sharp when the window moves to another monitor.
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _loading?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loading?.Dispose();
            _titleFont?.Dispose();
            foreach ((ToolStripButton button, _) in _toolButtons)
            {
                button.Image?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------------------------------------
    // Ingesting files (drag and drop, command line, dialogs)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Decides what to do with paths that arrive from outside: a single archive is opened, anything else
    /// is queued for compression.
    /// </summary>
    private async Task HandleIncomingPathsAsync(IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count == 1 && File.Exists(paths[0]) && await IsArchiveAsync(paths[0]))
            {
                await OpenArchiveAsync(paths[0]);
            }
            else
            {
                AddToQueue(paths);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.Show(this, ex);
        }
    }

    private async Task<bool> IsArchiveAsync(string path)
    {
        try
        {
            await _service.ResolveArchiverAsync(path);
            return true;
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void AddToQueue(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsArchiveOpen)
        {
            known.UnionWith(_list.Rows.Select(row => row.SourcePath!));
        }

        var added = new List<FileListItem>();
        foreach (string path in paths)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && known.Add(Path.GetFullPath(path)))
            {
                added.Add(FileListItem.FromDisk(path));
            }
        }

        if (IsArchiveOpen)
        {
            ShowQueue(added);
        }
        else
        {
            _list.AddRows(added);
        }

        _status.Text = added.Count switch
        {
            0 => "Nothing new to add.",
            1 => $"Added “{added[0].Name}”.",
            _ => $"Added {added.Count} items.",
        };
    }

    private void AddFilesFromDialog()
    {
        using var dialog = new OpenFileDialog { Title = "Add files to compress", Multiselect = true, Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddToQueue(dialog.FileNames);
        }
    }

    private void AddFolderFromDialog()
    {
        using var dialog = new FolderBrowserDialog { Description = "Add a folder to compress", UseDescriptionForTitle = true, Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddToQueue(dialog.SelectedPaths);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------------------------------------

    private void CompressQueue()
    {
        string[] sources = [.. _list.Rows.Select(row => row.SourcePath!)];
        if (IsArchiveOpen || sources.Length == 0)
        {
            return;
        }

        // The workflow shows its own modal progress dialog; the work itself runs on the thread pool.
        string? archive = _workflows.Compress(this, sources);
        if (archive is not null)
        {
            ShowQueue([]);
            _status.Text = $"Created “{Path.GetFileName(archive)}” ({Format.Size(new FileInfo(archive).Length)}).";
        }
    }

    /// <summary>Compresses the images among the queued (or selected) items, without creating an archive.</summary>
    private void CompressImages(bool selectedOnly = false)
    {
        IReadOnlyList<FileListItem> rows = selectedOnly && _list.SelectedIndices.Count > 0 ? _list.SelectedRows : _list.Rows;
        string[] sources = [.. rows.Select(row => row.SourcePath!)];
        if (IsArchiveOpen || sources.Length == 0)
        {
            return;
        }

        if (_imageWorkflow.Compress(this, sources) is { Compressed: > 0 } summary)
        {
            // Sizes changed on disk; re-read the rows so the list shows them.
            ShowQueue([.. _list.Rows.Select(row => row.SourcePath!).Where(path => File.Exists(path) || Directory.Exists(path)).Select(FileListItem.FromDisk)]);
            _status.Text = summary.SavedBytes > 0
                ? $"Compressed {summary.Compressed} image{(summary.Compressed == 1 ? string.Empty : "s")}, saved {Format.Size(summary.SavedBytes)} ({summary.SavedPercent}%)."
                : $"Compressed {summary.Compressed} image{(summary.Compressed == 1 ? string.Empty : "s")}.";
        }
    }

    /// <summary>Handles a click on the version link.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates || _busy)
        {
            return;
        }

        _checkingForUpdates = true;
        _version.Text = "Checking for updates…";
        try
        {
            // May not return: installing an update restarts the application.
            _update = await _updates.CheckAndOfferAsync(this, _update);
        }
        finally
        {
            _checkingForUpdates = false;
            if (!IsDisposed)
            {
                ShowVersion();
            }
        }
    }

    private void ShowVersion()
    {
        (_version.Text, _version.ToolTipText, _version.LinkColor) = _update switch
        {
            { Status: UpdateCheckStatus.UpdateAvailable } u =>
                ($"{_updates.DisplayVersion}  ·  Update to v{u.NewVersion}", $"ZipPack v{u.NewVersion} is available. Click to install it.", AppTheme.Accent),
            { Status: UpdateCheckStatus.PendingRestart } u =>
                ($"{_updates.DisplayVersion}  ·  Restart to update", $"ZipPack v{u.NewVersion} is downloaded. Click to restart.", AppTheme.Accent),
            _ => (_updates.DisplayVersion, "ZipPack version. Click to check for updates.", SystemColors.GrayText),
        };
        _version.ActiveLinkColor = _version.VisitedLinkColor = _version.LinkColor;
    }

    private void ExtractArchive()
    {
        if (ChooseArchives(multiselect: true, "Choose the archives to extract") is { Count: > 0 } archives
            && _workflows.Extract(this, archives, _archivePassword))
        {
            _status.Text = archives.Count == 1 ? $"Extracted “{Path.GetFileName(archives[0])}”." : $"Extracted {archives.Count} archives.";
        }
    }

    private void TestArchive()
    {
        if (ChooseArchives(multiselect: false, "Choose the archive to test") is { Count: 1 } archives
            && _workflows.Test(this, archives[0], _archivePassword))
        {
            _status.Text = $"“{Path.GetFileName(archives[0])}” is intact.";
        }
    }

    /// <summary>Returns the open archive, or asks the user to pick archives when none is open.</summary>
    private IReadOnlyList<string>? ChooseArchives(bool multiselect, string title)
    {
        if (_archivePath is not null)
        {
            return [_archivePath];
        }

        using var dialog = new OpenFileDialog { Title = title, Multiselect = multiselect, Filter = ArchiveFilter };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : null;
    }

    private async Task OpenArchiveFromDialogAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Open archive", Filter = ArchiveFilter };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await OpenArchiveAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// Reads the listing of an archive without blocking the window, asking for a password when the archive
    /// hides its file names.
    /// </summary>
    private async Task OpenArchiveAsync(string path)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = new CancellationTokenSource();

        string name = Path.GetFileName(path);
        string? password = null;
        SetBusy(true, $"Reading “{name}”…");
        try
        {
            while (true)
            {
                try
                {
                    // Awaiting keeps the UI thread free: the window repaints and can be closed meanwhile.
                    IReadOnlyList<ArchiveEntryInfo> entries = await _service.ListEntriesAsync(path, password, loading.Token);

                    _archivePath = path;
                    _archivePassword = password;
                    _list.SetRows(entries.Select(FileListItem.FromEntry));
                    _status.Text = entries.Any(entry => entry.IsEncrypted) ? "This archive is password protected." : "Ready";
                    return;
                }
                catch (InvalidPasswordException)
                {
                    password = PasswordDialog.Prompt(this, name, previousAttemptFailed: password is not null);
                    if (password is null)
                    {
                        _status.Text = "Ready";
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by another archive or the window is closing.
        }
        catch (Exception ex)
        {
            _status.Text = "Ready";
            ErrorPresenter.Show(this, ex);
        }
        finally
        {
            if (ReferenceEquals(_loading, loading) && !IsDisposed)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>
    /// Opens the selected row: an archive entry is extracted on its own and opened with its program (a folder is
    /// shown in Explorer); a queued file or folder on disk is opened directly.
    /// </summary>
    private void OpenSelected()
    {
        if (_busy || _list.SelectedRows is not [var row])
        {
            return;
        }

        try
        {
            if (_archivePath is { } archive)
            {
                string entryPath = row.Location.Length == 0 ? row.Name : $"{row.Location}/{row.Name}";
                string? password = _archivePassword;
                if (_workflows.OpenEntry(this, archive, entryPath, row.IsDirectory, ref password))
                {
                    _archivePassword = password;
                    _status.Text = $"Opened “{row.Name}”.";
                }
            }
            else if (row.SourcePath is { } path)
            {
                ArchiveWorkflows.OpenInExplorer(path);
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.Show(this, ex);
        }
    }

    private void RevealSelected()
    {
        if (_list.SelectedRows is [{ Location.Length: > 0 } row])
        {
            ArchiveWorkflows.OpenInExplorer(row.Location);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // View state
    // ------------------------------------------------------------------------------------------------

    /// <summary>Leaves archive mode and shows the given queue.</summary>
    private void ShowQueue(IEnumerable<FileListItem> rows)
    {
        _archivePath = null;
        _archivePassword = null;
        _list.SetRows(rows);
        _status.Text = "Ready";
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _busyBar.Visible = busy;
        _toolbar.Enabled = !busy;
        UseWaitCursor = busy;
        if (status is not null)
        {
            _status.Text = status;
        }

        UpdateView();
    }

    private void UpdateView()
    {
        if (_archivePath is { } archive)
        {
            _title.Text = Path.GetFileName(archive);
            _subtitle.Text = Path.GetDirectoryName(archive);
            Text = $"{Path.GetFileName(archive)} – ZipPack";
        }
        else
        {
            _title.Text = "Files to compress";
            _subtitle.Text = _list.Rows.Count == 0
                ? "Add files and folders, then choose Compress — or Images to make pictures smaller."
                : "Choose Compress to create an archive from these items, or Images to make the pictures among them smaller.";
            Text = "ZipPack";
        }

        _closeArchive.Visible = IsArchiveOpen;
        _compressButton.Enabled = !IsArchiveOpen && _list.Rows.Count > 0;
        _imagesButton.Enabled = _compressButton.Enabled;
        _emptyHint.Visible = _list.Rows.Count == 0 && !_busy;
        _status.Text = string.IsNullOrEmpty(_status.Text) ? "Ready" : _status.Text;
        CenterEmptyHint();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int selected = _list.SelectedIndices.Count;
        IReadOnlyList<FileListItem> rows = selected > 0 ? _list.SelectedRows : _list.Rows;
        long size = rows.Sum(row => row.Size ?? 0);
        string count = rows.Count == 1 ? "1 item" : $"{rows.Count:N0} items";

        _summary.Text = rows.Count == 0
            ? string.Empty
            : $"{count}{(selected > 0 ? " selected" : string.Empty)}  ·  {Format.Size(size)}";
    }

    private void CenterEmptyHint()
    {
        _emptyHint.Location = new Point(
            Math.Max(0, (_list.ClientSize.Width - _emptyHint.Width) / 2),
            Math.Max(0, (_list.ClientSize.Height - _emptyHint.Height) / 2));
    }
}
