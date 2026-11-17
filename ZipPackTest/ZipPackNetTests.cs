using Microsoft.Win32;
using ZipPack;
using ZipPackNet.Cli;
using ZipPackNet.Services;
using ZipPackNet.Shell;
using ZipPackNet.UI;

namespace ZipPackTest;

/// <summary>Tests for the non-visual parts of the WinForms application.</summary>
public sealed class ZipPackNetTests
{
    [Fact]
    public void CommandLine_WithoutArguments_OpensTheMainWindow()
    {
        Assert.Equal(new LaunchRequest(LaunchAction.Open, []), CommandLine.Parse([]), LaunchRequestComparer.Instance);
    }

    [Theory]
    [InlineData("--compress", "Compress")]
    [InlineData("--COMPRESS", "Compress")]
    [InlineData("-c", "Compress")]
    [InlineData("--extract", "Extract")]
    [InlineData("-x", "Extract")]
    public void CommandLine_RoutesShellVerbsWithTheirPaths(string verb, string expected)
    {
        LaunchRequest request = CommandLine.Parse([verb, @"C:\data\My Files\a.txt", @"C:\data\folder"]);

        Assert.Equal(Enum.Parse<LaunchAction>(expected), request.Action);
        Assert.Equal([@"C:\data\My Files\a.txt", @"C:\data\folder"], request.Paths);
        Assert.Null(request.Error);
    }

    [Fact]
    public void CommandLine_BarePaths_AreOpenedAndMadeAbsolute()
    {
        LaunchRequest request = CommandLine.Parse(["relative.7z"]);

        Assert.Equal(LaunchAction.Open, request.Action);
        Assert.Equal([Path.GetFullPath("relative.7z")], request.Paths);
    }

    [Theory]
    [InlineData("--register-shell", "RegisterShell")]
    [InlineData("--unregister-shell", "UnregisterShell")]
    [InlineData("--help", "Help")]
    [InlineData("/?", "Help")]
    public void CommandLine_RecognisesSwitchesWithoutPaths(string argument, string expected)
    {
        LaunchRequest request = CommandLine.Parse([argument]);

        Assert.Equal(Enum.Parse<LaunchAction>(expected), request.Action);
        Assert.Null(request.Error);
    }

    [Theory]
    [InlineData("--compress")]
    [InlineData("--extract")]
    [InlineData("--frobnicate")]
    public void CommandLine_ReportsMistakesInsteadOfGuessing(string argument)
    {
        LaunchRequest request = CommandLine.Parse([argument]);

        Assert.Equal(LaunchAction.Help, request.Action);
        Assert.NotNull(request.Error);
    }

