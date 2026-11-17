using System.Diagnostics;
using ZipPack;
using ZipPackNet.Services;

namespace ZipPackNet.UI;

/// <summary>
/// The three user-facing workflows — compress, extract, test — from the first question to the final message.
/// </summary>
/// <remarks>
/// Both entry points use this class: the main window and the Explorer context menu (where there is no main
/// window and <c>owner</c> is <see langword="null"/>). Each workflow is: ask for options, run the operation
/// behind a <see cref="ProgressDialog"/>, ask for a password if it turns out one is needed, report failures.
/// </remarks>
internal sealed class ArchiveWorkflows(ICompressionService service, AppSettings settings)
{
    /// <summary>Creates an archive from files and folders.</summary>
    /// <returns>The path of the new archive, or <see langword="null"/> when cancelled or failed.</returns>
    public string? Compress(IWin32Window? owner, IReadOnlyList<string> sources)
    {
        if (!AllExist(owner, sources))
        {
            return null;
        }

        if (CompressOptionsDialog.Prompt(owner, service, settings, sources) is not { } choices)
        {
            return null;
        }

        ArchiveCreateRequest request = choices.Request;
        OperationResult result = ProgressDialog.Run(
            owner,
            $"Compressing to {Path.GetFileName(request.DestinationPath)}",
            (progress, cancellationToken) => service.CreateArchiveAsync(request, progress, cancellationToken));

        if (result.Status == OperationStatus.Failed)
        {
            ErrorPresenter.Show(owner, result.Error!);
        }

        if (result.Status != OperationStatus.Completed)
        {
            return null;
        }

        if (choices.DeleteSources)
        {
            DeleteSourcesAfterVerifying(owner, request);
        }

        return request.DestinationPath;
    }

