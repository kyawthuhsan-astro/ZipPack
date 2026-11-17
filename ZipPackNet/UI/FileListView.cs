using System.ComponentModel;
using System.Runtime.InteropServices;
using ZipPack;

namespace ZipPackNet.UI;

/// <summary>One row of the file list: a file queued for compression or an entry of an open archive.</summary>
/// <param name="Name">The file or folder name.</param>
/// <param name="Location">The containing folder — on disk for queued files, inside the archive for entries.</param>
/// <param name="IsDirectory"><see langword="true"/> for a folder.</param>
/// <param name="Size">The size in bytes, when known.</param>
/// <param name="CompressedSize">The size inside the archive, when the format records it.</param>
/// <param name="Modified">The last modification time, when known.</param>
/// <param name="SourcePath">The full path on disk for queued files; <see langword="null"/> for archive entries.</param>
internal sealed record FileListItem(
    string Name,
    string Location,
    bool IsDirectory,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? Modified,
    string? SourcePath = null)
{
    /// <summary>Creates the row for a file or folder on disk.</summary>
    public static FileListItem FromDisk(string path)
    {
        string fullPath = Path.GetFullPath(path);
        bool isDirectory = Directory.Exists(fullPath);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        return new FileListItem(
            info.Name,
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath)) ?? string.Empty,
            isDirectory,
            isDirectory ? null : ((FileInfo)info).Length,
            CompressedSize: null,
            info.LastWriteTime,
            fullPath);
    }

    /// <summary>Creates the row for an entry of an archive.</summary>
    public static FileListItem FromEntry(ArchiveEntryInfo entry)
    {
        int separator = entry.Path.LastIndexOf('/');
        return new FileListItem(
            entry.Path[(separator + 1)..],
            separator < 0 ? string.Empty : entry.Path[..separator],
            entry.IsDirectory,
            entry.IsDirectory ? null : entry.Size,
            entry.IsDirectory ? null : entry.CompressedSize,
            entry.LastModified?.ToLocalTime());
    }
}

/// <summary>
/// A details list of <see cref="FileListItem"/>s with shell icons and click-to-sort columns.
/// </summary>
/// <remarks>
/// The list is virtual: rows are produced on demand from the backing collection, so an archive with a
/// hundred thousand entries opens instantly and scrolls smoothly.
/// </remarks>
[DesignerCategory("Code")]
internal sealed partial class FileListView : ListView
{
    private static readonly (string Title, int Width, HorizontalAlignment Alignment)[] ColumnDefinitions =
    [
        ("Name", 260, HorizontalAlignment.Left),
        ("Size", 90, HorizontalAlignment.Right),
        ("Compressed", 100, HorizontalAlignment.Right),
        ("Type", 160, HorizontalAlignment.Left),
        ("Modified", 130, HorizontalAlignment.Left),
        ("Location", 210, HorizontalAlignment.Left),
    ];

    private readonly List<FileListItem> _items = [];
    private readonly ShellFileInfo _shell = new();
    private int _sortColumn = -1;
    private bool _sortDescending;

    public FileListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = true;
        BorderStyle = BorderStyle.None;
        SmallImageList = _shell.Images;
        DoubleBuffered = true; // No flicker while resizing or scrolling.