    [Fact]
    public void ShellIntegration_RegistersRepairsAndRemovesItsMenuEntries()
    {
        // A private key stands in for HKCU\Software\Classes, so the test never touches the real menu.
        string sandbox = $@"Software\ZipPackNet.Tests\{Guid.NewGuid():N}";
        RegistryKey OpenRoot() => Registry.CurrentUser.CreateSubKey(sandbox);
        try
        {
            using (RegistryKey root = OpenRoot())
            {
                // Something another application owns must survive untouched.
                root.CreateSubKey(@"SystemFileAssociations\.zip\shell\OtherApp").Dispose();
            }

            var shell = new ShellIntegration(OpenRoot, @"C:\Apps\Zip Pack\ZipPackNet.exe", [".7z", ".ZIP", ".tar.zst", ".zst"], notifyShell: false);
            Assert.Equal(ShellRegistrationState.NotRegistered, shell.GetState());
            Assert.Equal([".7z", ".zip", ".zst"], shell.ArchiveExtensions);

            shell.Register();

            Assert.Equal(ShellRegistrationState.Registered, shell.GetState());
            using (RegistryKey root = OpenRoot())
            {
                foreach (string target in new[] { "*", "Directory" })
                {
                    using RegistryKey verb = root.OpenSubKey($@"{target}\shell\ZipPackNet.Compress")!;
                    Assert.Equal("Compress with ZipPack", verb.GetValue(null));
                    Assert.Equal("Player", verb.GetValue("MultiSelectModel"));
                    Assert.Equal("\"C:\\Apps\\Zip Pack\\ZipPackNet.exe\",0", verb.GetValue("Icon"));
                    using RegistryKey command = verb.OpenSubKey("command")!;
                    Assert.Equal("\"C:\\Apps\\Zip Pack\\ZipPackNet.exe\" --compress \"%1\"", command.GetValue(null));
                }

                foreach (string extension in shell.ArchiveExtensions)
                {
                    using RegistryKey command = root.OpenSubKey($@"SystemFileAssociations\{extension}\shell\ZipPackNet.Extract\command")!;
                    Assert.Equal("\"C:\\Apps\\Zip Pack\\ZipPackNet.exe\" --extract \"%1\"", command.GetValue(null));
                }
            }

            // The same entries seen from a copy of the app in another folder need a repair …
            var moved = new ShellIntegration(OpenRoot, @"D:\Elsewhere\ZipPackNet.exe", [".7z"], notifyShell: false);
            Assert.Equal(ShellRegistrationState.RegisteredElsewhere, moved.GetState());

            // … and removing them also removes extensions the current version no longer lists.
            moved.Unregister();

            Assert.Equal(ShellRegistrationState.NotRegistered, shell.GetState());
            using (RegistryKey root = OpenRoot())
            {
                Assert.Null(root.OpenSubKey(@"*\shell\ZipPackNet.Compress"));
                Assert.Null(root.OpenSubKey(@"Directory\shell\ZipPackNet.Compress"));
                Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.7z"));
                Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.zst"));
                Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.zip\shell\ZipPackNet.Extract"));
                Assert.NotNull(root.OpenSubKey(@"SystemFileAssociations\.zip\shell\OtherApp"));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sandbox, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ShellIntegration_CoversEveryExtractableSingleExtension()
    {
        var shell = new ShellIntegration(
            () => throw new InvalidOperationException("The registry must not be touched here."),
            "ZipPackNet.exe",
            CompressionService.CreateDefault().GetStrategies<IArchiver>().SelectMany(a => a.FileExtensions),
            notifyShell: false);

        Assert.Superset(new HashSet<string> { ".7z", ".zip", ".zst", ".tzst", ".tar", ".gz", ".bz2", ".xz", ".rar" }, shell.ArchiveExtensions.ToHashSet());
        Assert.DoesNotContain(".tar.zst", shell.ArchiveExtensions);
    }

    [Fact]
    public async Task LaunchBatcher_MergesTheProcessesOfAMultiSelection()
    {
        // Each "process" is a thread here: the mutex and pipe behave the same across threads.
        string verb = "Test" + Guid.NewGuid().ToString("N");
        var collectorStarted = new TaskCompletionSource();

        Task<IReadOnlyList<string>?> collector = Task.Factory.StartNew(
            () =>
            {
                collectorStarted.SetResult();
                return LaunchBatcher.Collect(verb, [@"C:\a.txt"], TimeSpan.FromMilliseconds(1500));
            },
            TaskCreationOptions.LongRunning);

        await collectorStarted.Task;
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Task<IReadOnlyList<string>?>[] followers =
        [
            .. new[] { @"C:\b.txt", @"C:\folder", @"C:\A.TXT" }.Select(path =>
                Task.Factory.StartNew(() => LaunchBatcher.Collect(verb, [path]), TaskCreationOptions.LongRunning)),
        ];

        // Followers hand their path over and are told to exit; the collector ends up with everything, once.
        Assert.All(await Task.WhenAll(followers), Assert.Null);
        IReadOnlyList<string>? batch = await collector;
        Assert.NotNull(batch);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt", @"C:\folder"], batch.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ErrorPresenter_WordsFailuresForTheUser()
    {
        Assert.Equal("Wrong password", ErrorPresenter.Describe(new InvalidPasswordException("nope")).Title);
        Assert.Equal("Format not supported", ErrorPresenter.Describe(new UnsupportedFormatException("gzip holds one file")).Title);
        Assert.Equal("Archive error", ErrorPresenter.Describe(new CompressionException("damaged")).Title);
        Assert.Equal("File not found", ErrorPresenter.Describe(new FileNotFoundException("gone")).Title);
        Assert.Equal("Access denied", ErrorPresenter.Describe(new UnauthorizedAccessException("no")).Title);
        Assert.Equal("Disk full", ErrorPresenter.Describe(new IOException("full", unchecked((int)0x80070070))).Title);
        Assert.Equal("File in use", ErrorPresenter.Describe(new IOException("locked", unchecked((int)0x80070020))).Title);
        Assert.Equal("File error", ErrorPresenter.Describe(new IOException("other")).Title);
        Assert.Equal("Wrong password", ErrorPresenter.Describe(new AggregateException(new InvalidPasswordException("x"))).Title);

        UserMessage unexpected = ErrorPresenter.Describe(new InvalidOperationException("boom"));
        Assert.Equal("Unexpected error", unexpected.Title);
        Assert.Contains("boom", unexpected.Text);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024 + 322122547, "3.3 GB")]
    public void Format_Size_IsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, Format.Size(bytes).Replace(',', '.'));
    }

    [Fact]
    public void Settings_RoundTripAndSurviveADamagedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"zippack-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { DefaultFormatId = "zstd", DefaultPreset = CompressionPreset.Ultra, OpenFolderAfterExtract = false }.Save(path);
            AppSettings loaded = AppSettings.Load(path);
            Assert.Equal(("zstd", CompressionPreset.Ultra, false), (loaded.DefaultFormatId, loaded.DefaultPreset, loaded.OpenFolderAfterExtract));

            File.WriteAllText(path, "{ not json");
            Assert.Equal("7z", AppSettings.Load(path).DefaultFormatId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(@"C:\x\backup.tar.zst", "backup")]
    [InlineData(@"C:\x\photos.7z", "photos")]
    [InlineData(@"C:\x\notes.txt.gz", "notes.txt")]
    public void ExtractDestination_DropsCompoundArchiveExtensions(string archive, string expected)
    {
        Assert.Equal(expected, ExtractOptionsDialog.ArchiveBaseName(archive));
    }

    [Fact]
    public void FileList_CanBeEmptiedWhileRowsAreSelectedAndFocused()
    {
        // Regression: clearing the queue after a successful compression threw ArgumentOutOfRangeException
        // because the virtual list still asked for the selected/focused row.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000) };
                var list = new FileListView { Dock = DockStyle.Fill };
                int summaries = 0;
                list.SelectedIndexChanged += (_, _) => summaries += list.SelectedRows.Count;
                list.VirtualItemsSelectionRangeChanged += (_, _) => summaries += list.SelectedRows.Count;
                form.Controls.Add(list);
                form.Show();

                FileListItem[] rows = [.. Enumerable.Range(0, 3).Select(i => new FileListItem($"file{i}.txt", @"C:\data", false, i, null, null, $@"C:\data\file{i}.txt"))];
                list.AddRows(rows);
                list.Focus();
                list.SelectAllRows();
                list.FocusedItem = list.Items[2];
                Application.DoEvents();

                list.SetRows([]);
                Application.DoEvents();
                Assert.Empty(list.Rows);
                Assert.Empty(list.SelectedRows);

                // Removing the selected rows from the middle and the end must be just as safe.
                list.AddRows(rows);
                list.SelectedIndices.Add(1);
                list.SelectedIndices.Add(2);
                list.FocusedItem = list.Items[2];
                Application.DoEvents();
                list.RemoveSelectedRows();
                Application.DoEvents();
                Assert.Equal(["file0.txt"], list.Rows.Select(row => row.Name));
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

    private sealed class LaunchRequestComparer : IEqualityComparer<LaunchRequest>
    {
        public static LaunchRequestComparer Instance { get; } = new();

        public bool Equals(LaunchRequest? x, LaunchRequest? y) =>
            x is not null && y is not null && x.Action == y.Action && x.Error == y.Error && x.Paths.SequenceEqual(y.Paths);

        public int GetHashCode(LaunchRequest obj) => obj.Action.GetHashCode();
    }
}
