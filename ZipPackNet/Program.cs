using ZipPack;
using ZipPackNet.Cli;
using ZipPackNet.Services;
using ZipPackNet.Shell;
using ZipPackNet.UI;

namespace ZipPackNet;

internal static class Program
{
    /// <summary>
    /// The entry point. The command line decides between the main window and the short, window-less
    /// workflows started from the Explorer context menu (see <see cref="CommandLine.Usage"/>).
    /// </summary>
    /// <returns>0 on success, 1 when the requested operation was cancelled or failed, 2 for a bad command line.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        // Last line of defence: a bug in an event handler shows a message instead of killing the process.
        // This must be the very first Windows Forms call: once anything on this thread has created a window,
        // WinForms refuses the change and throws (which used to crash the Explorer-menu launches).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorPresenter.Show(Form.ActiveForm, e.Exception);

        // Must run first, before ApplicationConfiguration.Initialize(): during install, update and uninstall
        // Velopack starts the app with its own arguments, runs the matching hook here and exits before any
        // window is created. Normal launches pass straight through.
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => InstallerHooks.AfterInstall())
            .OnAfterUpdateFastCallback(_ => InstallerHooks.AfterUpdate())
            .OnBeforeUninstallFastCallback(_ => InstallerHooks.BeforeUninstall())
            .Run();

        // Routing: "--compress" / "--extract" come from the Explorer menu; a bare path comes from a
        // double-click on an associated .7z / .zst file (or drag-and-drop onto the exe) and opens the window.
        LaunchRequest request = CommandLine.Parse(args);

        // Explorer starts one process per selected item. Merge them before any window is created:
        // all but the first hand their path over and leave.
        IReadOnlyList<string> paths = request.Paths;
        if (request.Action is LaunchAction.Compress or LaunchAction.Extract)
        {
            if (LaunchBatcher.Collect(request.Action.ToString(), paths) is not { } batch)
            {
                return 0;
            }

            paths = batch;
        }

        // High DPI mode, visual styles and the default font come from the project file.
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);

        try
        {
            ICompressionService service = CompressionService.CreateDefault();
            AppSettings settings = AppSettings.Load();
            var workflows = new ArchiveWorkflows(service, settings);

            switch (request.Action)
            {
                case LaunchAction.Compress:
                    return workflows.Compress(owner: null, paths) is not null ? 0 : 1;

                case LaunchAction.Extract:
                    return workflows.Extract(owner: null, paths) ? 0 : 1;

                case LaunchAction.RegisterShell:
                    ShellIntegration.ForCurrentUser(service).Install();
                    return 0;

                case LaunchAction.UnregisterShell:
                    ShellIntegration.ForCurrentUser(service).Uninstall();
                    return 0;

                case LaunchAction.Help:
                    MessageBox.Show(
                        request.Error is null ? CommandLine.Usage : $"{request.Error}\n\n{CommandLine.Usage}",
                        "ZipPack command line",
                        MessageBoxButtons.OK,
                        request.Error is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    return request.Error is null ? 0 : 2;

                default:
                    Application.Run(new MainForm(
                        service,
                        settings,
                        workflows,
                        new ImageWorkflow(service, settings),
                        new UpdateWorkflow(UpdateService.CreateDefault()),
                        paths));
                    return 0;
            }
        }
        catch (Exception ex)
        {
            ErrorPresenter.Show(owner: null, ex);
            return 1;
        }
    }
}
