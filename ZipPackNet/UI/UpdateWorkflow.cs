using System.Net.Http;
using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>The user-facing side of <see cref="UpdateService"/>: check, ask, download, restart.</summary>
internal sealed class UpdateWorkflow(UpdateService updates)
{
    private const int MaxNotesLength = 700;

    /// <summary>Gets the version label text, e.g. <c>v1.0.0</c>.</summary>
    public string DisplayVersion => updates.DisplayVersion;

    /// <summary>Gets a value indicating whether a background check at start-up makes sense.</summary>
    public bool CanCheck => updates.CanCheck;

    /// <summary>Checks without bothering the user: any failure (offline, feed down) simply yields <see langword="null"/>.</summary>
    public async Task<UpdateCheckResult?> CheckQuietlyAsync()
    {
        try
        {
            return await updates.CheckAsync();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Checks (or reuses a result from start-up), tells the user and offers to install.</summary>
    /// <param name="owner">The main window.</param>
    /// <param name="known">A result of an earlier quiet check, or <see langword="null"/> to check now.</param>
    /// <returns>The latest result, or <see langword="null"/> when the check failed.</returns>
    public async Task<UpdateCheckResult?> CheckAndOfferAsync(IWin32Window owner, UpdateCheckResult? known)
    {
        UpdateCheckResult result;
        try
        {
            result = known is { Status: UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.PendingRestart }
                ? known
                : await updates.CheckAsync();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            MessageBox.Show(
                owner,
                $"ZipPack could not check for updates.\n\n{ex.Message}\n\nCheck your internet connection and try again.",
                "Check for updates",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.Unavailable:
                MessageBox.Show(owner, $"ZipPack {DisplayVersion}\n\n{result.Reason}", "About ZipPack", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;

            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(owner, $"You have the latest version of ZipPack ({DisplayVersion}).", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;

            case UpdateCheckStatus.PendingRestart:
                if (Ask(owner, $"ZipPack v{result.NewVersion} is downloaded and ready.\n\nRestart ZipPack now to finish the update?", result.ReleaseNotes))
                {
                    updates.ApplyAndRestart(result);
                }

                break;

            case UpdateCheckStatus.UpdateAvailable:
                if (Ask(owner, $"ZipPack v{result.NewVersion} is available (you have {DisplayVersion}).\n\nDownload and install it now? ZipPack restarts when it is done.", result.ReleaseNotes))
                {
                    Install(owner, result);
                }

                break;
        }

        return result;
    }

    private static bool Ask(IWin32Window owner, string question, string? notes)
    {
        string text = string.IsNullOrWhiteSpace(notes)
            ? question
            : $"{question}\n\nWhat's new:\n{Shorten(notes.Trim())}";
        return MessageBox.Show(owner, text, "Update ZipPack", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private static string Shorten(string text) => text.Length <= MaxNotesLength ? text : text[..MaxNotesLength].TrimEnd() + " …";

    /// <summary>Network, feed and file errors are expected for an update check; bugs are not hidden.</summary>
    private static bool IsExpectedFailure(Exception ex) =>
        ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
            or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException;

    private void Install(IWin32Window owner, UpdateCheckResult update)
    {
        OperationResult download = ProgressDialog.Run(
            owner,
            $"Downloading ZipPack v{update.NewVersion}",
            (progress, cancellationToken) => updates.DownloadAsync(
                update,
                percent => progress.Report(new CompressionProgress(percent, 100, $"ZipPack v{update.NewVersion}")),
                cancellationToken),
            describe: _ => string.Empty);

        switch (download.Status)
        {
            case OperationStatus.Completed:
                // Exits this process; Velopack replaces the files and starts the new version.
                updates.ApplyAndRestart(update);
                break;

            case OperationStatus.Failed:
                MessageBox.Show(
                    owner,
                    $"The update could not be downloaded.\n\n{download.Error!.Message}",
                    "Update ZipPack",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                break;
        }
    }
}
