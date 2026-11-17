using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ZipPackNet.UI;

/// <summary>The colours shared by the views.</summary>
internal static class AppTheme
{
    /// <summary>Gets the brand colour: the teal of the application icon (see Assets\make-icon.ps1).</summary>
    public static Color Accent { get; } = Color.FromArgb(14, 160, 162);
}

/// <summary>Formatting helpers shared by the views.</summary>
internal static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    /// <summary>Formats a byte count the way Explorer does: "0 bytes", "1.5 KB", "23.4 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {Units[unit]}";
    }

    /// <summary>Formats a duration as "m:ss" or "h:mm:ss".</summary>
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
}

/// <summary>Icons drawn from the Segoe icon font that ships with Windows, so no image assets are needed
/// and the icons stay sharp at every DPI and follow the system colours.</summary>
internal static class Glyphs
{
    public const string AddFiles = "";
    public const string AddFolder = "";
    public const string Compress = "";
    public const string Open = "";
    public const string Extract = "";
    public const string Test = "";
    public const string Images = "";
    public const string Settings ="";
    public const string Remove = "";
    public const string Clear = "";
    public const string Lock = "";

    private static readonly string FontName = ResolveFontName();

    /// <summary>Renders a glyph into a square bitmap of <paramref name="pixelSize"/> pixels.</summary>
    public static Bitmap Render(string glyph, int pixelSize, Color color)
    {
        var bitmap = new Bitmap(pixelSize, pixelSize);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var font = new Font(FontName, pixelSize * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, pixelSize, pixelSize), format);
        return bitmap;
    }

    private static string ResolveFontName()
    {
        // "Segoe Fluent Icons" ships with Windows 11, "Segoe MDL2 Assets" with Windows 10; the code points match.
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(family => family.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
}

/// <summary>A flat tool strip look: no gradient, no border, a soft highlight on hover.</summary>
internal sealed class FlatToolStripRenderer : ToolStripProfessionalRenderer
{
    public FlatToolStripRenderer() : base(new FlatColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // A single hairline under the tool bar separates it from the content.
        if (e.ToolStrip is not ToolStripDropDown and not StatusStrip)
        {
            using var pen = new Pen(SystemColors.ControlLight);
            e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => SystemColors.Window;

        public override Color ToolStripGradientMiddle => SystemColors.Window;

        public override Color ToolStripGradientEnd => SystemColors.Window;

        public override Color ButtonSelectedHighlight => SystemColors.ControlLight;

        public override Color ButtonSelectedGradientBegin => SystemColors.ControlLight;

        public override Color ButtonSelectedGradientMiddle => SystemColors.ControlLight;

        public override Color ButtonSelectedGradientEnd => SystemColors.ControlLight;

        public override Color ButtonPressedGradientBegin => SystemColors.ControlDark;

        public override Color ButtonPressedGradientMiddle => SystemColors.ControlDark;

        public override Color ButtonPressedGradientEnd => SystemColors.ControlDark;

        public override Color ButtonSelectedBorder => SystemColors.ControlLight;

        public override Color ButtonPressedBorder => SystemColors.ControlDark;

        public override Color SeparatorDark => SystemColors.ControlLight;

        public override Color SeparatorLight => SystemColors.Window;
    }
}

/// <summary>
/// The base of every dialog: fixed border, centred on its owner, laid out with auto-sizing panels and
/// scaled with the monitor DPI.
/// </summary>
[DesignerCategory("Code")]
internal abstract class DialogBase : Form
{
    protected DialogBase(string title)
    {
        Text = title;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(12);
    }

    /// <summary>Makes the dialog behave like a top-level window when it has no owner (shell launches).</summary>
    protected override void OnLoad(EventArgs e)
    {
        // Every size in code is written for 96 DPI. Declaring that baseline here, once all controls exist,
        // makes WinForms scale the whole tree to the DPI of the monitor (the mode must be set first:
        // changing it resets the dimensions).
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        PerformAutoScale();

        if (Owner is null)
        {
            ShowInTaskbar = true;
            ShowIcon = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            CenterToScreen();
        }

        base.OnLoad(e);
    }

    /// <summary>Creates a grid whose rows size to their content and whose second column takes the spare width.</summary>
    protected static TableLayoutPanel CreateGrid(int columns)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        for (int i = 0; i < columns; i++)
        {
            grid.ColumnStyles.Add(i == 1 ? new ColumnStyle(SizeType.Percent, 100F) : new ColumnStyle(SizeType.AutoSize));
        }

        return grid;
    }

    protected static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 12, 6),
    };

    protected static Label CreateHint(string text, int maxWidth = 460) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(maxWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, 8),
    };

    /// <summary>Creates the right-aligned OK / Cancel row and wires it to Enter and Escape.</summary>
    protected FlowLayoutPanel CreateButtonRow(string acceptText, out Button accept, bool withCancel = true)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };

        accept = CreateButton(acceptText);
        accept.DialogResult = DialogResult.OK;
        AcceptButton = accept;

        if (withCancel)
        {
            Button cancel = CreateButton("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            CancelButton = cancel;
            row.Controls.Add(cancel);
        }

        row.Controls.Add(accept);
        return row;
    }

    protected static Button CreateButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(88, 28),
        Padding = new Padding(8, 0, 8, 0),
        Margin = new Padding(8, 0, 0, 0),
        UseVisualStyleBackColor = true,
    };
}

/// <summary>Icons and type names from the Windows shell, looked up by extension and cached.</summary>
internal sealed partial class ShellFileInfo : IDisposable
{
    private const uint Icon = 0x100;
    private const uint SmallIcon = 0x1;
    private const uint TypeName = 0x400;
    private const uint UseFileAttributes = 0x10;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeNormal = 0x80;

    private readonly Dictionary<string, (int ImageIndex, string TypeName)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the image list holding the icons handed out by <see cref="Get"/>.</summary>
    public ImageList Images { get; } = new()
    {
        ColorDepth = ColorDepth.Depth32Bit,
        ImageSize = SystemInformation.SmallIconSize,
    };

    /// <summary>Gets the icon index and the type description ("Text Document") for a file name or a folder.</summary>
    public unsafe (int ImageIndex, string TypeName) Get(string fileName, bool isDirectory)
    {
        string extension = Path.GetExtension(fileName);
        string key = isDirectory ? "<folder>" : extension;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // USEFILEATTRIBUTES makes the shell answer from the name alone, so entries inside archives work too.
        var info = default(SHFILEINFOW);
        nint result = SHGetFileInfo(
            isDirectory ? "folder" : "file" + extension,
            isDirectory ? AttributeDirectory : AttributeNormal,
            ref info,
            (uint)sizeof(SHFILEINFOW),
            Icon | SmallIcon | TypeName | UseFileAttributes);

        int imageIndex = -1;
        string typeName = isDirectory ? "File folder" : "File";
        if (result != 0)
        {
            typeName = new string(info.szTypeName);
            if (info.hIcon != 0)
            {
                using (var icon = System.Drawing.Icon.FromHandle(info.hIcon))
                {
                    Images.Images.Add(icon.ToBitmap());
                    imageIndex = Images.Images.Count - 1;
                }

                DestroyIcon(info.hIcon);
            }
        }

        return _cache[key] = (imageIndex, typeName);
    }

    public void Dispose() => Images.Dispose();

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFOW info, uint infoSize, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }
}