    /// <summary>
    /// Tests the new archive and, only when it is intact, moves its sources to the Recycle Bin. Anything that
    /// cannot or must not be removed is reported; nothing is ever deleted on a failed or cancelled test.
    /// </summary>
    private void DeleteSourcesAfterVerifying(IWin32Window? owner, ArchiveCreateRequest request)
    {
        string name = Path.GetFileName(request.DestinationPath);
        OperationResult test = ProgressDialog.Run(
            owner,
            $"Verifying {name}",
            (progress, cancellationToken) => service.TestArchiveAsync(request.DestinationPath, request.Options.Password, progress, cancellationToken));

        if (test.Status != OperationStatus.Completed)
        {
            string why = test.Status == OperationStatus.Cancelled
                ? "The check was cancelled."
                : $"The check failed: {test.Error?.Message}";
            MessageBox.Show(
                owner,
                $"“{name}” was created, but it could not be verified, so the original files were kept.\n\n{why}",
                "Delete files after compression",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        CleanupPlan plan = SourceCleanup.Plan(request.SourcePaths, request.DestinationPath);
        CleanupResult cleanup = SourceCleanup.Run(plan.Delete);

        var problems = new List<string>();
        problems.AddRange(plan.Keep.Select(path => $"•  {Path.GetFileName(path)} — it contains the new archive"));
        problems.AddRange(cleanup.Failed.Select(failure => $"•  {Path.GetFileName(failure.Path)} — {failure.Reason}"));
        if (problems.Count > 0)
        {
            MessageBox.Show(
                owner,
                $"“{name}” was created and verified. {cleanup.Deleted.Count} item(s) were moved to the Recycle Bin; these were kept:\n\n" +
                string.Join("\n", problems.Take(10)),
                "Delete files after compression",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    /// <summary>Extracts one or more archives.</summary>
    /// <param name="owner">The window to centre the dialogs on, or <see langword="null"/>.</param>
    /// <param name="archives">The archives to extract.</param>
    /// <param name="knownPassword">A password the user already entered for these archives, if any.</param>
    /// <returns><see langword="true"/> when everything was extracted.</returns>
    public bool Extract(IWin32Window? owner, IReadOnlyList<string> archives, string? knownPassword = null)
    {
        if (!AllExist(owner, archives))
        {
            return false;
        }

        ExtractChoices? choices = ExtractOptionsDialog.Prompt(owner, settings, archives);
        if (choices is null)
        {
            return false;
        }

        string? password = knownPassword;
        for (int i = 0; i < archives.Count; i++)
        {
            string archive = archives[i];
            string name = Path.GetFileName(archive);
            string destination = archives.Count == 1
                ? choices.Destination
                : Path.Combine(choices.Destination, ExtractOptionsDialog.ArchiveBaseName(archive));
            string headline = archives.Count == 1 ? $"Extracting {name}" : $"Extracting {name}  ({i + 1} of {archives.Count})";

            bool extracted = RunWithPassword(owner, headline, name, ref password, (candidate, progress, cancellationToken) =>
                service.ExtractArchiveAsync(
                    new ArchiveExtractRequest
                    {
                        ArchivePath = archive,
                        DestinationDirectory = destination,
                        Password = candidate,
                        Overwrite = choices.Overwrite,
                    },
                    progress,
                    cancellationToken));

            if (!extracted)
            {
                return false;
            }
        }

        if (choices.OpenWhenDone)
        {
            OpenInExplorer(choices.Destination);
        }

        return true;
    }

    /// <summary>Verifies an archive and tells the user the result.</summary>
    /// <returns><see langword="true"/> when the archive is intact.</returns>
    public bool Test(IWin32Window? owner, string archive, string? knownPassword = null)
    {
        if (!AllExist(owner, [archive]))
        {
            return false;
        }

        string name = Path.GetFileName(archive);
        string? password = knownPassword;
        bool intact = RunWithPassword(owner, $"Testing {name}", name, ref password, (candidate, progress, cancellationToken) =>
            service.TestArchiveAsync(archive, candidate, progress, cancellationToken));

        if (intact)
        {
            MessageBox.Show(owner, $"No errors were found in “{name}”.", "Test complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return intact;
    }

    /// <summary>
    /// Opens one entry of an archive the way Explorer opens a file: it is extracted on its own into a temporary
    /// folder (<see cref="OpenedEntries"/>) and handed to the program Windows associates with it. A folder entry
    /// is extracted with its content and shown in Explorer.
    /// </summary>
    /// <param name="owner">The main window.</param>
    /// <param name="archive">The open archive.</param>
    /// <param name="entryPath">The entry, as listed (<c>/</c> separators).</param>
    /// <param name="isDirectory"><see langword="true"/> for a folder entry.</param>
    /// <param name="password">The password known for the archive; updated when the user enters a new one.</param>
    /// <returns><see langword="true"/> when the entry was opened.</returns>
    public bool OpenEntry(IWin32Window owner, string archive, string entryPath, bool isDirectory, ref string? password)
    {
        string name = Path.GetFileName(entryPath.TrimEnd('/'));
        if (!isDirectory && OpenedEntries.IsExecutable(entryPath))
        {
            DialogResult answer = MessageBox.Show(
                owner,
                $"“{name}” is a program or script. Running it can change your computer.\n\n" +
                "Only continue if you trust where this archive came from. Run it?",
                "Open file from archive",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return false;
            }
        }

        string folder = OpenedEntries.FolderFor(archive);
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); // Keeps the start-up clean-up away while in use.

        bool extracted = RunWithPassword(owner, $"Opening {name}", Path.GetFileName(archive), ref password, (candidate, progress, cancellationToken) =>
            service.ExtractArchiveAsync(
                new ArchiveExtractRequest
                {
                    ArchivePath = archive,
                    DestinationDirectory = folder,
                    Password = candidate,
                    Overwrite = true,
                    EntryPaths = [entryPath],
                },
                progress,
                cancellationToken));
        if (!extracted)
        {
            return false;
        }

        string target = OpenedEntries.PathOf(folder, entryPath);
        OpenedEntries.CopyMarkOfTheWeb(archive, target);

        if (isDirectory || Directory.Exists(target))
        {
            OpenInExplorer(target);
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No program is associated with this type: let the user pick one, as Explorer does.
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {target}"))?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                ErrorPresenter.Show(owner, new IOException($"“{name}” could not be opened: {ex.Message}", ex));
                return false;
            }
        }

        return true;
    }

    /// <summary>Opens a folder in Windows Explorer.</summary>
    public static void OpenInExplorer(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            // Showing the result is a courtesy; the operation itself succeeded.
        }
    }

    private static bool AllExist(IWin32Window? owner, IReadOnlyList<string> paths)
    {
        string? missing = paths.FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path));
        if (missing is not null)
        {
            ErrorPresenter.Show(owner, new FileNotFoundException($"“{missing}” does not exist.", missing));
        }

        return missing is null && paths.Count > 0;
    }

    /// <summary>
    /// Runs an operation that may need a password. It is first tried with the password at hand (often none);
    /// only when the archive rejects it is the user asked — repeatedly, until it works or they give up.
    /// </summary>
    private static bool RunWithPassword(
        IWin32Window? owner,
        string headline,
        string archiveName,
        ref string? password,
        Func<string?, IProgress<CompressionProgress>, CancellationToken, Task> operation)
    {
        while (true)
        {
            string? candidate = password;
            OperationResult result = ProgressDialog.Run(
                owner, headline, (progress, cancellationToken) => operation(candidate, progress, cancellationToken));

            switch (result)
            {
                case { Status: OperationStatus.Completed }:
                    return true;

                case { Status: OperationStatus.Failed, Error: InvalidPasswordException }:
                    password = PasswordDialog.Prompt(owner, archiveName, previousAttemptFailed: candidate is not null);
                    if (password is null)
                    {
                        return false;
                    }

                    continue;

                case { Status: OperationStatus.Failed }:
                    ErrorPresenter.Show(owner, result.Error!);
                    return false;

                default:
                    return false;
            }
        }
    }
}
