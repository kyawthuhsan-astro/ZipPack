using ZipPack;
using ZipPackNet.Services;
using ZipPackNet.Shell;
using ZipPackNet.UI;

namespace ZipPackTest;

/// <summary>Tests for "Delete files after compression" and the taskbar progress indicator.</summary>
public sealed class ZipPackNetCleanupAndTaskbarTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Cleanup_NeverDeletesTheArchiveOrAFolderThatContainsIt()
    {
        string[] sources =
        [
            @"C:\data\photos",            // contains the archive → kept
            @"C:\data\notes.txt",
            @"C:\data\notes.txt",         // listed twice → once
            @"C:\data\photos.zip",        // a different, unrelated file of a similar name → deleted
            @"C:\data\out\backup.7z",     // the archive itself → kept
            @"C:\data\music\",
        ];

        CleanupPlan plan = SourceCleanup.Plan(sources, @"C:\data\photos\..\out\..\photos\backup.7z");
        Assert.Equal([@"C:\data\notes.txt", @"C:\data\photos.zip", @"C:\data\out\backup.7z", @"C:\data\music\"], plan.Delete);
        Assert.Equal([@"C:\data\photos"], plan.Keep);

        CleanupPlan sameName = SourceCleanup.Plan([@"C:\data\out\backup.7z"], @"C:\data\out\backup.7z");
        Assert.Empty(sameName.Delete);
    }

    [Fact]
    public void Cleanup_RemovesEverythingItCan_AndReportsTheRest()
    {
        var removed = new List<string>();
        CleanupResult result = SourceCleanup.Run(
            [@"C:\a.txt", @"C:\locked.txt", @"C:\b"],
            path =>
            {
                if (path.Contains("locked", StringComparison.Ordinal))
                {
                    throw new IOException("The file is in use.");
                }

                removed.Add(path);
            });

        Assert.Equal([@"C:\a.txt", @"C:\b"], result.Deleted);
        Assert.Equal([(@"C:\locked.txt", "The file is in use.")], result.Failed);
        Assert.Equal(result.Deleted, removed);
    }

    [Fact]
    public async Task Cleanup_AfterARealArchive_LeavesTheArchiveAndRemovesTheSources()
    {
        // The create → verify → remove sequence of ArchiveWorkflows, without the dialogs. Removal is injected
        // so the test does not fill the Recycle Bin.
        ICompressionService service = CompressionService.CreateDefault();
        CancellationToken token = TestContext.Current.CancellationToken;
        string archive = Path.Combine(_workspace.Source, "inside.7z");
        string[] sources = [_workspace.Source, Path.Combine(_workspace.Source, "hello.txt")];
        await service.CreateArchiveAsync(new ArchiveCreateRequest { SourcePaths = [_workspace.TextFile], DestinationPath = archive }, cancellationToken: token);
        await service.TestArchiveAsync(archive, cancellationToken: token);

        CleanupPlan plan = SourceCleanup.Plan(sources, archive);
        CleanupResult result = SourceCleanup.Run(plan.Delete, File.Delete);

        Assert.Equal([_workspace.Source], plan.Keep);
        Assert.Equal([_workspace.TextFile], result.Deleted);
        Assert.False(File.Exists(_workspace.TextFile));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public void Taskbar_ShowsValueAndStatesOnARealWindow()
    {
        RunOnUiThread(form =>
        {
            var changes = new List<(TaskbarProgressState State, double Fraction)>();
            void Record(nint window, TaskbarProgressState state, double fraction)
            {
                if (window == form.Handle)
                {
                    changes.Add((state, fraction));
                }
            }

            TaskbarProgress.Changed += Record;
            try
            {
                var taskbar = new TaskbarProgress(form.Handle);
                Assert.True(taskbar.IsSupported, "ITaskbarList3 could not be created.");

                taskbar.SetState(TaskbarProgressState.Indeterminate);
                taskbar.SetValue(0.4);
                Assert.Equal((TaskbarProgressState.Normal, 0.4), (taskbar.State, taskbar.Fraction));

                taskbar.SetValue(7);       // clamped
                Assert.Equal(1d, taskbar.Fraction);
                taskbar.SetState(TaskbarProgressState.Paused);
                taskbar.SetState(TaskbarProgressState.Error);
                taskbar.Clear();
                Assert.Equal((TaskbarProgressState.None, 0d), (taskbar.State, taskbar.Fraction));

                Assert.Contains((TaskbarProgressState.Normal, 0.4), changes);
                Assert.Equal((TaskbarProgressState.None, 0d), changes[^1]);
            }
            finally
            {
                TaskbarProgress.Changed -= Record;
            }

            // A window that does not exist is ignored rather than failing.
            new TaskbarProgress(0).SetValue(0.5);
        });
    }

    [Fact]
    public void ProgressDialog_DrivesTheOwnersTaskbarButton_AndClearsItWhenDone()
    {
        RunOnUiThread(form =>
        {
            var changes = new List<(TaskbarProgressState State, double Fraction)>();
            void Record(nint window, TaskbarProgressState state, double fraction)
            {
                if (window == form.Handle)
                {
                    changes.Add((state, fraction));
                }
            }

            TaskbarProgress.Changed += Record;
            try
            {
                OperationResult result = ProgressDialog.Run(form, "Taskbar test", async (progress, cancellationToken) =>
                {
                    await Task.Delay(150, cancellationToken);
                    progress.Report(new CompressionProgress(250, 1000));
                    await Task.Delay(200, cancellationToken);
                    progress.Report(new CompressionProgress(750, 1000));
                    await Task.Delay(200, cancellationToken);
                });

                Assert.Equal(OperationStatus.Completed, result.Status);
            }
            finally
            {
                TaskbarProgress.Changed -= Record;
            }

            Assert.Equal(TaskbarProgressState.Indeterminate, changes[0].State);
            Assert.Contains((TaskbarProgressState.Normal, 0.25), changes);
            Assert.Contains((TaskbarProgressState.Normal, 0.75), changes);
            Assert.Equal(TaskbarProgressState.None, changes[^1].State);
        });
    }

    /// <summary>Runs <paramref name="test"/> on an STA thread with a shown (off-screen) top-level window.</summary>
    private static void RunOnUiThread(Action<Form> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form
                {
                    Text = "ZipPack taskbar test",
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-4000, -4000),
                    ShowInTaskbar = true,
                };
                form.Show();
                Application.DoEvents();
                test(form);
                form.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