        foreach ((string title, int width, HorizontalAlignment alignment) in ColumnDefinitions)
        {
            Columns.Add(title, LogicalToDeviceUnits(width), alignment);
        }
    }

    /// <summary>Raised whenever rows are added or removed.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Gets the rows in display order.</summary>
    public IReadOnlyList<FileListItem> Rows => _items;

    /// <summary>Gets the currently selected rows.</summary>
    public IReadOnlyList<FileListItem> SelectedRows =>
        [.. SelectedIndices.Cast<int>().Where(index => index < _items.Count).Select(index => _items[index])];

    /// <summary>Replaces all rows.</summary>
    public void SetRows(IEnumerable<FileListItem> rows)
    {
        FileListItem[] replacement = [.. rows];
        ShrinkSafely(() =>
        {
            _items.Clear();
            _items.AddRange(replacement);
        });

        _sortColumn = -1;
        UpdateColumnHeaders();
        NotifyChanged();
    }

    /// <summary>Appends rows.</summary>
    public void AddRows(IEnumerable<FileListItem> rows)
    {
        // Growing is always safe: every index the control knows about stays valid.
        _items.AddRange(rows);
        VirtualListSize = _items.Count;
        NotifyChanged();
    }

    /// <summary>Removes the selected rows.</summary>
    public void RemoveSelectedRows()
    {
        int[] selected = [.. SelectedIndices.Cast<int>().Where(index => index < _items.Count).OrderDescending()];
        if (selected.Length == 0)
        {
            return;
        }

        ShrinkSafely(() =>
        {
            foreach (int index in selected)
            {
                _items.RemoveAt(index);
            }
        });
        NotifyChanged();
    }

    /// <summary>Selects every row.</summary>
    public void SelectAllRows()
    {
        BeginUpdate();
        for (int i = 0; i < _items.Count; i++)
        {
            SelectedIndices.Add(i);
        }

        EndUpdate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // The Explorer look: soft selection and hover instead of the classic solid bar.
        if (!Application.IsDarkModeEnabled)
        {
            _ = SetWindowTheme(Handle, "Explorer", null);
        }
    }

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        // The native control may still ask for a row it remembers (focus, accessibility, a pending
        // repaint) at the moment the list shrinks. Answer with a blank row instead of throwing.
        if ((uint)e.ItemIndex >= (uint)_items.Count)
        {
            e.Item = new ListViewItem([string.Empty, .. Enumerable.Repeat(string.Empty, Columns.Count - 1)]);
            return;
        }

        FileListItem row = _items[e.ItemIndex];
        (int imageIndex, string typeName) = _shell.Get(row.Name, row.IsDirectory);

        var item = new ListViewItem(row.Name, imageIndex);
        item.SubItems.AddRange(
        [
            row.Size is { } size ? Format.Size(size) : string.Empty,
            row.CompressedSize is { } compressed ? Format.Size(compressed) : string.Empty,
            typeName,
            row.Modified?.ToString("g") ?? string.Empty,
            row.Location,
        ]);
        e.Item = item;
        base.OnRetrieveVirtualItem(e);
    }

    protected override void OnColumnClick(ColumnClickEventArgs e)
    {
        _sortDescending = e.Column == _sortColumn && !_sortDescending;
        _sortColumn = e.Column;

        Comparison<FileListItem> byColumn = _sortColumn switch
        {
            1 => (a, b) => Nullable.Compare(a.Size, b.Size),
            2 => (a, b) => Nullable.Compare(a.CompressedSize, b.CompressedSize),
            3 => (a, b) => string.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => Nullable.Compare(a.Modified, b.Modified),
            5 => (a, b) => string.Compare(a.Location, b.Location, StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        // Folders stay on top, like in Explorer; the name breaks ties so the order is stable.
        _items.Sort((a, b) =>
        {
            int result = b.IsDirectory.CompareTo(a.IsDirectory);
            if (result == 0)
            {
                result = _sortDescending ? byColumn(b, a) : byColumn(a, b);
            }

            return result != 0 ? result : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        UpdateColumnHeaders();
        SelectedIndices.Clear();
        Invalidate();
        base.OnColumnClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shell.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Applies a change that removes rows. In a virtual list the control only stores a row <em>count</em>,
    /// so the order matters: the selection is cleared and the control is told it is empty while every
    /// index it may still ask about is valid; only then is the backing list changed.
    /// </summary>
    private void ShrinkSafely(Action change)
    {
        BeginUpdate();
        try
        {
            SelectedIndices.Clear();
            VirtualListSize = 0;
            change();
            VirtualListSize = _items.Count;
        }
        finally
        {
            EndUpdate();
        }
    }

    private void NotifyChanged()
    {
        Invalidate();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateColumnHeaders()
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            string arrow = i == _sortColumn ? (_sortDescending ? "  ▾" : "  ▴") : string.Empty;
            Columns[i].Text = ColumnDefinitions[i].Title + arrow;
        }
    }

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint window, string? appName, string? idList);
}
