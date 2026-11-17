using System.ComponentModel;

namespace ZipPackNet.UI;

/// <summary>Asks for the password of a protected archive.</summary>
[DesignerCategory("Code")]
internal sealed class PasswordDialog : DialogBase
{
    private readonly TextBox _password;

    private PasswordDialog(string archiveName, bool previousAttemptFailed) : base("Password required")
    {
        Label prompt = CreateLabel(previousAttemptFailed
            ? $"That password did not work. Enter the password for “{archiveName}”:"
            : $"“{archiveName}” is password protected. Enter the password:");
        prompt.MaximumSize = new Size(380, 0);
        prompt.UseMnemonic = false;
        if (previousAttemptFailed)
        {
            prompt.ForeColor = Color.Firebrick;
        }

        _password = new TextBox
        {
            UseSystemPasswordChar = true,
            Width = 380,
            Margin = new Padding(0, 4, 0, 6),
        };

        var show = new CheckBox { Text = "&Show password", AutoSize = true, Margin = Padding.Empty };
        show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !show.Checked;

        FlowLayoutPanel buttons = CreateButtonRow("OK", out Button ok);
        ok.Enabled = false;
        _password.TextChanged += (_, _) => ok.Enabled = _password.TextLength > 0;

        TableLayoutPanel grid = CreateGrid(columns: 1);
        grid.Controls.Add(prompt);
        grid.Controls.Add(_password);
        grid.Controls.Add(show);
        grid.Controls.Add(buttons);
        Controls.Add(grid);
    }

    /// <summary>Shows the dialog and returns the password, or <see langword="null"/> when the user gave up.</summary>
    public static string? Prompt(IWin32Window? owner, string archiveName, bool previousAttemptFailed)
    {
        using var dialog = new PasswordDialog(archiveName, previousAttemptFailed);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._password.Text : null;
    }
}
